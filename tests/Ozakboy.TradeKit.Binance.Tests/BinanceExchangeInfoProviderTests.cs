using System.Net;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceExchangeInfoProviderTests
{
    private static (BinanceExchangeInfoProvider Provider, StubHttpMessageHandler Stub, TestClock Clock, HttpClient Http)
        Create(StubHttpMessageHandler stub, BinanceEnvironment environment = BinanceEnvironment.Mainnet)
    {
        var options = TestPipeline.CreateOptions(environment);
        var clock = TestClock.AtFixedInstant();
        var (client, http) = TestPipeline.Create(options, stub, clock);

        return (new BinanceExchangeInfoProvider(client, options, clock), stub, clock, http);
    }

    [TestMethod]
    public async Task FetchesTheRulesOnceAndServesTheRestFromCache()
    {
        var (provider, stub, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            Assert.IsTrue((await provider.GetSymbolsAsync()).IsSuccess);
            Assert.IsTrue((await provider.GetSymbolAsync("BTCUSDT")).IsSuccess);
            Assert.IsTrue((await provider.GetSymbolDetailAsync("ETHUSDT")).IsSuccess);

            // 交易規則是全系統共用的資料。每次查詢都重抓一份約 1 MB 的回應只會吃掉限流額度。
            // The rules are shared by the whole system; refetching a megabyte per lookup only spends quota.
            Assert.AreEqual(1, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task RefetchesOnceTheSnapshotHasExpired()
    {
        var (provider, stub, clock, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            Assert.IsTrue((await provider.GetSymbolsAsync()).IsSuccess);

            clock.Advance(TimeSpan.FromHours(23));
            Assert.IsTrue((await provider.GetSymbolsAsync()).IsSuccess);
            Assert.AreEqual(1, stub.CallCount);

            // §12.3 要求每日更新一次。幣安會新增商品、調整 stepSize、下架商品,過期的規則會算出
            // 交易所已經不接受的價量。
            // The specification calls for a daily refresh: Binance adds symbols, changes step sizes, and
            // delists, and stale rules normalise to values the exchange no longer accepts.
            clock.Advance(TimeSpan.FromHours(1));
            Assert.IsTrue((await provider.GetSymbolsAsync()).IsSuccess);
            Assert.AreEqual(2, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task RefreshForcesAFetchEvenWhenTheCacheIsFresh()
    {
        var (provider, stub, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            Assert.IsTrue((await provider.GetSnapshotAsync()).IsSuccess);
            Assert.IsTrue((await provider.RefreshAsync()).IsSuccess);

            Assert.AreEqual(2, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task AFailedRefreshKeepsTheRulesItAlreadyHas()
    {
        // 過期的規則遠勝於沒有規則:清空之後每一次校正都會失敗,交易所抖一下就讓整個系統停擺。
        // Stale rules beat no rules; clearing them would let one hiccup at the exchange stop everything.
        var stub = new StubHttpMessageHandler((_, attempt) => attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Fixtures.ExchangeInfoMainnet),
            }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"code":-1001,"msg":"Internal error."}"""),
            });

        var (provider, _, _, http) = Create(stub);

        using (provider)
        using (http)
        {
            Assert.IsTrue((await provider.GetSnapshotAsync()).IsSuccess);

            var refreshed = await provider.RefreshAsync();
            Assert.IsTrue(refreshed.IsFailure);
            Assert.AreEqual(TradeErrorCodes.ExchangeUnavailable, refreshed.Error!.Code);
            Assert.IsTrue(refreshed.Error.IsTransient);

            // 舊快照還在。
            // The previous snapshot is still there.
            Assert.IsTrue((await provider.GetSnapshotAsync()).IsSuccess);
        }
    }

    [TestMethod]
    public async Task ConcurrentCallersShareOneFetch()
    {
        var (provider, stub, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            var calls = Enumerable.Range(0, 16).Select(_ => provider.GetSnapshotAsync()).ToArray();
            var results = await Task.WhenAll(calls);

            Assert.IsTrue(results.All(result => result.IsSuccess));
            Assert.AreEqual(1, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task DeclaresTheExchangeInfoWeightAndCallsTheRightPath()
    {
        var (provider, stub, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            _ = await provider.GetSymbolsAsync();

            Assert.AreEqual("/fapi/v1/exchangeInfo", stub.LastRequest.RequestUri!.AbsolutePath);
            Assert.AreEqual(HttpMethod.Get, stub.LastRequest.Method);

            // 公開端點不該帶金鑰標頭。
            // A public endpoint must not carry the key header.
            Assert.IsFalse(stub.LastRequest.Headers.ContainsKey("X-MBX-APIKEY"));
        }
    }

    [TestMethod]
    public async Task ServerTimeAlwaysGoesToTheExchangeAndNeverToTheCache()
    {
        // 這個方法存在的目的是偵測本機時鐘偏移。回傳幾小時前快取下來的時間會讓它完全失去意義。
        // The method exists to detect local clock drift; a cached answer defeats it entirely.
        var (provider, stub, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ServerTime));

        using (provider)
        using (http)
        {
            var first = await provider.GetServerTimeAsync();
            var second = await provider.GetServerTimeAsync();

            Assert.IsTrue(first.IsSuccess);
            Assert.IsTrue(second.IsSuccess);
            Assert.AreEqual(2, stub.CallCount);
            Assert.AreEqual("/fapi/v1/time", stub.LastRequest.RequestUri!.AbsolutePath);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789110429074L), first.GetValueOrThrow());
        }
    }

    [TestMethod]
    public async Task UnknownSymbolsAreReportedAsNotFoundWithTheEnvironmentNamed()
    {
        var (provider, _, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            var result = await provider.GetSymbolAsync("NOSUCHUSDT");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.SymbolNotFound, result.Error!.Code);

            var detail = await provider.GetSymbolDetailAsync("NOSUCHUSDT");
            Assert.IsTrue(detail.Error!.TryGetData(BinanceErrorDataKeys.Environment, out var environment));
            Assert.AreEqual(BinanceEndpoints.Mainnet.DisplayName, environment);
        }
    }

    [TestMethod]
    public async Task ABlankSymbolIsAnInvalidQueryNotANotFound()
    {
        var (provider, stub, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (provider)
        using (http)
        {
            var result = await provider.GetSymbolDetailAsync("   ");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task ExchangeErrorsArriveAlreadyTranslated()
    {
        var stub = StubHttpMessageHandler.Json(
            """{"code":-1003,"msg":"Too many requests."}""",
            HttpStatusCode.TooManyRequests);

        var (provider, _, _, http) = Create(stub);

        using (provider)
        using (http)
        {
            var result = await provider.GetSymbolsAsync();

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.RateLimited, result.Error!.Code);
            Assert.IsTrue(result.Error.IsTransient);
            Assert.IsTrue(result.Error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var apiCode));
            Assert.AreEqual(-1003L, apiCode);
        }
    }

    [TestMethod]
    public async Task AnUnreadableResponseIsAFailureRatherThanAnEmptyRuleSet()
    {
        var (provider, _, _, http) = Create(StubHttpMessageHandler.Json("{\"serverTime\":1}"));

        using (provider)
        using (http)
        {
            var result = await provider.GetSymbolsAsync();

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task UsingADisposedProviderThrows()
    {
        var (provider, _, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.ExchangeInfoMainnet));

        using (http)
        {
            provider.Dispose();
            provider.Dispose();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetSnapshotAsync());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.RefreshAsync());
        }
    }

    [TestMethod]
    public void RejectsInvalidOptionsAtConstruction()
    {
        var options = TestPipeline.CreateOptions();
        options.RecvWindow = TimeSpan.FromMinutes(5);

        using var stub = StubHttpMessageHandler.Json("{}");
        using var http = new HttpClient(stub) { BaseAddress = BinanceEndpoints.Mainnet.RestBaseUri };
        var client = new Ozakboy.Http.HttpPipelineClient(http);

        Assert.ThrowsExactly<ArgumentException>(() => new BinanceExchangeInfoProvider(client, options));
        Assert.ThrowsExactly<ArgumentNullException>(() => new BinanceExchangeInfoProvider(client, null!));
    }
}
