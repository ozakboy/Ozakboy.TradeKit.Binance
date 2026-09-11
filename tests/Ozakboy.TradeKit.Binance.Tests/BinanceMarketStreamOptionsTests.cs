using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 行情串流設定的驗證與轉換。
/// Validating and converting the market stream settings.
/// </summary>
[TestClass]
public sealed class BinanceMarketStreamOptionsTests
{
    private static readonly Uri StreamUri = new("wss://stream.binancefuture.com/stream", UriKind.Absolute);

    [TestMethod]
    public void TheDefaultsAreValid()
    {
        Assert.IsTrue(new BinanceMarketStreamOptions().Validate().IsSuccess);
    }

    [TestMethod]
    public void TheHeartbeatIsWellInsideTheIdleTimeoutByDefault()
    {
        // 心跳若不比閒置逾時短,冷清的串流會在兩次心跳之間被判死並無止境重連。
        // A heartbeat no shorter than the idle timeout leaves a quiet stream condemned between beats and
        // reconnecting for ever.
        var options = new BinanceMarketStreamOptions();

        Assert.IsLessThan(options.IdleTimeout, options.KeepAliveInterval);
    }

    [TestMethod]
    public void AHeartbeatSlowerThanTheIdleTimeoutIsRejected()
    {
        var options = new BinanceMarketStreamOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };

        var validation = options.Validate();

        Assert.IsTrue(validation.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, validation.Error.Code);
    }

    [TestMethod]
    public void ADisabledHeartbeatIsAllowed()
    {
        var options = new BinanceMarketStreamOptions { KeepAliveInterval = TimeSpan.Zero };

        Assert.IsTrue(options.Validate().IsSuccess);
        Assert.IsNull(CreateWebSocketOptions(options).ApplicationPingPayloadFactory);
    }

    [TestMethod]
    [DynamicData(nameof(InvalidOptions))]
    public void InvalidSettingsAreRejected(BinanceMarketStreamOptions options, string because)
    {
        var validation = options.Validate();

        Assert.IsTrue(validation.IsFailure, because);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, validation.Error.Code, because);
    }

    [TestMethod]
    public void TheConnectionLayerSettingsCarryEveryValueAcross()
    {
        var options = new BinanceMarketStreamOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(45),
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            ConnectTimeout = TimeSpan.FromSeconds(7),
            MaxReconnectAttempts = 3,
            QueueCapacity = 64,
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
        var created = CreateWebSocketOptions(new BinanceMarketStreamOptions());

        var payload = created.ApplicationPingPayloadFactory!();

        Assert.IsTrue(payload.Contains("LIST_SUBSCRIPTIONS", StringComparison.Ordinal), payload);
    }

    private static WebSocketClientOptions CreateWebSocketOptions(BinanceMarketStreamOptions options) =>
        options.CreateWebSocketOptions(StreamUri, () => BinanceStreamCommands.ListSubscriptions(1));

    public static IEnumerable<object[]> InvalidOptions =>
    [
        [new BinanceMarketStreamOptions { IdleTimeout = TimeSpan.Zero }, "閒置逾時為零"],
        [new BinanceMarketStreamOptions { ConnectTimeout = TimeSpan.Zero }, "握手逾時為零"],
        [new BinanceMarketStreamOptions { KeepAliveInterval = TimeSpan.FromSeconds(-1) }, "心跳間隔為負"],
        [new BinanceMarketStreamOptions { QueueCapacity = 0 }, "佇列容量為零"],
        [new BinanceMarketStreamOptions { MaxReconnectAttempts = -1 }, "重連次數上限為負"],
        [new BinanceMarketStreamOptions { BackpressureStrategy = (BackpressureStrategy)99 }, "背壓策略未定義"],
    ];
}
