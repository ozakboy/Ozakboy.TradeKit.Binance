using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 使用者資料串流的設定:憑證續期週期、連線行為與各層佇列容量。
/// The settings of the user data stream: how often the credential is renewed, how the connection behaves, and
/// how deep each queue is.
/// </summary>
/// <remarks>
/// <para>
/// 重連、退避與有界佇列背壓由 <c>Ozakboy.WebSockets</c> 負責,這裡只調整幣安這一側有意見的幾個值。
/// 與行情串流分成兩份設定,是因為兩者的取捨方向相反:行情丟得起舊報價,帳戶事件一則都丟不起。
/// Reconnection, backoff, and bounded-queue backpressure belong to <c>Ozakboy.WebSockets</c>; this type tunes
/// only the values the Binance side has an opinion about. It is separate from the market stream settings
/// because the two trade off in opposite directions: market data can afford to drop a stale quote and account
/// events cannot afford to drop anything.
/// </para>
/// </remarks>
public sealed class BinanceUserDataStreamOptions
{
    /// <summary>
    /// 串流憑證的有效期。實測與官方文件一致:建立之後 60 分鐘失效。
    /// The lifetime of a stream credential: 60 minutes from creation, as measured and as documented.
    /// </summary>
    public static readonly TimeSpan ListenKeyLifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// 憑證續期週期的預設值,取有效期的一半。
    /// The default renewal interval, half the lifetime.
    /// </summary>
    /// <remarks>
    /// 取一半是為了容許連續失敗一次。續期失敗的原因通常是網路抖動或限流,下一次就會成功;
    /// 若把週期設到接近 60 分鐘,一次失敗就直接等於憑證過期、串流斷掉、期間的委託與成交全部漏掉。
    /// Half leaves room for one consecutive failure. A renewal usually fails because of a network hiccup or the
    /// rate limiter and succeeds on the next attempt; a period close to 60 minutes turns a single failure into
    /// an expired credential, a dropped stream, and every order and fill in between going unseen.
    /// </remarks>
    public static readonly TimeSpan DefaultListenKeyKeepAliveInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 多久續期一次串流憑證。
    /// How often the stream credential is renewed.
    /// </summary>
    public TimeSpan ListenKeyKeepAliveInterval { get; set; } = DefaultListenKeyKeepAliveInterval;

    /// <summary>
    /// 握手逾時。
    /// The handshake timeout.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 多久沒收到任何訊息就判定連線已死並重連。<see cref="TimeSpan.Zero"/> 表示不做這個判定,為預設值。
    /// How long without a message before the connection is treated as dead and re-established;
    /// <see cref="TimeSpan.Zero"/>, the default, disables the check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>預設關閉,而且這個預設值是有代價的,請讀完再決定。</b> 行情串流可以靠主動送
    /// <c>LIST_SUBSCRIPTIONS</c> 把閒置計時撐住,所以它敢開存活偵測;使用者資料串流不行 ——
    /// 一個沒有委託、沒有成交、沒有資金費結算的帳戶本來就可以安靜好幾個小時,那是<b>正常</b>的沉默。
    /// 在這種串流上設一個逾時,等於每隔那麼久就無故重連一次,而每次重連都會送出一則
    /// <see cref="ResyncReason.Reconnected"/>,逼上層做一次不必要的全量對帳。
    /// <b>Off by default, and the default has a cost; read this before changing it.</b> The market stream can
    /// hold its idle clock open by sending <c>LIST_SUBSCRIPTIONS</c>, which is why it dares to enable liveness
    /// detection. The user data stream cannot: an account with no orders, no fills, and no funding settlement is
    /// entitled to hours of silence, and that silence is <b>normal</b>. A timeout on this stream means an
    /// unprompted reconnect that often, and every reconnect raises a
    /// <see cref="ResyncReason.Reconnected"/> that forces an unnecessary full reconciliation upstairs.
    /// </para>
    /// <para>
    /// 代價是另一邊:連線若「握手成功、狀態顯示已連線、資料流卻被中介設備吃掉」,關閉存活偵測就發現不了,
    /// 而帳戶串流上發現不了代表成交照樣發生、本地卻完全不知道。已知的緩衝只有兩個 ——
    /// 憑證每 30 分鐘續期一次會打到 REST(但它驗的是 REST 不是 WebSocket),
    /// 以及憑證滿 60 分鐘後交易所會主動斷開。要更早發現,就把這個值設成大於帳戶正常沉默期的長度。
    /// The cost sits on the other side: a connection whose handshake succeeded, whose state reads connected, and
    /// whose data flow is being swallowed by something in the path goes unnoticed with the check off — and
    /// unnoticed on this stream means fills happening that the local side never learns about. The only known
    /// backstops are the 30-minute renewal, which exercises REST rather than the socket, and the exchange
    /// dropping the connection once the credential reaches 60 minutes. To notice sooner, set this comfortably
    /// above the account's normal quiet period.
    /// </para>
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// 重連次數上限,<see langword="null"/> 表示不限次數,為預設值。
    /// The reconnect ceiling; <see langword="null"/>, the default, means unlimited.
    /// </summary>
    /// <remarks>
    /// 預設不限次數。帳戶串流一旦放棄重連就再也不會有人把它接回來,而「放棄」與「交易所維護中」
    /// 在現場是同一個樣子;維護結束後串流沒回來,症狀是委託狀態從此停在最後一次收到的樣子。
    /// Unlimited by default. Once the account stream gives up reconnecting nothing brings it back, and "gave up"
    /// looks exactly like "the exchange is under maintenance" while it is happening; when the maintenance ends
    /// and the stream does not return, the symptom is order state frozen at whatever arrived last.
    /// </remarks>
    public int? MaxReconnectAttempts { get; set; }

    /// <summary>
    /// 連線層待處理訊息的佇列容量。
    /// The capacity of the connection-layer pending-message queue.
    /// </summary>
    public int ConnectionQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// 每一個訂閱者各自的佇列容量。
    /// The capacity of each individual subscriber's queue.
    /// </summary>
    /// <remarks>
    /// 每個訂閱者一份佇列,所以一個慢的消費端不會拖累其他訂閱者。佇列塞滿時<b>不會</b>靜默丟棄:
    /// 那一條訂閱會以一筆說明「已漏事件,請重新訂閱並全量對帳」的失敗結束。
    /// Each subscriber has its own queue, so one slow consumer does not hold up the others. A full queue is
    /// <b>never</b> drained silently: that one subscription ends with a failure saying that events were lost and
    /// that it must resubscribe and reconcile in full.
    /// </remarks>
    public int SubscriberQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// 連線層佇列滿了之後怎麼辦。
    /// What the connection-layer queue does once it is full.
    /// </summary>
    /// <remarks>
    /// 預設 <see cref="BackpressureStrategy.Wait"/>,與行情串流相反。帳戶事件沒有「舊的不重要」這回事:
    /// 丟掉一則成交回報,本地部位就與交易所分岔,而帳面上的數字依然合理。
    /// <see cref="BackpressureStrategy.Wait"/> 把「丟訊息」換成「丟連線」,而丟連線會被偵測到、會重連、
    /// 會送出對帳訊號 —— 換句話說,它把一個無聲的錯誤換成一個有聲的錯誤。
    /// 實務上也走不到:讀取迴圈只做解析與非阻塞的入列,不會慢到把連線佇列塞滿。
    /// The default is <see cref="BackpressureStrategy.Wait"/>, the opposite of the market stream. There is no
    /// "the old one no longer matters" for account events: drop one fill and local positions diverge from the
    /// exchange's while the books stay plausible. <see cref="BackpressureStrategy.Wait"/> trades losing messages
    /// for losing the connection — and a lost connection is detected, reconnected, and announced with a
    /// reconciliation signal, which turns a silent failure into a loud one. In practice it is never reached: the
    /// read loop only parses and enqueues without blocking, and cannot fall far enough behind to fill the queue.
    /// </remarks>
    public BackpressureStrategy BackpressureStrategy { get; set; } = BackpressureStrategy.Wait;

    /// <summary>
    /// 檢查設定是否可用。
    /// Checks that the settings are usable.
    /// </summary>
    /// <returns>設定合法時為成功,否則為說明哪一項不合法的失敗。Success, or a failure naming the bad setting.</returns>
    public Result Validate()
    {
        if (ListenKeyKeepAliveInterval <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions(
                "憑證續期週期必須為正值,否則憑證會在 60 分鐘後過期而沒有人續期。The credential renewal interval must be positive; otherwise nothing renews the credential and it lapses after 60 minutes.");
        }

        // 續期週期一旦追上有效期,就等於沒有續期:計時器響的那一刻憑證剛好(或已經)過期。
        // A renewal interval that reaches the lifetime is no renewal at all: by the time the timer fires the
        // credential has just expired, or expired already.
        if (ListenKeyKeepAliveInterval >= ListenKeyLifetime)
        {
            return BinanceErrors.InvalidOptions(
                $"憑證續期週期 {ListenKeyKeepAliveInterval} 必須短於憑證有效期 {ListenKeyLifetime},否則計時器響之前憑證就已經過期。The credential renewal interval {ListenKeyKeepAliveInterval} must be shorter than the credential lifetime {ListenKeyLifetime}, or the credential expires before the timer fires.");
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions("握手逾時必須為正值。The connect timeout must be positive.");
        }

        if (IdleTimeout < TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions(
                "閒置逾時不可為負值;設為零表示不做存活偵測。The idle timeout must not be negative; zero disables liveness detection.");
        }

        if (ConnectionQueueCapacity <= 0)
        {
            return BinanceErrors.InvalidOptions("連線佇列容量必須為正整數。The connection queue capacity must be a positive integer.");
        }

        if (SubscriberQueueCapacity <= 0)
        {
            return BinanceErrors.InvalidOptions("訂閱者佇列容量必須為正整數。The subscriber queue capacity must be a positive integer.");
        }

        if (MaxReconnectAttempts is { } attempts && attempts < 0)
        {
            return BinanceErrors.InvalidOptions("重連次數上限不可為負值。The reconnect ceiling must not be negative.");
        }

        return Enum.IsDefined(BackpressureStrategy)
            ? Result.Success()
            : BinanceErrors.InvalidOptions("未定義的背壓策略。Undefined backpressure strategy.");
    }

    /// <summary>
    /// 建立連線層的設定。
    /// Builds the connection-layer settings.
    /// </summary>
    /// <param name="uri">要連線的位址。The address to dial.</param>
    /// <returns>連線層的設定。The connection-layer settings.</returns>
    /// <remarks>
    /// 刻意不設定應用層 ping。幣安沒有為 <c>/ws/{listenKey}</c> 定義過應用層的心跳訊息,
    /// 而 <c>Ozakboy.WebSockets</c> 說得很清楚:對不認得這種訊息的對方送出未定義的內容,
    /// 輕則被忽略,重則被視為協定違規而斷線 —— 在帳戶串流上斷線的代價是漏掉委託與成交。
    /// No application-level ping is configured. Binance defines no such heartbeat for
    /// <c>/ws/{listenKey}</c>, and <c>Ozakboy.WebSockets</c> is explicit about the risk: sending an undefined
    /// payload to a peer that does not recognise it is ignored at best and treated as a protocol violation at
    /// worst — and a disconnect on the account stream costs orders and fills.
    /// </remarks>
    internal WebSocketClientOptions CreateWebSocketOptions(Uri uri) => new()
    {
        Uri = uri,
        IdleTimeout = IdleTimeout,
        ConnectTimeout = ConnectTimeout,
        MaxReconnectAttempts = MaxReconnectAttempts,
        QueueCapacity = ConnectionQueueCapacity,
        BackpressureStrategy = BackpressureStrategy,
    };
}
