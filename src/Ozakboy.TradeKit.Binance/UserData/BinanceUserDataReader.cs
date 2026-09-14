using System.Globalization;
using System.Text.Json;

using Ozakboy.TradeKit.Binance.MarketData;

namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 判讀幣安使用者資料串流的訊息,把事件轉成抽象層的型別。
/// Reads the frames of the Binance user data stream and turns the events into the abstraction's types.
/// </summary>
/// <remarks>
/// <para>
/// <b>沒有外層包裝。</b> 使用者資料串流走的是單一串流格式
/// <c>{WebSocketBaseUri}/private/ws?listenKey=…&amp;events=…</c>(<c>…/ws</c> 而不是 <c>…/stream</c>),
/// 事件物件就是最外層,不像組合串流那樣包在 <c>{"stream":…,"data":…}</c> 裡面。
/// 照著行情那一側拆外層,會在正常訊息上找不到 <c>stream</c> 欄位而原樣往下走 —— 剛好也能動,
/// 但那是碰巧,不是設計。
/// <b>There is no envelope.</b> The user data stream uses the single-stream form
/// <c>{WebSocketBaseUri}/private/ws?listenKey=…&amp;events=…</c> — <c>…/ws</c> rather than <c>…/stream</c> — so the
/// event object is the outermost one rather than being wrapped in <c>{"stream":…,"data":…}</c> the way a combined
/// stream is. Copying the market side's unwrapping would find no <c>stream</c> field and pass the object through
/// unchanged — which happens to work, but by luck rather than design.
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
/// <b>指令回應判為忽略,而且內容一個字都不讀。</b> 這條連線上的心跳是 <c>LIST_SUBSCRIPTIONS</c>,
/// Testnet 實測它的回應在 <c>/private</c> 路由上是 <c>{"result":["&lt;listenKey&gt;@ACCOUNT_UPDATE",…],"id":N}</c>
/// (2026-09-12;0.1.0 撥的 <c>/ws/{listenKey}</c> 上則是 <c>{"result":["&lt;listenKey&gt;"],"id":N}</c>)——
/// <b>每一則心跳回應都帶著憑證本身</b>,預設設定下每 30 秒一則。判別條件是「有 <c>id</c>、沒有 <c>e</c>」:交易所的事件一律帶 <c>e</c>,
/// 指令回應一律帶 <c>id</c> 而不帶 <c>e</c>。判定之後 <c>result</c> 與 <c>error</c> 都不讀、不轉述;
/// 被拒的回應(<c>{"error":…,"id":N}</c>)一併忽略,因為心跳被拒的後果只是那一次沒有刷新閒置計時,
/// 閒置逾時會接手,而轉述它的內容就得先保證那段文字不含任何連線片段,這一點沒有辦法保證。
/// 沒有 <c>id</c> 也沒有 <c>e</c> 的物件仍判失敗(只說缺少 <c>e</c>):那既不是事件也不是回應,是協定變了。
/// <b>Command replies are ignored, and not one character of them is read.</b> The heartbeat on this connection
/// is <c>LIST_SUBSCRIPTIONS</c>, and on the testnet's <c>/private</c> route its reply measured as
/// <c>{"result":["&lt;listenKey&gt;@ACCOUNT_UPDATE",…],"id":N}</c> on 2026-09-12 — on the <c>/ws/{listenKey}</c>
/// that 0.1.0 dialled it was <c>{"result":["&lt;listenKey&gt;"],"id":N}</c> — so <b>every heartbeat reply carries
/// the credential itself</b>, one every 30 seconds under the defaults. The test is "has <c>id</c>, has no <c>e</c>": exchange events always carry
/// <c>e</c>, and command replies always carry <c>id</c> without <c>e</c>. Once so classified, neither
/// <c>result</c> nor <c>error</c> is read or relayed. A rejection (<c>{"error":…,"id":N}</c>) is ignored as well:
/// a rejected heartbeat merely fails to refresh the idle clock once, which the idle timeout then handles, whereas
/// relaying its content would first require proving the text carries no piece of the connection — which cannot
/// be proven. An object with neither <c>id</c> nor <c>e</c> still fails, naming only the missing <c>e</c>: it is
/// neither an event nor a reply, so the protocol has changed.
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
/// <b><c>ACCOUNT_UPDATE</c> 與 <c>MARGIN_CALL</c> 對映到只含事件欄位的增量型別。</b>
/// <see cref="PositionChange"/>、<see cref="BalanceChange"/>、<see cref="MarginCallPosition"/> 刻意沒有事件不帶的
/// 欄位(帳戶變動沒有標記價、名目價值、槓桿、可用餘額),所以這裡不必、也無從替它們填值。
/// 事件<b>可能</b>不帶的欄位對映成 <see langword="null"/>,不是零:零是一個說得出口的數字,
/// 「交易所沒說」不是。
/// <b><c>ACCOUNT_UPDATE</c> and <c>MARGIN_CALL</c> map onto delta types holding only what the events carry.</b>
/// <see cref="PositionChange"/>, <see cref="BalanceChange"/>, and <see cref="MarginCallPosition"/> deliberately
/// lack the fields these events do not deliver — an account change has no mark price, notional, leverage, or
/// available balance — so there is nothing to fill in and no way to. A field an event <b>may</b> omit maps to
/// <see langword="null"/> rather than zero: zero is a number one can state, and "the exchange did not say" is
/// not.
/// </para>
/// <list type="table">
/// <listheader>
/// <term>幣安欄位 / Binance field</term>
/// <description>對映到 / Mapped to</description>
/// </listheader>
/// <item><term><c>a.m</c></term><description><see cref="AccountUpdate.Reason"/> 與 <see cref="AccountUpdate.RawReason"/></description></item>
/// <item><term><c>a.B[].a</c>、<c>a.B[].wb</c></term><description><see cref="BalanceChange.Asset"/>、<see cref="BalanceChange.WalletBalance"/>(必填)</description></item>
/// <item><term><c>a.B[].cw</c></term><description><see cref="BalanceChange.CrossWalletBalance"/>(缺席為 <see langword="null"/>)</description></item>
/// <item><term><c>a.B[].bc</c></term><description><see cref="BalanceChange.NonTradingChange"/>(缺席為 <see langword="null"/>)</description></item>
/// <item><term><c>a.P[].s</c>、<c>a.P[].pa</c>、<c>a.P[].ep</c>、<c>a.P[].up</c></term><description><see cref="PositionChange.Symbol"/>、<see cref="PositionChange.Quantity"/>、<see cref="PositionChange.EntryPrice"/>、<see cref="PositionChange.UnrealizedPnl"/>(必填)</description></item>
/// <item><term><c>a.P[].ps</c>、<c>a.P[].mt</c></term><description><see cref="PositionChange.Side"/>、<see cref="PositionChange.MarginMode"/></description></item>
/// <item><term><c>a.P[].cr</c></term><description><see cref="PositionChange.AccumulatedRealizedPnl"/>(缺席為 <see langword="null"/>)</description></item>
/// <item><term><c>a.P[].iw</c></term><description><see cref="PositionChange.IsolatedMargin"/>(只有逐倉部位有值;全倉或缺席為 <see langword="null"/>)</description></item>
/// <item><term><c>a.P[].bep</c></term><description>損益兩平價,抽象層沒有對應欄位,不對映。</description></item>
/// <item><term><c>cw</c></term><description><see cref="MarginCall.CrossWalletBalance"/>(缺席為 <see langword="null"/>)</description></item>
/// <item><term><c>p[].s</c>、<c>p[].pa</c>、<c>p[].up</c></term><description><see cref="MarginCallPosition.Symbol"/>、<see cref="MarginCallPosition.Quantity"/>、<see cref="MarginCallPosition.UnrealizedPnl"/>(必填)</description></item>
/// <item><term><c>p[].mp</c></term><description><see cref="MarginCallPosition.MarkPrice"/>(必填,理由見 <see cref="ReadMarginCall"/>)</description></item>
/// <item><term><c>p[].ps</c>、<c>p[].mt</c></term><description><see cref="MarginCallPosition.Side"/>、<see cref="MarginCallPosition.MarginMode"/></description></item>
/// <item><term><c>p[].iw</c></term><description><see cref="MarginCallPosition.IsolatedMargin"/>(只有逐倉部位有值;全倉或缺席為 <see langword="null"/>)</description></item>
/// <item><term><c>p[].mm</c></term><description><see cref="MarginCallPosition.MaintenanceMargin"/>(缺席為 <see langword="null"/>)</description></item>
/// </list>
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

    // ── ALGO_UPDATE 專屬欄位 / fields specific to ALGO_UPDATE ──

    /// <summary>用戶端條件單編號。The client algo id.</summary>
    private const string ClientAlgoIdField = "caid";

    /// <summary>交易所條件單編號。The exchange algo id.</summary>
    private const string AlgoIdField = "aid";

    /// <summary>觸發價。The trigger price.</summary>
    private const string TriggerPriceField = "tp";

    /// <summary>觸發價種類(標記價或成交價)。The working type: mark price or contract price.</summary>
    private const string WorkingTypeField = "wt";

    /// <summary>
    /// 觸發後撮合引擎裡那張實際委託的編號。未觸發時是空字串。
    /// The id of the real order in the matching engine once triggered; an empty string before that.
    /// </summary>
    private const string ActualOrderIdField = "ai";

    /// <summary>觸發時間。未觸發時是 0。The trigger time; 0 before the trigger.</summary>
    private const string TriggerTimeField = "tt";

    /// <summary>
    /// 條件單被拒的原因。
    /// The reason a conditional order failed.
    /// </summary>
    /// <remarks>
    /// 這是 <c>CONDITIONAL_ORDER_TRIGGER_REJECT</c> 在 2025-12-15 棄用之後,拒絕原因唯一的去處。
    /// This is where rejection reasons went after <c>CONDITIONAL_ORDER_TRIGGER_REJECT</c> was retired on
    /// 2025-12-15, and the only place they appear.
    /// </remarks>
    private const string RejectReasonField = "rm";

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

    private const string BalanceChangeField = "bc";

    private const string PositionAmountField = "pa";

    private const string EntryPriceField = "ep";

    private const string UnrealizedPnlField = "up";

    private const string AccumulatedRealizedField = "cr";

    private const string MarginTypeField = "mt";

    private const string IsolatedWalletField = "iw";

    private const string MarkPriceField = "mp";

    private const string MaintenanceMarginField = "mm";

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

            // 心跳的回應:有 id、沒有 e。這一支必須排在任何欄位讀取之前,而且只看「有沒有」,
            // 不看值 —— 回應的 result 裡就是憑證,理由見類別說明。
            // A heartbeat reply: an id and no e. This branch has to come before any field is read, and it checks
            // presence only, never values — the reply's result is the credential; see the type remarks.
            if (!root.TryGetProperty(EventTypeField, out _)
                && root.TryGetProperty(BinanceStreamCommands.IdProperty, out _))
            {
                return Result.Success(BinanceUserDataEvent.FromCommandReply());
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
                BinanceUserDataPaths.AlgoUpdateEvent => ReadAlgoUpdate(root, eventTime),
                BinanceUserDataPaths.AccountUpdateEvent => ReadAccountUpdate(root, eventTime),
                BinanceUserDataPaths.MarginCallEvent => ReadMarginCall(root, eventTime),
                BinanceUserDataPaths.ListenKeyExpiredEvent =>
                    Result.Success(BinanceUserDataEvent.FromListenKeyExpired(eventTime)),
                _ => Result.Success(BinanceUserDataEvent.FromUnknown(eventTime)),
            };
        }
    }

    /// <summary>
    /// 判讀 <c>ALGO_UPDATE</c>(條件單狀態變化)。
    /// Reads an <c>ALGO_UPDATE</c>, the conditional order state change.
    /// </summary>
    /// <param name="root">事件物件。The event object.</param>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果,或失敗原因。The outcome, or the reason it failed.</returns>
    /// <remarks>
    /// <para>
    /// 欄位對映(來源:官方 USDⓈ-M Futures User Data Streams 文件,擷取日期 2026-09-14):
    /// <c>o.caid</c> 用戶端條件單編號、<c>o.aid</c> 交易所條件單編號、<c>o.o</c> 委託類型、
    /// <c>o.X</c> 條件單狀態、<c>o.ai</c> 觸發後的實際委託編號(未觸發時是空字串)、
    /// <c>o.tp</c> 觸發價、<c>o.p</c> 委託價、<c>o.wt</c> 觸發價種類、<c>o.tt</c> 觸發時間(未觸發時是 0)、
    /// <c>o.rm</c> 被拒原因。
    /// The field mapping, from the official USDⓈ-M Futures User Data Streams documentation retrieved
    /// 2026-09-14: <c>o.caid</c> client algo id, <c>o.aid</c> exchange algo id, <c>o.o</c> order type,
    /// <c>o.X</c> algo status, <c>o.ai</c> the real order id once triggered (an empty string before that),
    /// <c>o.tp</c> trigger price, <c>o.p</c> order price, <c>o.wt</c> working type, <c>o.tt</c> trigger time
    /// (0 before the trigger), and <c>o.rm</c> the rejection reason.
    /// </para>
    /// <para>
    /// <b>外層的 <c>o</c> 是物件,內層還有一個 <c>o</c> 是委託類型字串。</b>同名不同層,讀錯一層拿到的是
    /// 一個型別不符的元素而不是例外 —— 這正是為什麼這裡先取出 <c>o</c> 物件再從它身上讀欄位,
    /// 而不是在根物件上找。
    /// <b>The outer <c>o</c> is an object and the inner <c>o</c> is the order type string.</b> Same name, two
    /// levels; reading the wrong one yields an element of the wrong kind rather than an exception, which is why
    /// the <c>o</c> object is taken out first and every field read from it rather than from the root.
    /// </para>
    /// <para>
    /// <b>移動停損在觸發前會推<b>兩</b>則 <c>X=NEW</c>。</b>官方 change-log 2026-08-21 說明 <c>o.ia</c>
    /// (是否已啟動)從佔位欄位變成真的會動:先來一則 <c>ia:false</c>,啟動之後再來一則 <c>ia:true</c>。
    /// 消費端若要去重,鍵必須含 <c>ia</c>,只用「條件單編號 + 狀態」會把第二則吃掉。
    /// <b>A trailing stop pushes <b>two</b> <c>X=NEW</c> events before triggering.</b> The change-log of
    /// 2026-08-21 records <c>o.ia</c> — whether the order has activated — graduating from a placeholder to a
    /// live field: one event arrives with <c>ia:false</c> and another with <c>ia:true</c> once it activates.
    /// A consumer that de-duplicates must include <c>ia</c> in the key, or the second event is swallowed.
    /// </para>
    /// <para>
    /// <b>條件單被拒的原因只在這裡出現一次。</b>官方 change-log 2025-12-10 說明
    /// <c>CONDITIONAL_ORDER_TRIGGER_REJECT</c> 自 2025-12-15 起棄用,拒絕原因改放進這個事件的
    /// <c>o.rm</c>。事後再查那張條件單只會得到一個「已拒絕」,看不出為什麼。
    /// <b>The reason a conditional order was rejected appears exactly once, here.</b> The change-log of
    /// 2025-12-10 records <c>CONDITIONAL_ORDER_TRIGGER_REJECT</c> being retired on 2025-12-15, with rejection
    /// reasons moving into this event's <c>o.rm</c>. Looking the order up afterwards yields a bare "rejected".
    /// </para>
    /// </remarks>
    private static Result<BinanceUserDataEvent> ReadAlgoUpdate(JsonElement root, DateTimeOffset eventTime)
    {
        if (!root.TryGetProperty(OrderField, out var algo) || algo.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MissingField(OrderField, BinanceUserDataPaths.Context);
        }

        if (!BinanceJson.TryGetString(algo, SymbolField, out var symbol))
        {
            return BinanceErrors.MissingField(SymbolField, BinanceUserDataPaths.Context);
        }

        if (!BinanceJson.TryGetString(algo, ClientAlgoIdField, out var clientAlgoId))
        {
            return WithSymbol(BinanceErrors.MissingField(ClientAlgoIdField, symbol), symbol);
        }

        var statusText = BinanceJson.TryGetString(algo, OrderStatusField, out var readStatus) ? readStatus : null;
        var status = BinanceAlgoOrderMapper.ParseAlgoStatus(statusText);

        if (status == ConditionalOrderStatus.Unspecified)
        {
            return WithSymbol(
                BinanceErrors.MalformedResponse(
                        $"{symbol} 的條件單狀態「{statusText}」無法對映到任何已知狀態。The algo status \"{statusText}\" on {symbol} maps to no known state.")
                    .WithData(BinanceErrorDataKeys.Field, OrderStatusField),
                symbol);
        }

        var sideText = BinanceJson.TryGetString(algo, SideField, out var readSide) ? readSide : null;
        var side = BinanceOrderMapper.ParseOrderSide(sideText);

        if (side == OrderSide.Unspecified)
        {
            return WithSymbol(
                BinanceErrors.MalformedResponse(
                        $"{symbol} 的買賣方向「{sideText}」無法對映。The order side \"{sideText}\" on {symbol} maps to nothing.")
                    .WithData(BinanceErrorDataKeys.Field, SideField),
                symbol);
        }

        var typeText = BinanceJson.TryGetString(algo, OrderTypeField, out var readType) ? readType : null;
        var conditionalOrderType = BinanceAlgoOrderMapper.ParseConditionalOrderType(typeText);

        if (conditionalOrderType == ConditionalOrderType.Unspecified)
        {
            // 類型不放行:條件單的類型就是「它會在什麼時候、以什麼方式動用部位」,不知道類型等於不知道
            // 這張單會做什麼。這條串流已經以 algoType 之外的事件為 Ignored,走到這裡的都該是條件單。
            // The type is not let through: a conditional order's type is when and how it will move the
            // position, and not knowing it is not knowing what the order will do. Events that are not
            // conditional orders are already classified as Ignored, so anything reaching here should be one.
            return WithSymbol(
                BinanceErrors.MalformedResponse(
                        $"{symbol} 的條件單類型「{typeText}」無法對映到任何已知類型。The conditional order type \"{typeText}\" on {symbol} maps to no known type.")
                    .WithData(BinanceErrorDataKeys.Field, OrderTypeField),
                symbol);
        }

        var conditionalOrder = new ConditionalOrder
        {
            Symbol = symbol,
            ClientConditionalOrderId = clientAlgoId,

            // aid 是數值,中立模型用字串裝 —— 別的交易所的編號不一定是數字。
            // The id is numeric here while the neutral model stores a string, because other exchanges do not
            // necessarily use numbers.
            ExchangeConditionalOrderId = BinanceJson.TryGetInt64(algo, AlgoIdField, out var algoId)
                ? algoId.ToString(CultureInfo.InvariantCulture)
                : null,
            Side = side,
            ConditionalOrderType = conditionalOrderType,
            Status = status,
            PositionSide = BinanceOrderMapper.ParsePositionSide(
                BinanceJson.TryGetString(algo, PositionSideField, out var positionSide) ? positionSide : null),
            TimeInForce = BinanceOrderMapper.ParseTimeInForce(
                BinanceJson.TryGetString(algo, TimeInForceField, out var timeInForce) ? timeInForce : null),
            Quantity = BinanceJson.TryGetDecimal(algo, OriginalQuantityField, out var quantity) ? quantity : 0m,
            TriggerPrice = ReadOptionalPrice(algo, TriggerPriceField),
            TriggerPriceType = ParseWorkingType(
                BinanceJson.TryGetString(algo, WorkingTypeField, out var workingType) ? workingType : null),
            Price = ReadOptionalPrice(algo, PriceField),
            ReduceOnly = BinanceJson.TryGetBoolean(algo, ReduceOnlyField, out var reduceOnly) && reduceOnly,
            ClosePosition = BinanceJson.TryGetBoolean(algo, ClosePositionField, out var closePosition)
                && closePosition,

            // ai 在未觸發時是空字串,不是省略也不是 0。照抄成 "" 會讓上層拿一個空字串去查單。
            // ai is an empty string before the trigger, neither absent nor zero. Passing it through sends the
            // caller to look up "".
            TriggeredOrderId = ReadNonEmptyString(algo, ActualOrderIdField),

            // tt 未觸發時是 0,而 0 在 Unix 毫秒是 1970 年 —— 直接轉換會讓一張還沒觸發的停損看起來
            // 像是五十年前就觸發過了。
            // tt is 0 before the trigger, and zero in Unix milliseconds is 1970: converting it directly makes
            // an untriggered stop look as though it fired half a century ago.
            TriggeredAt = ReadOptionalTimestamp(algo, TriggerTimeField),

            // 這個事件不帶條件單的建立時間,兩個時間只好同值 —— 與 ORDER_TRADE_UPDATE 同樣的取捨。
            // The event carries no creation time, so both timestamps share one value, as on
            // ORDER_TRADE_UPDATE.
            CreatedAt = eventTime,
            UpdatedAt = eventTime,
        };

        return Result.Success(BinanceUserDataEvent.FromConditionalOrder(
            new ConditionalOrderUpdate
            {
                ConditionalOrder = conditionalOrder,
                RawStatus = statusText,

                // rm 在沒有被拒的事件上是空字串或不存在。空字串當成「沒有原因」,不是「原因是空的」。
                // rm is absent or empty on an event that is not a rejection. An empty string means there is no
                // reason rather than that the reason is blank.
                RejectReason = ReadNonEmptyString(algo, RejectReasonField),
                Timestamp = eventTime,
            },
            eventTime));
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
    /// <b>開倉均價 <c>ep</c> 缺席時整則判失敗。</b> 帳戶變動的每一筆部位都帶這個欄位;
    /// 缺了就填零,會讓一個還開著的部位看起來「進場價為零」,而 <see cref="PositionChange.EntryPrice"/>
    /// 的零本來是留給「已平倉」的,兩者從此分不開。
    /// <b>A missing entry price <c>ep</c> fails the frame.</b> Every position entry of an account change carries
    /// it; defaulting it to zero would give an open position an entry price of zero, while
    /// <see cref="PositionChange.EntryPrice"/> reserves zero for a closed one, and the two would become
    /// indistinguishable.
    /// </para>
    /// </remarks>
    private static Result<BinanceUserDataEvent> ReadAccountUpdate(JsonElement root, DateTimeOffset eventTime)
    {
        if (!root.TryGetProperty(AccountField, out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MissingField(AccountField, BinanceUserDataPaths.Context);
        }

        var rawReason = BinanceJson.TryGetString(account, UpdateReasonField, out var reason) ? reason : null;

        // 這次沒有任何餘額(或部位)變動,欄位就不會出現。空清單是正確的答案,不是失敗。
        // The field is simply absent when no balance (or position) changed. An empty list is the right answer,
        // not a failure.
        var balances = ReadEntries<BalanceChange>(account, BalancesField, required: false, ReadBalanceChange);

        if (!balances.TryGetValue(out var readBalances))
        {
            return balances.ToFailure<BinanceUserDataEvent>();
        }

        var positions = ReadEntries<PositionChange>(account, PositionsField, required: false, ReadPositionChange);

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
    /// 「以目前標記價來看,這些部位快撐不住了」,少了它這則警告就沒有意義,
    /// <see cref="MarginCallPosition.Notional"/> 也會跟著變成零,因此標記價缺席時判失敗。
    /// Unlike <c>ACCOUNT_UPDATE</c>, this event <b>does</b> carry the mark price in <c>mp</c> — the warning is
    /// precisely that these positions are close to failing at the current mark. Without it the warning means
    /// nothing and <see cref="MarginCallPosition.Notional"/> collapses to zero along with it, so a missing mark
    /// price fails the frame.
    /// </remarks>
    private static Result<BinanceUserDataEvent> ReadMarginCall(JsonElement root, DateTimeOffset eventTime)
    {
        var positions = ReadEntries<MarginCallPosition>(
            root,
            MarginCallPositionsField,
            required: true,
            ReadMarginCallPosition);

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
                CrossWalletBalance = ReadOptionalDecimal(root, CrossWalletBalanceField),
                Positions = readPositions,
                Timestamp = eventTime,
            },
            eventTime));
    }

    /// <summary>
    /// 讀出一個物件陣列的每一個元素。
    /// Reads every element of an array of objects.
    /// </summary>
    /// <typeparam name="T">元素對映成的型別。The type each element maps onto.</typeparam>
    /// <param name="container">陣列所在的物件。The object holding the array.</param>
    /// <param name="propertyName">陣列的欄位名。The array's property name.</param>
    /// <param name="required">
    /// 欄位缺席時是否判失敗。帳戶增量沒變動就不帶欄位,追繳警告則一定帶。
    /// Whether an absent property fails. An account delta omits it when nothing changed; a margin call always
    /// carries it.
    /// </param>
    /// <param name="readEntry">讀一個元素的方法。Reads one element.</param>
    /// <returns>讀出的元素,或第一個讀不出來的元素的失敗。The elements, or the failure of the first unreadable one.</returns>
    /// <remarks>
    /// 任何一個元素讀不出來就整則判失敗,不跳過。跳過一筆部位增量,本地的部位就停在舊值,
    /// 而沒有任何東西會提醒這件事。
    /// One unreadable element fails the whole frame rather than being skipped: skipping a position delta leaves the
    /// local position at its old value with nothing to say so.
    /// </remarks>
    private static Result<IReadOnlyList<T>> ReadEntries<T>(
        JsonElement container,
        string propertyName,
        bool required,
        Func<JsonElement, Result<T>> readEntry)
        where T : class
    {
        if (!container.TryGetProperty(propertyName, out var array))
        {
            return required
                ? BinanceErrors.MissingField(propertyName, BinanceUserDataPaths.Context)
                : Result.Success<IReadOnlyList<T>>([]);
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return BinanceErrors.MissingField(propertyName, BinanceUserDataPaths.Context);
        }

        var entries = new List<T>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            var entry = readEntry(element);

            if (!entry.TryGetValue(out var value))
            {
                return entry.ToFailure<IReadOnlyList<T>>();
            }

            entries.Add(value);
        }

        return Result.Success<IReadOnlyList<T>>(entries);
    }

    private static Result<BalanceChange> ReadBalanceChange(JsonElement element)
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

        return new BalanceChange
        {
            Asset = asset,
            WalletBalance = walletBalance,
            CrossWalletBalance = ReadOptionalDecimal(element, CrossWalletBalanceField),

            // 入金、出金、轉帳造成的變動量。權益曲線要扣掉的就是它,所以缺席時是「不知道」而不是「零」——
            // 零會讓一筆入金被當成策略賺的。
            // The change from deposits, withdrawals, and transfers — what an equity curve takes out. Absent means
            // unknown rather than zero, because zero would count a deposit as something the strategy earned.
            NonTradingChange = ReadOptionalDecimal(element, BalanceChangeField),
        };
    }

    private static Result<PositionChange> ReadPositionChange(JsonElement element)
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

        if (!BinanceJson.TryGetDecimal(element, EntryPriceField, out var entryPrice))
        {
            // 理由見 ReadAccountUpdate 的說明:零是留給「已平倉」的。
            // See the remarks on ReadAccountUpdate: zero is reserved for a closed position.
            return WithSymbol(BinanceErrors.MissingField(EntryPriceField, symbol), symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, UnrealizedPnlField, out var unrealizedPnl))
        {
            return WithSymbol(BinanceErrors.MissingField(UnrealizedPnlField, symbol), symbol);
        }

        if (!BinanceJson.TryGetString(element, MarginTypeField, out var marginType))
        {
            // 缺席就判失敗,不猜 —— 與 REST 持倉查詢(BinanceResponseReader)的處理一致。
            // 原本缺席會落到「不是 cross 就是逐倉」那一支,等於替資料編一個值:全倉部位被讀成逐倉,
            // 保證金與強平的計算整個走錯邊,而欄位看起來完全正常。
            // Absent means failure, never a guess — matching the REST position reader. Previously an absent
            // value fell through to "not cross, therefore isolated", inventing a value: a cross position read as
            // isolated sends every margin and liquidation calculation down the wrong branch while looking normal.
            return WithSymbol(BinanceErrors.MissingField(MarginTypeField, symbol), symbol);
        }

        var marginMode = ParseMarginMode(marginType);

        return new PositionChange
        {
            Symbol = symbol,
            Quantity = quantity,
            Side = ReadPositionSide(element),
            EntryPrice = entryPrice,
            UnrealizedPnl = unrealizedPnl,
            AccumulatedRealizedPnl = ReadOptionalDecimal(element, AccumulatedRealizedField),
            MarginMode = marginMode,
            IsolatedMargin = ReadIsolatedMargin(element, marginMode),
        };
    }

    private static Result<MarginCallPosition> ReadMarginCallPosition(JsonElement element)
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

        if (!BinanceJson.TryGetDecimal(element, MarkPriceField, out var markPrice))
        {
            // 標記價是這則警告的全部意義,理由見 ReadMarginCall 的說明。
            // The mark price is the whole point of the warning; see the remarks on ReadMarginCall.
            return WithSymbol(BinanceErrors.MissingField(MarkPriceField, symbol), symbol);
        }

        if (!BinanceJson.TryGetString(element, MarginTypeField, out var marginType))
        {
            // 缺席就判失敗,不猜 —— 與 REST 持倉查詢(BinanceResponseReader)的處理一致。
            // 原本缺席會落到「不是 cross 就是逐倉」那一支,等於替資料編一個值:全倉部位被讀成逐倉,
            // 保證金與強平的計算整個走錯邊,而欄位看起來完全正常。
            // Absent means failure, never a guess — matching the REST position reader. Previously an absent
            // value fell through to "not cross, therefore isolated", inventing a value: a cross position read as
            // isolated sends every margin and liquidation calculation down the wrong branch while looking normal.
            return WithSymbol(BinanceErrors.MissingField(MarginTypeField, symbol), symbol);
        }

        var marginMode = ParseMarginMode(marginType);

        return new MarginCallPosition
        {
            Symbol = symbol,
            Quantity = quantity,
            Side = ReadPositionSide(element),
            MarkPrice = markPrice,
            UnrealizedPnl = unrealizedPnl,
            MaintenanceMargin = ReadOptionalDecimal(element, MaintenanceMarginField),
            MarginMode = marginMode,
            IsolatedMargin = ReadIsolatedMargin(element, marginMode),
        };
    }

    /// <summary>
    /// 讀逐倉保證金:只有逐倉部位有這個值。
    /// Reads the isolated margin, which only an isolated position has.
    /// </summary>
    /// <param name="element">部位物件。The position object.</param>
    /// <param name="marginMode">這個部位的保證金模式。The position's margin mode.</param>
    /// <returns>逐倉保證金;全倉部位或欄位缺席時為 <see langword="null"/>。The isolated margin, or null.</returns>
    /// <remarks>
    /// 全倉部位的 <c>iw</c> 一律是 <c>"0"</c>。照抄會得到一個「逐倉保證金為零」的全倉部位 ——
    /// 型別文件把這個欄位定義成「全倉為 <see langword="null"/>」,正是為了讓讀的人不必知道這個慣例。
    /// A cross position's <c>iw</c> is always <c>"0"</c>, and copying it through yields a cross position with an
    /// isolated margin of zero. The type defines the field as null for cross precisely so that readers need not know
    /// that convention.
    /// </remarks>
    private static decimal? ReadIsolatedMargin(JsonElement element, MarginMode marginMode) =>
        marginMode == MarginMode.Isolated ? ReadOptionalDecimal(element, IsolatedWalletField) : null;

    private static PositionSide ReadPositionSide(JsonElement element) =>
        BinanceOrderMapper.ParsePositionSide(
            BinanceJson.TryGetString(element, PositionSideField, out var positionSide) ? positionSide : null);


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

    private static MarginMode ParseMarginMode(string value) =>
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

    /// <summary>
    /// 把幣安的 <c>wt</c>(<c>workingType</c>)轉回中立的觸發價種類。
    /// Converts a Binance <c>wt</c> (<c>workingType</c>) back into the neutral trigger price type.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的種類;對不上時為 <see cref="TriggerPriceType.LastPrice"/>。
    /// The mapped type, falling back to <see cref="TriggerPriceType.LastPrice"/>.
    /// </returns>
    /// <remarks>
    /// 退路是成交價而不是標記價,因為<b>幣安的預設就是 <c>CONTRACT_PRICE</c></b>。退成標記價會讓一張
    /// 實際看成交價的停損被回報成「看標記價」,而那正是「為什麼被一根影線掃掉」查不出原因的來源。
    /// The fallback is the traded price rather than the mark price because <b>Binance's own default is
    /// <c>CONTRACT_PRICE</c></b>. Falling back to the mark price would report a stop that really watches traded
    /// prices as watching the mark, which is exactly what makes "why did a single wick take it out"
    /// unanswerable.
    /// </remarks>
    private static TriggerPriceType ParseWorkingType(string? value) => value switch
    {
        "MARK_PRICE" => TriggerPriceType.MarkPrice,
        _ => TriggerPriceType.LastPrice,
    };

    /// <summary>
    /// 讀一個交易所以空字串表示「沒有」的字串欄位。
    /// Reads a string field the exchange writes as an empty string to mean "none".
    /// </summary>
    /// <param name="element">所在的物件。The containing object.</param>
    /// <param name="propertyName">欄位名。The property name.</param>
    /// <returns>讀到的值,或 <see langword="null"/>。The value, or <see langword="null"/>.</returns>
    private static string? ReadNonEmptyString(JsonElement element, string propertyName) =>
        BinanceJson.TryGetString(element, propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>
    /// 讀一個交易所以 0 表示「還沒發生」的時間欄位。
    /// Reads a timestamp the exchange writes as 0 to mean "has not happened yet".
    /// </summary>
    /// <param name="element">所在的物件。The containing object.</param>
    /// <param name="propertyName">欄位名。The property name.</param>
    /// <returns>讀到的時刻,或 <see langword="null"/>。The instant, or <see langword="null"/>.</returns>
    /// <remarks>
    /// 0 在 Unix 毫秒是 1970 年。直接轉換不會失敗,只會讓一張還沒觸發的停損看起來像五十年前就觸發過了。
    /// Zero in Unix milliseconds is 1970. Converting it directly does not fail; it merely makes an untriggered
    /// stop look as though it fired half a century ago.
    /// </remarks>
    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement element, string propertyName) =>
        BinanceJson.TryGetInt64(element, propertyName, out var milliseconds) && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    /// <summary>
    /// 讀一個交易所可能不給的數值;缺席時為 <see langword="null"/>,不是零。
    /// Reads a number the exchange may omit; absent is <see langword="null"/>, never zero.
    /// </summary>
    /// <param name="element">所在的物件。The containing object.</param>
    /// <param name="propertyName">欄位名。The property name.</param>
    /// <returns>讀到的值,或 <see langword="null"/>。The value, or <see langword="null"/>.</returns>
    private static decimal? ReadOptionalDecimal(JsonElement element, string propertyName) =>
        BinanceJson.TryGetDecimal(element, propertyName, out var value) ? value : null;

    private static Error WithSymbol(Error error, string symbol) =>
        error.WithData(BinanceErrorDataKeys.Symbol, symbol);
}
