using Microsoft.Extensions.DependencyInjection;

using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 實際連上幣安<b>主網公開行情</b>的整合測試。不設任何憑證、不打任何需要簽章的端點。
/// Integration tests that really connect to Binance <b>production public market data</b>. No credential is set and
/// no signed endpoint is called.
/// </summary>
/// <remarks>
/// <para>
/// 這幾條會連線,因此以 <c>dotnet test --filter "TestCategory=MainnetPublic"</c> 明確指定才會跑。
/// 主網只准連公開行情:這個類別裡沒有 API 金鑰、沒有下單、沒有 listenKey。
/// These reach the network; select them with <c>dotnet test --filter "TestCategory=MainnetPublic"</c>. Only public
/// market data is touched on production: there is no API key, no order, and no listenKey anywhere in this class.
/// </para>
/// <para>
/// <b>這是 0.1.1 路由修正有效的唯一證據。</b> 0.1.0 走不帶路由的 <c>/stream</c>,在主網上「握手成功、
/// 一個 frame 都沒有」,當時被誤判成本機網路問題;Testnet 對行情仍相容舊位址,所以 Testnet 的測試抓不到這件事。
/// 這條測試以舊位址跑過一次確認會紅(60 秒內零資料),改回 <c>/market/stream</c> 才綠。
/// <b>This is the only evidence that the 0.1.1 route fix works.</b> 0.1.0 dialled the unprefixed <c>/stream</c>,
/// which on production completes the handshake and then delivers not one frame — at the time misread as a local
/// network problem — and because the testnet still honours the old address for market data, the testnet tests
/// cannot catch it. This test was run once against the old address and went red, no data in 60 seconds, and went
/// green only on <c>/market/stream</c>.
/// </para>
/// <para>
/// <b>0.2.0 以前這裡沒抓到「未簽章管線的 query 參數整個消失」,原因有兩層。</b>其一,原本唯一的一條只測 WebSocket
/// 串流,完全不走 REST。其二,就算走 REST,它用的是 <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>,
/// 那一律以 <c>EnableSigning = true</c> 註冊管線;<c>Ozakboy.Http</c> 0.3.2 的簽章處理器對未標記簽章的請求照樣把參數寫進位址,
/// 所以沒有憑證也能正確送出。出事的是下游那種「自己以 <c>AddOzakboyHttpPipeline</c> 關掉簽章」的組法:
/// 簽章處理器不掛,在 0.3.2 就沒有任何東西寫 query,<c>GET /fapi/v1/klines</c> 缺 <c>symbol</c>,幣安回 <c>-1102</c>。
/// <see cref="FetchesKlinesOverRestThroughACredentialFreeUnsignedPipeline"/> 照那個組法建用戶端,
/// 在 <c>Ozakboy.Http</c> 0.3.2 下實跑為紅(<c>-1102</c>),升上 0.3.3 才綠。
/// <b>Before 0.2.1 nothing here caught "an unsigned pipeline drops its query parameters", for two reasons.</b> The
/// only test covered the WebSocket stream and never touched REST. And even over REST it would have used
/// <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>, which always registers
/// <c>EnableSigning = true</c>; <c>Ozakboy.Http</c> 0.3.2's signing handler writes the parameters into the URI for
/// unsigned requests too, so a credential-free call went out correctly. What broke is the downstream assembly that
/// switches signing off through <c>AddOzakboyHttpPipeline</c> itself: no signing handler, so on 0.3.2 nothing wrote
/// the query, <c>GET /fapi/v1/klines</c> lacked <c>symbol</c>, and Binance answered <c>-1102</c>.
/// <see cref="FetchesKlinesOverRestThroughACredentialFreeUnsignedPipeline"/> builds its client that way; run live it
/// was red on <c>Ozakboy.Http</c> 0.3.2 (<c>-1102</c>) and green only on 0.3.3.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceMarketDataMainnetPublicTests
{
    private static readonly string[] Btc = ["BTCUSDT"];

    [TestMethod]
    [TestCategory("MainnetPublic")]
    public async Task FetchesKlinesOverRestThroughACredentialFreeUnsignedPipeline()
    {
        // 主網、不設任何憑證、簽章關閉:與下游混接主網公開行情時的組法相同。
        // Production, no credential, signing off: the same assembly downstream uses for production public market data.
        var options = new BinanceOptions { Environment = BinanceEnvironment.Mainnet };
        var (provider, feed) = UnsignedPublicPipeline.Create(options);

        using (provider)
        {
            Assert.IsFalse(feed.Endpoints.IsTestnet, "這條測試必須連主網公開行情。This test must reach production public market data.");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var result = await feed.GetKlinesAsync(
                new KlineQuery
                {
                    Symbol = "BTCUSDT",
                    Interval = KlineInterval.OneMinute,
                    Limit = 5,
                },
                cts.Token);

            Assert.IsTrue(
                result.TryGetValue(out var candles),
                $"未簽章管線查 K 線失敗:{result.Error?.Code} {result.Error?.Message}。若是 -1102,代表 query 參數沒有送出。The kline query through the unsigned pipeline failed: {result.Error?.Code} {result.Error?.Message}. A -1102 means the query parameters were not sent.");
            Assert.HasCount(5, candles);

            foreach (var candle in candles)
            {
                Assert.AreEqual("BTCUSDT", candle.Symbol);
                Assert.AreEqual(KlineInterval.OneMinute, candle.Interval);
                Assert.IsGreaterThan(0m, candle.Close);
            }
        }
    }

    [TestMethod]
    [TestCategory("MainnetPublic")]
    public async Task ReceivesKlinesFromTheMainnetPublicStream()
    {
        var services = new ServiceCollection();

        // 刻意只設環境,不設任何憑證。
        // Only the environment is set, deliberately; no credential of any kind.
        services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Mainnet);
        services.AddBinanceMarketData();

        using var provider = services.BuildServiceProvider();

        var feed = provider.GetRequiredService<BinanceMarketDataFeed>();

        Assert.IsFalse(feed.Endpoints.IsTestnet, "這條測試必須連主網,否則證明不了路由修正。");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Kline? received = null;

        try
        {
            await foreach (var item in feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute, cts.Token))
            {
                Assert.IsTrue(item.TryGetValue(out var candle), item.Error?.Message);

                received = candle;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // 逾時由底下的斷言說明原因,不讓取消例外蓋掉它。
            // A timeout is explained by the assertion below rather than by the cancellation exception.
        }

        Assert.IsNotNull(
            received,
            "60 秒內沒有從主網公開行情收到任何 K 線。若位址不帶 /market 路由,主網會握手成功卻零資料。");
        Assert.AreEqual("BTCUSDT", received.Symbol);
        Assert.AreEqual(KlineInterval.OneMinute, received.Interval);
        Assert.IsGreaterThan(0m, received.Close);
    }
}
