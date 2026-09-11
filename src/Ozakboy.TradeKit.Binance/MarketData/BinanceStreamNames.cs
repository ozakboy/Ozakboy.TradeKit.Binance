namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 幣安 USDⓈ-M 合約行情串流的名稱與路徑組裝。
/// Builds the stream names and URLs of the Binance USDⓈ-M futures market streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>路徑是實測出來的,不是照文件抄的。</b> 2026-09-11 對 Testnet
/// (<c>wss://stream.binancefuture.com</c>)逐一撥號驗證,結論如下:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>/ws/&lt;串流名&gt;</c> 可用,訊息<b>沒有</b>外層包裝,直接就是事件物件。
/// <c>/ws/&lt;stream&gt;</c> works and the frames carry <b>no</b> envelope: the event object arrives bare.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>/stream?streams=a/b</c> 可用,訊息包在 <c>{"stream":…,"data":…}</c> 裡;分隔符號必須是斜線,
/// 換成逗號連握手都不會成功。
/// <c>/stream?streams=a/b</c> works and wraps every frame in <c>{"stream":…,"data":…}</c>. The separator must
/// be a slash; a comma fails the handshake outright.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>/ws</c> 與 <c>/stream</c> 可以不帶任何串流直接連上,之後用 <c>SUBSCRIBE</c> 控制訊息訂閱。
/// <b>外層包裝由路徑決定,與訂閱幾檔無關</b>:走 <c>/stream</c> 訂一檔也有包裝,走 <c>/ws</c> 訂十檔也沒有。
/// Both <c>/ws</c> and <c>/stream</c> accept a connection with no streams at all and take <c>SUBSCRIBE</c>
/// control messages afterwards. <b>The envelope follows the path, not the number of streams</b>: one stream on
/// <c>/stream</c> is still wrapped, and ten streams on <c>/ws</c> are still bare.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>文件上的 <c>/public/ws/…</c> 與 <c>/public/stream…</c> 在 Testnet 收不到任何資料。</b>
/// 而且失敗的方式最壞:握手成功、<c>SUBSCRIBE</c> 還回了 <c>{"result":null,"id":1}</c> 表示受理,
/// 然後一筆行情都不送。沒有錯誤、沒有斷線,只有永遠不動的價格。本類別因此把已驗證的路徑寫死,
/// 不接受由設定拼裝路徑 —— 這個錯拼不出任何症狀。
/// <b>The documented <c>/public/ws/…</c> and <c>/public/stream…</c> forms deliver nothing on the testnet</b>,
/// and they fail in the worst possible way: the handshake succeeds, a <c>SUBSCRIBE</c> is even acknowledged
/// with <c>{"result":null,"id":1}</c>, and then no market data ever arrives. No error, no disconnect, just a
/// price that never moves. This type therefore hard-codes the verified paths instead of letting configuration
/// assemble them, because that mistake produces no symptom at all.
/// </description>
/// </item>
/// </list>
/// <para>
/// 串流名稱的<b>交易對一律小寫</b>,週期則保持原本的大小寫。這兩件事只能分開做:整串轉小寫會把
/// <c>kline_1M</c>(一個月)變成 <c>kline_1m</c>(一分鐘),訂閱照樣成功、資料照樣進來,
/// 只是週期完全不是要的那個,而且要等到對帳時才看得出來。
/// The <b>symbol is always lower-cased</b> while the interval keeps its own casing, and the two must be handled
/// separately: lower-casing the whole name turns <c>kline_1M</c> (one month) into <c>kline_1m</c> (one minute).
/// The subscription still succeeds and data still flows — on an interval nobody asked for, discovered only when
/// something is reconciled much later.
/// </para>
/// </remarks>
public static class BinanceStreamNames
{
    /// <summary>
    /// 原始串流的路徑區段:訊息沒有外層包裝。
    /// The raw stream path segment, whose frames carry no envelope.
    /// </summary>
    public const string RawStreamPath = "ws";

    /// <summary>
    /// 組合串流的路徑區段:訊息包在 <c>{"stream":…,"data":…}</c> 裡。
    /// The combined stream path segment, whose frames are wrapped in <c>{"stream":…,"data":…}</c>.
    /// </summary>
    public const string CombinedStreamPath = "stream";

    /// <summary>
    /// <c>streams=</c> 查詢字串裡的串流分隔符號。必須是斜線,逗號會讓握手失敗。
    /// The separator inside the <c>streams=</c> query. It must be a slash; a comma fails the handshake.
    /// </summary>
    public const char StreamNameSeparator = '/';

    /// <summary>
    /// K 線推送的事件型別值(<c>e</c> 欄位)。
    /// The event type value of a kline push, carried in the <c>e</c> field.
    /// </summary>
    public const string KlineEventType = "kline";

    /// <summary>
    /// 標記價推送的事件型別值(<c>e</c> 欄位)。注意是 <c>markPriceUpdate</c>,不是串流名稱裡的 <c>markPrice</c>。
    /// The event type value of a mark price push. Note that it is <c>markPriceUpdate</c> rather than the
    /// <c>markPrice</c> that appears in the stream name.
    /// </summary>
    public const string MarkPriceEventType = "markPriceUpdate";

    /// <summary>
    /// 組合串流外層的串流名欄位。
    /// The property naming the stream in the combined-stream envelope.
    /// </summary>
    public const string EnvelopeStreamProperty = "stream";

    /// <summary>
    /// 組合串流外層的資料欄位。
    /// The property carrying the payload in the combined-stream envelope.
    /// </summary>
    public const string EnvelopeDataProperty = "data";

    /// <summary>
    /// 單一連線可訂閱的串流數上限。
    /// The maximum number of streams one connection may carry.
    /// </summary>
    /// <remarks>
    /// 超過上限的訂閱會被交易所拒絕或截斷,而截斷的症狀是「有些標的就是沒有資料」——
    /// 看起來像那幾檔沒成交,不像設定超標。呼叫端在送出之前就該被擋下來。
    /// A subscription beyond the ceiling is rejected or truncated by the exchange, and truncation shows up as
    /// "some symbols simply have no data", which reads like a quiet market rather than an over-sized request.
    /// The caller is stopped before anything goes out.
    /// </remarks>
    public const int MaxStreamsPerConnection = 200;

    private const string KlineSuffix = "@kline_";

    private const string MarkPriceSuffix = "@markPrice";

    private const string FastUpdateSuffix = "@1s";

    /// <summary>
    /// 組出 K 線串流名稱,例如 <c>btcusdt@kline_15m</c>。
    /// Builds a kline stream name such as <c>btcusdt@kline_15m</c>.
    /// </summary>
    /// <param name="symbol">交易對代碼,大小寫不拘,會轉成小寫。The symbol; any casing, lower-cased here.</param>
    /// <param name="interval">K 線週期。The interval.</param>
    /// <returns>
    /// 串流名稱,或說明哪一項不合法的失敗。
    /// The stream name, or a failure saying which argument is invalid.
    /// </returns>
    public static Result<string> Kline(string symbol, KlineInterval interval)
    {
        var normalized = NormalizeSymbol(symbol);

        if (!normalized.TryGetValue(out var lowerSymbol))
        {
            return normalized;
        }

        if (interval == KlineInterval.Unspecified || !Enum.IsDefined(interval))
        {
            return TradeErrors.UnsupportedInterval(interval.ToString());
        }

        // 週期字串刻意不跟著轉小寫,見類別註解:1M 與 1m 只差在大小寫,意義差了四萬倍。
        // The interval string deliberately keeps its casing; see the type remarks. 1M and 1m differ by case
        // alone and by a factor of forty thousand in meaning.
        return string.Concat(lowerSymbol, KlineSuffix, interval.ToExchangeString());
    }

    /// <summary>
    /// 組出標記價串流名稱,例如 <c>btcusdt@markPrice@1s</c>。
    /// Builds a mark price stream name such as <c>btcusdt@markPrice@1s</c>.
    /// </summary>
    /// <param name="symbol">交易對代碼,大小寫不拘,會轉成小寫。The symbol; any casing, lower-cased here.</param>
    /// <param name="fastUpdates">
    /// 是否使用每秒更新的版本。<see langword="false"/> 為每三秒更新一次。
    /// Whether to use the one-second variant; <see langword="false"/> updates every three seconds.
    /// </param>
    /// <returns>
    /// 串流名稱,或說明交易對不合法的失敗。
    /// The stream name, or a failure saying the symbol is invalid.
    /// </returns>
    public static Result<string> MarkPrice(string symbol, bool fastUpdates = true)
    {
        var normalized = NormalizeSymbol(symbol);

        return normalized.TryGetValue(out var lowerSymbol)
            ? string.Concat(lowerSymbol, MarkPriceSuffix, fastUpdates ? FastUpdateSuffix : string.Empty)
            : normalized;
    }

    /// <summary>
    /// 組出組合串流的連線位址,例如 <c>wss://stream.binancefuture.com/stream</c>。
    /// Builds the combined-stream address, such as <c>wss://stream.binancefuture.com/stream</c>.
    /// </summary>
    /// <param name="webSocketBaseUri">
    /// WebSocket 基底位址,取自 <see cref="BinanceEndpoints.WebSocketBaseUri"/>。
    /// The WebSocket base address from <see cref="BinanceEndpoints.WebSocketBaseUri"/>.
    /// </param>
    /// <returns>連線位址。The address to dial.</returns>
    /// <remarks>
    /// 這裡不訂任何串流,連上之後才用 <c>SUBSCRIBE</c> 控制訊息訂閱。這樣做有兩個好處:
    /// 重連時由 <c>Ozakboy.WebSockets</c> 自動重放同一則 <c>SUBSCRIBE</c>,不必為了換訂閱而重新撥號;
    /// 而且 <c>streams=</c> 查詢字串有長度上限,標的一多就會踩到。
    /// No streams are named here; they are subscribed with a <c>SUBSCRIBE</c> control message after the
    /// connection is up. That buys two things: <c>Ozakboy.WebSockets</c> replays the very same <c>SUBSCRIBE</c>
    /// on reconnect, so changing a subscription never needs a redial, and the <c>streams=</c> query has a length
    /// ceiling that a long symbol list runs into.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="webSocketBaseUri"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="webSocketBaseUri"/> is <see langword="null"/>.
    /// </exception>
    public static Uri CombinedStreamUri(Uri webSocketBaseUri) => AppendSegment(webSocketBaseUri, CombinedStreamPath);

    /// <summary>
    /// 組出單一原始串流的連線位址,例如 <c>wss://stream.binancefuture.com/ws/btcusdt@kline_1m</c>。
    /// Builds a raw single-stream address, such as
    /// <c>wss://stream.binancefuture.com/ws/btcusdt@kline_1m</c>.
    /// </summary>
    /// <param name="webSocketBaseUri">WebSocket 基底位址。The WebSocket base address.</param>
    /// <param name="streamName">串流名稱。The stream name.</param>
    /// <returns>連線位址。The address to dial.</returns>
    /// <remarks>
    /// <para>
    /// 行情訂閱不走這條路徑(行情走 <see cref="CombinedStreamUri"/> 加 <c>SUBSCRIBE</c>),
    /// 但<b>使用者資料串流用它撥號</b>:<see cref="BinanceUserDataFeed"/> 以 listenKey 當串流名稱,
    /// 連上 <c>{WebSocketBaseUri}/ws/{listenKey}</c>。走 <c>/ws/</c> 收到的訊息沒有外層包裝,
    /// 事件物件就是最外層,手動比對原始欄位時也比較直接。
    /// Market subscriptions do not use this path — they go through <see cref="CombinedStreamUri"/> plus
    /// <c>SUBSCRIBE</c> — but <b>the user data stream dials it</b>: <see cref="BinanceUserDataFeed"/> passes the
    /// listenKey as the stream name and connects to <c>{WebSocketBaseUri}/ws/{listenKey}</c>. Frames on
    /// <c>/ws/</c> carry no envelope, so the event object is the outermost one, which also makes raw fields easier
    /// to compare by hand.
    /// </para>
    /// <para>
    /// 因此這個方法的回傳值<b>可能含有憑證</b>:拿它當串流名稱時,產生的 <see cref="Uri"/> 本身就是祕密,
    /// 不可以寫進錯誤訊息、診斷資料或日誌。
    /// Its return value can therefore <b>carry a credential</b>: when the stream name is a listenKey, the
    /// resulting <see cref="Uri"/> is itself a secret and must not reach an error message, diagnostic data, or a
    /// log.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="webSocketBaseUri"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="webSocketBaseUri"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="streamName"/> 為空白時擲出。Thrown when <paramref name="streamName"/> is blank.
    /// </exception>
    public static Uri RawStreamUri(Uri webSocketBaseUri, string streamName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        return AppendSegment(AppendSegment(webSocketBaseUri, RawStreamPath), streamName);
    }

    /// <summary>
    /// 檢查並小寫化交易對代碼。
    /// Validates a symbol and lower-cases it.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <returns>小寫後的代碼,或驗證失敗。The lower-cased symbol, or a validation failure.</returns>
    /// <remarks>
    /// 只接受 ASCII 英數與底線。底線是必要的 —— 交割合約的代碼長成 <c>BTCUSDT_250926</c>;
    /// 而 <c>@</c> 與 <c>/</c> 必須擋下,它們是串流名稱與串流清單的分隔符號,
    /// 混進代碼裡會把一個訂閱悄悄變成兩個(或一個不存在的)訂閱。
    /// Only ASCII alphanumerics and the underscore are accepted. The underscore is required because delivery
    /// contracts look like <c>BTCUSDT_250926</c>, while <c>@</c> and <c>/</c> must be rejected: they separate
    /// stream names and stream lists, and letting one through quietly turns one subscription into two — or into
    /// one that does not exist.
    /// </remarks>
    public static Result<string> NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        foreach (var character in symbol)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return TradeErrors.InvalidQuery(
                    $"交易對代碼「{symbol}」含有不合法的字元「{character}」,串流名稱只接受英數與底線。The symbol \"{symbol}\" contains the invalid character '{character}'; stream names accept only alphanumerics and underscores.")
                    .WithData(BinanceErrorDataKeys.Symbol, symbol);
            }
        }

        return symbol.ToLowerInvariant();
    }

    private static Uri AppendSegment(Uri baseUri, string segment)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        // 用 UriBuilder 逐段接,而不是 new Uri(baseUri, "stream")。相對位址的解析會把基底既有的路徑
        // 當成檔名丟掉,指向代理或重播伺服器(例如 wss://proxy/binance)時就會靜默打到 wss://proxy/stream。
        // The segment is appended with UriBuilder rather than resolved as a relative URI, because relative
        // resolution discards the base path: pointing at a proxy such as wss://proxy/binance would quietly dial
        // wss://proxy/stream instead.
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');

        builder.Path = string.Concat(path, "/", segment);

        return builder.Uri;
    }
}
