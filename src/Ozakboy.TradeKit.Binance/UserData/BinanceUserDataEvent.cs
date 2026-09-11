namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 一則使用者資料串流訊息被判讀成什麼。
/// What one user data frame turned out to be.
/// </summary>
internal enum BinanceUserDataEventKind
{
    /// <summary>
    /// 本套件不處理的事件型別,沒有東西要交給訂閱者。
    /// An event type this package does not model; there is nothing to hand to a subscriber.
    /// </summary>
    /// <remarks>
    /// 這條串流是<b>多工</b>的:一條連線上跑著本套件對映的四種事件,還有帳戶設定變更、策略單、網格單、
    /// 條件單觸發被拒等等,而且交易所會持續新增。行情那一側對「非預期的事件型別」判失敗是對的 ——
    /// 那裡一條連線只跑一種串流,出現別的就代表協定變了;這裡照抄那條規則,只會讓消費端被例行事件
    /// 灌滿假警報,真正的失敗反而被淹掉。
    /// This stream is <b>multiplexed</b>: one connection carries the four event types this package maps plus
    /// account configuration changes, strategy orders, grid orders, rejected conditional triggers and more, and
    /// the exchange keeps adding to the list. Failing on an unexpected event type is right on the market side,
    /// where one connection carries one stream and anything else means the protocol changed; copying that rule
    /// here would flood the consumer with false alarms from routine events and bury the failures that matter.
    /// </remarks>
    Ignored = 0,

    /// <summary>
    /// 委託狀態變化,可能同時含有一筆成交。
    /// An order state change, possibly carrying a fill as well.
    /// </summary>
    OrderTradeUpdate = 1,

    /// <summary>
    /// 帳戶餘額與部位的<b>增量</b>。
    /// A <b>delta</b> of account balances and positions.
    /// </summary>
    AccountUpdate = 2,

    /// <summary>
    /// 保證金追繳警告。
    /// A margin call.
    /// </summary>
    MarginCall = 3,

    /// <summary>
    /// 串流憑證已失效,這條連線不會再送任何東西。
    /// The stream credential expired; this connection will deliver nothing further.
    /// </summary>
    ListenKeyExpired = 4,
}

/// <summary>
/// 判讀一則使用者資料串流訊息的結果。
/// The outcome of reading one user data frame.
/// </summary>
/// <remarks>
/// <para>
/// 一則 <c>ORDER_TRADE_UPDATE</c> 可能同時是「委託狀態變了」和「成交了一筆」,所以這個型別容得下兩個
/// 酬載。把它拆成兩次解析會讓同一則訊息被解兩遍,而兩遍的欄位對映一旦分岔,委託與成交就會互相對不上。
/// One <c>ORDER_TRADE_UPDATE</c> can be both an order state change and a fill, so this type holds room for two
/// payloads. Parsing the frame twice instead would read the same fields twice, and once those two readings
/// drift apart the order and the fill stop agreeing with each other.
/// </para>
/// <para>
/// 刻意不用 <see cref="Result{T}"/> 裝「這則訊息不必處理」:那個型別只有成功與失敗兩種狀態,
/// 而這條多工串流上的未知事件既不是資料也不是錯誤。
/// <see cref="Result{T}"/> is deliberately not used to express "nothing to do with this frame": it has only
/// success and failure, and an unknown event on this multiplexed stream is neither data nor an error.
/// </para>
/// </remarks>
internal readonly record struct BinanceUserDataEvent
{
    private BinanceUserDataEvent(
        BinanceUserDataEventKind kind,
        Order? orderUpdate,
        Trade? fill,
        AccountUpdate? account,
        MarginCall? marginWarning,
        DateTimeOffset eventTime)
    {
        Kind = kind;
        OrderUpdate = orderUpdate;
        Fill = fill;
        Account = account;
        MarginWarning = marginWarning;
        EventTime = eventTime;
    }

    /// <summary>
    /// 這則訊息被判讀成什麼。
    /// What the frame turned out to be.
    /// </summary>
    public BinanceUserDataEventKind Kind { get; }

    /// <summary>
    /// 委託狀態,僅在 <see cref="BinanceUserDataEventKind.OrderTradeUpdate"/> 時有值。
    /// The order state, set only for <see cref="BinanceUserDataEventKind.OrderTradeUpdate"/>.
    /// </summary>
    public Order? OrderUpdate { get; }

    /// <summary>
    /// 這次事件附帶的成交,沒有成交時為 <see langword="null"/>。
    /// The fill this event carried, or <see langword="null"/> when it carried none.
    /// </summary>
    public Trade? Fill { get; }

    /// <summary>
    /// 帳戶增量,僅在 <see cref="BinanceUserDataEventKind.AccountUpdate"/> 時有值。
    /// The account delta, set only for <see cref="BinanceUserDataEventKind.AccountUpdate"/>.
    /// </summary>
    public AccountUpdate? Account { get; }

    /// <summary>
    /// 保證金追繳警告,僅在 <see cref="BinanceUserDataEventKind.MarginCall"/> 時有值。
    /// The margin call, set only for <see cref="BinanceUserDataEventKind.MarginCall"/>.
    /// </summary>
    public MarginCall? MarginWarning { get; }

    /// <summary>
    /// 交易所標在這則訊息上的事件時間(UTC 語意)。
    /// The event time the exchange stamped on the frame, in UTC semantics.
    /// </summary>
    /// <remarks>
    /// 憑證失效時這是唯一知道「串流從哪一刻起不可信」的來源,會直接填進
    /// <see cref="ResyncRequired.UntrustedSince"/>。
    /// On a credential expiry this is the only available answer to "from when did the stream stop being
    /// trustworthy", and it goes straight into <see cref="ResyncRequired.UntrustedSince"/>.
    /// </remarks>
    public DateTimeOffset EventTime { get; }

    /// <summary>
    /// 建立「委託狀態變化」的結果。
    /// Creates an order-update outcome.
    /// </summary>
    /// <param name="orderUpdate">委託狀態。The order state.</param>
    /// <param name="fill">附帶的成交,沒有時傳 <see langword="null"/>。The fill, or <see langword="null"/>.</param>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceUserDataEvent FromOrder(Order orderUpdate, Trade? fill, DateTimeOffset eventTime) =>
        new(BinanceUserDataEventKind.OrderTradeUpdate, orderUpdate, fill, null, null, eventTime);

    /// <summary>
    /// 建立「帳戶增量」的結果。
    /// Creates an account-delta outcome.
    /// </summary>
    /// <param name="account">帳戶增量。The account delta.</param>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceUserDataEvent FromAccount(AccountUpdate account, DateTimeOffset eventTime) =>
        new(BinanceUserDataEventKind.AccountUpdate, null, null, account, null, eventTime);

    /// <summary>
    /// 建立「保證金追繳」的結果。
    /// Creates a margin-call outcome.
    /// </summary>
    /// <param name="marginWarning">警告內容。The warning.</param>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceUserDataEvent FromMarginCall(MarginCall marginWarning, DateTimeOffset eventTime) =>
        new(BinanceUserDataEventKind.MarginCall, null, null, null, marginWarning, eventTime);

    /// <summary>
    /// 建立「憑證已失效」的結果。
    /// Creates a credential-expired outcome.
    /// </summary>
    /// <param name="eventTime">事件時間,也就是憑證失效的時刻。The event time, which is when the credential lapsed.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceUserDataEvent FromListenKeyExpired(DateTimeOffset eventTime) =>
        new(BinanceUserDataEventKind.ListenKeyExpired, null, null, null, null, eventTime);

    /// <summary>
    /// 建立「這則訊息不必交給訂閱者」的結果。
    /// Creates an ignored outcome.
    /// </summary>
    /// <param name="eventTime">事件時間。The event time.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceUserDataEvent FromUnknown(DateTimeOffset eventTime) =>
        new(BinanceUserDataEventKind.Ignored, null, null, null, null, eventTime);
}
