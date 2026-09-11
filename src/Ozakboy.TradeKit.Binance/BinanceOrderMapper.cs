using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 交易所中立的下單模型與幣安 <c>/fapi/v1/order</c> 參數之間的雙向對映。
/// The two-way mapping between the exchange-neutral order models and the parameters of the Binance
/// <c>/fapi/v1/order</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// 幣安對「哪些參數該出現」比抽象層嚴格得多:每個 <c>type</c> 有自己的必填集合,而<b>多送</b>一個
/// 不屬於該類型的參數同樣會被拒(<c>-1106 Parameter sent when not required</c>)。
/// 因此參數是逐型別「按需加入」而不是「全部加入再清掉」—— 後者只要漏清一個就是一張被拒的單,
/// 而拒單訊息不會告訴你是哪一個參數多了。
/// Binance is far stricter than the abstraction about which parameters may appear: each <c>type</c> has its own
/// mandatory set, and sending one that does not belong is rejected just as firmly
/// (<c>-1106 Parameter sent when not required</c>). Parameters are therefore added per type on demand rather
/// than added wholesale and pruned: one missed prune is a rejected order, and the rejection does not say which
/// parameter was the extra one.
/// </para>
/// <para>
/// 對映表(抽象層類型 → 幣安 <c>type</c> → 必帶參數):
/// The mapping table, neutral type to Binance <c>type</c> to mandatory parameters:
/// </para>
/// <list type="table">
///   <listheader>
///     <term><see cref="OrderType"/></term>
///     <description>幣安 <c>type</c> 與參數 / Binance type and parameters</description>
///   </listheader>
///   <item>
///     <term><see cref="OrderType.Limit"/></term>
///     <description><c>LIMIT</c>:<c>quantity</c>、<c>price</c>、<c>timeInForce</c></description>
///   </item>
///   <item>
///     <term><see cref="OrderType.Market"/></term>
///     <description><c>MARKET</c>:<c>quantity</c>(不可帶 <c>price</c> 或 <c>timeInForce</c>)</description>
///   </item>
///   <item>
///     <term><see cref="OrderType.StopMarket"/></term>
///     <description><c>STOP_MARKET</c>:<c>stopPrice</c> 加 <c>quantity</c> 或 <c>closePosition</c></description>
///   </item>
///   <item>
///     <term><see cref="OrderType.StopLimit"/></term>
///     <description><c>STOP</c>:<c>quantity</c>、<c>price</c>、<c>stopPrice</c>、<c>timeInForce</c></description>
///   </item>
///   <item>
///     <term><see cref="OrderType.TakeProfitMarket"/></term>
///     <description><c>TAKE_PROFIT_MARKET</c>:<c>stopPrice</c> 加 <c>quantity</c> 或 <c>closePosition</c></description>
///   </item>
///   <item>
///     <term><see cref="OrderType.TakeProfitLimit"/></term>
///     <description><c>TAKE_PROFIT</c>:<c>quantity</c>、<c>price</c>、<c>stopPrice</c>、<c>timeInForce</c></description>
///   </item>
///   <item>
///     <term><see cref="OrderType.TrailingStopMarket"/></term>
///     <description><c>TRAILING_STOP_MARKET</c>:<c>quantity</c>、<c>callbackRate</c>(0.1 至 10)</description>
///   </item>
/// </list>
/// </remarks>
internal static class BinanceOrderMapper
{
    private const string SideParameterName = "side";
    private const string PositionSideParameterName = "positionSide";
    private const string TypeParameterName = "type";
    private const string TimeInForceParameterName = "timeInForce";
    private const string QuantityParameterName = "quantity";
    private const string PriceParameterName = "price";
    private const string StopPriceParameterName = "stopPrice";
    private const string CallbackRateParameterName = "callbackRate";
    private const string WorkingTypeParameterName = "workingType";
    private const string ReduceOnlyParameterName = "reduceOnly";
    private const string ClosePositionParameterName = "closePosition";
    private const string NewClientOrderIdParameterName = "newClientOrderId";
    private const string OrderIdParameterName = "orderId";
    private const string OriginalClientOrderIdParameterName = "origClientOrderId";
    private const string LeverageParameterName = "leverage";
    private const string MarginTypeParameterName = "marginType";

    /// <summary>
    /// 幣安對移動停損回撤比例的下限,單位為百分比。
    /// The lowest trailing-stop callback rate Binance accepts, as a percentage.
    /// </summary>
    public const decimal MinCallbackRate = 0.1m;

    /// <summary>
    /// 幣安對移動停損回撤比例的上限,單位為百分比。
    /// The highest trailing-stop callback rate Binance accepts, as a percentage.
    /// </summary>
    /// <remarks>
    /// 抽象層的 <see cref="OrderRequest.Validate()"/> 只要求落在 0 到 100 之間,那是所有交易所的聯集;
    /// 幣安的實際上限是 10。這一檔差距若不在本地擋下,就是送出去換一次 <c>-1102</c> 與一份限流額度。
    /// The abstraction's validation only requires 0 to 100, which is the union across exchanges; the real
    /// Binance ceiling is 10. Not catching the gap locally costs a round trip, a <c>-1102</c>, and quota.
    /// </remarks>
    public const decimal MaxCallbackRate = 10m;

    /// <summary>
    /// 把下單請求翻譯成幣安的查詢參數。
    /// Translates an order request into Binance query parameters.
    /// </summary>
    /// <param name="request">已通過抽象層驗證與價量校正的請求。The validated, normalised request.</param>
    /// <param name="clientOrderId">這張單的冪等識別碼。The idempotency key for this order.</param>
    /// <returns>
    /// 依幣安規則組好的參數,或不符幣安規則的失敗原因。
    /// The parameters assembled to Binance's rules, or the reason they break one.
    /// </returns>
    /// <remarks>
    /// 參數順序就是簽章的待簽順序,因此用的是有序的 <see cref="QueryParametersBuilder"/> 而非字典。
    /// The parameter order is the order that gets signed, hence the ordered builder rather than a dictionary.
    /// </remarks>
    public static Result<QueryParametersBuilder> BuildPlaceOrder(OrderRequest request, string clientOrderId)
    {
        var validation = ValidateForBinance(request, clientOrderId);

        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var isConditional = NeedsStopPrice(request.OrderType)
            || request.OrderType == OrderType.TrailingStopMarket;

        var builder = QueryParameters.CreateBuilder()
            .Add(BinanceConstants.SymbolParameterName, request.Symbol)
            .Add(SideParameterName, ToBinance(request.Side))
            .Add(PositionSideParameterName, ToBinance(request.PositionSide))
            .Add(TypeParameterName, ToBinance(request.OrderType));

        if (NeedsPrice(request.OrderType))
        {
            builder.Add(TimeInForceParameterName, ToBinance(request.TimeInForce));
        }

        if (!request.ClosePosition)
        {
            builder.Add(QuantityParameterName, request.Quantity);
        }

        if (request.Price is { } price)
        {
            builder.Add(PriceParameterName, price);
        }

        if (request.StopPrice is { } stopPrice)
        {
            builder.Add(StopPriceParameterName, stopPrice);
        }

        if (request.CallbackRate is { } callbackRate)
        {
            builder.Add(CallbackRateParameterName, callbackRate);
        }

        if (isConditional)
        {
            builder.Add(WorkingTypeParameterName, ToBinance(request.TriggerPriceType));
        }

        // reduceOnly 與 closePosition 只在為真時出現。幣安不接受 closePosition=false 與 reduceOnly 併送,
        // 而「送出 false」與「不送」在這裡語意相同,少送一個參數就少一種被拒的方式。
        // Both flags appear only when true. Binance does not accept closePosition alongside reduceOnly, and
        // sending false means the same as omitting it, so omitting removes one way to be rejected.
        if (request.ReduceOnly)
        {
            builder.Add(ReduceOnlyParameterName, value: true);
        }

        if (request.ClosePosition)
        {
            builder.Add(ClosePositionParameterName, value: true);
        }

        builder.Add(NewClientOrderIdParameterName, clientOrderId);

        return builder;
    }

    /// <summary>
    /// 組出查單與撤單共用的識別參數。
    /// Builds the identifying parameters shared by order lookup and cancellation.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="identifier">訂單識別碼。The order identifier.</param>
    /// <returns>參數,或識別碼不可用時的失敗原因。The parameters, or why the identifier cannot be used.</returns>
    /// <remarks>
    /// 幣安要求 <c>orderId</c> 與 <c>origClientOrderId</c> 至少給一個,兩個都不給會回 <c>-1102</c>。
    /// 空識別碼在這裡就擋下來,省一趟往返。
    /// Binance requires at least one of <c>orderId</c> and <c>origClientOrderId</c> and answers <c>-1102</c>
    /// when neither arrives. An empty identifier is stopped here instead, saving the round trip.
    /// </remarks>
    public static Result<QueryParametersBuilder> BuildOrderLookup(string symbol, OrderIdentifier identifier)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        if (identifier.IsEmpty)
        {
            return TradeErrors.InvalidQuery(
                "訂單識別碼是空的,必須提供交易所訂單編號或用戶端訂單編號其中之一。The order identifier is empty; supply either the exchange order id or the client order id.");
        }

        var builder = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        if (identifier.ExchangeOrderId is { } exchangeOrderId)
        {
            builder.Add(OrderIdParameterName, exchangeOrderId);
        }
        else
        {
            builder.Add(OriginalClientOrderIdParameterName, identifier.ClientOrderId!);
        }

        return builder;
    }

    /// <summary>
    /// 組出調整槓桿的參數。
    /// Builds the parameters for a leverage change.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="leverage">槓桿倍數。The leverage.</param>
    /// <returns>參數。The parameters.</returns>
    public static QueryParametersBuilder BuildLeverage(string symbol, int leverage) =>
        QueryParameters.CreateBuilder()
            .Add(BinanceConstants.SymbolParameterName, symbol)
            .Add(LeverageParameterName, (long)leverage);

    /// <summary>
    /// 組出調整保證金模式的參數。
    /// Builds the parameters for a margin mode change.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="marginMode">保證金模式。The margin mode.</param>
    /// <returns>參數。The parameters.</returns>
    public static QueryParametersBuilder BuildMarginType(string symbol, MarginMode marginMode) =>
        QueryParameters.CreateBuilder()
            .Add(BinanceConstants.SymbolParameterName, symbol)
            .Add(MarginTypeParameterName, ToBinance(marginMode));

    /// <summary>
    /// 套用幣安比抽象層更嚴的那幾條規則。
    /// Applies the rules where Binance is stricter than the abstraction.
    /// </summary>
    /// <param name="request">下單請求。The order request.</param>
    /// <param name="clientOrderId">冪等識別碼。The idempotency key.</param>
    /// <returns>符合時為成功,否則為失敗原因。Success when it passes, otherwise the reason it does not.</returns>
    /// <remarks>
    /// <see cref="OrderRequest.Validate()"/> 檢查的是「所有交易所的共同底線」,這裡補的是幣安獨有的四條:
    /// 編號格式、<c>closePosition</c> 只用於市價型條件單、雙向模式不可帶 <c>reduceOnly</c>、
    /// 回撤比例上限 10 而非 100。每一條都是「不擋就換一次拒單」。
    /// <see cref="OrderRequest.Validate()"/> covers the floor common to every exchange; these four are Binance's
    /// own: the id format, <c>closePosition</c> being limited to market-style conditional orders, hedge mode
    /// refusing <c>reduceOnly</c>, and a callback ceiling of 10 rather than 100. Each one is a rejection saved.
    /// </remarks>
    public static Result ValidateForBinance(OrderRequest request, string clientOrderId)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!BinanceClientOrderId.IsValid(clientOrderId))
        {
            return TradeErrors.InvalidOrderRequest(
                $"用戶端訂單編號「{clientOrderId}」不符幣安格式:長度需為 1 至 {BinanceClientOrderId.MaxLength},且只能是英數與 . : / _ - 。The client order id \"{clientOrderId}\" does not match the Binance format: 1 to {BinanceClientOrderId.MaxLength} characters of letters, digits, and . : / _ - only.");
        }

        // 列舉的「未定義值」要在這裡變成失敗,而不是留給底下的對映表擲例外。
        // 一個 (OrderSide)99 通過了抽象層的驗證(它只擋 Unspecified),而下單方法的契約是回傳 Result;
        // 讓它擲例外會讓呼叫端的 try/catch 與 Result 判斷各漏一半。
        // An undefined enum value becomes a failure here rather than an exception from the mapping table
        // below. A cast such as (OrderSide)99 passes the abstraction's validation, which only rejects
        // Unspecified, and the contract of the placement method is to return a Result: throwing instead leaves
        // callers catching in one place and checking in another.
        if (!Enum.IsDefined(request.Side))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的買賣方向:{(int)request.Side}。Undefined order side: {(int)request.Side}.");
        }

        if (!Enum.IsDefined(request.OrderType))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的委託類型:{(int)request.OrderType}。Undefined order type: {(int)request.OrderType}.");
        }

        if (NeedsPrice(request.OrderType) && !Enum.IsDefined(request.TimeInForce))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的有效期限規則:{(int)request.TimeInForce}。Undefined time in force: {(int)request.TimeInForce}.");
        }

        if (!Enum.IsDefined(request.PositionSide))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的持倉方向:{(int)request.PositionSide}。Undefined position side: {(int)request.PositionSide}.");
        }

        if (!Enum.IsDefined(request.TriggerPriceType))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的觸發價種類:{(int)request.TriggerPriceType}。Undefined trigger price type: {(int)request.TriggerPriceType}.");
        }

        if (request.ClosePosition
            && request.OrderType is not (OrderType.StopMarket or OrderType.TakeProfitMarket))
        {
            return TradeErrors.InvalidOrderRequest(
                $"幣安的 closePosition 只能用於 STOP_MARKET 與 TAKE_PROFIT_MARKET,{request.OrderType} 不適用。Binance accepts closePosition only on STOP_MARKET and TAKE_PROFIT_MARKET, not on {request.OrderType}.");
        }

        // 雙向模式下,部位方向已經由 positionSide 指定,幣安因此拒收 reduceOnly(-1106)。
        // 要在雙向模式縮小部位,請對著那一側送反向的單,而不是打 reduceOnly 旗標。
        // In hedge mode the side is already carried by positionSide, so Binance refuses reduceOnly (-1106).
        // Shrinking a hedge-mode position means sending an opposing order on that side, not setting the flag.
        if (request.ReduceOnly && request.PositionSide != PositionSide.Both)
        {
            return TradeErrors.InvalidOrderRequest(
                $"雙向模式({request.PositionSide})不可指定 reduceOnly,幣安會以 -1106 拒單。Binance rejects reduceOnly in hedge mode ({request.PositionSide}) with -1106.");
        }

        if (request.OrderType == OrderType.TrailingStopMarket
            && request.CallbackRate is not (>= MinCallbackRate and <= MaxCallbackRate))
        {
            return TradeErrors.InvalidOrderRequest(
                $"幣安的移動停損回撤比例必須介於 {MinCallbackRate} 與 {MaxCallbackRate} 之間(百分比),收到 {request.CallbackRate}。The Binance trailing-stop callback rate must be between {MinCallbackRate} and {MaxCallbackRate} percent but was {request.CallbackRate}.");
        }

        return Result.Success();
    }

    /// <summary>
    /// 把幣安的 <c>type</c> 字串轉回中立的委託類型。
    /// Converts a Binance <c>type</c> string back into the neutral order type.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的類型;對不上時為 <see cref="OrderType.Unspecified"/>。
    /// The mapped type, or <see cref="OrderType.Unspecified"/> when it maps to none.
    /// </returns>
    /// <remarks>
    /// 對不上時回 <see cref="OrderType.Unspecified"/> 而不是讓整份解析失敗。查詢掛單會一併回傳
    /// 手動在網頁下的單,那裡可能出現本套件不送、也不模型化的類型;為了一張別人的單就讓整份清單讀不到,
    /// 代價遠大於一個誠實的「未指定」。
    /// An unmapped value becomes <see cref="OrderType.Unspecified"/> rather than failing the whole parse. A
    /// listing of open orders includes ones placed by hand in the web interface, which can carry types this
    /// package neither sends nor models; losing the entire list over someone else's order costs far more than
    /// one honest "unspecified".
    /// </remarks>
    public static OrderType ParseOrderType(string? value) => value switch
    {
        "LIMIT" => OrderType.Limit,
        "MARKET" => OrderType.Market,
        "STOP_MARKET" => OrderType.StopMarket,
        "STOP" => OrderType.StopLimit,
        "TAKE_PROFIT_MARKET" => OrderType.TakeProfitMarket,
        "TAKE_PROFIT" => OrderType.TakeProfitLimit,
        "TRAILING_STOP_MARKET" => OrderType.TrailingStopMarket,
        _ => OrderType.Unspecified,
    };

    /// <summary>
    /// 把幣安的 <c>side</c> 字串轉回中立的買賣方向。
    /// Converts a Binance <c>side</c> string back into the neutral order side.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的方向;對不上時為 <see cref="OrderSide.Unspecified"/>。
    /// The mapped side, or <see cref="OrderSide.Unspecified"/> when it maps to none.
    /// </returns>
    public static OrderSide ParseOrderSide(string? value) => value switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => OrderSide.Unspecified,
    };

    /// <summary>
    /// 把幣安的 <c>status</c> 字串轉回中立的委託狀態。
    /// Converts a Binance <c>status</c> string back into the neutral order status.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的狀態;對不上時為 <see cref="OrderStatus.Unspecified"/>,呼叫端必須把它當成解析失敗。
    /// The mapped status, or <see cref="OrderStatus.Unspecified"/>, which the caller must treat as a parse
    /// failure.
    /// </returns>
    /// <remarks>
    /// 狀態與類型不同,對不上<b>不可以</b>放過。<see cref="OrderStatus.Unspecified"/> 的
    /// <c>IsOpen</c> 與 <c>IsFinal</c> 同時為 <see langword="false"/> —— 一張既沒結束也沒在簿上的單會讓
    /// 部位追蹤永遠等不到終態,那比整份查詢失敗難查得多。
    /// Unlike the type, an unmapped status must <b>not</b> be let through.
    /// <see cref="OrderStatus.Unspecified"/> reports both <c>IsOpen</c> and <c>IsFinal</c> as
    /// <see langword="false"/>, and an order that is neither live nor finished leaves position tracking waiting
    /// for a terminal state that never arrives — far harder to diagnose than an outright parse failure.
    /// </remarks>
    public static OrderStatus ParseOrderStatus(string? value) => value switch
    {
        "NEW" => OrderStatus.New,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELED" => OrderStatus.Canceled,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
        "PENDING_CANCEL" => OrderStatus.PendingCancel,

        // NEW_INSURANCE 與 NEW_ADL 是強平與自動減倉產生的委託,狀態上仍是「已接受、尚未成交」。
        // NEW_INSURANCE and NEW_ADL come from liquidation and auto-deleveraging; both are still "accepted, unfilled".
        "NEW_INSURANCE" or "NEW_ADL" => OrderStatus.New,
        _ => OrderStatus.Unspecified,
    };

    /// <summary>
    /// 把幣安的 <c>timeInForce</c> 字串轉回中立的有效期限規則。
    /// Converts a Binance <c>timeInForce</c> string back into the neutral time in force.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的規則;對不上時為 <see cref="TimeInForce.Unspecified"/>。
    /// The mapped rule, or <see cref="TimeInForce.Unspecified"/> when it maps to none.
    /// </returns>
    /// <remarks>
    /// 幣安另有 <c>GTD</c> 等本套件不送的值。對不上時誠實回報未指定,不要猜成 GTC ——
    /// 把一張有到期時間的單說成「掛到撤銷為止」,會讓上層以為它會一直在那裡。
    /// Binance also has values such as <c>GTD</c> that this package never sends. An unmapped value is reported
    /// honestly rather than guessed as GTC: calling an order with an expiry "good til cancelled" tells the
    /// caller it will stay on the book when it will not.
    /// </remarks>
    public static TimeInForce ParseTimeInForce(string? value) => value switch
    {
        "GTC" => TimeInForce.GoodTilCanceled,
        "IOC" => TimeInForce.ImmediateOrCancel,
        "FOK" => TimeInForce.FillOrKill,
        "GTX" => TimeInForce.GoodTilCrossing,
        _ => TimeInForce.Unspecified,
    };

    /// <summary>
    /// 把幣安的 <c>positionSide</c> 字串轉回中立的持倉方向。
    /// Converts a Binance <c>positionSide</c> string back into the neutral position side.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的方向,預設 <see cref="PositionSide.Both"/>(單向模式)。
    /// The mapped side, defaulting to <see cref="PositionSide.Both"/> for one-way mode.
    /// </returns>
    public static PositionSide ParsePositionSide(string? value) => value switch
    {
        "LONG" => PositionSide.Long,
        "SHORT" => PositionSide.Short,
        _ => PositionSide.Both,
    };

    private static bool NeedsPrice(OrderType orderType) =>
        orderType is OrderType.Limit or OrderType.StopLimit or OrderType.TakeProfitLimit;

    private static bool NeedsStopPrice(OrderType orderType) =>
        orderType is OrderType.StopMarket or OrderType.StopLimit
            or OrderType.TakeProfitMarket or OrderType.TakeProfitLimit;

    public static string ToBinance(OrderSide side) => side switch
    {
        OrderSide.Buy => "BUY",
        OrderSide.Sell => "SELL",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "未定義的買賣方向。Undefined order side."),
    };

    public static string ToBinance(PositionSide positionSide) => positionSide switch
    {
        PositionSide.Both => "BOTH",
        PositionSide.Long => "LONG",
        PositionSide.Short => "SHORT",
        _ => throw new ArgumentOutOfRangeException(
            nameof(positionSide),
            positionSide,
            "未定義的持倉方向。Undefined position side."),
    };

    public static string ToBinance(OrderType orderType) => orderType switch
    {
        OrderType.Limit => "LIMIT",
        OrderType.Market => "MARKET",
        OrderType.StopMarket => "STOP_MARKET",
        OrderType.StopLimit => "STOP",
        OrderType.TakeProfitMarket => "TAKE_PROFIT_MARKET",
        OrderType.TakeProfitLimit => "TAKE_PROFIT",
        OrderType.TrailingStopMarket => "TRAILING_STOP_MARKET",
        _ => throw new ArgumentOutOfRangeException(
            nameof(orderType),
            orderType,
            "未定義的委託類型。Undefined order type."),
    };

    public static string ToBinance(TimeInForce timeInForce) => timeInForce switch
    {
        TimeInForce.GoodTilCanceled => "GTC",
        TimeInForce.ImmediateOrCancel => "IOC",
        TimeInForce.FillOrKill => "FOK",
        TimeInForce.GoodTilCrossing => "GTX",
        _ => throw new ArgumentOutOfRangeException(
            nameof(timeInForce),
            timeInForce,
            "未定義的有效期限規則。Undefined time in force."),
    };

    // 幣安把「最新成交價」叫做 CONTRACT_PRICE,不是 LAST_PRICE。字面照抄,不要照語意改寫。
    // Binance calls the last traded price CONTRACT_PRICE rather than LAST_PRICE. Copy the literal, do not
    // rewrite it to match the concept.
    public static string ToBinance(TriggerPriceType triggerPriceType) => triggerPriceType switch
    {
        TriggerPriceType.MarkPrice => "MARK_PRICE",
        TriggerPriceType.LastPrice => "CONTRACT_PRICE",
        _ => throw new ArgumentOutOfRangeException(
            nameof(triggerPriceType),
            triggerPriceType,
            "未定義的觸發價種類。Undefined trigger price type."),
    };

    // 幣安的全倉是 CROSSED,不是 CROSS。持倉查詢回來的 marginType 卻是小寫的 cross ——
    // 讀與寫用的字面值不同,這是必須逐字照抄的地方。
    // Binance writes cross margin as CROSSED here, while the position query returns a lower-case cross. The
    // literal differs between reading and writing, so it has to be copied exactly.
    public static string ToBinance(MarginMode marginMode) => marginMode switch
    {
        MarginMode.Cross => "CROSSED",
        MarginMode.Isolated => "ISOLATED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(marginMode),
            marginMode,
            "未定義的保證金模式。Undefined margin mode."),
    };
}
