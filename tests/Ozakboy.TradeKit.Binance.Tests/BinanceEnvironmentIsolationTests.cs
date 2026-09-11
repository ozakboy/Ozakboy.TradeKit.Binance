using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 驗證「交易規則快取以環境為鍵」這件事在型別層面成立。
/// Verifies that the trading-rule cache really is keyed by environment at the type level.
/// </summary>
/// <remarks>
/// 這組測試針對的是一個沒有症狀的 bug:Testnet 的 <c>BTCUSDT</c> 步進是 <c>0.0001</c>、主網是 <c>0.001</c>,
/// 用錯的那一份校正出來的數量多數時候在兩邊都合法,只有某些尾數會被拒 ——
/// 而拒單訊息只會說「參數不合法」,看起來像偶發的網路問題。
/// These target a bug with no symptom: with step sizes of 0.0001 on Testnet and 0.001 on production, most
/// quantities are legal under both rule sets and only certain fractional values get rejected, with a message
/// that says nothing more than "invalid parameter".
/// </remarks>
[TestClass]
public sealed class BinanceEnvironmentIsolationTests
{
    [TestMethod]
    public async Task AProviderOnlyEverSeesItsOwnEnvironmentsRules()
    {
        var mainnetOptions = TestPipeline.CreateOptions(BinanceEnvironment.Mainnet);
        var testnetOptions = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();

        using var mainnetStub = StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet);
        using var testnetStub = StubHttpMessageHandler.Json(Fixtures.ExchangeInfoTestnet);

        var (mainnetClient, mainnetHttp) = TestPipeline.Create(mainnetOptions, mainnetStub, clock);
        var (testnetClient, testnetHttp) = TestPipeline.Create(testnetOptions, testnetStub, clock);

        using (mainnetHttp)
        using (testnetHttp)
        using (var mainnet = new BinanceExchangeInfoProvider(mainnetClient, mainnetOptions, clock))
        using (var testnet = new BinanceExchangeInfoProvider(testnetClient, testnetOptions, clock))
        {
            var live = (await mainnet.GetSymbolAsync("BTCUSDT")).GetValueOrThrow();
            var sandbox = (await testnet.GetSymbolAsync("BTCUSDT")).GetValueOrThrow();

            Assert.AreEqual(0.001m, live.StepSize);
            Assert.AreEqual(0.0001m, sandbox.StepSize);

            // 兩個實例各自打了自己的端點,沒有任何一份快取被共用。
            // Each instance called its own endpoint; no cache was shared.
            Assert.AreEqual(1, mainnetStub.CallCount);
            Assert.AreEqual(1, testnetStub.CallCount);

            Assert.AreEqual(BinanceEndpoints.Mainnet, mainnet.Endpoints);
            Assert.AreEqual(BinanceEndpoints.Testnet, testnet.Endpoints);
        }
    }

    [TestMethod]
    public async Task ASnapshotAlwaysCarriesTheEnvironmentItCameFrom()
    {
        // 快照被存進資料庫再讀回來時(§12.3 的每日快照),這個欄位是唯一還記得來源的東西。
        // When a snapshot is persisted and read back, this field is the only thing that still remembers where
        // the rules came from.
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();

        using var stub = StubHttpMessageHandler.Json(Fixtures.ExchangeInfoTestnet);
        var (client, http) = TestPipeline.Create(options, stub, clock);

        using (http)
        using (var provider = new BinanceExchangeInfoProvider(client, options, clock))
        {
            var snapshot = (await provider.GetSnapshotAsync()).GetValueOrThrow();

            Assert.AreEqual(BinanceEndpoints.Testnet, snapshot.Endpoints);
            Assert.IsTrue(snapshot.Endpoints.IsTestnet);
            Assert.AreNotEqual(BinanceEndpoints.Mainnet, snapshot.Endpoints);

            // 排除紀錄也屬於快照的一部分,持久化之後同樣說得出「這個商品當時為什麼不能用」。
            // The rejection log is part of the snapshot too, so a persisted copy still explains why a symbol
            // was unusable at the time.
            Assert.HasCount(1, snapshot.RejectedSymbols);
            Assert.AreEqual("ELSAUSDT", snapshot.RejectedSymbols[0].Name);
        }
    }

    [TestMethod]
    public void AClientRefusesATradingRuleProviderFromAnotherEnvironment()
    {
        // 共用交易規則來源是允許的,但只限同一個環境。這是唯一一條可能把兩個環境接在一起的路徑,
        // 所以在建構時就擋下來,而不是等到某張單因為步進值不符被拒才發現。
        // Sharing a provider is allowed within one environment only. This is the single path that could join
        // two environments, so it fails at construction rather than at the first rejected order.
        var mainnetOptions = TestPipeline.CreateOptions(BinanceEnvironment.Mainnet);
        var testnetOptions = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();

        using var stub = StubHttpMessageHandler.Json(Fixtures.ExchangeInfoTestnet);
        var (testnetClient, testnetHttp) = TestPipeline.Create(testnetOptions, stub, clock);

        using (testnetHttp)
        using (var testnetProvider = new BinanceExchangeInfoProvider(testnetClient, testnetOptions, clock))
        {
            var exception = Assert.ThrowsExactly<ArgumentException>(
                () => _ = new BinanceFuturesClient(testnetClient, mainnetOptions, testnetProvider, clock));

            StringAssert.Contains(exception.Message, "Testnet", StringComparison.Ordinal);
            StringAssert.Contains(exception.Message, "Mainnet", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void AClientAcceptsATradingRuleProviderFromTheSameEnvironment()
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();

        using var stub = StubHttpMessageHandler.Json(Fixtures.ExchangeInfoTestnet);
        var (client, http) = TestPipeline.Create(options, stub, clock);

        using (http)
        using (var provider = new BinanceExchangeInfoProvider(client, options, clock))
        using (var futures = new BinanceFuturesClient(client, options, provider, clock))
        {
            Assert.AreEqual(BinanceEndpoints.Testnet, futures.Endpoints);
            Assert.AreSame(provider, futures.ExchangeInfo);
            Assert.IsTrue(futures.Endpoints.IsTestnet);
        }
    }

    [TestMethod]
    public void ThereIsNoWayToHandASnapshotToAProviderOfAnotherEnvironment()
    {
        // 這一條守的是公開 API 的形狀:快取是實例的私有狀態,沒有任何公開成員可以塞快照進去。
        // 只要有人加了一個 setter 或一個接受 snapshot 的方法,這條測試就該被改掉 —— 而那正是該停下來想一想的時候。
        // This guards the shape of the public API: the cache is private instance state with no public member
        // that accepts a snapshot. Anyone adding a setter has to change this test, which is the moment to stop
        // and think.
        var members = typeof(BinanceExchangeInfoProvider)
            .GetMembers()
            .Where(member => member.DeclaringType == typeof(BinanceExchangeInfoProvider))
            .Select(member => member.Name)
            .ToArray();

        Assert.IsFalse(
            members.Any(name => name.StartsWith("set_", StringComparison.Ordinal)),
            "交易規則來源不應該有任何可寫入的公開屬性。");

        var methodsTakingASnapshot = typeof(BinanceExchangeInfoProvider)
            .GetMethods()
            .Where(method => method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(BinanceExchangeInfoSnapshot)))
            .ToArray();

        Assert.IsEmpty(methodsTakingASnapshot);
    }
}
