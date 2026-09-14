namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 使用者資料串流專用的 REST 路徑、事件型別字串與操作名稱。
/// The REST path, event type strings, and operation names used by the user data stream.
/// </summary>
/// <remarks>
/// <para>
/// 路徑不帶開頭斜線,理由與 <c>BinanceApiPaths</c> 相同:相對位址若以斜線開頭,
/// <see cref="Uri"/> 會把它當成「從主機根目錄重新開始」,在有路徑前綴的代理環境下會悄悄打到錯的位址。
/// 這裡另立一份而不是併進 <c>BinanceApiPaths</c>,是為了讓使用者資料這一塊的新增不與其他同時進行的工作搶同一個檔案。
/// The path carries no leading slash for the same reason as <c>BinanceApiPaths</c>: a relative URI that begins
/// with a slash restarts from the host root and quietly targets the wrong address behind a path-prefixed proxy.
/// It lives in its own file rather than in <c>BinanceApiPaths</c> so that user data additions do not contend for
/// a file other work is touching.
/// </para>
/// <para>
/// <b><see cref="StreamIdentifier"/> 是一個固定字面值,不是連線位址。</b> 使用者資料串流的位址是
/// <c>{WebSocketBaseUri}/private/ws?listenKey={listenKey}&amp;events=…</c>,而 listenKey 是能連上該帳戶私有資料的憑證 ——
/// 它絕對不可以出現在任何錯誤訊息、診斷資料或日誌裡。行情那一側把串流名稱放進
/// <c>BinanceStreamErrorDataKeys.StreamNames</c>,這一側改放這個固定字串,診斷時仍分得出是哪一條串流,
/// 又不會把憑證一起帶出去。
/// <b><see cref="StreamIdentifier"/> is a fixed literal, not an address.</b> The user data stream dials
/// <c>{WebSocketBaseUri}/private/ws?listenKey={listenKey}&amp;events=…</c>, and the listenKey is a credential that
/// reaches the account's private data: it must never appear in an error message, in diagnostic data, or in a log.
/// Where the market side puts real stream names into <c>BinanceStreamErrorDataKeys.StreamNames</c>, this side puts
/// this constant instead — still enough to say which stream failed, without carrying the credential out with it.
/// </para>
/// </remarks>
internal static class BinanceUserDataPaths
{
    /// <summary>
    /// 使用者資料串流憑證。<c>POST</c> 建立、<c>PUT</c> 續期、<c>DELETE</c> 關閉共用這一個路徑。
    /// The user data stream credential: <c>POST</c> creates, <c>PUT</c> renews, and <c>DELETE</c> closes, all on
    /// this one path.
    /// </summary>
    /// <remarks>
    /// USDⓈ-M 合約的 <c>PUT</c> 與 <c>DELETE</c> <b>不需要</b>帶 listenKey 參數 —— 交易所是從 API 金鑰認出
    /// 是哪一把。這一點很重要:少一個參數,就少一條把憑證寫進查詢字串(以及寫進任何記錄查詢字串的地方)的路。
    /// 現貨的同名端點要求帶參數,照現貨的寫法抄過來就會讓憑證出現在 URL 裡。
    /// The USDⓈ-M <c>PUT</c> and <c>DELETE</c> take <b>no</b> listenKey parameter: the exchange identifies the key
    /// from the API key alone. That matters — one parameter fewer is one fewer route for the credential into a
    /// query string and into anything that records query strings. The spot endpoints of the same name do require
    /// the parameter, and copying their shape here would put the credential into the URL.
    /// </remarks>
    public const string ListenKey = "fapi/v1/listenKey";

    /// <summary>
    /// 建立回應中帶回憑證的欄位名。
    /// The field carrying the credential in the create response.
    /// </summary>
    public const string ListenKeyField = "listenKey";

    /// <summary>
    /// 診斷資料中用來代表這條串流的固定字面值。
    /// The fixed literal that stands in for this stream in diagnostic data.
    /// </summary>
    public const string StreamIdentifier = "userDataStream";

    /// <summary>委託與成交事件。The order and fill event.</summary>
    public const string OrderTradeUpdateEvent = "ORDER_TRADE_UPDATE";

    /// <summary>帳戶餘額與部位的增量事件。The balance and position delta event.</summary>
    public const string AccountUpdateEvent = "ACCOUNT_UPDATE";

    /// <summary>
    /// 條件單狀態變化事件。The conditional order state change event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 幣安 2025-12-09 把條件單移到 Algo Service 之後新增的事件。條件單的狀態變化<b>只</b>出現在這裡,
    /// <b>不會</b>出現在 <see cref="OrderTradeUpdateEvent"/> —— 只訂閱委託更新的話,「停損被觸發了」
    /// 這件事會整個消失。
    /// Added when Binance moved conditional orders to the Algo Service on 2025-12-09. Conditional order state
    /// changes appear <b>only</b> here and <b>never</b> on <see cref="OrderTradeUpdateEvent"/>: subscribing
    /// only to order updates loses the fact that a stop triggered at all.
    /// </para>
    /// <para>
    /// 出處:官方 change-log 2025-11-06「Websocket User Stream Update — New algo order event:
    /// <c>ALGO_UPDATE</c>」(<c>https://developers.binance.com/docs/derivatives/change-log</c>);
    /// 欄位結構見
    /// <c>https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/user-data-streams</c>。
    /// 兩者擷取日期 2026-09-14。
    /// Sources: the official change-log of 2025-11-06 and the user-data-streams page for the field layout, both
    /// retrieved 2026-09-14.
    /// </para>
    /// <para>
    /// 同一個事件名在統一帳戶(Portfolio Margin)那一側的巢狀物件是 <c>ao</c>,USDⓈ-M 這一側是 <c>o</c>。
    /// 抄錯那一個字母的結果是每一則條件單事件都判失敗。
    /// The same event name nests its payload under <c>ao</c> on Portfolio Margin and under <c>o</c> here on
    /// USDⓈ-M. Copying the wrong letter fails every conditional order event.
    /// </para>
    /// </remarks>
    public const string AlgoUpdateEvent = "ALGO_UPDATE";

    /// <summary>保證金追繳警告事件。The margin call event.</summary>
    public const string MarginCallEvent = "MARGIN_CALL";

    /// <summary>
    /// 串流憑證已失效的事件。注意它是小寫駝峰,與其他三個全大寫的事件不同。
    /// The credential-expired event. Note that it is lower camel case while the other three are upper snake case.
    /// </summary>
    /// <remarks>
    /// 大小寫是交易所定的,照抄即可。用 <see cref="StringComparison.OrdinalIgnoreCase"/> 比對反而危險:
    /// 那會讓「事件名稱改了」這種協定變更被吃掉。
    /// The casing is the exchange's and is copied verbatim. Matching it case-insensitively would be worse: it
    /// swallows a protocol change in the event name rather than surfacing it.
    /// </remarks>
    public const string ListenKeyExpiredEvent = "listenKeyExpired";

    /// <summary>
    /// 撥號時放進 <c>events=</c> 查詢參數的事件清單:委託與成交、帳戶增量、保證金追繳、憑證失效。
    /// The event list placed in the <c>events=</c> query when dialling: order and fill updates, account deltas,
    /// margin calls, and credential expiry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>漏列等於永遠收不到那一類事件,而且名稱不會被驗證。</b> <c>events</c> 是真的過濾器(只帶
    /// <c>ACCOUNT_UPDATE</c> 的連線收不到 <c>ORDER_TRADE_UPDATE</c>);夾一個不存在的名稱照樣連得上、
    /// 照樣收到其他事件。拼錯或漏列都不會有任何錯誤,只會讓某一條訂閱安靜地永遠沒有資料 ——
    /// 漏了 <c>listenKeyExpired</c>,憑證過期後串流就不會自己重建;漏了 <c>MARGIN_CALL</c>,追繳警告就不會出現。
    /// <b>Leaving a name out means never receiving that kind of event, and the names are not validated.</b>
    /// <c>events</c> really filters — a connection with only <c>ACCOUNT_UPDATE</c> never receives
    /// <c>ORDER_TRADE_UPDATE</c> — while a made-up name still connects and still receives the rest. Neither a
    /// misspelling nor an omission raises anything; one subscription just stays silent for ever. Without
    /// <c>listenKeyExpired</c> the stream never rebuilds itself after the credential lapses; without
    /// <c>MARGIN_CALL</c> no margin warning ever appears.
    /// </para>
    /// <para>
    /// 所以這份清單<b>直接引用解析器分派用的同一組常數</b>,不另寫字串。兩份字串就是兩個會拼錯的地方,
    /// 而引用同一個常數,解析器認得的名稱與訂閱出去的名稱就不可能不一致。
    /// The list therefore <b>references the very constants the reader dispatches on</b> rather than spelling the
    /// names again. Two copies of a string are two places to misspell it; sharing the constant makes it impossible
    /// for the names subscribed to and the names recognised to drift apart.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> StreamEvents =
    [
        OrderTradeUpdateEvent,
        AlgoUpdateEvent,
        AccountUpdateEvent,
        MarginCallEvent,
        ListenKeyExpiredEvent,
    ];

    /// <summary>建立憑證的操作名稱,用於憑證缺漏時的錯誤訊息。The create operation name, used when credentials are missing.</summary>
    public const string CreateOperation = "建立使用者資料串流 / start user data stream";

    /// <summary>續期憑證的操作名稱。The renew operation name.</summary>
    public const string KeepAliveOperation = "續期使用者資料串流 / keep alive user data stream";

    /// <summary>關閉憑證的操作名稱。The close operation name.</summary>
    public const string DeleteOperation = "關閉使用者資料串流 / close user data stream";

    /// <summary>
    /// 解析失敗時的上下文描述。刻意不含任何會隨執行變動的內容。
    /// The context shown on a parse failure. Deliberately free of anything that varies at run time.
    /// </summary>
    public const string Context = "使用者資料串流 / user data stream";
}
