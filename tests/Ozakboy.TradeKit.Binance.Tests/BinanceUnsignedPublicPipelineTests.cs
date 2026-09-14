using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 離線鎖住「簽章關閉的公開行情管線,query 參數確實送出」。
/// Pins offline that a public market data pipeline with signing switched off really sends its query parameters.
/// </summary>
/// <remarks>
/// <para>
/// 最內層換成假傳輸,其餘是 <see cref="UnsignedPublicPipeline"/> 以 <c>AddOzakboyHttpPipeline</c>(<c>EnableSigning = false</c>)
/// 組出來的真管線。本套件其他 REST 測試走的 <see cref="TestPipeline"/> 與 <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>
/// 都掛著簽章處理器,碰不到這條路徑(理由見 <see cref="UnsignedPublicPipeline"/>)。
/// Only the innermost transport is fake; the rest is the real pipeline <see cref="UnsignedPublicPipeline"/> builds
/// with <c>AddOzakboyHttpPipeline</c> and <c>EnableSigning = false</c>. The other REST tests in this package run
/// through <see cref="TestPipeline"/> or <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>, both of
/// which carry the signing handler and cannot reach this path (see <see cref="UnsignedPublicPipeline"/>).
/// </para>
/// <para>
/// 在 <c>Ozakboy.Http</c> 0.3.2 下這條為紅:送出的位址是 <c>/fapi/v1/klines</c>,一個參數都沒有。
/// On <c>Ozakboy.Http</c> 0.3.2 this is red: the request goes out as a bare <c>/fapi/v1/klines</c> with no
/// parameter at all.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceUnsignedPublicPipelineTests
{
    private static readonly string[] KlineParameterOrder = ["symbol", "interval", "limit"];

    [TestMethod]
    public async Task AKlineQueryThroughTheUnsignedPipelineSendsEveryParameterInOrder()
    {
        var stub = StubHttpMessageHandler.Json(MarketDataSamples.RestKlines);
        var clock = new TestClock(DateTimeOffset.FromUnixTimeMilliseconds(MarketDataSamples.RestKlinesServerTimeMs));
        var options = new BinanceOptions { Environment = BinanceEnvironment.Mainnet };

        var (provider, feed) = UnsignedPublicPipeline.Create(options, stub, clock);

        using (provider)
        {
            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
                Limit = 5,
            });

            Assert.IsTrue(result.TryGetValue(out var candles), result.Error?.Message);
            Assert.HasCount(2, candles);

            var request = stub.LastRequest;

            Assert.AreEqual("/" + BinanceMarketDataPaths.Klines, request.RequestUri!.AbsolutePath);
            CollectionAssert.AreEqual(
                KlineParameterOrder,
                request.ParameterNames.ToArray(),
                $"未簽章管線送出的 query 不完整:'{request.Query}'。The unsigned pipeline sent an incomplete query: '{request.Query}'.");
            Assert.AreEqual("BTCUSDT", request.Parameter("symbol"));
            Assert.AreEqual("1m", request.Parameter("interval"));
            Assert.AreEqual("5", request.Parameter("limit"));

            // 公開端點:不簽章、不帶金鑰標頭。
            // A public endpoint: no signature and no key header.
            Assert.IsNull(request.Parameter("signature"));
            Assert.IsNull(request.Parameter("timestamp"));
            Assert.IsFalse(request.Headers.ContainsKey(BinanceConstants.ApiKeyHeaderName));
        }
    }
}
