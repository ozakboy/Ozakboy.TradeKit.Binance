using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流設定的驗證規則。
/// The validation rules of the user data stream settings.
/// </summary>
[TestClass]
public sealed class BinanceUserDataStreamOptionsTests
{
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
    public void ANegativeIdleTimeoutIsRejectedWhileZeroIsAccepted()
    {
        // 零是「不做存活偵測」,不是筆誤;負值才是筆誤。
        // Zero means liveness detection is off rather than being a typo; a negative value is the typo.
        Assert.IsTrue(
            new BinanceUserDataStreamOptions { IdleTimeout = TimeSpan.FromSeconds(-1) }.Validate().IsFailure);

        Assert.IsTrue(
            new BinanceUserDataStreamOptions { IdleTimeout = TimeSpan.Zero }.Validate().IsSuccess);
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

    [TestMethod]
    public void LivenessDetectionIsOffByDefault()
    {
        // 一個沒有委託、沒有成交、沒有資金費結算的帳戶本來就可以安靜好幾個小時,那是正常的沉默;
        // 在這種串流上設逾時,等於每隔那麼久就無故重連一次並逼上層做一次不必要的全量對帳。
        // An account with no orders, no fills, and no funding is entitled to hours of silence, and that silence
        // is normal; a timeout here means an unprompted reconnect that often and an unnecessary full
        // reconciliation each time.
        Assert.AreEqual(TimeSpan.Zero, new BinanceUserDataStreamOptions().IdleTimeout);
    }
}
