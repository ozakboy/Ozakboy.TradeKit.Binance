using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 行情串流連線的設定。
/// The settings of a market stream connection.
/// </summary>
/// <remarks>
/// <para>
/// 重連、退避、重連後重放訂閱、有界佇列背壓這些都由 <c>Ozakboy.WebSockets</c> 負責,這裡不重做,
/// 只設定幣安這一側需要調整的幾個值。
/// Reconnection, backoff, subscription replay, and bounded-queue backpressure all belong to
/// <c>Ozakboy.WebSockets</c> and are not reimplemented here; this type only tunes the few values that the
/// Binance side has an opinion about.
/// </para>
/// <para>
/// <b>存活偵測為什麼要靠心跳。</b> <see cref="System.Net.WebSockets.ClientWebSocket"/> 會自動回應對方的
/// ping,應用層完全看不到,所以唯一能判斷連線死活的訊號是「多久沒收到任何訊息」。
/// 但 K 線推送只在有成交時才來,冷門標的可以安靜好幾分鐘 —— 這時候閒置逾時會把一條健康的連線
/// 判成死的,然後進入「斷線、重連、又被判死」的無效迴圈。因此這裡主動送
/// <c>LIST_SUBSCRIPTIONS</c>:它一定會有回覆,回覆會更新閒置計時,冷清的市場就不會被誤殺,
/// 而真正死掉的連線仍然會在 <see cref="IdleTimeout"/> 之內被抓到。
/// <b>Why liveness needs a heartbeat.</b> <see cref="System.Net.WebSockets.ClientWebSocket"/> answers the
/// peer's pings by itself, invisibly to the application, so the only available liveness signal is how long it
/// has been since any message arrived. Kline pushes, however, only happen when trades happen, and a quiet
/// symbol can go minutes without one — at which point the idle timeout condemns a healthy connection and the
/// client settles into a disconnect-reconnect-condemn loop. A <c>LIST_SUBSCRIPTIONS</c> is therefore sent on a
/// timer: it always draws a reply, the reply refreshes the idle clock, a quiet market is no longer mistaken
/// for a dead one, and a genuinely dead connection is still caught within <see cref="IdleTimeout"/>.
/// </para>
/// </remarks>
public sealed class BinanceMarketStreamOptions
{
    /// <summary>
    /// 心跳間隔的預設值。
    /// The default heartbeat interval.
    /// </summary>
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 閒置逾時的預設值,取心跳間隔的三倍,容許連續漏掉兩次心跳才判定連線已死。
    /// The default idle timeout, three times the heartbeat interval, so two heartbeats may be missed in a row
    /// before the connection is declared dead.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 心跳間隔。設為 <see cref="TimeSpan.Zero"/> 表示不送心跳。
    /// The heartbeat interval; <see cref="TimeSpan.Zero"/> disables it.
    /// </summary>
    /// <remarks>
    /// 幣安對單一連線的進站訊息限制是每秒十則,這個心跳一分鐘兩次,離上限很遠。
    /// Binance caps incoming messages at ten per second per connection; twice a minute is nowhere near it.
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; set; } = DefaultKeepAliveInterval;

    /// <summary>
    /// 多久沒收到任何訊息就判定連線已死並重連。
    /// How long without any message before the connection is treated as dead and re-established.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = DefaultIdleTimeout;

    /// <summary>
    /// 握手逾時。
    /// The handshake timeout.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 重連次數上限,<see langword="null"/> 表示不限次數。
    /// The reconnect ceiling; <see langword="null"/> means unlimited.
    /// </summary>
    /// <remarks>
    /// 預設不限次數。行情中斷通常是網路或交易所維護,會自己好轉;設上限則會讓長一點的維護直接把串流
    /// 終結掉,而終結之後不會有人自動把它接回來。非暫時性的失敗不受這個值影響 ——
    /// <c>Ozakboy.WebSockets</c> 對那種失敗一次都不重試。
    /// Unlimited by default. A market outage is usually a network hiccup or exchange maintenance and heals on
    /// its own, whereas a ceiling lets a longer maintenance window end the stream for good, with nothing to
    /// bring it back. Non-transient failures are unaffected: <c>Ozakboy.WebSockets</c> does not retry those at
    /// all.
    /// </remarks>
    public int? MaxReconnectAttempts { get; set; }

    /// <summary>
    /// 待處理訊息的佇列容量。
    /// The capacity of the pending-message queue.
    /// </summary>
    public int QueueCapacity { get; set; } = 1024;

    /// <summary>
    /// 佇列滿了之後怎麼辦。
    /// What happens once the queue is full.
    /// </summary>
    /// <remarks>
    /// 預設丟最舊的。行情的價值隨時間遞減,留新的比留舊的有用;而且丟棄不會被藏起來 ——
    /// <see cref="BinanceMarketDataFeed"/> 會把丟棄補成串流上的一筆失敗,消費端看得到缺口。
    /// The oldest is dropped by default: market data loses value with age, so keeping the newer message is the
    /// better trade. The drop is not hidden either — <see cref="BinanceMarketDataFeed"/> republishes it as a
    /// failure on the stream so the consumer sees the gap.
    /// </remarks>
    public BackpressureStrategy BackpressureStrategy { get; set; } = BackpressureStrategy.DropOldest;

    /// <summary>
    /// 標記價是否使用每秒更新的串流,預設是。<see langword="false"/> 為每三秒一次。
    /// Whether mark prices use the one-second stream; on by default. <see langword="false"/> updates every
    /// three seconds.
    /// </summary>
    /// <remarks>
    /// 標記價是未實現損益與強平距離的計算基準,風控看的就是它。每秒與每三秒的流量差異對幾十檔標的
    /// 來說可以忽略,但「部位離強平還有多遠」這個數字晚三秒才更新,在急跌時差很多。
    /// The mark price is what unrealised PnL and liquidation distance are computed from, and it is what risk
    /// management watches. The bandwidth difference between one and three seconds is negligible for a few dozen
    /// symbols, whereas learning how close a position is to liquidation three seconds late matters a great deal
    /// during a fast move.
    /// </remarks>
    public bool UseFastMarkPriceUpdates { get; set; } = true;

    /// <summary>
    /// 檢查設定是否可用。
    /// Checks that the settings are usable.
    /// </summary>
    /// <returns>設定合法時為成功,否則為說明哪一項不合法的失敗。Success, or a failure naming the bad setting.</returns>
    public Result Validate()
    {
        if (IdleTimeout <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions(
                "閒置逾時必須為正值,否則死掉的連線永遠不會被發現。The idle timeout must be positive; otherwise a dead connection is never noticed.");
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions("握手逾時必須為正值。The connect timeout must be positive.");
        }

        if (KeepAliveInterval < TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions("心跳間隔不可為負值。The keep-alive interval must not be negative.");
        }

        // 心跳比閒置逾時還慢等於沒有心跳:冷清的串流會在兩次心跳之間就被判死,然後無止境地重連。
        // A heartbeat slower than the idle timeout is no heartbeat at all: a quiet stream is condemned between
        // two beats and reconnects for ever.
        if (KeepAliveInterval > TimeSpan.Zero && KeepAliveInterval >= IdleTimeout)
        {
            return BinanceErrors.InvalidOptions(
                $"心跳間隔 {KeepAliveInterval} 必須短於閒置逾時 {IdleTimeout},否則安靜的串流會在兩次心跳之間被判定斷線並無止境重連。The keep-alive interval {KeepAliveInterval} must be shorter than the idle timeout {IdleTimeout}, or a quiet stream is declared dead between beats and reconnects for ever.");
        }

        if (QueueCapacity <= 0)
        {
            return BinanceErrors.InvalidOptions("佇列容量必須為正整數。The queue capacity must be a positive integer.");
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
    internal WebSocketClientOptions CreateWebSocketOptions(Uri uri, Func<string> keepAlivePayloadFactory)
    {
        var options = new WebSocketClientOptions
        {
            Uri = uri,
            IdleTimeout = IdleTimeout,
            ConnectTimeout = ConnectTimeout,
            MaxReconnectAttempts = MaxReconnectAttempts,
            QueueCapacity = QueueCapacity,
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
