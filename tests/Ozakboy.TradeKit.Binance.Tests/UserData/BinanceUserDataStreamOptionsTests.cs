using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流設定的驗證規則。
/// The validation rules of the user data stream settings.
/// </summary>
[TestClass]
public sealed class BinanceUserDataStreamOptionsTests
{
    /// <summary>
    /// 測試用的連線位址。路徑段是假的 —— 真的位址含憑證,不該出現在任何測試資料以外的地方。
    /// The address used here. Its path segment is fake: a real one embeds the credential.
    /// </summary>
    private static readonly Uri StreamUri = new("wss://stream.binancefuture.com/ws/not-a-listen-key", UriKind.Absolute);

    [TestMethod]
    public void TheDefaultsAreValid()
    {
        Assert.IsTrue(new BinanceUserDataStreamOptions().Validate().IsSuccess);
    }

    [TestMethod]
    public void TheDefaultRenewalIntervalLeavesRoomForOneFailedAttempt()
    {
        // 續期週期取有效期的一半,才容得下一次失敗。設到接近 60 分鐘的話,一次網路抖動就等於憑證過期。
        // Half the lifetime is what leaves room for one failure. Close to 60 minutes, one network hiccup is an
        // expired credential.
        Assert.AreEqual(
            BinanceUserDataStreamOptions.ListenKeyLifetime / 2,
            BinanceUserDataStreamOptions.DefaultListenKeyKeepAliveInterval);
    }

    [TestMethod]
    public void ARenewalIntervalThatReachesTheLifetimeIsRejected()
    {
        // 計時器響的那一刻憑證剛好過期,等於完全沒有續期。
        // The credential expires exactly as the timer fires, which is no renewal at all.
        var options = new BinanceUserDataStreamOptions
        {
            ListenKeyKeepAliveInterval = BinanceUserDataStreamOptions.ListenKeyLifetime,
        };

        var validation = options.Validate();

        Assert.IsTrue(validation.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, validation.Error?.Code);
    }

    [TestMethod]
    public void ANonPositiveRenewalIntervalIsRejected()
    {
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { ListenKeyKeepAliveInterval = TimeSpan.Zero }
                .Validate()
                .IsFailure);
    }

    [TestMethod]
    public void ANonPositiveConnectTimeoutIsRejected()
    {
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { ConnectTimeout = TimeSpan.Zero }.Validate().IsFailure);
    }

    [TestMethod]
    public void LivenessDetectionIsOnByDefaultAndMatchesTheMarketStream()
    {
        // 心跳解決了「帳戶的正常沉默」與「死掉的連線」分不開的問題,所以預設開啟,並與行情串流同值 ——
        // 兩條串流用同一個心跳指令,同一個設定值不該在兩邊代表不同的意思。
        // The heartbeat is what tells an account's normal silence from a dead connection, so liveness is on by
        // default and matches the market stream: both use the same heartbeat, and one value should not mean two
        // things.
        var options = new BinanceUserDataStreamOptions();

        Assert.AreEqual(BinanceMarketStreamOptions.DefaultIdleTimeout, options.IdleTimeout);
        Assert.AreEqual(BinanceMarketStreamOptions.DefaultKeepAliveInterval, options.KeepAliveInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(90), options.IdleTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.KeepAliveInterval);
        Assert.IsLessThan(options.IdleTimeout, options.KeepAliveInterval);
    }

    [TestMethod]
    public void ANonPositiveIdleTimeoutIsRejected()
    {
        // 與行情串流一致:沒有閒置逾時,死掉的連線永遠不會被發現,而帳戶串流上那代表成交照樣發生、本地卻不知道。
        // As on the market stream: without an idle timeout a dead connection is never noticed, which on the
        // account stream means fills happening that the local side never learns about.
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { IdleTimeout = TimeSpan.Zero }.Validate().IsFailure);
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { IdleTimeout = TimeSpan.FromSeconds(-1) }.Validate().IsFailure);
    }

    [TestMethod]
    public void AHeartbeatSlowerThanTheIdleTimeoutIsRejected()
    {
        // 安靜的帳戶會在兩次心跳之間被判死並無止境重連,每一次都要求一次全量對帳。
        // A quiet account would be condemned between beats and reconnect for ever, each time demanding a full
        // reconciliation.
        var validation = new BinanceUserDataStreamOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        }.Validate();

        Assert.IsTrue(validation.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, validation.Error?.Code);
    }

    [TestMethod]
    public void ANegativeHeartbeatIsRejected()
    {
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { KeepAliveInterval = TimeSpan.FromSeconds(-1) }.Validate().IsFailure);
    }

    [TestMethod]
    public void ADisabledHeartbeatIsAllowedAndConfiguresNoPing()
    {
        var options = new BinanceUserDataStreamOptions { KeepAliveInterval = TimeSpan.Zero };

        Assert.IsTrue(options.Validate().IsSuccess);

        var created = CreateWebSocketOptions(options);

        Assert.AreEqual(TimeSpan.Zero, created.ApplicationPingInterval);
        Assert.IsNull(created.ApplicationPingPayloadFactory);
    }

    [TestMethod]
    public void TheConnectionLayerSettingsCarryEveryValueAcross()
    {
        var options = new BinanceUserDataStreamOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(45),
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            ConnectTimeout = TimeSpan.FromSeconds(7),
            MaxReconnectAttempts = 3,
            ConnectionQueueCapacity = 64,
            BackpressureStrategy = BackpressureStrategy.DropNewest,
        };

        var created = CreateWebSocketOptions(options);

        Assert.AreEqual(StreamUri, created.Uri);
        Assert.AreEqual(TimeSpan.FromSeconds(45), created.IdleTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(15), created.ApplicationPingInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(7), created.ConnectTimeout);
        Assert.AreEqual(3, created.MaxReconnectAttempts);
        Assert.AreEqual(64, created.QueueCapacity);
        Assert.AreEqual(BackpressureStrategy.DropNewest, created.BackpressureStrategy);
        Assert.IsTrue(created.Validate().IsSuccess);
    }

    [TestMethod]
    public void TheHeartbeatPayloadIsAListSubscriptionsCommand()
    {
        var created = CreateWebSocketOptions(new BinanceUserDataStreamOptions());

        Assert.AreEqual("""{"method":"LIST_SUBSCRIPTIONS","id":1}""", created.ApplicationPingPayloadFactory!());
    }

    [TestMethod]
    public void NonPositiveQueueCapacitiesAreRejected()
    {
        Assert.IsTrue(new BinanceUserDataStreamOptions { ConnectionQueueCapacity = 0 }.Validate().IsFailure);
        Assert.IsTrue(new BinanceUserDataStreamOptions { SubscriberQueueCapacity = 0 }.Validate().IsFailure);
    }

    [TestMethod]
    public void ANegativeReconnectCeilingIsRejected()
    {
        Assert.IsTrue(new BinanceUserDataStreamOptions { MaxReconnectAttempts = -1 }.Validate().IsFailure);
    }

    [TestMethod]
    public void TheDefaultRenewalRetryBackoffsAllFitInsideOneRenewalPeriod()
    {
        // 退避長過續期週期就永遠用不到:每一次都會跨過下一個排程時刻而被跳過,
        // 「有重試」就成了一句設定上寫著、實際上從來沒發生過的話。
        // A backoff longer than the renewal period can never be taken: every one would cross the next scheduled
        // instant and be skipped, leaving "it retries" true only in the settings.
        var options = new BinanceUserDataStreamOptions();

        Assert.IsTrue(options.Validate().IsSuccess);

        foreach (var backoff in options.ListenKeyRenewalRetryBackoffs)
        {
            Assert.IsLessThan(
                options.ListenKeyKeepAliveInterval,
                backoff,
                $"退避 {backoff} 長過續期週期,永遠用不到。");
        }

        Assert.AreEqual(
            TimeSpan.FromMinutes(1),
            options.ListenKeyRenewalRetryBackoffs[0],
            "第一次重試應該一分鐘後就發動,而不是等下一個三十分鐘。");
    }

    [TestMethod]
    public void ANonPositiveRenewalRetryBackoffIsRejected()
    {
        // 零或負值等於不等待就重打,一次網路中斷會變成一串連發的請求,而限流正是續期失敗的原因之一。
        // A zero or negative backoff retries immediately, turning one outage into a burst — and the rate limiter
        // is among the reasons a renewal fails at all.
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { ListenKeyRenewalRetryBackoffs = [TimeSpan.Zero] }
                .Validate()
                .IsFailure);

        Assert.IsTrue(
            new BinanceUserDataStreamOptions
            {
                ListenKeyRenewalRetryBackoffs = [TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1)],
            }
                .Validate()
                .IsFailure);
    }

    [TestMethod]
    public void AnEmptyRenewalRetryBackoffListIsAcceptedAndMeansNoRetry()
    {
        // 明確表示「不要重試」要有辦法說得出來,而且它與「漏設定成 null」必須分得開。
        // There has to be a way to say "do not retry", and it must be distinguishable from having left it null.
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { ListenKeyRenewalRetryBackoffs = [] }.Validate().IsSuccess);

        Assert.IsTrue(
            new BinanceUserDataStreamOptions { ListenKeyRenewalRetryBackoffs = null! }.Validate().IsFailure);
    }

    [TestMethod]
    public void AnUndefinedBackpressureStrategyIsRejected()
    {
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { BackpressureStrategy = (BackpressureStrategy)99 }
                .Validate()
                .IsFailure);
    }

    [TestMethod]
    public void TheConnectionQueueWaitsRatherThanDroppingByDefault()
    {
        // 與行情串流相反。帳戶事件沒有「舊的不重要」這回事:丟掉一則成交回報,本地部位就與交易所分岔。
        // The opposite of the market stream. There is no "the old one no longer matters" for account events:
        // drop a fill and local positions diverge from the exchange's.
        Assert.AreEqual(BackpressureStrategy.Wait, new BinanceUserDataStreamOptions().BackpressureStrategy);
    }

    private static WebSocketClientOptions CreateWebSocketOptions(BinanceUserDataStreamOptions options) =>
        options.CreateWebSocketOptions(StreamUri, static () => BinanceStreamCommands.ListSubscriptions(1));
}
