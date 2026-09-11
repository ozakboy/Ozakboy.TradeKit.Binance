using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 使用者資料串流的設定:憑證續期週期、連線行為與各層佇列容量。
/// The settings of the user data stream: how often the credential is renewed, how the connection behaves, and
/// how deep each queue is.
/// </summary>
/// <remarks>
/// <para>
/// 心跳間隔、閒置逾時與兩者的驗證規則刻意與 <see cref="BinanceMarketStreamOptions"/> 一致:
/// 兩條串流用的是同一個心跳指令、同一套存活判定,規則一旦分岔,同一個設定值在兩邊就會有不同的意思。
/// The heartbeat interval, the idle timeout, and their validation deliberately match
/// <see cref="BinanceMarketStreamOptions"/>: both streams use the same heartbeat command and the same liveness
/// test, and once the rules diverge the same value means different things on each side.
/// </para>
/// </remarks>
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
    /// 心跳間隔的預設值,與行情串流相同。
    /// The default heartbeat interval, the same as the market stream's.
    /// </summary>
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 閒置逾時的預設值,取心跳間隔的三倍,容許連續漏掉兩次心跳才判定連線已死。
    /// The default idle timeout, three times the heartbeat interval, so two heartbeats may be missed in a row
    /// before the connection is declared dead.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(90);

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
    /// 心跳間隔。設為 <see cref="TimeSpan.Zero"/> 表示不送心跳。
    /// The heartbeat interval; <see cref="TimeSpan.Zero"/> disables it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 心跳送的是 <c>{"method":"LIST_SUBSCRIPTIONS","id":N}</c>,它一定會有回覆,而回覆會刷新閒置計時。
    /// 帳戶本來就可以安靜好幾個小時,少了心跳,<see cref="IdleTimeout"/> 會把一條健康的連線判死,
    /// 每隔那麼久就無故重連一次,並且每次都送出一則 <see cref="ResyncReason.Reconnected"/>
    /// 逼上層做一次不必要的全量對帳。<b>關掉心跳而留著閒置逾時,正是這個組合。</b>
    /// The heartbeat sends <c>{"method":"LIST_SUBSCRIPTIONS","id":N}</c>, which always draws a reply, and the reply
    /// refreshes the idle clock. An account is entitled to hours of silence; without the heartbeat,
    /// <see cref="IdleTimeout"/> condemns a healthy connection, reconnects that often for no reason, and raises a
    /// <see cref="ResyncReason.Reconnected"/> each time that forces an unnecessary full reconciliation upstairs.
    /// <b>Disabling the heartbeat while keeping the idle timeout is exactly that combination.</b>
    /// </para>
    /// <para>
    /// 幣安對單一連線的進站訊息限制是每秒十則,這個心跳一分鐘兩次,離上限很遠。
    /// Binance caps incoming messages at ten per second per connection; twice a minute is nowhere near it.
    /// </para>
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; set; } = DefaultKeepAliveInterval;

    /// <summary>
    /// 多久沒收到任何訊息就判定連線已死並重連。
    /// How long without any message before the connection is treated as dead and re-established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>為什麼一定要有。</b> 連線若「握手成功、狀態顯示已連線、資料流卻被中介設備吃掉」,
    /// 少了存活偵測就發現不了,而帳戶串流上發現不了代表成交照樣發生、本地卻完全不知道。
    /// 沒有它的時候只有兩道很晚的緩衝:憑證每 30 分鐘續期一次(但驗的是 REST 不是 WebSocket),
    /// 以及憑證滿 60 分鐘後交易所主動斷開。
    /// <b>Why it has to exist.</b> A connection whose handshake succeeded, whose state reads connected, and whose
    /// data flow is being swallowed by something in the path goes unnoticed without liveness detection — and
    /// unnoticed on this stream means fills happening that the local side never learns about. Without it the only
    /// backstops come late: the 30-minute renewal, which exercises REST rather than the socket, and the exchange
    /// dropping the connection once the credential reaches 60 minutes.
    /// </para>
    /// <para>
    /// <b>為什麼現在可以預設開啟。</b> 原本預設關閉,理由是「帳戶正常的沉默」與「死掉的連線」無從分辨。
    /// 心跳解決了這一點:Testnet 實測,在 <c>/ws/{listenKey}</c> 上送 <c>LIST_SUBSCRIPTIONS</c>,
    /// 56–111 ms 內就收到回覆,與行情串流的行為相同。所以這裡比照行情串流,心跳 30 秒、閒置逾時 90 秒。
    /// <b>Why it can now default to on.</b> It used to default to off because an account's normal silence could
    /// not be told apart from a dead connection. The heartbeat settles that: measured on the testnet, a
    /// <c>LIST_SUBSCRIPTIONS</c> sent on <c>/ws/{listenKey}</c> is answered within 56–111 ms, just as on the market
    /// stream. The defaults therefore follow the market stream: a 30-second heartbeat and a 90-second idle timeout.
    /// </para>
    /// <para>
    /// <b>那則回覆帶著憑證。</b> 實測的回覆是 <c>{"result":["&lt;listenKey&gt;"],"id":N}</c>,也就是每 30 秒
    /// 一則帶著 listenKey 的訊息。解析器看到「有 <c>id</c>、沒有 <c>e</c>」就直接判為忽略,<c>result</c>
    /// 不讀也不轉述,見 <c>BinanceUserDataReader</c>。
    /// <b>That reply carries the credential.</b> The measured reply is
    /// <c>{"result":["&lt;listenKey&gt;"],"id":N}</c> — a frame holding the listenKey every 30 seconds. The reader
    /// ignores anything with an <c>id</c> and no <c>e</c> outright, never reading or relaying <c>result</c>; see
    /// <c>BinanceUserDataReader</c>.
    /// </para>
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = DefaultIdleTimeout;

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

        if (IdleTimeout <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions(
                "閒置逾時必須為正值,否則死掉的連線永遠不會被發現。The idle timeout must be positive; otherwise a dead connection is never noticed.");
        }

        if (KeepAliveInterval < TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions("心跳間隔不可為負值。The keep-alive interval must not be negative.");
        }

        // 心跳比閒置逾時還慢等於沒有心跳:安靜的帳戶會在兩次心跳之間就被判死,然後無止境地重連,
        // 每一次都附帶一則要求全量對帳的訊號。
        // A heartbeat slower than the idle timeout is no heartbeat at all: a quiet account is condemned between
        // two beats and reconnects for ever, each time with a signal demanding a full reconciliation.
        if (KeepAliveInterval > TimeSpan.Zero && KeepAliveInterval >= IdleTimeout)
        {
            return BinanceErrors.InvalidOptions(
                $"心跳間隔 {KeepAliveInterval} 必須短於閒置逾時 {IdleTimeout},否則安靜的帳戶會在兩次心跳之間被判定斷線並無止境重連。The keep-alive interval {KeepAliveInterval} must be shorter than the idle timeout {IdleTimeout}, or a quiet account is declared dead between beats and reconnects for ever.");
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
    /// <param name="keepAlivePayloadFactory">
    /// 心跳訊息的產生器,每次送出時呼叫一次,以便換新的請求編號。
    /// Produces the heartbeat message, called once per beat so that each carries a fresh request id.
    /// </param>
    /// <returns>連線層的設定。The connection-layer settings.</returns>
    /// <remarks>
    /// 應用層 ping 用的是幣安<b>本來就有</b>的控制指令 <c>LIST_SUBSCRIPTIONS</c>,不是自創的訊息。
    /// <c>Ozakboy.WebSockets</c> 警告過:對不認得的對方送未定義的內容,重則被當成協定違規而斷線;
    /// 這條限制因此不適用 —— <c>/ws/{listenKey}</c> 認得這個指令,Testnet 實測每一次都有回覆,
    /// 也沒有因此斷線。心跳間隔為零時不設定 ping,連產生器都不交出去。
    /// The application-level ping is <c>LIST_SUBSCRIPTIONS</c>, a control command Binance <b>already defines</b>,
    /// not an invented payload. <c>Ozakboy.WebSockets</c> warns that sending undefined content to a peer that does
    /// not recognise it can be treated as a protocol violation; that caveat does not apply here, because
    /// <c>/ws/{listenKey}</c> recognises the command — measured on the testnet, every one was answered and none
    /// caused a disconnect. With a zero interval no ping is configured and the factory is not handed over at all.
    /// </remarks>
    internal WebSocketClientOptions CreateWebSocketOptions(Uri uri, Func<string> keepAlivePayloadFactory)
    {
        var options = new WebSocketClientOptions
        {
            Uri = uri,
            IdleTimeout = IdleTimeout,
            ConnectTimeout = ConnectTimeout,
            MaxReconnectAttempts = MaxReconnectAttempts,
            QueueCapacity = ConnectionQueueCapacity,
            BackpressureStrategy = BackpressureStrategy,
        };

        if (KeepAliveInterval > TimeSpan.Zero)
        {
            options.ApplicationPingInterval = KeepAliveInterval;
            options.ApplicationPingPayloadFactory = keepAlivePayloadFactory;
        }

        return options;
    }
}
