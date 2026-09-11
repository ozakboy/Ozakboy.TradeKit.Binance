using System.Globalization;
using System.Text.Json;

namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 判讀幣安使用者資料串流的訊息,把事件轉成抽象層的型別。
/// Reads the frames of the Binance user data stream and turns the events into the abstraction's types.
/// </summary>
/// <remarks>
/// <para>
/// <b>沒有外層包裝。</b> 使用者資料串流走的是單一串流格式 <c>{WebSocketBaseUri}/ws/{listenKey}</c>,
/// 事件物件就是最外層,不像組合串流那樣包在 <c>{"stream":…,"data":…}</c> 裡面。
/// 照著行情那一側拆外層,會在正常訊息上找不到 <c>stream</c> 欄位而原樣往下走 —— 剛好也能動,
/// 但那是碰巧,不是設計。
/// <b>There is no envelope.</b> The user data stream uses the single-stream form
/// <c>{WebSocketBaseUri}/ws/{listenKey}</c>, so the event object is the outermost one rather than being wrapped
/// in <c>{"stream":…,"data":…}</c> the way a combined stream is. Copying the market side's unwrapping would find
/// no <c>stream</c> field and pass the object through unchanged — which happens to work, but by luck rather
/// than design.
/// </para>
/// <para>
/// <b>訊息原文絕不進任何錯誤。</b> <c>listenKeyExpired</c> 事件本體帶著 listenKey,
/// 而 listenKey 是能連上該帳戶私有資料的憑證。行情那一側把讀不懂的訊息截一段放進診斷資料,
/// 在這裡是不能做的事;因此本型別的失敗只說「哪個欄位讀不出來」,不說「收到了什麼」。
/// <b>Raw frame text never reaches an error.</b> The <c>listenKeyExpired</c> frame carries the listenKey, a
/// credential that reaches the account's private data. Truncating an unreadable frame into diagnostic data, as
/// the market side does, is not available here: a failure from this type says which field could not be read and
/// never what arrived.
/// </para>
/// <para>
/// <b><c>ORDER_TRADE_UPDATE</c> 的欄位對映</b>(欄位名取自官方 USDⓈ-M Futures User Data Streams 文件):
/// <b>The <c>ORDER_TRADE_UPDATE</c> field mapping</b>, with the names taken from the official USDⓈ-M Futures
/// User Data Streams documentation:
/// </para>
/// <list type="table">
/// <listheader>
/// <term>幣安欄位 / Binance field</term>
/// <description>對映到 / Mapped to</description>
/// </listheader>
/// <item><term><c>o.s</c></term><description><see cref="Order.Symbol"/> 與 <see cref="Trade.Symbol"/></description></item>
/// <item><term><c>o.c</c></term><description><see cref="Order.ClientOrderId"/></description></item>
/// <item><term><c>o.i</c></term><description><see cref="Order.ExchangeOrderId"/>(數值,中立模型用字串裝)</description></item>
/// <item><term><c>o.S</c></term><description><see cref="Order.Side"/></description></item>
/// <item><term><c>o.ot</c>(退回 <c>o.o</c>)</term><description><see cref="Order.OrderType"/>,取「當初送出的類型」</description></item>
/// <item><term><c>o.X</c></term><description><see cref="Order.Status"/>(委託狀態,<b>不是</b> <c>o.x</c> 的執行類型)</description></item>
/// <item><term><c>o.ps</c></term><description><see cref="Order.PositionSide"/></description></item>
/// <item><term><c>o.f</c></term><description><see cref="Order.TimeInForce"/></description></item>
/// <item><term><c>o.q</c></term><description><see cref="Order.Quantity"/>(原始委託量)</description></item>
/// <item><term><c>o.z</c></term><description><see cref="Order.FilledQuantity"/>(累計成交量)</description></item>
/// <item><term><c>o.ap</c></term><description><see cref="Order.AverageFillPrice"/></description></item>
/// <item><term><c>o.p</c>、<c>o.sp</c></term><description><see cref="Order.Price"/>、<see cref="Order.StopPrice"/>(0 視為「沒有這個價」)</description></item>
/// <item><term><c>o.R</c>、<c>o.cp</c></term><description><see cref="Order.ReduceOnly"/>、<see cref="Order.ClosePosition"/></description></item>
/// <item><term><c>o.T</c></term><description><see cref="Order.UpdatedAt"/> 與 <see cref="Trade.ExecutedAt"/></description></item>
/// <item><term><c>o.l</c>、<c>o.L</c></term><description><see cref="Trade.Quantity"/>、<see cref="Trade.Price"/>(本次成交量價)</description></item>
/// <item><term><c>o.t</c></term><description><see cref="Trade.TradeId"/></description></item>
/// <item><term><c>o.n</c>、<c>o.N</c></term><description><see cref="Trade.Fee"/>、<see cref="Trade.FeeAsset"/></description></item>
/// <item><term><c>o.rp</c>、<c>o.m</c></term><description><see cref="Trade.RealizedPnl"/>、<see cref="Trade.IsMaker"/></description></item>
/// <item><term><c>o.b</c>、<c>o.a</c>、<c>o.wt</c>、<c>o.AP</c>、<c>o.cr</c>、<c>o.pP</c>、<c>o.si</c>、<c>o.ss</c></term><description>掛單金額、觸發價種類、啟動價、回撤比例與三個文件未載明的欄位,抽象層沒有對應欄位,不對映。</description></item>
/// </list>
/// <para>
/// <b><c>o.X</c> 對不上已知狀態時整則判失敗,不放行。</b> 與 <c>BinanceResponseReader</c> 的理由相同:
/// <see cref="OrderStatus.Unspecified"/> 的 <c>IsOpen</c> 與 <c>IsFinal</c> 同時為 <see langword="false"/>,
/// 一張既沒結束也沒在簿上的單會讓部位追蹤永遠等不到終態,那比一筆說得出原因的失敗難查得多。
/// <b>An <c>o.X</c> that maps to no known status fails the frame.</b> The reason is the one in
/// <c>BinanceResponseReader</c>: <see cref="OrderStatus.Unspecified"/> reports both <c>IsOpen</c> and
/// <c>IsFinal</c> as <see langword="false"/>, and an order that is neither live nor finished leaves position
/// tracking waiting for an outcome that never arrives.
/// </para>
/// <para>
/// <b><see cref="Order.FilledNotional"/> 由 <c>o.z</c> 乘 <c>o.ap</c> 算出來。</b> 串流事件沒有 REST 那邊的
/// <c>cumQuote</c>,而均價的定義就是累計成交額除以累計成交量,所以乘回去就是累計成交額。
/// 填零會讓「已成交但金額為零」的委託流到上層,那個數字看起來完全合理。
/// <b><see cref="Order.FilledNotional"/> is computed as <c>o.z</c> times <c>o.ap</c>.</b> The stream event has
/// no <c>cumQuote</c> as REST does, and the average price is by definition the cumulative quote divided by the
/// cumulative quantity, so multiplying restores it. Leaving it at zero would publish a filled order with zero
/// notional, and that number looks entirely reasonable.
/// </para>
/// <para>
/// <b><see cref="Order.CreatedAt"/> 與 <see cref="Order.UpdatedAt"/> 同值。</b> 這個事件不帶委託建立時間;
/// 拿 <c>o.T</c> 當兩者是誠實的做法,填 <c>default</c> 會得到西元 0001 年,在時間軸上排序時特別容易誤導。
/// <b><see cref="Order.CreatedAt"/> equals <see cref="Order.UpdatedAt"/>.</b> The event carries no creation
/// time; using <c>o.T</c> for both is the honest reading, whereas a default would produce a year-0001 timestamp
/// that misleads anything sorting by time.
/// </para>
/// <para>
/// <b><c>ACCOUNT_UPDATE</c> 與 <c>MARGIN_CALL</c> 的欄位比抽象層少,缺的欄位<u>就是缺</u>。</b>
/// 具體缺哪些、為什麼不猜,見 <see cref="ReadAccountUpdate"/> 與 <see cref="ReadMarginCall"/> 的說明。
/// <b>The <c>ACCOUNT_UPDATE</c> and <c>MARGIN_CALL</c> payloads carry fewer fields than the abstraction has, and
/// what is missing stays missing.</b> Which fields, and why nothing is guessed, is on
/// <see cref="ReadAccountUpdate"/> and <see cref="ReadMarginCall"/>.
/// </para>
/// </remarks>
internal static class BinanceUserDataReader
{
    private const string EventTypeField = "e";

    private const string EventTimeField = "E";

    private const string OrderField = "o";

    private const string AccountField = "a";

    private const string SymbolField = "s";

    private const string ClientOrderIdField = "c";

    private const string OrderIdField = "i";

    private const string SideField = "S";

    private const string OrderTypeField = "o";

    private const string OriginalOrderTypeField = "ot";

    private const string OrderStatusField = "X";

    private const string ExecutionTypeField = "x";

    private const string PositionSideField = "ps";

    private const string TimeInForceField = "f";

    private const string OriginalQuantityField = "q";

    private const string FilledQuantityField = "z";

    private const string AverageFillPriceField = "ap";

    private const string PriceField = "p";

    private const string StopPriceField = "sp";

    private const string ReduceOnlyField = "R";

    private const string ClosePositionField = "cp";

    private const string OrderTradeTimeField = "T";

    private const string LastFilledQuantityField = "l";

    private const string LastFilledPriceField = "L";

    private const string TradeIdField = "t";

    private const string CommissionField = "n";

    private const string CommissionAssetField = "N";

    private const string RealizedPnlField = "rp";

    private const string IsMakerField = "m";

    private const string UpdateReasonField = "m";

    private const string BalancesField = "B";

    private const string PositionsField = "P";

    private const string MarginCallPositionsField = "p";

    private const string AssetField = "a";

    private const string WalletBalanceField = "wb";

    private const string CrossWalletBalanceField = "cw";

    private const string PositionAmountField = "pa";

    private const string EntryPriceField = "ep";

    private const string UnrealizedPnlField = "up";

    private const string MarginTypeField = "mt";

    private const string IsolatedWalletField = "iw";

    private const string MarkPriceField = "mp";

    /// <summary>
    /// 有成交時 <c>o.x</c> 會是這個值。
    /// The value <c>o.x</c> carries when the event includes a fill.
    /// </summary>
    private const string TradeExecutionType = "TRADE";

    /// <summary>
    /// 幣安在<b>這條串流</b>上表示全倉的字面值。
    /// The literal Binance uses for cross margin <b>on this stream</b>.
    /// </summary>
    /// <remarks>
    /// 同一個概念在不同地方的拼法不一樣:下單參數要 <c>CROSSED</c>,持倉查詢回小寫的 <c>cross</c>,
    /// <c>ACCOUNT_UPDATE</c> 回小寫的 <c>cross</c>,<c>MARGIN_CALL</c> 回大寫的 <c>CROSSED</c>。
    /// 所以這裡用不分大小寫的比對,而且兩種拼法都收 —— 只認其中一種的下場不是解析失敗,
    /// 而是把全倉部位讀成逐倉,風控對保證金的判斷就整個偏掉。
    /// The same concept is spelled differently in different places: an order parameter wants <c>CROSSED</c>, the
    /// position query answers a lower-case <c>cross</c>, <c>ACCOUNT_UPDATE</c> answers <c>cross</c>, and
    /// <c>MARGIN_CALL</c> answers <c>CROSSED</c>. Both spellings are therefore accepted, case-insensitively:
    /// recognising only one does not fail the parse, it reads a cross-margined position as isolated and skews
    /// every margin judgement built on it.
    /// </remarks>
    private const string CrossMarginType = "cross";

    /// <summary>
    /// <c>MARGIN_CALL</c> 使用的全倉字面值。
    /// The cross-margin literal used by <c>MARGIN_CALL</c>.
    /// </summary>
    private const string CrossedMarginType = "crossed";

    /// <summary>
    /// 判讀一則使用者資料串流訊息。
    /// Reads one user data frame.
    /// </summary>
    /// <param name="json">訊息原文。The frame text.</param>
    /// <returns>
    /// 判讀結果,或說明哪個欄位讀不出來的失敗(訊息原文不會出現在失敗裡)。
    /// The outcome, or a failure naming the unreadable field; the raw frame never appears in it.
    /// </returns>
    public static Result<BinanceUserDataEvent> Read(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 連 JsonException.Message 都不轉述:它會引用出錯位置附近的字元,而這條串流的訊息可能含有憑證。
            // Not even JsonException.Message is relayed: it quotes characters around the failure point, and
            // frames on this stream can carry the credential.
            return BinanceErrors.MalformedResponse(
                $"{BinanceUserDataPaths.Context} 收到的訊息不是合法的 JSON;內容不列出,因為這條串流的訊息可能含有串流憑證。The frame received on the {BinanceUserDataPaths.Context} is not valid JSON; the content is not shown because frames on this stream can carry the stream credential.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return BinanceErrors.MalformedResponse(
                    $"{BinanceUserDataPaths.Context} 收到的訊息不是 JSON 物件。The frame received on the {BinanceUserDataPaths.Context} is not a JSON object.");
            }

            if (!BinanceJson.TryGetString(root, EventTypeField, out var eventType))
            {
                return BinanceErrors.MissingField(EventTypeField, BinanceUserDataPaths.Context);
            }

            if (!BinanceJson.TryGetTimestamp(root, EventTimeField, out var eventTime))
            {
                return BinanceErrors.MissingField(EventTimeField, BinanceUserDataPaths.Context);
            }

            return eventType switch
            {
                BinanceUserDataPaths.OrderTradeUpdateEvent => ReadOrderTradeUpdate(root, eventTime),
                BinanceUserDataPaths.AccountUpdateEvent => ReadAccountUpdate(root, eventTime),
                BinanceUserDataPaths.MarginCallEvent => ReadMarginCall(root, eventTime),
                BinanceUserDataPaths.ListenKeyExpiredEvent =>
                    Result.Success(BinanceUserDataEvent.FromListenKeyExpired(eventTime)),
                _ => Result.Success(BinanceUserDataEvent.FromUnknown(eventTime)),
            };
        }
    }

    private static Result<BinanceUserDataEvent> ReadOrderTradeUpdate(JsonElement root, DateTimeOffset eventTime)
    {
        if (!root.TryGetProperty(OrderField, out var order) || order.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MissingField(OrderField, BinanceUserDataPaths.Context);
        }

        if (!BinanceJson.TryGetString(order, SymbolField, out var symbol))
        {
            return BinanceErrors.MissingField(SymbolField, BinanceUserDataPaths.Context);
        }

        if (!BinanceJson.TryGetString(order, ClientOrderIdField, out var clientOrderId))
        {
            return WithSymbol(BinanceErrors.MissingField(ClientOrderIdField, symbol), symbol);
        }

        var statusText = BinanceJson.TryGetString(order, OrderStatusField, out var readStatus) ? readStatus : null;
        var status = BinanceOrderMapper.ParseOrderStatus(statusText);

        if (status == OrderStatus.Unspecified)
        {
            return WithSymbol(
                BinanceErrors.MalformedResponse(
                        $"{symbol} 的委託狀態「{statusText}」無法對映到任何已知狀態。The order status \"{statusText}\" on {symbol} maps to no known state.")
                    .WithData(BinanceErrorDataKeys.Field, OrderStatusField),
                symbol);
        }

        var sideText = BinanceJson.TryGetString(order, SideField, out var readSide) ? readSide : null;
        var side = BinanceOrderMapper.ParseOrderSide(sideText);

        if (side == OrderSide.Unspecified)
        {
            // 方向對不上不放行。方向是「這張單會開出什麼部位」的全部資訊,猜錯就是反向部位。
            // An unmapped side is refused: the side is the whole answer to which position this order creates,
            // and guessing wrong is an inverted position.
            return WithSymbol(
                BinanceErrors.MalformedResponse(
                        $"{symbol} 的買賣方向「{sideText}」無法對映。The order side \"{sideText}\" on {symbol} maps to nothing.")
                    .WithData(BinanceErrorDataKeys.Field, SideField),
                symbol);
        }

        if (!BinanceJson.TryGetDecimal(order, OriginalQuantityField, out var quantity))
        {
            return WithSymbol(BinanceErrors.MissingField(OriginalQuantityField, symbol), symbol);
        }

        var updatedAt = BinanceJson.TryGetTimestamp(order, OrderTradeTimeField, out var tradeTime)
            ? tradeTime
            : eventTime;

        var filledQuantity = BinanceJson.TryGetDecimal(order, FilledQuantityField, out var filled) ? filled : 0m;
        var averageFillPrice = BinanceJson.TryGetDecimal(order, AverageFillPriceField, out var average)
            ? average
            : 0m;

        var mapped = new Order
        {
            Symbol = symbol,
            ClientOrderId = clientOrderId,

            // 交易所訂單編號是數值,中立模型用字串裝 —— 別的交易所的訂單編號不一定是數字。
            // The id is numeric here while the neutral model stores a string, because other exchanges do not
            // necessarily use numbers.
            ExchangeOrderId = BinanceJson.TryGetInt64(order, OrderIdField, out var exchangeOrderId)
                ? exchangeOrderId.ToString(CultureInfo.InvariantCulture)
                : null,
            Side = side,

            // ot 是「當初送出的類型」。條件單觸發之後 o 會變成實際掛出去的那一種,
            // 拿它回報等於把使用者下的 STOP_MARKET 說成 MARKET。
            // ot is the type as submitted. Once a conditional order triggers, o becomes whatever was actually
            // placed, and reporting that turns the caller's STOP_MARKET into a MARKET.
            OrderType = BinanceOrderMapper.ParseOrderType(ReadOrderTypeText(order)),
            Status = status,
            PositionSide = BinanceOrderMapper.ParsePositionSide(
                BinanceJson.TryGetString(order, PositionSideField, out var positionSide) ? positionSide : null),
            TimeInForce = BinanceOrderMapper.ParseTimeInForce(
                BinanceJson.TryGetString(order, TimeInForceField, out var timeInForce) ? timeInForce : null),
            Quantity = quantity,
            FilledQuantity = filledQuantity,
            AverageFillPrice = averageFillPrice,

            // 幣安對「沒有這個價格」的表示是 0,不是省略欄位。市價單的 p 就是 "0",
            // 照抄下去會讓上層看到一張「限價零元」的委託。
            // Binance writes "no such price" as 0 rather than omitting the field: a market order's p is "0",
            // and copying that through shows the caller an order priced at zero.
            Price = ReadOptionalPrice(order, PriceField),
            StopPrice = ReadOptionalPrice(order, StopPriceField),
            ReduceOnly = BinanceJson.TryGetBoolean(order, ReduceOnlyField, out var reduceOnly) && reduceOnly,
            ClosePosition = BinanceJson.TryGetBoolean(order, ClosePositionField, out var closePosition)
                && closePosition,

            // 累計成交額 = 累計成交量 × 均價,理由見類別說明。
            // The cumulative quote is the filled quantity times the average price; see the type remarks.
            FilledNotional = filledQuantity * averageFillPrice,

            // 這個事件不帶委託建立時間,兩個時間只好同值,理由見類別說明。
            // The event carries no creation time, so both timestamps share one value; see the type remarks.
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };

        var lastFilledQuantity = BinanceJson.TryGetDecimal(order, LastFilledQuantityField, out var lastQuantity)
            ? lastQuantity
            : 0m;

        var executionType = BinanceJson.TryGetString(order, ExecutionTypeField, out var readExecution)
            ? readExecution
            : null;

        // 沒有成交就只送委託更新。撤單、掛單成立、條件單重算都會走到這裡。
        // Without a fill only the order update goes out: cancellations, acceptances, and conditional
        // recalculations all land here.
        if (lastFilledQuantity <= 0m
            && !string.Equals(executionType, TradeExecutionType, StringComparison.Ordinal))
        {
            return Result.Success(BinanceUserDataEvent.FromOrder(mapped, null, eventTime));
        }

        var fill = ReadFill(order, mapped, symbol, updatedAt, lastFilledQuantity);

        // 成交讀不出來時整則判失敗,不退而求其次只送委託更新。少送一筆成交而不報錯,
        // 本地的已實現損益與手續費就會永遠少那一筆,而帳面上的數字依然合理。
        // A fill that cannot be read fails the whole frame rather than degrading to an order-only update:
        // silently dropping one leaves local realised PnL and fees permanently short by it, with the books
        // still looking plausible.
        return fill.TryGetValue(out var trade)
            ? Result.Success(BinanceUserDataEvent.FromOrder(mapped, trade, eventTime))
            : fill.ToFailure<BinanceUserDataEvent>();
    }

    private static Result<Trade> ReadFill(
        JsonElement order,
        Order mapped,
        string symbol,
        DateTimeOffset executedAt,
        decimal lastFilledQuantity)
    {
        if (!BinanceJson.TryGetInt64(order, TradeIdField, out var tradeId))
        {
            return WithSymbol(BinanceErrors.MissingField(TradeIdField, symbol), symbol);
        }

        if (!BinanceJson.TryGetDecimal(order, LastFilledPriceField, out var price))
        {
            return WithSymbol(BinanceErrors.MissingField(LastFilledPriceField, symbol), symbol);
        }

        return new Trade
        {
            Symbol = symbol,
            TradeId = tradeId.ToString(CultureInfo.InvariantCulture),
            ExchangeOrderId = mapped.ExchangeOrderId,
            ClientOrderId = mapped.ClientOrderId,
            Side = mapped.Side,
            PositionSide = mapped.PositionSide,
            Price = price,
            Quantity = lastFilledQuantity,
            Fee = BinanceJson.TryGetDecimal(order, CommissionField, out var fee) ? fee : 0m,

            // 手續費幣別缺席時留空字串而不是猜成計價幣。合約帳戶可以用別的資產抵扣手續費,
            // 猜錯的結果是把一筆 BNB 的費用當成 USDT 從損益裡扣掉。
            // A missing commission asset stays empty rather than being guessed as the quote currency: a
            // derivatives account can pay fees in another asset, and guessing subtracts a BNB fee from PnL as
            // though it were USDT.
            FeeAsset = BinanceJson.TryGetString(order, CommissionAssetField, out var feeAsset)
                ? feeAsset
                : string.Empty,
            RealizedPnl = BinanceJson.TryGetDecimal(order, RealizedPnlField, out var realized) ? realized : 0m,
            IsMaker = BinanceJson.TryGetBoolean(order, IsMakerField, out var isMaker) && isMaker,
            ExecutedAt = executedAt,
        };
    }

    /// <summary>
    /// 判讀帳戶增量事件。
    /// Reads an account delta event.
    /// </summary>
    /// <param name="root">事件物件。The event object.</param>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果。The outcome.</returns>
    /// <remarks>
    /// <para>
    /// <b><c>a.B</c> 與 <c>a.P</c> 都是增量,只含這次<u>有變動</u>的項目。</b> 原樣填進
    /// <see cref="AccountUpdate.Balances"/> 與 <see cref="AccountUpdate.Positions"/>,不補齊、不當成快照。
    /// 把它當快照用,等於把「沒被提到的餘額」讀成零 —— 一個只動到部位的更新,會讓 USDT 帳戶看起來被清空。
    /// <b>Both <c>a.B</c> and <c>a.P</c> are deltas carrying only what changed.</b> They go into
    /// <see cref="AccountUpdate.Balances"/> and <see cref="AccountUpdate.Positions"/> as they are: nothing is
    /// filled in and nothing is treated as a snapshot. Reading them as one turns "not mentioned" into "zero",
    /// and an update that only moved a position makes a USDT account look emptied.
    /// </para>
    /// <para>
    /// <b>這個事件不帶的欄位:</b><see cref="Balance.AvailableBalance"/>、<see cref="Balance.UnrealizedPnl"/>
    /// (事件只有錢包餘額 <c>wb</c> 與全倉錢包餘額 <c>cw</c>)、<see cref="Position.MarkPrice"/>、
    /// <see cref="Position.Leverage"/>、<see cref="Position.LiquidationPrice"/>。它們會停在型別的預設值。
    /// <b>特別注意 <see cref="Position.MarkPrice"/> 為零的後果:</b><see cref="Position.Notional"/> 是用它算的,
    /// 所以從這個事件建出來的部位,名目價值一律是零,也就是「看起來沒有風險」。
    /// 風控要判斷曝險請用 <c>/fapi/v2/positionRisk</c> 或行情的標記價串流,不要用這裡的部位。
    /// <b>Fields this event does not carry:</b> <see cref="Balance.AvailableBalance"/>,
    /// <see cref="Balance.UnrealizedPnl"/> — the event has only the wallet balance <c>wb</c> and the cross
    /// wallet balance <c>cw</c> — plus <see cref="Position.MarkPrice"/>, <see cref="Position.Leverage"/>, and
    /// <see cref="Position.LiquidationPrice"/>. They stay at their type defaults. <b>Mind what a zero
    /// <see cref="Position.MarkPrice"/> does:</b> <see cref="Position.Notional"/> is derived from it, so a
    /// position built from this event always has zero notional, which reads as no risk at all. Exposure belongs
    /// to <c>/fapi/v2/positionRisk</c> or the mark price stream, not to the positions here.
    /// </para>
    /// </remarks>
    private static Result<BinanceUserDataEvent> ReadAccountUpdate(JsonElement root, DateTimeOffset eventTime)
    {
        if (!root.TryGetProperty(AccountField, out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MissingField(AccountField, BinanceUserDataPaths.Context);
        }

        var rawReason = BinanceJson.TryGetString(account, UpdateReasonField, out var reason) ? reason : null;

        var balances = ReadBalances(account);

        if (!balances.TryGetValue(out var readBalances))
        {
            return balances.ToFailure<BinanceUserDataEvent>();
        }

        var positions = ReadPositions(account, PositionsField, eventTime, requireMarkPrice: false);

        if (!positions.TryGetValue(out var readPositions))
        {
            return positions.ToFailure<BinanceUserDataEvent>();
        }

        return Result.Success(BinanceUserDataEvent.FromAccount(
            new AccountUpdate
            {
                Reason = ParseReason(rawReason),
                RawReason = rawReason,
                Balances = readBalances,
                Positions = readPositions,
                Timestamp = eventTime,
            },
            eventTime));
    }

    /// <summary>
    /// 判讀保證金追繳警告。
    /// Reads a margin call.
    /// </summary>
    /// <param name="root">事件物件。The event object.</param>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果。The outcome.</returns>
    /// <remarks>
    /// 與 <c>ACCOUNT_UPDATE</c> 不同,這個事件<b>有</b>標記價(<c>mp</c>)—— 交易所要警告的正是
    /// 「以目前標記價來看,這些部位快撐不住了」,少了它這則警告就沒有意義,因此標記價缺席時判失敗。
    /// 至於 <see cref="Position.EntryPrice"/> 與 <see cref="Position.Leverage"/> 這個事件沒有,
    /// 停在型別預設值;要用到它們請另外查持倉。
    /// Unlike <c>ACCOUNT_UPDATE</c>, this event <b>does</b> carry the mark price in <c>mp</c> — the warning is
    /// precisely that these positions are close to failing at the current mark, and without it the warning
    /// means nothing, so a missing mark price fails the frame. <see cref="Position.EntryPrice"/> and
    /// <see cref="Position.Leverage"/> are absent and stay at their defaults; code needing them has to query
    /// the positions separately.
    /// </remarks>
    private static Result<BinanceUserDataEvent> ReadMarginCall(JsonElement root, DateTimeOffset eventTime)
    {
        var positions = ReadPositions(root, MarginCallPositionsField, eventTime, requireMarkPrice: true);

        if (!positions.TryGetValue(out var readPositions))
        {
            return positions.ToFailure<BinanceUserDataEvent>();
        }

        return Result.Success(BinanceUserDataEvent.FromMarginCall(
            new MarginCall
            {
                // 交易所沒給就是 null,不是零。零會讓「不知道全倉餘額」看起來像「全倉餘額剛好是零」,
                // 而後者代表帳戶已經空了。
                // Absent means null rather than zero: zero would make "the cross balance is unknown" look like
                // "the cross balance happens to be zero", and the latter says the account is empty.
                CrossWalletBalance = BinanceJson.TryGetDecimal(root, CrossWalletBalanceField, out var crossWallet)
                    ? crossWallet
                    : null,
                Positions = readPositions,
                Timestamp = eventTime,
            },
            eventTime));
    }

    private static Result<IReadOnlyList<Balance>> ReadBalances(JsonElement account)
    {
        if (!account.TryGetProperty(BalancesField, out var array))
        {
            // 這次沒有任何餘額變動,欄位就不會出現。空清單是正確的答案,不是失敗。
            // The field is simply absent when no balance changed. An empty list is the right answer, not a
            // failure.
            return Result.Success<IReadOnlyList<Balance>>([]);
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return BinanceErrors.MissingField(BalancesField, BinanceUserDataPaths.Context);
        }

        var balances = new List<Balance>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return BinanceErrors.MalformedResponse(
                    $"{BinanceUserDataPaths.Context} 的餘額陣列元素不是 JSON 物件。An element of the balance array on the {BinanceUserDataPaths.Context} is not a JSON object.");
            }

            if (!BinanceJson.TryGetString(element, AssetField, out var asset))
            {
                return BinanceErrors.MissingField(AssetField, BinanceUserDataPaths.Context);
            }

            if (!BinanceJson.TryGetDecimal(element, WalletBalanceField, out var walletBalance))
            {
                return BinanceErrors.MissingField(WalletBalanceField, asset);
            }

            balances.Add(new Balance
            {
                Asset = asset,
                WalletBalance = walletBalance,

                // 可用餘額與未實現損益這個事件都沒有,停在零,理由見 ReadAccountUpdate 的說明。
                // The available balance and the unrealised PnL are not in this event and stay at zero; see the
                // remarks on ReadAccountUpdate.
            });
        }

        return Result.Success<IReadOnlyList<Balance>>(balances);
    }

    private static Result<IReadOnlyList<Position>> ReadPositions(
        JsonElement container,
        string propertyName,
        DateTimeOffset eventTime,
        bool requireMarkPrice)
    {
        if (!container.TryGetProperty(propertyName, out var array))
        {
            return requireMarkPrice
                ? BinanceErrors.MissingField(propertyName, BinanceUserDataPaths.Context)
                : Result.Success<IReadOnlyList<Position>>([]);
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return BinanceErrors.MissingField(propertyName, BinanceUserDataPaths.Context);
        }

        var positions = new List<Position>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            var position = ReadPosition(element, eventTime, requireMarkPrice);

            if (!position.TryGetValue(out var value))
            {
                return position.ToFailure<IReadOnlyList<Position>>();
            }

            positions.Add(value);
        }

        return Result.Success<IReadOnlyList<Position>>(positions);
    }

    private static Result<Position> ReadPosition(
        JsonElement element,
        DateTimeOffset eventTime,
        bool requireMarkPrice)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                $"{BinanceUserDataPaths.Context} 的部位陣列元素不是 JSON 物件。An element of the position array on the {BinanceUserDataPaths.Context} is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, SymbolField, out var symbol))
        {
            return BinanceErrors.MissingField(SymbolField, BinanceUserDataPaths.Context);
        }

        if (!BinanceJson.TryGetDecimal(element, PositionAmountField, out var quantity))
        {
            return WithSymbol(BinanceErrors.MissingField(PositionAmountField, symbol), symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, UnrealizedPnlField, out var unrealizedPnl))
        {
            return WithSymbol(BinanceErrors.MissingField(UnrealizedPnlField, symbol), symbol);
        }

        var markPrice = 0m;

        if (requireMarkPrice && !BinanceJson.TryGetDecimal(element, MarkPriceField, out markPrice))
        {
            return WithSymbol(BinanceErrors.MissingField(MarkPriceField, symbol), symbol);
        }

        return new Position
        {
            Symbol = symbol,
            Quantity = quantity,
            Side = BinanceOrderMapper.ParsePositionSide(
                BinanceJson.TryGetString(element, PositionSideField, out var positionSide) ? positionSide : null),

            // 進場價只有 ACCOUNT_UPDATE 帶,MARGIN_CALL 沒有。缺席時停在零,不猜。
            // The entry price comes with ACCOUNT_UPDATE and not with MARGIN_CALL; when absent it stays at zero
            // rather than being guessed.
            EntryPrice = BinanceJson.TryGetDecimal(element, EntryPriceField, out var entryPrice) ? entryPrice : 0m,
            MarkPrice = markPrice,
            UnrealizedPnl = unrealizedPnl,
            MarginMode = ParseMarginMode(
                BinanceJson.TryGetString(element, MarginTypeField, out var marginType) ? marginType : null),
            Margin = BinanceJson.TryGetDecimal(element, IsolatedWalletField, out var isolatedWallet)
                ? isolatedWallet
                : 0m,
            UpdatedAt = eventTime,
        };
    }

    /// <summary>
    /// 把 <c>a.m</c> 的原因代碼對映成中立的原因。
    /// Maps the <c>a.m</c> reason code onto the neutral reason.
    /// </summary>
    /// <param name="value">交易所給的代碼。The code the exchange gave.</param>
    /// <returns>對映得到的原因;對不上時為 <see cref="AccountUpdateReason.Unknown"/>。The mapped reason.</returns>
    /// <remarks>
    /// 對不上一律落在 <see cref="AccountUpdateReason.Unknown"/> 而不是判失敗:原始代碼會原樣留在
    /// <see cref="AccountUpdate.RawReason"/>,所以交易所日後新增代碼不會讓整則帳戶變動被丟掉 ——
    /// 而丟掉一則帳戶變動,代價是本地權益從此與交易所差一截。
    /// An unmapped code lands on <see cref="AccountUpdateReason.Unknown"/> rather than failing: the original is
    /// kept verbatim in <see cref="AccountUpdate.RawReason"/>, so a code added later by the exchange does not
    /// discard the whole account change — and discarding one leaves local equity permanently out of step.
    /// </remarks>
    private static AccountUpdateReason ParseReason(string? value) => value switch
    {
        "ORDER" => AccountUpdateReason.Order,
        "FUNDING_FEE" => AccountUpdateReason.FundingFee,
        "DEPOSIT" => AccountUpdateReason.Deposit,
        "WITHDRAW" => AccountUpdateReason.Withdrawal,
        "MARGIN_TRANSFER" => AccountUpdateReason.MarginTransfer,
        "MARGIN_TYPE_CHANGE" => AccountUpdateReason.MarginModeChange,
        "ADJUSTMENT" => AccountUpdateReason.Adjustment,
        "INSURANCE_CLEAR" => AccountUpdateReason.Liquidation,
        "ASSET_TRANSFER" or "AUTO_EXCHANGE" => AccountUpdateReason.AssetConversion,
        _ => AccountUpdateReason.Unknown,
    };

    private static MarginMode ParseMarginMode(string? value) =>
        string.Equals(value, CrossMarginType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, CrossedMarginType, StringComparison.OrdinalIgnoreCase)
            ? MarginMode.Cross
            : MarginMode.Isolated;

    private static string? ReadOrderTypeText(JsonElement order)
    {
        if (BinanceJson.TryGetString(order, OriginalOrderTypeField, out var originalType))
        {
            return originalType;
        }

        return BinanceJson.TryGetString(order, OrderTypeField, out var type) ? type : null;
    }

    private static decimal? ReadOptionalPrice(JsonElement element, string propertyName) =>
        BinanceJson.TryGetDecimal(element, propertyName, out var value) && value > 0m ? value : null;

    private static Error WithSymbol(Error error, string symbol) =>
        error.WithData(BinanceErrorDataKeys.Symbol, symbol);
}
