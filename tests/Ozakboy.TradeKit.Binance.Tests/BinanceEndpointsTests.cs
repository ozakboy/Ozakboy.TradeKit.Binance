namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceEndpointsTests
{
    [TestMethod]
    public void MainnetPointsAtTheProductionHosts()
    {
        Assert.AreEqual("https://fapi.binance.com/", BinanceEndpoints.Mainnet.RestBaseUri.AbsoluteUri);
        Assert.AreEqual("wss://fstream.binance.com/", BinanceEndpoints.Mainnet.WebSocketBaseUri.AbsoluteUri);
        Assert.IsFalse(BinanceEndpoints.Mainnet.IsTestnet);
    }

    [TestMethod]
    public void TestnetPointsAtTheTestnetHosts()
    {
        Assert.AreEqual("https://testnet.binancefuture.com/", BinanceEndpoints.Testnet.RestBaseUri.AbsoluteUri);
        Assert.AreEqual("wss://stream.binancefuture.com/", BinanceEndpoints.Testnet.WebSocketBaseUri.AbsoluteUri);
        Assert.IsTrue(BinanceEndpoints.Testnet.IsTestnet);
    }

    [TestMethod]
    public void RestAndWebSocketAlwaysComeFromTheSameEnvironment()
    {
        // 這一組斷言看起來像廢話,但它守的正是這個型別存在的理由:
        // 只要有人加了一個能單獨設定 REST 或 WS 的入口,這裡就會壞掉。
        // The assertions look trivial, but they guard the reason the type exists: anyone adding a way to set
        // REST or WebSocket independently breaks this.
        foreach (var environment in Enum.GetValues<BinanceEnvironment>())
        {
            var endpoints = BinanceEndpoints.For(environment);
            var testnetHost = endpoints.RestBaseUri.Host.Contains("testnet", StringComparison.Ordinal);

            Assert.AreEqual(testnetHost, endpoints.IsTestnet);
            Assert.AreEqual(
                endpoints.IsTestnet,
                endpoints.WebSocketBaseUri.Host.Contains("binancefuture", StringComparison.Ordinal),
                $"{endpoints.DisplayName} 的 REST 與 WebSocket 指向了不同的環境。");
        }
    }

    [TestMethod]
    public void ForReturnsTheSharedInstance()
    {
        Assert.AreSame(BinanceEndpoints.Mainnet, BinanceEndpoints.For(BinanceEnvironment.Mainnet));
        Assert.AreSame(BinanceEndpoints.Testnet, BinanceEndpoints.For(BinanceEnvironment.Testnet));
    }

    [TestMethod]
    public void ForThrowsOnUndefinedEnvironment()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceEndpoints.For((BinanceEnvironment)99));
    }

    [TestMethod]
    public void MainnetAndTestnetAreNeverEqual()
    {
        // 兩者相等就代表快取可以互用,而那正是「Testnet 的 stepSize 送到主網」的前提。
        // Equality would mean the caches are interchangeable, which is the precondition for sending a Testnet
        // step size to production.
        Assert.AreNotEqual(BinanceEndpoints.Mainnet, BinanceEndpoints.Testnet);
    }

    [TestMethod]
    public void CreateOverrideProducesAUsableSet()
    {
        var result = BinanceEndpoints.CreateOverride(
            "本機重播 / local replay",
            new Uri("https://localhost:5001", UriKind.Absolute),
            new Uri("wss://localhost:5002", UriKind.Absolute),
            isTestnet: true);

        Assert.IsTrue(result.TryGetValue(out var endpoints));
        Assert.AreEqual("本機重播 / local replay", endpoints.DisplayName);
        Assert.AreEqual("本機重播 / local replay", endpoints.ToString());
        Assert.IsTrue(endpoints.IsTestnet);
        Assert.AreNotEqual(BinanceEndpoints.Mainnet, endpoints);
    }

    [TestMethod]
    public void CreateOverrideRejectsABlankName()
    {
        var result = BinanceEndpoints.CreateOverride(
            "   ",
            new Uri("https://localhost:5001"),
            new Uri("wss://localhost:5002"),
            isTestnet: true);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, result.Error!.Code);
    }

    [TestMethod]
    public void CreateOverrideRejectsRelativeAddresses()
    {
        var rest = BinanceEndpoints.CreateOverride(
            "x",
            new Uri("/fapi", UriKind.Relative),
            new Uri("wss://localhost:5002"),
            isTestnet: true);

        var socket = BinanceEndpoints.CreateOverride(
            "x",
            new Uri("https://localhost:5001"),
            new Uri("/ws", UriKind.Relative),
            isTestnet: true);

        Assert.IsTrue(rest.IsFailure);
        Assert.IsTrue(socket.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, rest.Error!.Code);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, socket.Error!.Code);
    }
}
