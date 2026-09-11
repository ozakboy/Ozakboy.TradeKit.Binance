using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

using Ozakboy.Http;
using Ozakboy.Http.Signing;
using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約的行情來源:歷史 K 線走 REST,即時 K 線與標記價走 WebSocket。
/// The Binance USDⓈ-M futures market data source: historical klines over REST, live klines and mark prices
/// over WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// <b>連線管理不在這裡。</b> 自動重連、退避與抖動、重連後重放訂閱、閒置逾時存活偵測、有界佇列背壓,
/// 全部由 <c>Ozakboy.WebSockets</c> 提供。本類別只負責幣安這一側的協定細節:串流名稱怎麼組、
/// 路徑走哪一條、外層包裝怎麼拆、欄位縮寫怎麼對映。
/// <b>Connection management does not live here.</b> Reconnection with backoff and jitter, subscription replay
/// after a reconnect, idle-timeout liveness detection, and bounded-queue backpressure all come from
/// <c>Ozakboy.WebSockets</c>. This type owns only the Binance side of the protocol: how a stream is named,
/// which path to dial, how the envelope comes off, and what the abbreviated fields mean.
/// </para>
/// <para>
/// <b>每一次訂閱都是自己的連線。</b> 兩個 <c>Subscribe</c> 方法各自撥號、各自訂閱、各自在取消時關閉,
/// 彼此不共用連線。共用一條連線可以省下一次握手,代價是取消 K 線訂閱時得小心不要順手把標記價也停掉,
/// 而那種錯誤的症狀是「某一種行情靜悄悄地不再更新」,連線狀態卻一切正常。
/// <b>Every subscription owns its connection.</b> The two <c>Subscribe</c> methods dial, subscribe, and close
/// independently and share nothing. Sharing one connection would save a handshake at the cost of having to be
/// careful that cancelling the kline subscription does not also stop the mark prices — a mistake whose only
/// symptom is one kind of data quietly ceasing to update while the connection status stays perfectly healthy.
/// </para>
/// <para>
/// <b>策略只能吃 <see cref="Kline.IsClosed"/> 為 <see langword="true"/> 的 K 線。</b> 串流會不斷推送
/// 同一根還在跳動的 K 線,每一筆的收盤價都是當下最新價;拿未收盤的 K 線算指標,訊號會在同一根 K 線內
/// 反覆翻面,策略就會反覆進出場。回測用的是收盤資料,所以這個錯誤在回測裡完全看不出來。
/// <b>A strategy consumes only candles whose <see cref="Kline.IsClosed"/> is <see langword="true"/>.</b> The
/// stream keeps pushing the same in-progress candle, each push carrying the latest price as its close. Feeding
/// an unfinished candle to an indicator makes the signal flip back and forth inside one candle and the strategy
/// enter and exit with it. A backtest runs on closed data, so it never shows this at all.
/// </para>
/// </remarks>
public sealed class BinanceMarketDataFeed : IMarketDataFeed
{
    private readonly BinanceApiClient _api;
    private readonly BinanceEndpoints _endpoints;
    private readonly BinanceMarketStreamOptions _streamOptions;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider? _timeProvider;
    private readonly IWebSocketConnectionFactory? _connectionFactory;

    private long _requestId;

    /// <summary>
    /// 建立行情來源。
    /// Creates the market data source.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="streamOptions">
    /// 串流設定,未提供時採用預設值。
    /// The stream settings; the defaults are used when none is supplied.
    /// </param>
    /// <param name="loggerFactory">
    /// 日誌工廠,轉交給連線層。
    /// The logger factory, handed to the connection layer.
    /// </param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <param name="connectionFactory">
    /// WebSocket 連線工廠。未提供時由 <c>Ozakboy.WebSockets</c> 建立真正的連線;
    /// 單元測試傳入假工廠,整組訂閱流程就能在完全不碰網路的情況下被驗證。
    /// The WebSocket connection factory. When none is supplied <c>Ozakboy.WebSockets</c> builds a real
    /// connection; a unit test passes a fake one and exercises the whole subscription path without touching the
    /// network.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="http"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="http"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> 或 <paramref name="streamOptions"/> 不合法時擲出。
    /// Thrown when <paramref name="options"/> or <paramref name="streamOptions"/> is invalid.
    /// </exception>
    public BinanceMarketDataFeed(
        HttpPipelineClient http,
        BinanceOptions options,
        BinanceMarketStreamOptions? streamOptions = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        IWebSocketConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _streamOptions = streamOptions ?? new BinanceMarketStreamOptions();

        var streamValidation = _streamOptions.Validate();

        if (streamValidation.IsFailure)
        {
            throw new ArgumentException(streamValidation.Error.Message, nameof(streamOptions));
        }

        _api = new BinanceApiClient(http, options, timeProvider);
        _endpoints = _api.Endpoints;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// 這個行情來源連的是哪一組端點。
    /// Which endpoint set this feed talks to.
    /// </summary>
    public BinanceEndpoints Endpoints => _endpoints;

    /// <summary>
    /// 這個行情來源採用的串流設定。
    /// The stream settings in force.
    /// </summary>
    public BinanceMarketStreamOptions StreamOptions => _streamOptions;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <see cref="KlineQuery"/> 的時間區間是左閉右開,幣安的 <c>endTime</c> 卻是<b>含端點</b>的
    /// (實測:<c>startTime</c> 到 <c>endTime</c> 之間的 1m K 線回了六根,把 <c>endTime</c> 減一毫秒
    /// 就回五根)。因此送出前會把結束時間減一毫秒,否則連續分頁抓歷史時,每一頁的最後一根都會
    /// 和下一頁的第一根重複 —— 而重複的 K 線在指標裡會變成一次不存在的價格變動。
    /// The range of a <see cref="KlineQuery"/> is half-open while the Binance <c>endTime</c> is
    /// <b>inclusive</b>: measured, a one-minute query from <c>startTime</c> to <c>endTime</c> returns six
    /// candles and the same query with <c>endTime</c> reduced by one millisecond returns five. One millisecond
    /// is therefore subtracted before the request goes out; without it, paging through history repeats the last
    /// candle of every page as the first candle of the next — and a duplicated candle reads to an indicator as
    /// a price move that never happened.
    /// </para>
    /// <para>
    /// 回傳的最後一根很可能<b>還沒收盤</b>,其 <see cref="Kline.IsClosed"/> 會是 <see langword="false"/>。
    /// 幣安的回應沒有這個旗標,是本套件用收盤時間與當下時間比對出來的,詳見實作註解。
    /// The last candle returned is very likely <b>still open</b>, with <see cref="Kline.IsClosed"/> set to
    /// <see langword="false"/>. Binance sends no such flag; it is derived here by comparing the close time
    /// against the current time.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<Kline>>> GetKlinesAsync(
        KlineQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var validation = query.Validate();

        if (validation.IsFailure)
        {
            return validation.ToFailure<IReadOnlyList<Kline>>();
        }

        if (query.Limit is { } requested && requested > BinanceMarketDataPaths.MaxKlineLimit)
        {
            // 不悄悄截成上限。要五千根卻拿到一千五百根,指標的暖機長度就少了七成,
            // 而算出來的數字看起來完全正常。
            // The limit is not quietly clamped: asking for five thousand candles and receiving fifteen hundred
            // leaves an indicator seventy per cent short of its warm-up, and the numbers it produces look
            // entirely reasonable.
            return TradeErrors.InvalidQuery(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"單次最多只能查 {BinanceMarketDataPaths.MaxKlineLimit} 根 K 線,收到 {requested};請自行分頁。At most {BinanceMarketDataPaths.MaxKlineLimit} candles may be fetched in one request but {requested} were asked for; page the query instead."));
        }

        var builder = QueryParameters.CreateBuilder()
            .Add(BinanceConstants.SymbolParameterName, query.Symbol)
            .Add(BinanceMarketDataPaths.IntervalParameterName, query.Interval.ToExchangeString());

        builder.AddIfNotNull(
            BinanceMarketDataPaths.StartTimeParameterName,
            query.StartTime?.ToUnixTimeMilliseconds());

        builder.AddIfNotNull(
            BinanceMarketDataPaths.EndTimeParameterName,
            query.EndTime is { } endTime ? endTime.ToUnixTimeMilliseconds() - 1L : null);

        builder.AddIfNotNull(BinanceMarketDataPaths.LimitParameterName, (long?)query.Limit);

        var weight = BinanceRequestWeights.Klines(query.Limit ?? BinanceMarketDataPaths.DefaultKlineLimit);

        var body = await _api
            .GetPublicAsync(BinanceMarketDataPaths.Klines, builder.Build(), weight, cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceKlineReader.Read(json, query.Symbol, query.Interval, _api.UtcNow)
            : body.ToFailure<IReadOnlyList<Kline>>();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<Kline>> SubscribeKlinesAsync(
        IReadOnlyCollection<string> symbols,
        KlineInterval interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streams = BuildStreamNames(symbols, symbol => BinanceStreamNames.Kline(symbol, interval));

        if (!streams.TryGetValue(out var streamNames))
        {
            yield return streams.ToFailure<Kline>();
            yield break;
        }

        await foreach (var item in SubscribeCoreAsync(streamNames, BinanceStreamReader.ReadKline, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<MarkPriceUpdate>> SubscribeMarkPricesAsync(
        IReadOnlyCollection<string> symbols,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streams = BuildStreamNames(
            symbols,
            symbol => BinanceStreamNames.MarkPrice(symbol, _streamOptions.UseFastMarkPriceUpdates));

        if (!streams.TryGetValue(out var streamNames))
        {
            yield return streams.ToFailure<MarkPriceUpdate>();
            yield break;
        }

        await foreach (var item in SubscribeCoreAsync(
            streamNames,
            BinanceStreamReader.ReadMarkPrice,
            cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static Result<IReadOnlyList<string>> BuildStreamNames(
        IReadOnlyCollection<string>? symbols,
        Func<string, Result<string>> build)
    {
        if (symbols is null || symbols.Count == 0)
        {
            return TradeErrors.InvalidQuery(
                "至少要指定一個交易對才能訂閱。At least one symbol is required for a subscription.");
        }

        var streamNames = new List<string>(symbols.Count);

        foreach (var symbol in symbols)
        {
            var name = build(symbol);

            if (!name.TryGetValue(out var value))
            {
                return name.ToFailure<IReadOnlyList<string>>();
            }

            // 重複的串流名稱幣安會照單全收,但同一筆行情就會在串流裡出現兩次;
            // 策略若逐筆累加成交量,重複的那一份會讓數字憑空變大。
            // Binance accepts a duplicated stream name happily, and the same update then appears twice on the
            // stream; a strategy accumulating volume tick by tick would quietly count it twice.
            if (!streamNames.Contains(value, StringComparer.Ordinal))
            {
                streamNames.Add(value);
            }
        }

        return streamNames.Count > BinanceStreamNames.MaxStreamsPerConnection
            ? TradeErrors.InvalidQuery(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"單一連線最多訂閱 {BinanceStreamNames.MaxStreamsPerConnection} 個串流,收到 {streamNames.Count};請分成多次訂閱。One connection carries at most {BinanceStreamNames.MaxStreamsPerConnection} streams but {streamNames.Count} were requested; split the subscription."))
            : streamNames;
    }

    private async IAsyncEnumerable<Result<T>> SubscribeCoreAsync<T>(
        IReadOnlyList<string> streamNames,
        Func<string, IReadOnlyList<string>, BinanceEndpoints, BinanceStreamRead<T>> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        var uri = BinanceStreamNames.CombinedStreamUri(_endpoints.WebSocketBaseUri);
        var options = _streamOptions.CreateWebSocketOptions(uri, NextKeepAlivePayload);
        var optionsValidation = options.Validate();

        if (optionsValidation.IsFailure)
        {
            // 先自己驗一次。連線層的建構式對不合法的設定是<b>擲例外</b>,而例外從 await foreach 裡竄出去
            // 會直接違反這個介面的契約 —— 消費端該看到的是一筆說得出原因的失敗,不是一個把迴圈炸開的例外。
            // 走到這裡的典型情況是端點覆寫填了 http 而不是 ws:那是後門,沒有人會替它擋。
            // The options are validated here first because the connection layer's constructor throws on bad
            // ones, and an exception tearing out of an await foreach breaks this interface's contract outright:
            // the consumer is owed a failure that says why, not an exception that blows the loop apart. The
            // usual way to arrive here is an endpoint override carrying http instead of ws — it is a back door,
            // and nothing else guards it.
            yield return Result.Failure<T>(
                BinanceStreamErrors.FromWebSocketError(optionsValidation.Error, streamNames, _endpoints));
            yield break;
        }

        var client = CreateClient(options);

        await using var clientLifetime = client.ConfigureAwait(false);

        var subscription = new WebSocketSubscription(
            string.Join(',', streamNames),
            BinanceStreamCommands.Subscribe(NextRequestId(), streamNames),
            BinanceStreamCommands.Unsubscribe(NextRequestId(), streamNames));

        // 訂閱先登記、連線後啟動。順序是重點:登記在前,這則 SUBSCRIBE 就會跟著每一次(含第一次)
        // 連線的重放一起送出,而重放是在連線公開給呼叫端之前完成的,不存在「連上了但還沒訂閱」的空窗。
        // The subscription is registered before the client starts, and the order matters: registering first
        // puts this SUBSCRIBE into the replay that runs on every connection including the first, and the replay
        // completes before the connection is published, so there is no window in which the socket is up but
        // nothing is subscribed.
        var startup = (await client.SubscribeAsync(subscription, cancellationToken).ConfigureAwait(false))
            .Then(client.Start);

        if (startup.IsFailure)
        {
            yield return Result.Failure<T>(
                BinanceStreamErrors.FromWebSocketError(startup.Error, streamNames, _endpoints));
            yield break;
        }

        var reportedDrops = 0L;

        await foreach (var message in client.Messages(cancellationToken).ConfigureAwait(false))
        {
            var dropped = client.Statistics.MessagesDropped;

            if (dropped > reportedDrops)
            {
                yield return Result.Failure<T>(
                    BinanceStreamErrors.MessagesDropped(dropped - reportedDrops, dropped, streamNames, _endpoints));

                reportedDrops = dropped;
            }

            if (!message.TryGetValue(out var frame))
            {
                // 斷線與重連在串流裡現身。分類原樣帶過來,消費端用 Error.IsTransient 就能分辨
                // 「有缺口、還在重連」與「這條串流結束了」。
                // Disconnects and reconnects surface on the stream. The category comes across untouched, so
                // Error.IsTransient alone tells a gap that is being repaired from a stream that has ended.
                // Error 在失敗結果上必定有值,可空性標註表達不了這個前提。
                // A failed result always carries an Error; the nullability annotation cannot say so.
                yield return Result.Failure<T>(
                    BinanceStreamErrors.FromWebSocketError(message.Error!, streamNames, _endpoints));

                continue;
            }

            if (frame.Kind != WebSocketMessageKind.Text || frame.Text is null)
            {
                yield return Result.Failure<T>(
                    BinanceStreamErrors.MalformedStreamMessage(
                        "行情串流收到非文字訊息,幣安的行情推送一律是文字。A non-text frame arrived on the market stream; Binance market pushes are always text.",
                        null,
                        streamNames,
                        _endpoints));

                continue;
            }

            var outcome = read(frame.Text, streamNames, _endpoints);

            switch (outcome.Kind)
            {
                case BinanceStreamReadKind.Payload:
                    yield return Result.Success(outcome.Value!);
                    break;

                case BinanceStreamReadKind.Failed:
                    yield return Result.Failure<T>(outcome.Error!);
                    break;

                case BinanceStreamReadKind.Ignored:
                default:
                    break;
            }
        }
    }

    private WebSocketClient CreateClient(WebSocketClientOptions options) =>
        // 沒有注入工廠時就用 Ozakboy.WebSockets 自己的預設工廠,而不是走另一個建構式多載。
        // 兩條路長得很像,但只有其中一條會被單元測試走到,另一條就成了永遠沒人驗過的程式碼。
        // With no injected factory, the package's own default is used rather than a second constructor
        // overload. The two paths look alike, but only one of them is ever exercised by a unit test and the
        // other becomes code nobody has run.
        new(
            options,
            _connectionFactory ?? new ClientWebSocketConnectionFactory(options),
            _loggerFactory?.CreateLogger<WebSocketClient>(),
            _timeProvider);

    private string NextKeepAlivePayload() => BinanceStreamCommands.ListSubscriptions(NextRequestId());

    private long NextRequestId() => Interlocked.Increment(ref _requestId);
}
