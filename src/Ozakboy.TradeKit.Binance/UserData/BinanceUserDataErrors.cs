using System.Globalization;

using Ozakboy.TradeKit.Binance.MarketData;

namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 使用者資料串流這一層的錯誤建構,統一補上環境與串流識別字,並確保憑證不會隨錯誤流出去。
/// Builds the failures of the user data stream layer, attaching the environment and the stream identifier, and
/// keeping the credential out of everything it produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>這裡的每一個方法都不接受、也不輸出訊息原文。</b> 行情那一側會把讀不懂的訊息截一段放進
/// <c>BinanceStreamErrorDataKeys.MessageSnippet</c>,那在公開行情上是安全的;
/// 使用者資料串流不行 —— <c>listenKeyExpired</c> 事件本體就帶著 listenKey,
/// 而 listenKey 是能連上該帳戶私有資料的憑證。因此這一層寧可少一點診斷資訊,也不留任何一條把它寫成字串的路。
/// <b>None of these methods accepts or emits raw frame text.</b> The market side truncates an unreadable frame
/// into <c>BinanceStreamErrorDataKeys.MessageSnippet</c>, which is safe on a public feed. It is not safe here:
/// the <c>listenKeyExpired</c> frame carries the listenKey itself, and that key reaches the account's private
/// data. This layer therefore accepts less diagnostic detail rather than leave any route that turns the
/// credential into a string.
/// </para>
/// <para>
/// 連線層失敗的代碼對映直接沿用 <see cref="BinanceStreamErrors.FromWebSocketError"/>,不另寫一份。
/// 兩份對映表遲早會分岔,而分岔的症狀是「同一種斷線在行情與帳戶兩條串流上被歸成不同的錯誤」,
/// 上層的告警規則就會只涵蓋其中一邊。
/// The connection-layer code mapping is reused from <see cref="BinanceStreamErrors.FromWebSocketError"/> rather
/// than duplicated. Two mapping tables drift apart eventually, and the symptom of that drift is one kind of
/// disconnect being classified differently on the market and account streams — leaving an alerting rule
/// upstairs covering only one of them.
/// </para>
/// </remarks>
internal static class BinanceUserDataErrors
{
    /// <summary>
    /// 診斷資料裡代表這條串流的識別字。固定字面值,絕不是連線位址。
    /// The identifier standing in for this stream in diagnostic data: a fixed literal, never the address.
    /// </summary>
    private static readonly string[] StreamIdentifiers = [BinanceUserDataPaths.StreamIdentifier];

    /// <summary>
    /// 補上環境名稱與串流識別字這兩個診斷資料。
    /// Attaches the environment name and the stream identifier as diagnostic data.
    /// </summary>
    /// <param name="error">原始錯誤。The original error.</param>
    /// <param name="endpoints">端點組合,用於標示環境。The endpoint set, used to name the environment.</param>
    /// <returns>補過資料的錯誤。The error with the data attached.</returns>
    public static Error Decorate(Error error, BinanceEndpoints endpoints) =>
        BinanceStreamErrors.Decorate(error, StreamIdentifiers, endpoints);

    /// <summary>
    /// 把 <c>Ozakboy.WebSockets</c> 的失敗翻成交易所中立的錯誤。
    /// Translates an <c>Ozakboy.WebSockets</c> failure into an exchange-neutral error.
    /// </summary>
    /// <param name="webSocketError">連線層的失敗。The connection-layer failure.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 分類原樣沿用,消費端用 <see cref="Error.IsTransient"/> 就能分辨「斷了還在重連」與「這條串流結束了」。
    /// 對帳戶串流而言這個差別特別重要:前者結束後會補一則
    /// <see cref="ResyncReason.Reconnected"/> 訊號,後者不會再有任何訊號。
    /// The category is carried across untouched so that <see cref="Error.IsTransient"/> separates "dropped and
    /// reconnecting" from "this stream has ended". The difference matters more on the account stream: the first
    /// is followed by a <see cref="ResyncReason.Reconnected"/> signal and the second by nothing at all.
    /// </remarks>
    public static Error FromWebSocketError(Error webSocketError, BinanceEndpoints endpoints) =>
        BinanceStreamErrors.FromWebSocketError(webSocketError, StreamIdentifiers, endpoints);

    /// <summary>
    /// 建立「串流訊息無法解析」的錯誤。
    /// Creates a failure for a stream frame that could not be read.
    /// </summary>
    /// <param name="reason">說明為什麼讀不出來,中英雙語。A bilingual explanation of why.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 刻意沒有「原始訊息」參數,理由見類別說明:這條串流的訊息可能含有憑證。
    /// There is deliberately no raw-message parameter; see the type remarks — frames on this stream can carry the
    /// credential.
    /// </remarks>
    public static Error MalformedEvent(string reason, BinanceEndpoints endpoints) =>
        Decorate(BinanceErrors.MalformedResponse(reason), endpoints);

    /// <summary>
    /// 建立「憑證回應讀不出 listenKey」的錯誤。
    /// Creates the failure for a credential response with no readable listenKey.
    /// </summary>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 訊息裡只說「缺少欄位」,不放回應本體 —— 那份本體在正常情況下<b>就是</b>憑證本身。
    /// 這是整個套件裡少數「診斷資訊刻意被削掉」的地方,因為把它留下來的代價是把憑證寫進日誌。
    /// The message says only that the field is missing and never includes the body, because in the normal case
    /// that body <b>is</b> the credential. This is one of the few places in the package where diagnostic detail
    /// is deliberately given up: keeping it would mean writing the credential into a log.
    /// </remarks>
    public static Error ListenKeyMissing(BinanceEndpoints endpoints) =>
        Decorate(
            BinanceErrors.MalformedResponse(
                    $"{BinanceUserDataPaths.Context} 的憑證回應缺少「{BinanceUserDataPaths.ListenKeyField}」欄位;回應本體不列出,因為它正常情況下就是憑證本身。The credential response for {BinanceUserDataPaths.Context} has no \"{BinanceUserDataPaths.ListenKeyField}\" field; the body is not shown because in the normal case it is the credential itself.")
                .WithData(BinanceErrorDataKeys.Field, BinanceUserDataPaths.ListenKeyField),
            endpoints);

    /// <summary>
    /// 建立「連線佇列已滿,訊息被丟棄」的錯誤。
    /// Creates the failure reporting that frames were dropped from a full connection queue.
    /// </summary>
    /// <param name="droppedSinceLastReport">自上次回報以來丟掉幾則。How many were dropped since the last report.</param>
    /// <param name="droppedTotal">這條連線累計丟掉幾則。The running total for this connection.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 預設的背壓策略是 <see cref="Ozakboy.WebSockets.BackpressureStrategy.Wait"/>,所以正常情況下走不到這裡。
    /// 這條路徑存在是為了「有人把策略改成會丟棄的那兩種」—— 那時候丟掉的可能正是一筆成交回報,
    /// 而靜默丟棄成交回報會讓本地部位與交易所安靜分岔。
    /// The default backpressure strategy is <see cref="Ozakboy.WebSockets.BackpressureStrategy.Wait"/>, so this
    /// path is not reached in normal operation. It exists for the case where someone switches to one of the
    /// dropping strategies: what gets dropped may be a fill, and a silently dropped fill leaves local positions
    /// quietly diverging from the exchange's.
    /// </remarks>
    public static Error MessagesDropped(long droppedSinceLastReport, long droppedTotal, BinanceEndpoints endpoints) =>
        Decorate(
            Error.Network(
                TradeErrorCodes.StreamDisconnected,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"連線佇列已滿並丟棄 {droppedSinceLastReport} 則帳戶事件(這條連線累計 {droppedTotal} 則),本地狀態已有缺口,請全量對帳。The connection queue was full and dropped {droppedSinceLastReport} account events ({droppedTotal} in total on this connection); local state has a gap and must be reconciled in full.")),
            endpoints);

    /// <summary>
    /// 建立「這個訂閱者消費太慢,已經漏掉事件」的錯誤。
    /// Creates the failure saying that this subscriber fell behind and has already lost events.
    /// </summary>
    /// <param name="capacity">該訂閱者的佇列容量。The subscriber's queue capacity.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// <para>
    /// 分類是 <see cref="ErrorCategory.Exhausted"/>,<see cref="Error.IsTransient"/> 因此為
    /// <see langword="false"/>:這不是「等一下就會好」,這條訂閱已經結束了,消費端必須重新訂閱並全量對帳。
    /// 歸成可重試的分類會讓上層照著「記一筆日誌然後繼續等」處理,而它等的那條串流已經不會再送任何東西。
    /// The category is <see cref="ErrorCategory.Exhausted"/>, so <see cref="Error.IsTransient"/> reports
    /// <see langword="false"/>: this is not something that heals on its own. The subscription has ended and the
    /// consumer must resubscribe and reconcile in full. A transient category would have the caller log and keep
    /// waiting on a stream that will never deliver again.
    /// </para>
    /// <para>
    /// 訂單事件<b>絕不</b>靜默丟棄,是這個設計的重點。丟掉一筆成交回報而不說,本地部位就會與交易所分岔,
    /// 而帳面上的數字依然是個合理的數字 —— 沒有任何徵兆可循。
    /// The point of the design is that order events are <b>never</b> dropped in silence. Losing a fill without
    /// saying so leaves local positions diverging from the exchange's while the number on the books stays
    /// perfectly plausible, with nothing to give it away.
    /// </para>
    /// </remarks>
    public static Error SubscriberFellBehind(int capacity, BinanceEndpoints endpoints) =>
        Decorate(
            Error.Exhausted(
                TradeErrorCodes.StreamDisconnected,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"這個訂閱者消費太慢,{capacity} 則的佇列已滿,事件已經漏掉;這條訂閱就此結束,請重新訂閱並全量對帳。This subscriber fell behind, its queue of {capacity} filled, and events have been lost; the subscription ends here — resubscribe and reconcile in full.")),
            endpoints);

    /// <summary>
    /// 建立「這條使用者資料串流已經結束」的錯誤,給結束之後才來的訂閱者。
    /// Creates the failure handed to a subscriber that arrives after the stream has already ended.
    /// </summary>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 串流結束之後若讓新的訂閱安靜地掛在那裡等,症狀會是「訂閱成功、永遠沒有事件」——
    /// 那正是這個套件最不願意產生的那種故障。寧可讓新訂閱立刻以一筆說得出原因的失敗結束。
    /// Letting a new subscription hang quietly after the stream has ended produces "subscribed successfully,
    /// never receives anything", which is the exact failure mode this package exists to avoid. A new
    /// subscription is ended immediately with a failure that says why instead.
    /// </remarks>
    public static Error StreamEnded(BinanceEndpoints endpoints) =>
        Decorate(
            Error.Exhausted(
                TradeErrorCodes.StreamDisconnected,
                "使用者資料串流已經結束,不會再有事件;請重新建立這個串流並全量對帳。The user data stream has ended and will deliver nothing further; rebuild it and reconcile in full."),
            endpoints);

    /// <summary>
    /// 建立「串流已經釋放」的錯誤。
    /// Creates the failure for a stream that has already been disposed.
    /// </summary>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    public static Error Disposed(BinanceEndpoints endpoints) =>
        Decorate(
            Error.Exhausted(
                TradeErrorCodes.StreamDisconnected,
                "使用者資料串流已經釋放,無法再訂閱。The user data stream has been disposed and cannot be subscribed to."),
            endpoints);

    /// <summary>
    /// 建立「訂閱在啟動途中被取消」的錯誤。
    /// Creates the failure for a subscription cancelled while it was still starting.
    /// </summary>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    public static Error StartupCancelled(BinanceEndpoints endpoints) =>
        Decorate(
            new Error(
                TradeErrorCodes.StreamDisconnected,
                "使用者資料串流在啟動途中被取消。The user data stream was cancelled while starting.",
                ErrorCategory.Cancelled),
            endpoints);
}
