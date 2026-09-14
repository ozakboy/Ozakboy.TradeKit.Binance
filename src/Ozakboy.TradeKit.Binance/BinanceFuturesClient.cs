using System.Globalization;

using Ozakboy.Http;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 永續合約的用戶端:交易規則、帳戶與持倉查詢,以及下單、撤單、查單與帳戶設定。
/// The Binance USDⓈ-M perpetual futures client: trading rules, account and position queries, and order
/// placement, cancellation, lookup, and account settings.
/// </summary>
/// <remarks>
/// <para>
/// 實作完整的 <see cref="IExchangeClient"/>。交易規則的部分整個委派給
/// <see cref="BinanceExchangeInfoProvider"/>,包含它的環境綁定快取。
/// This implements the whole of <see cref="IExchangeClient"/>. The trading-rule half is delegated wholesale to
/// <see cref="BinanceExchangeInfoProvider"/>, environment-bound cache and all.
/// </para>
/// <para>
/// <b>下單絕不重試。</b> <see cref="PlaceOrderAsync"/> 是這個型別裡唯一標記為非冪等的請求 ——
/// 逾時不代表交易所沒收到,盲目重送開出來的是兩倍的部位。撤單、查單、改槓桿與改保證金模式都是冪等的,
/// 可以安全重試。
/// <b>Orders are never retried.</b> <see cref="PlaceOrderAsync"/> is the only request here marked
/// non-idempotent: a timeout does not mean the exchange missed it, and re-sending blindly opens twice the
/// intended position. Cancellation, lookup, leverage, and margin mode are all idempotent and retry safely.
/// </para>
/// <para>
/// 送單之前一律以 <see cref="SymbolInfo"/> 的交易規則在本地校正價量,數量一律向下對齊;校正後低於最小
/// 下單量或最小名目價值時直接回傳失敗,不送出去換一次拒單。省下的不只是一趟往返,還有一份限流額度。
/// Every submission is normalised locally against the symbol's rules first, with quantities always aligned
/// downwards. A result below the minimum quantity or notional fails here rather than travelling to the exchange
/// to be rejected, which saves a round trip and a unit of rate-limit quota.
/// </para>
/// </remarks>
public sealed class BinanceFuturesClient : IExchangeClient, IDisposable
{
    private const string AccountOperation = "查詢帳戶資訊 / account query";
    private const string PositionOperation = "查詢持倉 / position query";
    private const string PlaceOrderOperation = "送出委託 / place order";
    private const string CancelOrderOperation = "撤銷委託 / cancel order";
    private const string CancelAllOrdersOperation = "撤銷全部掛單 / cancel all orders";
    private const string QueryOrderOperation = "查詢委託 / query order";
    private const string OpenOrdersOperation = "查詢未結委託 / open orders query";
    private const string LeverageOperation = "設定槓桿 / set leverage";
    private const string MarginModeOperation = "設定保證金模式 / set margin mode";
    private const string PlaceConditionalOrderOperation = "送出條件單 / place conditional order";
    private const string CancelConditionalOrderOperation = "撤銷條件單 / cancel conditional order";
    private const string CancelAllConditionalOrdersOperation = "撤銷全部條件單 / cancel all conditional orders";
    private const string QueryConditionalOrderOperation = "查詢條件單 / query conditional order";
    private const string OpenConditionalOrdersOperation = "查詢未結條件單 / open conditional orders query";
    private const string UserTradesOperation = "查詢成交紀錄 / user trades query";

    /// <summary>
    /// 查詢未結條件單時用來限定只看條件單的參數名。
    /// The parameter that narrows an open-conditional-order query to conditional orders alone.
    /// </summary>
    /// <remarks>
    /// Algo Service 底下不只有條件單,還有策略單與網格單。不帶這個參數,查回來的清單會混進本套件
    /// 沒有模型化的類型,而那會讓解析在 <c>orderType</c> 這一關失敗 —— 失敗的原因還會指向一張
    /// 根本不是本套件掛的單。
    /// The Algo Service carries strategy and grid orders as well as conditional ones. Without this parameter
    /// the listing mixes in types this package does not model, and the parse then fails at <c>orderType</c> —
    /// pointing at an order this package never placed.
    /// </remarks>
    private const string AlgoTypeParameterName = "algoType";

    private readonly BinanceApiClient _api;
    private readonly BinanceExchangeInfoProvider _exchangeInfo;
    private readonly bool _ownsExchangeInfo;
    private bool _disposed;

    /// <summary>
    /// 建立用戶端,並自行建立交易規則來源。
    /// Creates the client along with its own trading-rule provider.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="http"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="http"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public BinanceFuturesClient(HttpPipelineClient http, BinanceOptions options, TimeProvider? timeProvider = null)
        : this(http, options, new BinanceExchangeInfoProvider(http, options, timeProvider), ownsExchangeInfo: true, timeProvider)
    {
    }

    /// <summary>
    /// 建立用戶端,共用既有的交易規則來源。
    /// Creates the client sharing an existing trading-rule provider.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="exchangeInfo">交易規則來源。The trading-rule provider.</param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <exception cref="ArgumentNullException">
    /// 任一必要參數為 <see langword="null"/> 時擲出。Thrown when a required argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="exchangeInfo"/> 綁定的環境與 <paramref name="options"/> 不同時擲出。
    /// Thrown when <paramref name="exchangeInfo"/> is bound to a different environment than
    /// <paramref name="options"/>.
    /// </exception>
    /// <remarks>
    /// 共用是為了讓帳戶查詢與交易規則走同一份快取。建構時會比對兩者的環境:交易規則來自 Testnet
    /// 而帳戶查詢打主網的組合,會在這裡就擲出例外,而不是等到某張單因為步進值不符被拒才發現。
    /// Sharing keeps account queries and trading rules on one cache. The constructor compares the two
    /// environments: a provider on Testnet paired with account queries on production throws here rather than
    /// surfacing later as one order rejected for a step size that does not match.
    /// </remarks>
    public BinanceFuturesClient(
        HttpPipelineClient http,
        BinanceOptions options,
        BinanceExchangeInfoProvider exchangeInfo,
        TimeProvider? timeProvider = null)
        : this(http, options, exchangeInfo, ownsExchangeInfo: false, timeProvider)
    {
    }

    private BinanceFuturesClient(
        HttpPipelineClient http,
        BinanceOptions options,
        BinanceExchangeInfoProvider exchangeInfo,
        bool ownsExchangeInfo,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exchangeInfo);

        _api = new BinanceApiClient(http, options, timeProvider);

        if (exchangeInfo.Endpoints != _api.Endpoints)
        {
            throw new ArgumentException(
                $"交易規則來源綁定的是 {exchangeInfo.Endpoints.DisplayName},帳戶查詢設定的是 {_api.Endpoints.DisplayName},兩者必須是同一個環境。The trading-rule provider is bound to {exchangeInfo.Endpoints.DisplayName} while the account settings point at {_api.Endpoints.DisplayName}; both must be the same environment.",
                nameof(exchangeInfo));
        }

        _exchangeInfo = exchangeInfo;
        _ownsExchangeInfo = ownsExchangeInfo;
    }

    /// <summary>
    /// 這個用戶端連的是哪一組端點,也就是哪一個環境。
    /// Which endpoint set — and therefore which environment — this client talks to.
    /// </summary>
    public BinanceEndpoints Endpoints => _api.Endpoints;

    /// <summary>
    /// 交易規則來源。
    /// The trading-rule provider.
    /// </summary>
    public BinanceExchangeInfoProvider ExchangeInfo => _exchangeInfo;

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<SymbolInfo>>> GetSymbolsAsync(CancellationToken cancellationToken = default) =>
        _exchangeInfo.GetSymbolsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Result<SymbolInfo>> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default) =>
        _exchangeInfo.GetSymbolAsync(symbol, cancellationToken);

    /// <inheritdoc />
    public Task<Result<DateTimeOffset>> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
        _exchangeInfo.GetServerTimeAsync(cancellationToken);

    /// <summary>
    /// 取得帳戶快照:資產餘額、目前持倉與帳戶旗標。
    /// Gets an account snapshot: asset balances, current positions, and account flags.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>帳戶快照,或失敗原因。The snapshot, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 會打<b>兩個</b>端點:<c>/fapi/v2/account</c> 取餘額,<c>/fapi/v2/positionRisk</c> 取持倉,合計權重 10。
    /// 多花的 5 點是為了拿到 <c>markPrice</c> 與 <c>liquidationPrice</c> —— 帳戶端點的持倉沒有這兩項,
    /// 少了它們的 <see cref="Position.Notional"/> 會是零,而「名目價值為零」在風控眼中等於「沒有部位風險」。
    /// This calls <b>two</b> endpoints — <c>/fapi/v2/account</c> for balances and
    /// <c>/fapi/v2/positionRisk</c> for positions — for a combined weight of 10. The extra five buys
    /// <c>markPrice</c> and <c>liquidationPrice</c>, which the account endpoint's positions lack; without them
    /// <see cref="Position.Notional"/> comes out zero, and zero notional reads as no position risk at all.
    /// </remarks>
    public async Task<Result<AccountSnapshot>> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var positions = await GetPositionsAsync(cancellationToken).ConfigureAwait(false);

        if (!positions.TryGetValue(out var positionList))
        {
            return positions.ToFailure<AccountSnapshot>();
        }

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.Account,
                null,
                BinanceRequestWeights.Account,
                AccountOperation,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAccountSnapshot(json, positionList, _api.UtcNow)
            : body.ToFailure<AccountSnapshot>();
    }

    /// <summary>
    /// 取得目前實際持有的部位。
    /// Gets the positions currently held.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>持倉清單,或失敗原因。The positions, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 空手的商品會被濾掉。幣安會把帳戶碰過的<b>每一個</b>商品都列出來,不濾的話一個只做三個標的的帳戶
    /// 也可能回上百筆全零的持倉,讓真正有部位的那幾筆淹沒在裡面。要看全部請用
    /// <see cref="GetPositionAsync"/>,它對空手的商品會回傳數量為零的持倉而不是失敗。
    /// Flat symbols are filtered out. Binance lists <b>every</b> symbol the account has touched, so without the
    /// filter an account trading three instruments can still return hundreds of all-zero rows that bury the few
    /// real ones. To ask about a specific flat symbol use <see cref="GetPositionAsync"/>, which returns a
    /// zero-quantity position rather than a failure.
    /// </remarks>
    public async Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.PositionRisk,
                null,
                BinanceRequestWeights.PositionRisk,
                PositionOperation,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadPositions(json, _api.UtcNow)
            : body.ToFailure<IReadOnlyList<Position>>();
    }

    /// <summary>
    /// 取得單一商品的持倉。空手時回傳數量為零的持倉,而不是失敗。
    /// Gets the position on one symbol, returning a flat position rather than a failure when there is none.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>持倉,或失敗原因。The position, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 雙向(避險)模式下同一商品可能同時有多空兩個部位,而中立模型一個 <see cref="Position"/> 表達不了兩個。
    /// 遇到這種情況時回傳失敗並說明原因,而不是挑一邊回傳 —— 挑一邊會讓平倉指令只平掉一半,
    /// 另一半留在市場上,那比查不到部位危險得多。
    /// In hedge mode a symbol can hold a long and a short position at once, which a single
    /// <see cref="Position"/> cannot express. That case returns an explicit failure rather than picking a side:
    /// picking one makes a close instruction flatten half the exposure and leave the rest live, which is far
    /// more dangerous than not finding a position.
    /// </remarks>
    public async Task<Result<Position>> GetPositionAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        var query = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.PositionRisk,
                query,
                BinanceRequestWeights.PositionRisk,
                PositionOperation,
                cancellationToken)
            .ConfigureAwait(false);

        if (!body.TryGetValue(out var json))
        {
            return body.ToFailure<Position>();
        }

        var asOf = _api.UtcNow;
        var parsed = BinanceResponseReader.ReadPositions(json, asOf, includeFlat: true);

        if (!parsed.TryGetValue(out var positions))
        {
            return parsed.ToFailure<Position>();
        }

        // 就算已經帶了 symbol 參數,回來的清單仍要自己再篩一次。交易所回多回少不在這一側控制,
        // 而下面「有幾個未平部位」的判斷一旦把別的商品算進來,就會把單向模式誤判成雙向並整筆拒絕。
        // The list is filtered again even though the request carried a symbol. What the exchange returns is
        // not this side's decision, and letting another symbol into the open-position count below would read
        // one-way mode as hedge mode and refuse the whole query.
        var open = positions
            .Where(position => !position.IsFlat
                && string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return open.Count switch
        {
            0 => Result.Success(Position.Flat(symbol, asOf)),
            1 => Result.Success(open[0]),
            _ => TradeErrors.NotSupported(
                    $"{symbol} 在雙向模式下同時持有多空兩個部位,單一 Position 無法表示,請改用 GetPositionsAsync。{symbol} holds both a long and a short position in hedge mode, which a single Position cannot represent; use GetPositionsAsync instead.")
                .WithData(BinanceErrorDataKeys.Symbol, symbol),
        };
    }

    /// <summary>
    /// 送出一張委託。<b>失敗時絕不可以重送</b>,請用 <see cref="Order.ClientOrderId"/> 查單確認。
    /// Places one order. <b>Never re-send on failure</b>; confirm with <see cref="Order.ClientOrderId"/> instead.
    /// </summary>
    /// <param name="request">下單請求。The order request.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>交易所接受後的委託,或失敗原因。The order as accepted, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>逾時之後的正確處置是查單,不是重送。</b> HTTP 逾時只代表「沒收到回應」,不代表交易所沒收到請求 ——
    /// 那張單可能已經在簿上,甚至已經成交。這時重送會得到兩張單、兩倍的部位,而那是真金白銀的損失。
    /// 正確做法是拿這次使用的 <c>clientOrderId</c> 呼叫
    /// <see cref="GetOrderAsync(string, OrderIdentifier, CancellationToken)"/>:查得到就是進去了,
    /// 查到 <see cref="TradeErrorCodes.OrderNotFound"/> 才代表沒進去,那時才可以重下。
    /// <b>After a timeout, look the order up; do not send it again.</b> An HTTP timeout says only that no reply
    /// arrived, not that the request never landed: the order may already be resting, or filled. Re-sending then
    /// yields two orders and twice the position, in real money. The correct move is to call
    /// <see cref="GetOrderAsync(string, OrderIdentifier, CancellationToken)"/> with the same
    /// <c>clientOrderId</c>: finding it means it went through, and only a
    /// <see cref="TradeErrorCodes.OrderNotFound"/> licenses a fresh submission.
    /// </para>
    /// <para>
    /// 這個方法送出的請求以 <c>AsNonIdempotent()</c> 標記,並額外釘上
    /// <see cref="RetryPolicy.NoRetry"/>,因此 HTTP 管線不會替它重試。上層也不可以自行重試。
    /// The request is marked with <c>AsNonIdempotent()</c> and additionally pinned to
    /// <see cref="RetryPolicy.NoRetry"/>, so the HTTP pipeline will not retry it. Neither may callers.
    /// </para>
    /// <para>
    /// <see cref="OrderRequest.ClientOrderId"/> 留白時由本方法產生一個,並且無論成敗都會帶回來:
    /// 成功時在 <see cref="Order.ClientOrderId"/>,失敗時在
    /// <see cref="Error.Data"/> 的 <see cref="BinanceErrorDataKeys.ClientOrderId"/> 鍵。
    /// 若希望在請求送出<b>之前</b>就把編號寫進自己的委託紀錄,請自行呼叫
    /// <see cref="BinanceClientOrderId.Generate()"/> 並填進請求。
    /// A blank <see cref="OrderRequest.ClientOrderId"/> is generated here and comes back either way: in
    /// <see cref="Order.ClientOrderId"/> on success, and under the
    /// <see cref="BinanceErrorDataKeys.ClientOrderId"/> key of <see cref="Error.Data"/> on failure. To hold the
    /// id <b>before</b> the request leaves, generate it yourself with
    /// <see cref="BinanceClientOrderId.Generate()"/> and set it on the request.
    /// </para>
    /// <para>
    /// 送出之前會先取得該商品的交易規則並校正價量(數量向下對齊、價格對齊跳動點)。
    /// 校正後低於最小下單量或最小名目價值時直接失敗,請求不會送出。
    /// The symbol's trading rules are fetched and applied first: quantities align downwards, prices align to the
    /// tick. A result below the minimum quantity or notional fails without the request leaving.
    /// </para>
    /// </remarks>
    public async Task<Result<Order>> PlaceOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        // 先做本地驗證再查交易規則。順序有差:欄位組合不合法的請求不該為了取得規則而多打一次網路。
        // Validation comes before the rule lookup: a request with an invalid field combination should not cost
        // a network call just to fetch rules it will never use.
        var validation = request.Validate();

        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var symbolResult = await _exchangeInfo
            .GetSymbolAsync(request.Symbol, cancellationToken)
            .ConfigureAwait(false);

        if (!symbolResult.TryGetValue(out var symbolInfo))
        {
            return symbolResult.ToFailure<Order>();
        }

        var normalization = request.NormalizeFor(symbolInfo);

        if (!normalization.TryGetValue(out var normalized))
        {
            return normalization.ToFailure<Order>();
        }

        var clientOrderId = string.IsNullOrWhiteSpace(request.ClientOrderId)
            ? BinanceClientOrderId.Generate(_api.UtcNow)
            : request.ClientOrderId;

        var query = BinanceOrderMapper.BuildPlaceOrder(normalized, clientOrderId);

        if (!query.TryGetValue(out var builder))
        {
            // 這一條路徑上請求還沒送出,所以不附「請查單」的提示 —— 那個提示只在單可能已經在簿上時才成立。
            // Nothing has been sent on this path, so no "go look it up" hint is attached: that advice only
            // holds once the order might be resting.
            return query.ToFailure<Order>();
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Post,
                BinanceApiPaths.Order,
                builder,
                BinanceRequestWeights.PlaceOrder,
                PlaceOrderOperation,
                RequestIdempotency.NonIdempotent,
                cancellationToken)
            .ConfigureAwait(false);

        if (!body.TryGetValue(out var json))
        {
            return WithClientOrderId(body.Error!, clientOrderId);
        }

        var order = BinanceResponseReader.ReadOrder(json, _api.UtcNow);

        // 解析失敗代表「交易所收下了,但這一側讀不懂回應」——那張單確實存在,編號更不能弄丟。
        // A parse failure means the exchange accepted it and this side could not read the reply: the order is
        // real, which makes losing the id worse rather than better.
        return order.IsFailure ? WithClientOrderId(order.Error!, clientOrderId) : order;
    }

    /// <summary>
    /// 撤銷一張委託。
    /// Cancels one order.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="identifier">訂單識別碼。The order identifier.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>撤銷後的委託,或失敗原因。The order after cancellation, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 撤單是冪等的,因此標記為可重試。重複撤同一張單的結果是 <c>-2011</c>,對映成
    /// <see cref="TradeErrorCodes.OrderNotCancelable"/> —— 那句話的意思是「它已經不在簿上了」,
    /// 不是「撤單失敗,還掛著」。相較之下不重試的代價是留下一張以為已撤、實際還活著的單,那危險得多。
    /// Cancellation is idempotent and therefore marked retryable. Cancelling the same order twice earns a
    /// <c>-2011</c>, mapped to <see cref="TradeErrorCodes.OrderNotCancelable"/>, which means "it is no longer on
    /// the book" rather than "the cancellation failed and it is still live". Not retrying risks leaving an order
    /// believed cancelled but still working, which is far worse.
    /// </remarks>
    public async Task<Result<Order>> CancelOrderAsync(
        string symbol,
        OrderIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var query = BinanceOrderMapper.BuildOrderLookup(symbol, identifier);

        if (!query.TryGetValue(out var builder))
        {
            return query.ToFailure<Order>();
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Delete,
                BinanceApiPaths.Order,
                builder,
                BinanceRequestWeights.CancelOrder,
                CancelOrderOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadOrder(json, _api.UtcNow)
            : body.ToFailure<Order>();
    }

    /// <summary>
    /// 撤銷某商品的全部掛單。沒有掛單時視為成功。
    /// Cancels every open order on one symbol, treating "there were none" as success.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>全部撤銷成功時為成功,否則為失敗原因。Success when all were cancelled, otherwise the failure.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 緊急出場的流程會無條件先撤單再平倉,因此「本來就沒單」必須是成功而不是失敗 ——
    /// 在那條路徑上回報失敗會讓出場流程停在第一步,而部位還開著。
    /// An emergency exit cancels before closing unconditionally, so "there was nothing to cancel" has to be a
    /// success: a failure there stops the exit at its first step with the position still open.
    /// </remarks>
    public async Task<Result> CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        var query = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Delete,
                BinanceApiPaths.AllOpenOrders,
                query,
                BinanceRequestWeights.CancelAllOpenOrders,
                CancelAllOrdersOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAcknowledgement(json, BinanceApiPaths.AllOpenOrders, _api.Endpoints)
            : body.ToResult();
    }

    /// <summary>
    /// 查詢單一委託。
    /// Looks up one order.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="identifier">訂單識別碼。The order identifier.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>委託,或失敗原因。The order, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 這是 <see cref="PlaceOrderAsync"/> 逾時之後唯一正確的下一步:用同一個
    /// <see cref="OrderIdentifier.ClientOrderId"/> 查回來,確認那張單到底進去了沒有。
    /// This is the only correct next step after <see cref="PlaceOrderAsync"/> times out: look the order up by
    /// the same <see cref="OrderIdentifier.ClientOrderId"/> and find out whether it reached the exchange.
    /// </remarks>
    public async Task<Result<Order>> GetOrderAsync(
        string symbol,
        OrderIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var query = BinanceOrderMapper.BuildOrderLookup(symbol, identifier);

        if (!query.TryGetValue(out var builder))
        {
            return query.ToFailure<Order>();
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Get,
                BinanceApiPaths.Order,
                builder,
                BinanceRequestWeights.QueryOrder,
                QueryOrderOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadOrder(json, _api.UtcNow)
            : body.ToFailure<Order>();
    }

    /// <summary>
    /// 查詢尚未結束的委託。
    /// Lists the orders that are still live.
    /// </summary>
    /// <param name="symbol">
    /// 要查詢的交易對;<see langword="null"/> 代表全部商品。
    /// The symbol to query, or <see langword="null"/> for every symbol.
    /// </param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>掛單清單,或失敗原因。The open orders, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 不指定商品的權重是 <b>40</b>,指定時是 1 —— 四十倍。輪詢掛單時請務必帶上商品代碼,
    /// 不帶的版本只適合偶爾做一次全帳戶盤點。
    /// Omitting the symbol costs a weight of <b>40</b> against 1 with it, forty times as much. Always pass the
    /// symbol when polling; the symbol-less form suits an occasional whole-account sweep and nothing else.
    /// </para>
    /// <para>
    /// 空字串與空白字串<b>不</b>等同於 <see langword="null"/>,而是回報查詢條件不合法。
    /// 那幾乎一定是呼叫端的變數沒填到,靜默改打全商品會讓一個 bug 變成四十倍的權重支出。
    /// An empty or blank string is <b>not</b> treated as <see langword="null"/> but reported as an invalid
    /// query. It almost always means an unfilled variable, and silently widening it to every symbol turns one
    /// bug into forty times the weight.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<Order>>> GetOpenOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (symbol is not null && string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery(
                "交易對代碼是空白字串。要查詢全部商品請傳 null,空白幾乎都是變數沒填到。The symbol is a blank string; pass null to query every symbol, since a blank almost always means an unfilled variable.");
        }

        var query = symbol is null
            ? null
            : QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Get,
                BinanceApiPaths.OpenOrders,
                query,
                BinanceRequestWeights.OpenOrders(symbol is not null),
                OpenOrdersOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadOrders(json, _api.UtcNow)
            : body.ToFailure<IReadOnlyList<Order>>();
    }

    /// <summary>
    /// 送出一張條件單(停損、停利或移動停損)。<b>失敗時絕不可以重送</b>,請用
    /// <see cref="ConditionalOrder.ClientConditionalOrderId"/> 查單確認。
    /// Places one conditional order. <b>Never re-send on failure</b>; confirm with
    /// <see cref="ConditionalOrder.ClientConditionalOrderId"/> instead.
    /// </summary>
    /// <param name="request">條件單請求。The conditional order request.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>交易所接受後的條件單,或失敗原因。The conditional order as accepted, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 幣安自 2025-12-09 起把條件單移到 Algo Service,這個方法打的是
    /// <c>POST /fapi/v1/algoOrder</c>。同樣的委託類型送到 <see cref="PlaceOrderAsync"/> 會得到
    /// <c>-4120</c>。
    /// Binance moved conditional orders to the Algo Service on 2025-12-09 and this method calls
    /// <c>POST /fapi/v1/algoOrder</c>. The same order types sent to <see cref="PlaceOrderAsync"/> earn a
    /// <c>-4120</c>.
    /// </para>
    /// <para>
    /// <b>與下單一樣絕不重試</b>:請求以 <c>AsNonIdempotent()</c> 標記並釘上
    /// <see cref="RetryPolicy.NoRetry"/>。逾時之後的正確處置是用同一個 <c>clientAlgoId</c> 呼叫
    /// <see cref="GetConditionalOrderAsync"/>,查得到就是進去了。重送的後果比重複下單更糟:
    /// 兩張停損,其中一張在部位被另一張平掉之後會反手開出一個反向部位。
    /// <b>Never retried, exactly as with an ordinary order</b>: the request is marked
    /// <c>AsNonIdempotent()</c> and pinned to <see cref="RetryPolicy.NoRetry"/>. After a timeout the correct
    /// move is <see cref="GetConditionalOrderAsync"/> with the same <c>clientAlgoId</c>. Re-sending is worse
    /// here than for a plain order: two stops, and once one of them closes the position the other opens an
    /// inverted one.
    /// </para>
    /// <para>
    /// <b>掛上去不等於會成交。</b>幣安在條件單觸發<b>之前</b>不做保證金檢查,檢查發生在觸發當下,
    /// 所以一張成功掛上的停損仍可能在觸發時以
    /// <see cref="ConditionalOrderStatus.Rejected"/> 收場。拒絕原因只出現在串流事件裡,
    /// 見 <see cref="BinanceUserDataFeed.SubscribeConditionalOrderUpdatesAsync"/>。
    /// <b>Resting is not the same as filling.</b> Binance performs no margin check <b>before</b> a conditional
    /// order triggers; the check happens at the trigger, so a stop that was accepted can still end as
    /// <see cref="ConditionalOrderStatus.Rejected"/>. The reason appears only on the stream — see
    /// <see cref="BinanceUserDataFeed.SubscribeConditionalOrderUpdatesAsync"/>.
    /// </para>
    /// <para>
    /// <b>未觸發的條件單不支援改單。</b>要調整觸發價只能撤掉再掛一張,而那中間有一段沒有保護的空窗 ——
    /// 移動停損的邏輯要按這個前提設計,不要指望一個原子的「改價」。
    /// <b>An untriggered conditional order cannot be modified.</b> Adjusting a trigger price means cancelling
    /// and placing a new one, with an unprotected gap in between. Trailing logic has to be built on that
    /// premise rather than on an atomic price change.
    /// </para>
    /// <para>
    /// 條件單有<b>全帳戶合計 200 張</b>的上限(不是每個商品各 200 張)。多商品同時掛停損停利時,
    /// 這個數字比想像中容易碰到。
    /// There is a limit of <b>200 conditional orders across the whole account</b>, not 200 per symbol. With
    /// stops and take-profits on several symbols at once it is easier to reach than it sounds.
    /// </para>
    /// </remarks>
    public async Task<Result<ConditionalOrder>> PlaceConditionalOrderAsync(
        ConditionalOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        // 先做本地驗證再查交易規則,理由與一般下單相同:欄位組合不合法的請求不該為了取得規則多打一次網路。
        // Validation before the rule lookup, as on an ordinary order: an invalid field combination should not
        // cost a network call for rules it will never use.
        var validation = request.Validate();

        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var symbolResult = await _exchangeInfo
            .GetSymbolAsync(request.Symbol, cancellationToken)
            .ConfigureAwait(false);

        if (!symbolResult.TryGetValue(out var symbolInfo))
        {
            return symbolResult.ToFailure<ConditionalOrder>();
        }

        var normalization = request.NormalizeFor(symbolInfo);

        if (!normalization.TryGetValue(out var normalized))
        {
            return normalization.ToFailure<ConditionalOrder>();
        }

        // clientAlgoId 與 newClientOrderId 的規則是同一條(^[\.A-Z\:/a-z0-9_-]{1,36}$),
        // 所以產生器共用同一支,不另寫一份會漂移的複製品。
        // clientAlgoId and newClientOrderId share one rule (^[\.A-Z\:/a-z0-9_-]{1,36}$), so they share one
        // generator rather than keeping a second copy that can drift.
        var clientAlgoId = string.IsNullOrWhiteSpace(request.ClientConditionalOrderId)
            ? BinanceClientOrderId.Generate(_api.UtcNow)
            : request.ClientConditionalOrderId;

        var query = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(normalized, clientAlgoId);

        if (!query.TryGetValue(out var builder))
        {
            // 請求還沒送出,所以不附「請查單」的提示 —— 那個提示只在單可能已經掛上去時才成立。
            // Nothing has been sent, so no "go look it up" hint is attached: that advice only holds once the
            // order might be resting.
            return query.ToFailure<ConditionalOrder>();
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Post,
                BinanceApiPaths.AlgoOrder,
                builder,
                BinanceRequestWeights.PlaceAlgoOrder,
                PlaceConditionalOrderOperation,
                RequestIdempotency.NonIdempotent,
                cancellationToken)
            .ConfigureAwait(false);

        if (!body.TryGetValue(out var json))
        {
            return WithClientAlgoId(MapConditionalError(body.Error!), clientAlgoId);
        }

        var order = BinanceResponseReader.ReadConditionalOrder(json, _api.UtcNow);

        // 解析失敗代表「交易所收下了,但這一側讀不懂回應」—— 那張停損確實掛著,編號更不能弄丟。
        // A parse failure means the exchange accepted it and this side could not read the reply: the stop is
        // real, which makes losing the id worse rather than better.
        return order.IsFailure ? WithClientAlgoId(order.Error!, clientAlgoId) : order;
    }

    /// <summary>
    /// 撤銷一張條件單。
    /// Cancels one conditional order.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="identifier">條件單識別碼。The conditional order identifier.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>撤銷後的條件單,或失敗原因。The conditional order after cancellation, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>這個方法會打兩次網路</b>:先 <c>DELETE</c>,再 <c>GET</c> 查回那張撤掉的條件單。
    /// 原因是 <c>DELETE /fapi/v1/algoOrder</c> 的回應只有
    /// <c>{"algoId":…,"clientAlgoId":…,"code":"200","msg":"success"}</c> —— 沒有方向、沒有類型、
    /// 沒有觸發價,湊不出一個誠實的 <see cref="ConditionalOrder"/>。用預設值補齊會讓
    /// <see cref="ConditionalOrderType.Unspecified"/> 這種「無效值」流進上層,而這個抽象層把那個值
    /// 定義成「對映漏掉了」;多一次權重 1 的查詢,換的是不必在回傳值裡說謊。
    /// <b>This makes two calls</b>: a <c>DELETE</c>, then a <c>GET</c> to read the cancelled order back. The
    /// <c>DELETE</c> response carries only
    /// <c>{"algoId":…,"clientAlgoId":…,"code":"200","msg":"success"}</c> — no side, no type, no trigger price —
    /// which is not enough for an honest <see cref="ConditionalOrder"/>. Filling the gaps with defaults would
    /// push <see cref="ConditionalOrderType.Unspecified"/> into callers, a value this abstraction defines as
    /// "the mapping missed something". One extra request of weight 1 buys not lying in the return value.
    /// </para>
    /// <para>
    /// 撤單本身是冪等的,因此標記為可重試。若 <c>DELETE</c> 成功而後面那次 <c>GET</c> 失敗,
    /// 回傳的是失敗,但訊息會明講「撤單已經成功」—— 呼叫端據此重試是安全的(再撤一次不會有副作用),
    /// 而反過來把它當成「撤單失敗、停損還掛著」也是安全的方向。
    /// The cancellation itself is idempotent and marked retryable. When the <c>DELETE</c> succeeds and the
    /// following <c>GET</c> does not, the result is a failure whose message says plainly that the cancellation
    /// went through: retrying on it is safe, since cancelling twice has no side effect, and reading it as "the
    /// stop may still be resting" errs in the safe direction too.
    /// </para>
    /// </remarks>
    public async Task<Result<ConditionalOrder>> CancelConditionalOrderAsync(
        string symbol,
        ConditionalOrderIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var query = BinanceAlgoOrderMapper.BuildAlgoOrderLookup(symbol, identifier);

        if (!query.TryGetValue(out var builder))
        {
            return query.ToFailure<ConditionalOrder>();
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Delete,
                BinanceApiPaths.AlgoOrder,
                builder,
                BinanceRequestWeights.CancelAlgoOrder,
                CancelConditionalOrderOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        if (!body.TryGetValue(out var json))
        {
            return MapConditionalError(body.Error!);
        }

        var cancelled = BinanceResponseReader.ReadConditionalOrderCancellation(json);

        if (!cancelled.TryGetValue(out var cancelledIdentifier))
        {
            return cancelled.ToFailure<ConditionalOrder>();
        }

        var reread = await GetConditionalOrderAsync(symbol, cancelledIdentifier, cancellationToken)
            .ConfigureAwait(false);

        return reread.IsSuccess
            ? reread
            : new Error(
                reread.Error!.Code,
                $"{reread.Error.Message}(撤單本身已經成功,失敗的是撤完之後的回查;{cancelledIdentifier} 這張條件單已經不在了。The cancellation itself succeeded and it is the follow-up lookup that failed; conditional order {cancelledIdentifier} is gone.)",
                reread.Error.Category)
            {
                Exception = reread.Error.Exception,
                Data = reread.Error.Data,
            };
    }

    /// <summary>
    /// 查詢單一條件單。
    /// Looks up one conditional order.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="identifier">條件單識別碼。The conditional order identifier.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>條件單,或失敗原因。The conditional order, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 這是 <see cref="PlaceConditionalOrderAsync"/> 逾時之後唯一正確的下一步:用同一個
    /// <see cref="ConditionalOrderIdentifier.ClientConditionalOrderId"/> 查回來,確認那張停損到底掛上去
    /// 沒有。
    /// This is the only correct next step after <see cref="PlaceConditionalOrderAsync"/> times out: look it up
    /// by the same <see cref="ConditionalOrderIdentifier.ClientConditionalOrderId"/> and find out whether the
    /// stop reached the exchange.
    /// </para>
    /// <para>
    /// 幣安只保留有限期間的條件單紀錄:已撤銷或已失效且沒有任何成交的,建立超過 <b>3 天</b>就查不到;
    /// 任何條件單超過 <b>90 天</b>也查不到。查不到回的是
    /// <see cref="TradeErrorCodes.ConditionalOrderNotFound"/>,那代表「查不到」,不代表「沒掛過」。
    /// Binance keeps conditional orders for a limited window: one that was cancelled or expired without any
    /// fill disappears <b>3 days</b> after creation, and anything at all disappears after <b>90 days</b>. A
    /// miss answers <see cref="TradeErrorCodes.ConditionalOrderNotFound"/>, which means "not found" rather
    /// than "never existed".
    /// </para>
    /// </remarks>
    public async Task<Result<ConditionalOrder>> GetConditionalOrderAsync(
        string symbol,
        ConditionalOrderIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var query = BinanceAlgoOrderMapper.BuildAlgoOrderLookup(symbol, identifier);

        if (!query.TryGetValue(out var builder))
        {
            return query.ToFailure<ConditionalOrder>();
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Get,
                BinanceApiPaths.AlgoOrder,
                builder,
                BinanceRequestWeights.QueryAlgoOrder,
                QueryConditionalOrderOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadConditionalOrder(json, _api.UtcNow)
            : MapConditionalError(body.Error!);
    }

    /// <summary>
    /// 查詢尚未結束的條件單。
    /// Lists the conditional orders that are still live.
    /// </summary>
    /// <param name="symbol">
    /// 要查詢的交易對;<see langword="null"/> 代表全部商品。
    /// The symbol to query, or <see langword="null"/> for every symbol.
    /// </param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>條件單清單,或失敗原因。The open conditional orders, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><see cref="GetOpenOrdersAsync"/> 看不到這些單。</b>對帳如果只查那一邊,會得到「沒有任何掛單」
    /// 的結論,而停損其實好端端地掛在這一條路徑上 —— 或者根本不在,兩者長得一模一樣。
    /// <b><see cref="GetOpenOrdersAsync"/> does not see these.</b> Reconciling with that alone concludes "no
    /// resting orders" while the stops sit safely on this path — or are genuinely missing, which looks
    /// identical.
    /// </para>
    /// <para>
    /// 不指定商品的權重是 <b>40</b>,指定時是 1 —— 四十倍,與一般掛單查詢同樣的懸崖。
    /// 每個部位都要確認「停損還在不在」是高頻動作,請務必帶上商品代碼。
    /// Omitting the symbol costs a weight of <b>40</b> against 1, the same cliff as the ordinary open-orders
    /// query. Confirming that every position still has its stop is a frequent operation; always pass the
    /// symbol.
    /// </para>
    /// <para>
    /// 空字串與空白字串<b>不</b>等同於 <see langword="null"/>,而是回報查詢條件不合法,理由同
    /// <see cref="GetOpenOrdersAsync"/>。
    /// An empty or blank string is <b>not</b> treated as <see langword="null"/> but reported as an invalid
    /// query, for the reasons given on <see cref="GetOpenOrdersAsync"/>.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<ConditionalOrder>>> GetOpenConditionalOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (symbol is not null && string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery(
                "交易對代碼是空白字串。要查詢全部商品請傳 null,空白幾乎都是變數沒填到。The symbol is a blank string; pass null to query every symbol, since a blank almost always means an unfilled variable.");
        }

        var query = QueryParameters.CreateBuilder()
            .Add(AlgoTypeParameterName, BinanceAlgoOrderMapper.ConditionalAlgoType);

        if (symbol is not null)
        {
            query.Add(BinanceConstants.SymbolParameterName, symbol);
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Get,
                BinanceApiPaths.OpenAlgoOrders,
                query,
                BinanceRequestWeights.OpenAlgoOrders(symbol is not null),
                OpenConditionalOrdersOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadConditionalOrders(json, _api.UtcNow)
            : body.ToFailure<IReadOnlyList<ConditionalOrder>>();
    }

    /// <summary>
    /// 撤銷某商品的全部條件單。沒有條件單時視為成功。
    /// Cancels every open conditional order on one symbol, treating "there were none" as success.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>全部撤銷成功時為成功,否則為失敗原因。Success when all were cancelled, otherwise the failure.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <b>這個方法與 <see cref="CancelAllOrdersAsync"/> 打的是兩個不同的端點,互不涵蓋。</b>
    /// 緊急出場要兩個都呼叫:只撤一般委託會留下停損單,而部位平掉之後那張停損就成了反向開倉的引信。
    /// <b>This and <see cref="CancelAllOrdersAsync"/> hit two different endpoints and neither covers the
    /// other.</b> An emergency exit calls both: cancelling only the ordinary orders leaves the stops behind,
    /// and after the position closes such a stop becomes the fuse for an inverted one.
    /// </remarks>
    public async Task<Result> CancelAllConditionalOrdersAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        var query = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Delete,
                BinanceApiPaths.AllOpenAlgoOrders,
                query,
                BinanceRequestWeights.CancelAllOpenAlgoOrders,
                CancelAllConditionalOrdersOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAcknowledgement(json, BinanceApiPaths.AllOpenAlgoOrders, _api.Endpoints)
            : body.ToResult();
    }

    /// <summary>
    /// 設定某商品的槓桿倍數。
    /// Sets the leverage on one symbol.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="leverage">槓桿倍數,必須至少為 1。The leverage; at least 1.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>設定成功時為成功,否則為失敗原因。Success when applied, otherwise the failure.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 冪等:把槓桿設成它已經是的值不會出錯,交易所照樣回成功。因此這個請求可以重試。
    /// Idempotent: setting the leverage to what it already is is not an error and still answers success, so the
    /// request may be retried.
    /// </remarks>
    public async Task<Result> SetLeverageAsync(
        string symbol,
        int leverage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        if (leverage < 1)
        {
            return new Error(
                TradeErrorCodes.LeverageNotAllowed,
                $"槓桿倍數必須至少為 1,收到 {leverage}。The leverage must be at least 1 but was {leverage}.",
                ErrorCategory.Validation)
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Post,
                BinanceApiPaths.Leverage,
                BinanceOrderMapper.BuildLeverage(symbol, leverage),
                BinanceRequestWeights.AccountSetting,
                LeverageOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAcknowledgement(json, BinanceApiPaths.Leverage, _api.Endpoints)
            : body.ToResult();
    }

    /// <summary>
    /// 設定某商品的保證金模式。模式本來就是目標值時視為成功。
    /// Sets the margin mode on one symbol, treating "already in that mode" as success.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="marginMode">保證金模式。The margin mode.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>設定成功時為成功,否則為失敗原因。Success when applied, otherwise the failure.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 幣安對「模式沒有變」回的是錯誤 <c>-4046</c>,而不是成功。這裡把它吃掉並回報成功:
    /// 啟動流程通常會無條件把每個商品設成全倉,若不吃掉,每次啟動都會冒出一串假的錯誤告警,
    /// 而真正的失敗就淹沒在裡面。
    /// Binance answers "no change needed" with the error <c>-4046</c> rather than with success. That case is
    /// swallowed here: a start-up routine typically forces every symbol to cross margin unconditionally, and
    /// without swallowing it every start raises a row of false alerts that bury the real failures.
    /// </para>
    /// <para>
    /// 也因為這個吃掉,重試是安全的:重送只會再換一個 <c>-4046</c>,而它已經被當成成功。
    /// That swallowing is also what makes retrying safe: a re-send earns another <c>-4046</c>, which already
    /// counts as success.
    /// </para>
    /// </remarks>
    public async Task<Result> SetMarginModeAsync(
        string symbol,
        MarginMode marginMode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        if (!Enum.IsDefined(marginMode))
        {
            return TradeErrors.InvalidQuery(
                $"未定義的保證金模式:{(int)marginMode}。Undefined margin mode: {(int)marginMode}.");
        }

        var body = await _api
            .SendSignedAsync(
                HttpMethod.Post,
                BinanceApiPaths.MarginType,
                BinanceOrderMapper.BuildMarginType(symbol, marginMode),
                BinanceRequestWeights.AccountSetting,
                MarginModeOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        if (body.TryGetValue(out var json))
        {
            return BinanceResponseReader.ReadAcknowledgement(json, BinanceApiPaths.MarginType, _api.Endpoints);
        }

        return IsNoChangeNeeded(body.Error!)
            ? Result.Success()
            : body.ToResult();
    }

    /// <summary>
    /// 查詢帳戶在某個商品上的成交紀錄(<c>GET /fapi/v1/userTrades</c>,權重 5)。
    /// Lists the account's own fills on one symbol (<c>GET /fapi/v1/userTrades</c>, weight 5).
    /// </summary>
    /// <param name="symbol">交易對代碼,必填。The symbol; required.</param>
    /// <param name="since">
    /// 起點時刻,對映到 <c>startTime</c>;與 <paramref name="fromId"/> 擇一。
    /// The starting instant, sent as <c>startTime</c>; mutually exclusive with <paramref name="fromId"/>.
    /// </param>
    /// <param name="fromId">
    /// 起點成交編號,對映到 <c>fromId</c>;與 <paramref name="since"/> 擇一。
    /// The starting trade id, sent as <c>fromId</c>; mutually exclusive with <paramref name="since"/>.
    /// </param>
    /// <param name="limit">
    /// 單次筆數上限,最大 1000;<see langword="null"/> 交由幣安取它自己的預設值 500。
    /// The per-response cap, at most 1000; <see langword="null"/> leaves Binance's own default of 500 in place.
    /// </param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>成交清單,或失敗原因。The fills, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 幣安<b>不接受</b> <c>fromId</c> 與 <c>startTime</c> 同時出現,所以兩個都給會在這裡就被擋下來,
    /// 而不是送出去換一個看不出原因的參數錯誤。兩個都不給時幣安只回最近 7 天,而且單次查詢的時間跨度
    /// 也以 7 天為限 —— 中斷超過一週的缺口必須自己分段補,不能指望一個 <c>since</c> 就撈得回來。
    /// Binance <b>does not accept</b> <c>fromId</c> together with <c>startTime</c>, so supplying both is
    /// refused here instead of travelling out to come back as an opaque parameter error. Supplying neither
    /// returns the last seven days only, and one query may not span more than seven days either: a gap wider
    /// than a week has to be swept in segments rather than in one call.
    /// </para>
    /// <para>
    /// 補查回來的清單<b>一定會與串流已收到的重疊</b>,因為 <paramref name="since"/> 取的是本地最後一筆成交
    /// 的時間本身而不是它之後一瞬間。呼叫端必須以 <see cref="Trade.TradeId"/> 去重。
    /// The returned list <b>always overlaps</b> with what the stream already delivered, because
    /// <paramref name="since"/> is the local last fill's own timestamp rather than an instant after it. Callers
    /// de-duplicate on <see cref="Trade.TradeId"/>.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<Trade>>> GetUserTradesAsync(
        string symbol,
        DateTimeOffset? since = null,
        long? fromId = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        if (since is not null && fromId is not null)
        {
            return TradeErrors.InvalidQuery(
                    "since 與 fromId 只能擇一:幣安不接受 startTime 與 fromId 同時出現,靜默丟掉其中一個會讓補查的起點不是呼叫端以為的那一個。Supply either since or fromId: Binance does not accept startTime together with fromId, and quietly dropping one would start the sweep somewhere the caller did not choose.")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (limit is { } requested && (requested < 1 || requested > BinanceApiPaths.MaxUserTradesLimit))
        {
            // 不悄悄夾到上限。要五千筆卻只拿到一千筆,補查會以為缺口補完了,而剩下的成交從此沒有人再查 ——
            // 部位與已實現損益就停在一個錯的數字上,而且看起來很正常。
            // The limit is not quietly clamped: asking for five thousand and receiving one thousand lets the
            // sweep conclude the gap is closed, after which nothing ever looks for the fills left behind, and
            // the position and realised P&L settle on a wrong number that looks perfectly ordinary.
            return TradeErrors.InvalidQuery(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"單次最多只能查 {BinanceApiPaths.MaxUserTradesLimit} 筆成交,收到 {requested};請自行分頁,以這一頁最後一筆的成交編號當下一頁的 fromId。At most {BinanceApiPaths.MaxUserTradesLimit} fills may be fetched in one request but {requested} were asked for; page the query instead, using the last fill's id as the next fromId."))
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var query = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        query.AddIfNotNull(BinanceConstants.StartTimeParameterName, since?.ToUnixTimeMilliseconds());
        query.AddIfNotNull(BinanceConstants.FromIdParameterName, fromId);
        query.AddIfNotNull(BinanceConstants.LimitParameterName, (long?)limit);

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.UserTrades,
                query,
                BinanceRequestWeights.UserTrades,
                UserTradesOperation,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadUserTrades(json)
            : body.ToFailure<IReadOnlyList<Trade>>();
    }

    /// <summary>
    /// 判斷這個錯誤是不是幣安的「保證金模式不需要變更」(<c>-4046</c>)。
    /// Determines whether an error is Binance's "no need to change margin type" (<c>-4046</c>).
    /// </summary>
    private static bool IsNoChangeNeeded(Error error) =>
        error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var apiCode)
        && apiCode == BinanceApiErrorCodes.NoNeedToChangeMarginType;

    /// <summary>
    /// 把用戶端訂單編號接到錯誤上,讓呼叫端在下單失敗之後仍然查得回那張單。
    /// Attaches the client order id to an error so the caller can still find the order after a failed
    /// submission.
    /// </summary>
    private static Error WithClientOrderId(Error error, string clientOrderId) =>
        new Error(
            error.Code,
            $"{error.Message}(這張單的 clientOrderId 是 {clientOrderId};若無法確定它是否已經送達交易所,請用這個編號查單,不要重送。The clientOrderId of this order is {clientOrderId}; if it is unclear whether the exchange received it, look it up by that id rather than re-sending.)",
            error.Category)
        {
            Exception = error.Exception,
            Data = error.Data,
        }.WithData(BinanceErrorDataKeys.ClientOrderId, clientOrderId);

    private static Error WithClientAlgoId(Error error, string clientAlgoId) =>
        new Error(
            error.Code,
            $"{error.Message}(這張條件單的 clientAlgoId 是 {clientAlgoId};若無法確定它是否已經送達交易所,請用這個編號查單,不要重送。The clientAlgoId of this conditional order is {clientAlgoId}; if it is unclear whether the exchange received it, look it up by that id rather than re-sending.)",
            error.Category)
        {
            Exception = error.Exception,
            Data = error.Data,
        }.WithData(BinanceErrorDataKeys.ClientAlgoId, clientAlgoId);

    /// <summary>
    /// 把條件單路徑上的錯誤改標成條件單專屬的中立代碼。
    /// Re-labels an error raised on the conditional order path with the conditional-specific neutral code.
    /// </summary>
    /// <param name="error">錯誤對映器產出的錯誤。The error produced by the error mapper.</param>
    /// <returns>代碼換過的錯誤。The error carrying the conditional code.</returns>
    /// <remarks>
    /// <para>
    /// 幣安的 Algo 端點<b>沒有</b>自己的一組錯誤碼:官方 error-code 頁面上唯一與 algo 有關的只有
    /// <c>-4120</c>(擷取日期 2026-09-14)。條件單查不到回的是一般的 <c>-2013 NO_SUCH_ORDER</c>、
    /// 編號重複回的是 <c>-4116</c>、掛太多回的是 <c>-2025</c>,與一般委託完全同碼。
    /// The Binance algo endpoints have <b>no</b> error codes of their own: the only algo-related entry on the
    /// official error-code page is <c>-4120</c>, retrieved 2026-09-14. A missing conditional order answers the
    /// ordinary <c>-2013 NO_SUCH_ORDER</c>, a duplicate id answers <c>-4116</c>, and too many open orders
    /// answers <c>-2025</c> — the same codes as for ordinary orders.
    /// </para>
    /// <para>
    /// 因此區分只能在呼叫端做:錯誤是從哪一條路徑回來的,只有這一側知道。共用代碼的代價是上層分不出
    /// 「停損不見了」與「進場單不見了」,而前者代表部位正在裸奔 —— 那是這幾行存在的全部理由。
    /// The distinction can therefore only be made at the call site, which is the only place that knows which
    /// path the error came back from. Sharing the codes would leave callers unable to tell "the stop is gone"
    /// from "the entry is gone", and the first of those means a position is unprotected. That is the whole
    /// reason these lines exist.
    /// </para>
    /// </remarks>
    private static Error MapConditionalError(Error error)
    {
        // 先看幣安的原始代碼,再退回中立代碼。原始代碼分得比較細:-2025 是「掛太多」,
        // 與其他同樣落在 OrderRejected 的原因(保證金不足、部位不夠)語意完全不同,
        // 而條件單的上限是全帳戶合計 200 張,踩到的時候呼叫端需要知道是這一種。
        // The raw Binance code is consulted first and the neutral one is the fallback, because the raw code is
        // finer grained: -2025 means "too many resting orders", which is nothing like the other causes that
        // also land on OrderRejected such as insufficient margin or position. The conditional ceiling is 200
        // across the whole account, and a caller that hits it needs to know that is what happened.
        if (error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var apiCode))
        {
            var byApiCode = (int)apiCode switch
            {
                BinanceApiErrorCodes.NoSuchOrder => TradeErrorCodes.ConditionalOrderNotFound,
                BinanceApiErrorCodes.DuplicatedClientOrderId =>
                    TradeErrorCodes.DuplicateClientConditionalOrderId,
                BinanceApiErrorCodes.MaxOpenOrderExceeded => TradeErrorCodes.ConditionalOrderLimitExceeded,
                _ => null,
            };

            if (byApiCode is not null)
            {
                return WithCode(error, byApiCode);
            }
        }

        var code = error.Code switch
        {
            TradeErrorCodes.OrderNotFound => TradeErrorCodes.ConditionalOrderNotFound,
            TradeErrorCodes.DuplicateClientOrderId => TradeErrorCodes.DuplicateClientConditionalOrderId,
            TradeErrorCodes.OrderRejected => TradeErrorCodes.ConditionalOrderRejected,
            _ => error.Code,
        };

        return WithCode(error, code);
    }

    private static Error WithCode(Error error, string code) =>
        string.Equals(code, error.Code, StringComparison.Ordinal)
            ? error
            : new Error(code, error.Message, error.Category)
            {
                Exception = error.Exception,
                Data = error.Data,
            };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsExchangeInfo)
        {
            _exchangeInfo.Dispose();
        }
    }
}
