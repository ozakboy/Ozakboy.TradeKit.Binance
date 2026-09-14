using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 交易所中立的條件單模型與幣安 <c>/fapi/v1/algoOrder</c> 參數之間的雙向對映。
/// The two-way mapping between the exchange-neutral conditional order models and the parameters of the Binance
/// <c>/fapi/v1/algoOrder</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// 幣安在 2025-12-09 把條件單移出 <c>/fapi/v1/order</c>,改由 Algo Service 受理。舊端點對這幾個
/// <c>type</c> 一律回 <c>-4120</c>,而<b>參數的組法幾乎沒變</b> —— 換的是端點、識別碼與狀態欄位的名字:
/// <c>orderId</c> 變成 <c>algoId</c>、<c>newClientOrderId</c> 變成 <c>clientAlgoId</c>、
/// <c>status</c> 變成 <c>algoStatus</c>,並且多一個必填的 <c>algoType</c>。
/// Binance moved conditional orders off <c>/fapi/v1/order</c> to the Algo Service on 2025-12-09. The old
/// endpoint answers <c>-4120</c> for those <c>type</c> values, while <b>the parameter shape barely changed</b>:
/// what moved are the endpoint and the names of the identifier and status fields — <c>orderId</c> became
/// <c>algoId</c>, <c>newClientOrderId</c> became <c>clientAlgoId</c>, <c>status</c> became <c>algoStatus</c> —
/// plus one new mandatory <c>algoType</c>.
/// </para>
/// <para>
/// 因此這裡刻意與 <see cref="BinanceOrderMapper"/> 分成兩個型別,而不是加參數共用。兩條路徑的必填集合、
/// 識別參數與回應欄位都不同,共用一份會讓「哪些欄位屬於哪一邊」只存在於一串 <c>if</c> 裡,
/// 而那正是 <c>-1106 Parameter sent when not required</c> 最容易長出來的地方。
/// This is deliberately a separate type from <see cref="BinanceOrderMapper"/> rather than a shared one with
/// extra flags. The two paths differ in their mandatory sets, their identifying parameters, and their response
/// fields, and merging them would leave "which field belongs to which side" living inside a chain of
/// <c>if</c> statements — which is exactly where a <c>-1106 Parameter sent when not required</c> grows.
/// </para>
/// <para>
/// 對映表(抽象層類型 → 幣安 <c>type</c> → 必帶參數):
/// The mapping table, neutral type to Binance <c>type</c> to mandatory parameters:
/// </para>
/// <list type="table">
///   <listheader>
///     <term><see cref="ConditionalOrderType"/></term>
///     <description>幣安 <c>type</c> 與參數 / Binance type and parameters</description>
///   </listheader>
///   <item>
///     <term><see cref="ConditionalOrderType.StopMarket"/></term>
///     <description><c>STOP_MARKET</c>:<c>triggerPrice</c> 加 <c>quantity</c> 或 <c>closePosition</c></description>
///   </item>
///   <item>
///     <term><see cref="ConditionalOrderType.StopLimit"/></term>
///     <description><c>STOP</c>:<c>quantity</c>、<c>price</c>、<c>triggerPrice</c>、<c>timeInForce</c></description>
///   </item>
///   <item>
///     <term><see cref="ConditionalOrderType.TakeProfitMarket"/></term>
///     <description><c>TAKE_PROFIT_MARKET</c>:<c>triggerPrice</c> 加 <c>quantity</c> 或 <c>closePosition</c></description>
///   </item>
///   <item>
///     <term><see cref="ConditionalOrderType.TakeProfitLimit"/></term>
///     <description><c>TAKE_PROFIT</c>:<c>quantity</c>、<c>price</c>、<c>triggerPrice</c>、<c>timeInForce</c></description>
///   </item>
///   <item>
///     <term><see cref="ConditionalOrderType.TrailingStopMarket"/></term>
///     <description><c>TRAILING_STOP_MARKET</c>:<c>quantity</c>、<c>callbackRate</c>(0.1 至 10)、選用 <c>activationPrice</c></description>
///   </item>
/// </list>
/// </remarks>
internal static class BinanceAlgoOrderMapper
{
    /// <summary>
    /// 條件單的 <c>algoType</c> 值。Algo Service 底下還有其他類型(策略單、網格單),
    /// 本套件只送這一種。
    /// The <c>algoType</c> of a conditional order. The Algo Service carries other types as well, such as
    /// strategy and grid orders; this package sends only this one.
    /// </summary>
    public const string ConditionalAlgoType = "CONDITIONAL";

    private const string AlgoTypeParameterName = "algoType";
    private const string SideParameterName = "side";
    private const string PositionSideParameterName = "positionSide";
    private const string TypeParameterName = "type";
    private const string TimeInForceParameterName = "timeInForce";
    private const string QuantityParameterName = "quantity";
    private const string PriceParameterName = "price";
    private const string TriggerPriceParameterName = "triggerPrice";
    private const string CallbackRateParameterName = "callbackRate";
    // 幣安的參數名是 activatePrice(動詞),不是 activationPrice(名詞)。抽象層那一側叫
    // ActivationPrice,兩邊不同名是刻意的:這裡照抄交易所的字面值,拼成 activationPrice 會拿到
    // -1104(未讀取的參數),而錯誤訊息不會說是哪一個參數多了。
    // The Binance parameter is activatePrice, not activationPrice. The neutral side calls it ActivationPrice
    // and the mismatch is deliberate: the literal is copied from the exchange, and spelling it activationPrice
    // earns a -1104 whose message never names the offending parameter.
    private const string ActivatePriceParameterName = "activatePrice";
    private const string WorkingTypeParameterName = "workingType";
    private const string ReduceOnlyParameterName = "reduceOnly";
    private const string ClosePositionParameterName = "closePosition";
    private const string ClientAlgoIdParameterName = "clientAlgoId";
    private const string AlgoIdParameterName = "algoId";

    /// <summary>
    /// 把條件單請求翻譯成幣安的查詢參數。
    /// Translates a conditional order request into Binance query parameters.
    /// </summary>
    /// <param name="request">已通過抽象層驗證與價量校正的請求。The validated, normalised request.</param>
    /// <param name="clientAlgoId">這張條件單的冪等識別碼。The idempotency key for this conditional order.</param>
    /// <returns>
    /// 依幣安規則組好的參數,或不符幣安規則的失敗原因。
    /// The parameters assembled to Binance's rules, or the reason they break one.
    /// </returns>
    /// <remarks>
    /// 參數順序就是簽章的待簽順序,因此用的是有序的 <see cref="QueryParametersBuilder"/> 而非字典。
    /// The parameter order is the order that gets signed, hence the ordered builder rather than a dictionary.
    /// </remarks>
    public static Result<QueryParametersBuilder> BuildPlaceAlgoOrder(
        ConditionalOrderRequest request,
        string clientAlgoId)
    {
        var validation = ValidateForBinance(request, clientAlgoId);

        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var builder = QueryParameters.CreateBuilder()
            .Add(AlgoTypeParameterName, ConditionalAlgoType)
            .Add(BinanceConstants.SymbolParameterName, request.Symbol)
            .Add(SideParameterName, BinanceOrderMapper.ToBinance(request.Side))
            .Add(PositionSideParameterName, BinanceOrderMapper.ToBinance(request.PositionSide))
            .Add(TypeParameterName, ToBinance(request.ConditionalOrderType));

        if (request.ConditionalOrderType.PlacesLimitOrder())
        {
            builder.Add(TimeInForceParameterName, BinanceOrderMapper.ToBinance(request.TimeInForce));
        }

        if (!request.ClosePosition)
        {
            builder.Add(QuantityParameterName, request.Quantity);
        }

        if (request.Price is { } price)
        {
            builder.Add(PriceParameterName, price);
        }

        if (request.TriggerPrice is { } triggerPrice)
        {
            builder.Add(TriggerPriceParameterName, triggerPrice);
        }

        if (request.CallbackRate is { } callbackRate)
        {
            builder.Add(CallbackRateParameterName, callbackRate);
        }

        if (request.ActivationPrice is { } activationPrice)
        {
            builder.Add(ActivatePriceParameterName, activationPrice);
        }

        // workingType 每一種條件單都要:它決定觸發價跟哪個價格比。不送會落在交易所的預設(CONTRACT_PRICE),
        // 而本套件的預設是 MARK_PRICE —— 兩者差的是「會不會被一根影線掃出場」。
        // Every conditional order carries workingType: it decides which price the trigger is compared against.
        // Omitting it falls back to the exchange default of CONTRACT_PRICE while this package defaults to
        // MARK_PRICE, and the difference is whether a single wick takes the position out.
        builder.Add(WorkingTypeParameterName, BinanceOrderMapper.ToBinance(request.TriggerPriceType));

        // 兩個旗標都只在為真時出現,理由與一般委託相同:送出 false 與不送語意相同,
        // 少送一個參數就少一種被拒的方式。
        // Both flags appear only when true, for the same reason as on an ordinary order: sending false means the
        // same as omitting it, and omitting removes one way to be rejected.
        if (request.ReduceOnly)
        {
            builder.Add(ReduceOnlyParameterName, value: true);
        }

        if (request.ClosePosition)
        {
            builder.Add(ClosePositionParameterName, value: true);
        }

        builder.Add(ClientAlgoIdParameterName, clientAlgoId);

        return builder;
    }

    /// <summary>
    /// 組出查詢與撤銷條件單共用的識別參數。
    /// Builds the identifying parameters shared by conditional order lookup and cancellation.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="identifier">條件單識別碼。The conditional order identifier.</param>
    /// <returns>參數,或識別碼不可用時的失敗原因。The parameters, or why the identifier cannot be used.</returns>
    /// <remarks>
    /// 幣安要求 <c>algoId</c> 與 <c>clientAlgoId</c> 至少給一個,兩個都不給會回 <c>-1102</c>。
    /// 空識別碼在這裡就擋下來,省一趟往返。
    /// Binance requires at least one of <c>algoId</c> and <c>clientAlgoId</c> and answers <c>-1102</c> when
    /// neither arrives. An empty identifier is stopped here instead, saving the round trip.
    /// </remarks>
    public static Result<QueryParametersBuilder> BuildAlgoOrderLookup(
        string symbol,
        ConditionalOrderIdentifier identifier)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        if (identifier.IsEmpty)
        {
            return TradeErrors.InvalidQuery(
                "條件單識別碼是空的,必須提供交易所條件單編號或用戶端條件單編號其中之一。The conditional order identifier is empty; supply either the exchange conditional order id or the client one.");
        }

        var builder = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        if (identifier.ExchangeConditionalOrderId is { } algoId)
        {
            builder.Add(AlgoIdParameterName, algoId);
        }
        else
        {
            builder.Add(ClientAlgoIdParameterName, identifier.ClientConditionalOrderId!);
        }

        return builder;
    }

    /// <summary>
    /// 套用幣安比抽象層更嚴的那幾條條件單規則。
    /// Applies the conditional order rules where Binance is stricter than the abstraction.
    /// </summary>
    /// <param name="request">條件單請求。The conditional order request.</param>
    /// <param name="clientAlgoId">冪等識別碼。The idempotency key.</param>
    /// <returns>符合時為成功,否則為失敗原因。Success when it passes, otherwise the reason it does not.</returns>
    /// <remarks>
    /// <see cref="ConditionalOrderRequest.Validate()"/> 檢查的是所有交易所的共同底線,這裡補的是幣安獨有的:
    /// <c>clientAlgoId</c> 的格式(與 <c>newClientOrderId</c> 同一條規則
    /// <c>^[\.A-Z\:/a-z0-9_-]{1,36}$</c>)、雙向模式不可帶 <c>reduceOnly</c>、回撤比例上限 10 而非 100。
    /// 每一條都是「不擋就換一次拒單」。
    /// <see cref="ConditionalOrderRequest.Validate()"/> covers the floor common to every exchange; these are
    /// Binance's own: the <c>clientAlgoId</c> format, which is the same
    /// <c>^[\.A-Z\:/a-z0-9_-]{1,36}$</c> rule as <c>newClientOrderId</c>; hedge mode refusing
    /// <c>reduceOnly</c>; and a callback ceiling of 10 rather than 100. Each one is a rejection saved.
    /// </remarks>
    public static Result ValidateForBinance(ConditionalOrderRequest request, string clientAlgoId)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!BinanceClientOrderId.IsValid(clientAlgoId))
        {
            return TradeErrors.InvalidOrderRequest(
                $"用戶端條件單編號「{clientAlgoId}」不符幣安格式:長度需為 1 至 {BinanceClientOrderId.MaxLength},且只能是英數與 . : / _ - 。The client algo id \"{clientAlgoId}\" does not match the Binance format: 1 to {BinanceClientOrderId.MaxLength} characters of letters, digits, and . : / _ - only.");
        }

        // 列舉的未定義值在這裡變成失敗,而不是留給底下的對映表擲例外,理由與一般委託相同:
        // 送單方法的契約是回傳 Result,擲例外會讓呼叫端的 try/catch 與 Result 判斷各漏一半。
        // Undefined enum values become failures here rather than exceptions from the mapping tables below, for
        // the same reason as on an ordinary order: the contract is to return a Result, and throwing leaves
        // callers catching in one place and checking in another.
        if (!Enum.IsDefined(request.Side))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的買賣方向:{(int)request.Side}。Undefined order side: {(int)request.Side}.");
        }

        if (!Enum.IsDefined(request.ConditionalOrderType))
        {
            return TradeErrors.InvalidOrderRequest(
                $"未定義的條件單類型:{(int)request.ConditionalOrderType}。Undefined conditional order type: {(int)request.ConditionalOrderType}.");
        }

        if (request.ConditionalOrderType.PlacesLimitOrder() && !Enum.IsDefined(request.TimeInForce))
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

        // 雙向模式下部位方向已由 positionSide 指定,幣安因此拒收 reduceOnly(-1106)。
        // In hedge mode the side is already carried by positionSide, so Binance refuses reduceOnly (-1106).
        if (request.ReduceOnly && request.PositionSide != PositionSide.Both)
        {
            return TradeErrors.InvalidOrderRequest(
                $"雙向模式({request.PositionSide})不可指定 reduceOnly,幣安會以 -1106 拒單。Binance rejects reduceOnly in hedge mode ({request.PositionSide}) with -1106.");
        }

        if (request.ConditionalOrderType == ConditionalOrderType.TrailingStopMarket
            && request.CallbackRate is not (>= BinanceOrderMapper.MinCallbackRate
                and <= BinanceOrderMapper.MaxCallbackRate))
        {
            return TradeErrors.InvalidOrderRequest(
                $"幣安的移動停損回撤比例必須介於 {BinanceOrderMapper.MinCallbackRate} 與 {BinanceOrderMapper.MaxCallbackRate} 之間(百分比),收到 {request.CallbackRate}。The Binance trailing-stop callback rate must be between {BinanceOrderMapper.MinCallbackRate} and {BinanceOrderMapper.MaxCallbackRate} percent but was {request.CallbackRate}.");
        }

        return Result.Success();
    }

    /// <summary>
    /// 把幣安的條件單 <c>type</c> 字串轉回中立的條件單類型。
    /// Converts a Binance conditional order <c>type</c> string back into the neutral conditional order type.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的類型;對不上時為 <see cref="ConditionalOrderType.Unspecified"/>。
    /// The mapped type, or <see cref="ConditionalOrderType.Unspecified"/> when it maps to none.
    /// </returns>
    /// <remarks>
    /// 字面值與 <see cref="BinanceOrderMapper.ParseOrderType"/> 收的那一組相同:Algo Service 沿用了
    /// 原本的 <c>type</c> 字串,只是換了端點。
    /// The literals are the same ones <see cref="BinanceOrderMapper.ParseOrderType"/> accepts: the Algo Service
    /// kept the original <c>type</c> strings and changed only the endpoint.
    /// </remarks>
    public static ConditionalOrderType ParseConditionalOrderType(string? value) => value switch
    {
        "STOP_MARKET" => ConditionalOrderType.StopMarket,
        "STOP" => ConditionalOrderType.StopLimit,
        "TAKE_PROFIT_MARKET" => ConditionalOrderType.TakeProfitMarket,
        "TAKE_PROFIT" => ConditionalOrderType.TakeProfitLimit,
        "TRAILING_STOP_MARKET" => ConditionalOrderType.TrailingStopMarket,
        _ => ConditionalOrderType.Unspecified,
    };

    /// <summary>
    /// 把幣安的 <c>algoStatus</c> 字串轉回中立的條件單狀態。
    /// Converts a Binance <c>algoStatus</c> string back into the neutral conditional order status.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的狀態;對不上時為 <see cref="ConditionalOrderStatus.Unspecified"/>,呼叫端必須把它當成解析失敗。
    /// The mapped status, or <see cref="ConditionalOrderStatus.Unspecified"/>, which the caller must treat as a
    /// parse failure.
    /// </returns>
    /// <remarks>
    /// <para>
    /// 狀態對不上<b>不可以</b>放過,理由與 <see cref="BinanceOrderMapper.ParseOrderStatus"/> 相同:
    /// <see cref="ConditionalOrderStatus.Unspecified"/> 的 <c>IsOpen</c> 與 <c>IsFinal</c> 同時為
    /// <see langword="false"/>,一張既沒結束也沒在等的停損會讓對帳永遠等不到終態。
    /// An unmapped status must <b>not</b> be let through, for the same reason as on
    /// <see cref="BinanceOrderMapper.ParseOrderStatus"/>: <see cref="ConditionalOrderStatus.Unspecified"/>
    /// reports both <c>IsOpen</c> and <c>IsFinal</c> as <see langword="false"/>, and a stop that is neither
    /// waiting nor finished leaves reconciliation waiting for a terminal state that never comes.
    /// </para>
    /// <para>
    /// <c>algoStatus</c> 的七個值與各自的原文定義來自官方 user-data-streams 頁面
    /// (<c>https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/user-data-streams</c>,
    /// 擷取日期 2026-09-14)。有兩個容易記錯的地方:拼字是 <c>CANCELED</c>(<b>一個 L</b>),
    /// 而且<b>沒有</b> <c>WORKING</c> 與 <c>FILLED</c> —— 那兩個是 <c>GRID_UPDATE</c> 與
    /// <c>STRATEGY_UPDATE</c> 的狀態,不是條件單的。憑印象多寫一個 <c>CANCELLED</c> 分支不會報錯,
    /// 但會讓人以為兩種拼法都處理過了。
    /// The seven values and their wording come from the official user-data-streams page, retrieved
    /// 2026-09-14. Two are easy to misremember: the spelling is <c>CANCELED</c> with <b>one L</b>, and there is
    /// <b>no</b> <c>WORKING</c> or <c>FILLED</c> — those belong to <c>GRID_UPDATE</c> and
    /// <c>STRATEGY_UPDATE</c> rather than to conditional orders. Adding a <c>CANCELLED</c> branch from memory
    /// costs nothing at run time but leaves the impression that both spellings were verified.
    /// </para>
    /// <para>
    /// <c>FINISHED</c> 對映到 <see cref="ConditionalOrderStatus.Finished"/> 而<b>不是</b>
    /// <see cref="ConditionalOrderStatus.Filled"/>。官方定義是「filled or canceled in the matching engine」——
    /// 它涵蓋兩種結局,而幣安在這個欄位上不區分。讀成成交,會讓一張觸發後被撤掉的停損在帳上變成
    /// 一次不存在的平倉;要知道究竟成交多少,得拿 <c>actualOrderId</c> 去查那張委託。
    /// <c>FINISHED</c> maps to <see cref="ConditionalOrderStatus.Finished"/> rather than
    /// <see cref="ConditionalOrderStatus.Filled"/>: the official definition is "filled or canceled in the
    /// matching engine", covering both outcomes without distinguishing them. Reading it as a fill turns a stop
    /// cancelled after triggering into a close that never happened; how much actually filled has to be read
    /// from the order named by <c>actualOrderId</c>.
    /// </para>
    /// </remarks>
    public static ConditionalOrderStatus ParseAlgoStatus(string? value) => value switch
    {
        "NEW" => ConditionalOrderStatus.New,
        "TRIGGERING" => ConditionalOrderStatus.Triggering,
        "TRIGGERED" => ConditionalOrderStatus.Triggered,
        "FINISHED" => ConditionalOrderStatus.Finished,
        "CANCELED" => ConditionalOrderStatus.Canceled,
        "EXPIRED" => ConditionalOrderStatus.Expired,
        "REJECTED" => ConditionalOrderStatus.Rejected,
        _ => ConditionalOrderStatus.Unspecified,
    };

    /// <summary>
    /// 把中立的條件單類型轉成幣安的 <c>type</c> 字串。
    /// Converts the neutral conditional order type into the Binance <c>type</c> string.
    /// </summary>
    /// <param name="type">條件單類型。The conditional order type.</param>
    /// <returns>幣安的字面值。The Binance literal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> 未定義時擲出。Thrown when <paramref name="type"/> is undefined.
    /// </exception>
    public static string ToBinance(ConditionalOrderType type) => type switch
    {
        ConditionalOrderType.StopMarket => "STOP_MARKET",
        ConditionalOrderType.StopLimit => "STOP",
        ConditionalOrderType.TakeProfitMarket => "TAKE_PROFIT_MARKET",
        ConditionalOrderType.TakeProfitLimit => "TAKE_PROFIT",
        ConditionalOrderType.TrailingStopMarket => "TRAILING_STOP_MARKET",
        _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "未定義的條件單類型。Undefined conditional order type."),
    };
}
