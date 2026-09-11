using Microsoft.Extensions.DependencyInjection;

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
/// </remarks>
[TestClass]
public sealed class BinanceMarketDataMainnetPublicTests
{
    private static readonly string[] Btc = ["BTCUSDT"];

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
