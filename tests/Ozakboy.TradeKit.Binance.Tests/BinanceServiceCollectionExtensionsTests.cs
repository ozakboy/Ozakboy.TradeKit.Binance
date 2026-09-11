using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Http;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Action<BinanceOptions>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(options =>
        {
            options.Environment = BinanceEnvironment.Testnet;
            options.ApiKey = "FAKE-API-KEY-NOT-A-REAL-CREDENTIAL";
            options.SecretKey = "FAKE-SECRET-NOT-A-REAL-CREDENTIAL";
            configure?.Invoke(options);
        });

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void ResolvesEveryPublicService()
    {
        using var provider = Build();

        Assert.IsNotNull(provider.GetRequiredService<BinanceOptions>());
        Assert.IsNotNull(provider.GetRequiredService<HttpPipelineClient>());
        Assert.IsNotNull(provider.GetRequiredService<BinanceExchangeInfoProvider>());
        Assert.IsNotNull(provider.GetRequiredService<IExchangeInfoProvider>());
        Assert.IsNotNull(provider.GetRequiredService<BinanceFuturesClient>());
    }

    [TestMethod]
    public void TheTradingRuleProviderIsASingletonSoTheCacheIsSharedNotRebuilt()
    {
        // 註冊成 scoped 或 transient 會讓每次解析都重抓一份約 1 MB 的 exchangeInfo。
        // A scoped or transient registration would refetch roughly a megabyte per resolution.
        using var provider = Build();

        Assert.AreSame(
            provider.GetRequiredService<BinanceExchangeInfoProvider>(),
            provider.GetRequiredService<BinanceExchangeInfoProvider>());

        Assert.AreSame(
            provider.GetRequiredService<BinanceExchangeInfoProvider>(),
            provider.GetRequiredService<IExchangeInfoProvider>());
    }

    [TestMethod]
    public void TheClientSharesTheRegisteredTradingRuleProvider()
    {
        using var provider = Build();

        Assert.AreSame(
            provider.GetRequiredService<BinanceExchangeInfoProvider>(),
            provider.GetRequiredService<BinanceFuturesClient>().ExchangeInfo);
    }

    [TestMethod]
    public void TheHttpClientIsBoundToTheChosenEnvironment()
    {
        using var provider = Build();

        var http = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(BinanceConstants.HttpClientName);

        Assert.AreEqual(BinanceEndpoints.Testnet.RestBaseUri, http.BaseAddress);
        Assert.IsTrue(provider.GetRequiredService<BinanceFuturesClient>().Endpoints.IsTestnet);
    }

    [TestMethod]
    public void InvalidOptionsFailAtRegistrationRatherThanAtTheFirstRequest()
    {
        var services = new ServiceCollection();

        var exception = Assert.ThrowsExactly<ArgumentException>(() => services.AddBinanceFutures(
            options => options.RecvWindow = TimeSpan.FromMinutes(5)));

        StringAssert.Contains(exception.Message, "recvWindow", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RejectsMissingArguments()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceServiceCollectionExtensions.AddBinanceFutures(null!, _ => { }));

        Assert.ThrowsExactly<ArgumentNullException>(() => new ServiceCollection().AddBinanceFutures(null!));
    }

    [TestMethod]
    public void RegistrationWorksWithoutCredentialsForPublicEndpointsOnly()
    {
        var services = new ServiceCollection();
        services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Mainnet);

        using var provider = services.BuildServiceProvider();

        Assert.IsFalse(provider.GetRequiredService<BinanceOptions>().HasCredentials);
        Assert.IsNotNull(provider.GetRequiredService<IExchangeInfoProvider>());
    }
}
