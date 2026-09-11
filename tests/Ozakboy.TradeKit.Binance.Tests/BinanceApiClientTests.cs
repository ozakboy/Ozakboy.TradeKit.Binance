using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 呼叫器本身的測試:公開請求的參數附加,以及「冪等性必須明講」這條規則。
/// Tests for the caller itself: attaching parameters to a public request, and the rule that idempotency must
/// be stated explicitly.
/// </summary>
[TestClass]
public sealed class BinanceApiClientTests
{
    private static (BinanceApiClient Api, StubHttpMessageHandler Stub, HttpClient Http) Create()
    {
        var options = TestPipeline.CreateOptions();
        var clock = TestClock.AtFixedInstant();
        var stub = StubHttpMessageHandler.Json("{}");
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        return (new BinanceApiClient(pipeline, options, clock), stub, http);
    }

    [TestMethod]
    public async Task APublicRequestCarriesTheQueryParametersItWasGiven()
    {
        var (api, stub, http) = Create();

        using (http)
        {
            var query = QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Add("limit", 5L).Build();

            var result = await api.GetPublicAsync(
                BinanceApiPaths.ExchangeInfo,
                query,
                BinanceRequestWeights.ExchangeInfo,
                CancellationToken.None);

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);

            var sent = stub.LastRequest.Query;

            StringAssert.Contains(sent, "symbol=BTCUSDT", StringComparison.Ordinal);
            StringAssert.Contains(sent, "limit=5", StringComparison.Ordinal);

            // 公開端點不簽章,所以查詢字串裡不該出現 signature。
            // A public endpoint is unsigned, so no signature belongs in the query string.
            Assert.IsFalse(sent.Contains("signature=", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task APublicRequestWithoutParametersSendsNone()
    {
        var (api, stub, http) = Create();

        using (http)
        {
            _ = await api.GetPublicAsync(
                BinanceApiPaths.Ping,
                null,
                BinanceRequestWeights.Ping,
                CancellationToken.None);

            Assert.AreEqual(string.Empty, stub.LastRequest.Query);
        }
    }

    [TestMethod]
    public async Task ASignedRequestMustStateItsIdempotencyExplicitly()
    {
        // 推定規則是「安全方法可重試,其餘不可」。那條規則對撤單是對的,對下單卻只是靠方法名稱僥倖 ——
        // 一旦有人把下單改寫成別的形狀,推定就會悄悄把它變成可重試,而那代表重複的部位。
        // The inference rule is "safe methods retry, the rest do not". It happens to be right for a
        // cancellation but is only luck for an order: reshape the call and inference quietly turns it
        // retryable, and that means a duplicated position.
        var (api, stub, http) = Create();

        using (http)
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                () => api.SendSignedAsync(
                    HttpMethod.Post,
                    BinanceApiPaths.Order,
                    null,
                    BinanceRequestWeights.PlaceOrder,
                    "測試 / test",
                    RequestIdempotency.Inferred,
                    CancellationToken.None));

            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public void TheCallerReportsTheEnvironmentItIsBoundTo()
    {
        var (api, _, http) = Create();

        using (http)
        {
            Assert.AreEqual(BinanceEndpoints.Mainnet, api.Endpoints);
        }
    }
}
