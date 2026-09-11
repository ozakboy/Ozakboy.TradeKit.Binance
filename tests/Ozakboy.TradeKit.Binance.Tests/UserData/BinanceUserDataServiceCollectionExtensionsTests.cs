using Microsoft.Extensions.DependencyInjection;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流的相依注入註冊。
/// The dependency injection registration of the user data stream.
/// </summary>
/// <remarks>
/// 容器一律以 <c>await using</c> 收尾。<see cref="BinanceUserDataFeed"/> 只實作
/// <see cref="IAsyncDisposable"/>(關閉憑證要打一次 <c>DELETE</c>,那是非同步的),而
/// <c>ServiceProvider.Dispose()</c> 遇到這種服務會直接擲例外。宿主端同理:用 <c>IHost</c> 沒問題,
/// 自己建容器的話要記得非同步收尾。
/// The container is always disposed with <c>await using</c>. <see cref="BinanceUserDataFeed"/> implements only
/// <see cref="IAsyncDisposable"/> — closing the credential is a <c>DELETE</c>, which is asynchronous — and
/// <c>ServiceProvider.Dispose()</c> throws outright on such a service. The same applies to a host: an
/// <c>IHost</c> handles it, and a hand-built container has to dispose asynchronously.
/// </remarks>
[TestClass]
public sealed class BinanceUserDataServiceCollectionExtensionsTests
{
    [TestMethod]
    public async Task TheStreamIsResolvableThroughBothTheConcreteTypeAndTheAbstraction()
    {
        await using var provider = Build(options => options.Environment = BinanceEnvironment.Testnet);

        var feed = provider.GetRequiredService<BinanceUserDataFeed>();

        Assert.AreEqual(BinanceEndpoints.Testnet, feed.Endpoints);
        Assert.AreSame(feed, provider.GetRequiredService<IUserDataFeed>());
    }

    [TestMethod]
    public async Task TheStreamIsASingletonSoOneAccountKeepsOneCredential()
    {
        // 帳戶同時只有一把 listenKey。兩個實例會互相搶那一把,而任何一方 DELETE 都會把另一方的串流
        // 一起弄斷,那一方只會看到「連線莫名其妙斷了」。
        // An account holds one listenKey at a time. Two instances contend for it, and a DELETE from either
        // kills the other's stream, which the other sees only as a connection that dropped for no reason.
        await using var provider = Build(options => options.Environment = BinanceEnvironment.Testnet);

        Assert.AreSame(
            provider.GetRequiredService<BinanceUserDataFeed>(),
            provider.GetRequiredService<BinanceUserDataFeed>());
    }

    [TestMethod]
    public async Task TheStreamInheritsTheEnvironmentOfTheTradingRegistration()
    {
        await using var provider = Build(options => options.Environment = BinanceEnvironment.Mainnet);

        Assert.AreEqual(BinanceEndpoints.Mainnet, provider.GetRequiredService<BinanceUserDataFeed>().Endpoints);
    }

    [TestMethod]
    public async Task StreamSettingsAreAppliedAndShared()
    {
        await using var provider = Build(
            options => options.Environment = BinanceEnvironment.Testnet,
            stream => stream.SubscriberQueueCapacity = 64);

        var streamOptions = provider.GetRequiredService<BinanceUserDataStreamOptions>();

        Assert.AreEqual(64, streamOptions.SubscriberQueueCapacity);
        Assert.AreSame(streamOptions, provider.GetRequiredService<BinanceUserDataFeed>().StreamOptions);
    }

    [TestMethod]
    public void InvalidStreamSettingsFailAtRegistrationRatherThanAtTheFirstSubscription()
    {
        // 延後到第一則帳戶事件才爆,宿主會先看到「啟動成功」,而真正發現的時機是某一次成交沒有進來。
        // Deferring the failure lets the host see a successful start-up, and the real discovery happens when
        // some fill fails to arrive.
        var services = new ServiceCollection();

        services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Testnet);

        Assert.ThrowsExactly<ArgumentException>(
            () => services.AddBinanceUserData(stream => stream.SubscriberQueueCapacity = 0));
    }

    [TestMethod]
    public void ANullServiceCollectionIsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceUserDataServiceCollectionExtensions.AddBinanceUserData(null!));
    }

    private static ServiceProvider Build(
        Action<BinanceOptions> configure,
        Action<BinanceUserDataStreamOptions>? configureStream = null)
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(configure);
        services.AddBinanceUserData(configureStream);

        return services.BuildServiceProvider();
    }
}
