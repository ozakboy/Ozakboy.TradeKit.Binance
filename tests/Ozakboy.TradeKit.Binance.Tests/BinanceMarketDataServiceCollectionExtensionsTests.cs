using Microsoft.Extensions.DependencyInjection;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 行情來源的相依注入註冊。
/// The dependency injection registration of the market data feed.
/// </summary>
[TestClass]
public sealed class BinanceMarketDataServiceCollectionExtensionsTests
{
    [TestMethod]
    public void TheFeedIsResolvableThroughBothTheConcreteTypeAndTheAbstraction()
    {
        using var provider = Build(options => options.Environment = BinanceEnvironment.Testnet);

        var feed = provider.GetRequiredService<BinanceMarketDataFeed>();

        Assert.AreEqual(BinanceEndpoints.Testnet, feed.Endpoints);
        Assert.AreSame(feed, provider.GetRequiredService<IMarketDataFeed>());
    }

    [TestMethod]
    public void TheFeedIsASingletonSoOneHostKeepsOneSetOfConnections()
    {
        using var provider = Build(options => options.Environment = BinanceEnvironment.Testnet);

        Assert.AreSame(
            provider.GetRequiredService<BinanceMarketDataFeed>(),
            provider.GetRequiredService<BinanceMarketDataFeed>());
    }

    [TestMethod]
    public void TheFeedInheritsTheEnvironmentOfTheTradingRegistration()
    {
        // REST 打 Testnet、行情接主網這種組合無法在型別層面被組出來,註冊時也不該有第二個入口。
        // A REST-on-testnet, stream-on-production mix cannot be expressed in the type system, and the
        // registration must not reintroduce a second place to configure it.
        using var provider = Build(options => options.Environment = BinanceEnvironment.Mainnet);

        Assert.AreEqual(BinanceEndpoints.Mainnet, provider.GetRequiredService<BinanceMarketDataFeed>().Endpoints);
    }

    [TestMethod]
    public void StreamSettingsAreAppliedAndShared()
    {
        using var provider = Build(
            options => options.Environment = BinanceEnvironment.Testnet,
            stream => stream.UseFastMarkPriceUpdates = false);

        var streamOptions = provider.GetRequiredService<BinanceMarketStreamOptions>();

        Assert.IsFalse(streamOptions.UseFastMarkPriceUpdates);
        Assert.AreSame(streamOptions, provider.GetRequiredService<BinanceMarketDataFeed>().StreamOptions);
    }

    [TestMethod]
    public void InvalidStreamSettingsFailAtRegistrationRatherThanAtTheFirstSubscription()
    {
        // 延後到第一筆行情才爆,宿主會先看到「啟動成功」,幾秒後才在背景工作裡失敗。
        // Deferring the failure to the first frame lets the host see a successful start-up and then fail
        // seconds later inside a background task.
        var services = new ServiceCollection();

        services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Testnet);

        Assert.ThrowsExactly<ArgumentException>(
            () => services.AddBinanceMarketData(stream => stream.QueueCapacity = 0));
    }

    [TestMethod]
    public void ANullServiceCollectionIsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => BinanceMarketDataServiceCollectionExtensions
            .AddBinanceMarketData(null!));
    }

    private static ServiceProvider Build(
        Action<BinanceOptions> configure,
        Action<BinanceMarketStreamOptions>? configureStream = null)
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(configure);
        services.AddBinanceMarketData(configureStream);

        return services.BuildServiceProvider();
    }
}
