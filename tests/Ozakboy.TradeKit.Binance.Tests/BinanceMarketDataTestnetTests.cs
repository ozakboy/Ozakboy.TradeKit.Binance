using Microsoft.Extensions.DependencyInjection;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 實際連上幣安 Testnet 行情的整合測試。行情是公開端點,不需要憑證。
/// Integration tests that really connect to the Binance testnet market data. The endpoints are public and
/// need no credentials.
/// </summary>
/// <remarks>
/// <para>
/// 這幾條會連線,因此不在一般測試回合中執行:以
/// <c>dotnet test --filter "TestCategory=Testnet"</c> 明確指定才會跑。
/// These reach the network and so do not run in an ordinary pass; select them with
/// <c>dotnet test --filter "TestCategory=Testnet"</c>.
/// </para>
/// <para>
/// 一律連 Testnet(<c>wss://stream.binancefuture.com</c>)。串流路徑與外層包裝的差異都是在這裡實測出來的,
/// 文件上的 <c>/public/ws/…</c> 在這個環境會「連得上、訂閱受理、一筆資料都沒有」,
/// 那種失敗從連線狀態完全看不出來,只能靠真的收到資料才算驗過。
/// The testnet is the only host used. The stream paths and the envelope difference were established here by
/// measurement, and the documented <c>/public/ws/…</c> form behaves in this environment as "connects,
/// acknowledges the subscription, delivers nothing" — a failure the connection state says nothing about, which
/// is why only actually receiving data counts as verification.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceMarketDataTestnetTests
{
    private static readonly string[] Btc = ["BTCUSDT"];

    private static readonly string[] BtcAndEth = ["BTCUSDT", "ETHUSDT"];

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReceivesKlinesFromTheTestnetStream()
    {
        using var provider = Build();
        var feed = provider.GetRequiredService<IMarketDataFeed>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var received = 0;

        await foreach (var item in feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute, cts.Token))
        {
            Assert.IsTrue(item.TryGetValue(out var candle), item.Error?.Message);
            Assert.AreEqual("BTCUSDT", candle.Symbol);
            Assert.AreEqual(KlineInterval.OneMinute, candle.Interval);
            Assert.IsGreaterThan(0m, candle.Open);
            Assert.IsGreaterThan(0m, candle.Close);
            Assert.IsGreaterThanOrEqualTo(candle.Low, candle.High);
            Assert.IsGreaterThan(candle.OpenTime, candle.CloseTime);

            if (++received >= 3)
            {
                break;
            }
        }

        Assert.IsGreaterThanOrEqualTo(3, received, "60 秒內沒有從 Testnet 收到任何 K 線推送。");
    }

    /// <summary>
    /// 在真實連線上證明「同一根 K 線內 <see cref="Kline.IsClosed"/> 為 <see langword="false"/>,
    /// 收盤那一筆為 <see langword="true"/>」。
    /// Proves on a live connection that <see cref="Kline.IsClosed"/> is <see langword="false"/> throughout a
    /// candle and <see langword="true"/> on its closing push.
    /// </summary>
    /// <remarks>
    /// 這件事錯了不會有任何徵兆:價格、成交量、時間全部照樣正確,回測用收盤資料所以一路綠燈,
    /// 要到真錢在市場上同一根 K 線內反覆進出場才會被發現。
    /// Getting this wrong raises nothing: prices, volumes, and timestamps all stay correct, a backtest running
    /// on closed data stays green, and it is discovered only when real money enters and exits repeatedly
    /// inside a single candle.
    /// </remarks>
    [TestMethod]
    [TestCategory("Testnet")]
    public async Task TheClosedFlagFlipsOnlyOnTheClosingPushOfTheSameCandle()
    {
        using var provider = Build();
        var feed = provider.GetRequiredService<IMarketDataFeed>();

        // 一根 1m K 線最多等 60 秒,加上訂閱與收尾的餘裕。
        // One 1m candle takes at most 60 seconds, plus headroom for subscribing and winding down.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        DateTimeOffset? watching = null;
        var inProgress = 0;
        Kline? closed = null;

        await foreach (var item in feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute, cts.Token))
        {
            if (!item.TryGetValue(out var candle))
            {
                Assert.Fail(item.Error?.Message ?? "串流推出了失敗元素,但沒有附帶訊息。");
                return;
            }

            if (watching != candle.OpenTime)
            {
                // 盯住一根完整的 K 線。第一根多半是中途加入的,換根時重來。
                // Watch one whole candle: the first is usually joined mid-flight, so start over on a new one.
                watching = candle.OpenTime;
                inProgress = 0;
            }

            if (candle.IsClosed)
            {
                closed = candle;
                break;
            }

            inProgress++;
        }

        Assert.IsNotNull(closed, "沒有等到任何一筆收盤的 K 線推送。");
        Assert.IsGreaterThanOrEqualTo(1, inProgress, "同一根 K 線在收盤前應該先出現未收盤的推送。");
        Assert.AreEqual(watching, closed!.OpenTime, "收盤那一筆必須和先前未收盤的推送是同一根 K 線。");
        Assert.IsTrue(closed!.IsClosed);
        Assert.IsGreaterThan(closed!.OpenTime, closed!.CloseTime);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReceivesMarkPricesFromTheTestnetStream()
    {
        using var provider = Build();
        var feed = provider.GetRequiredService<IMarketDataFeed>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await foreach (var item in feed.SubscribeMarkPricesAsync(Btc, cts.Token))
        {
            Assert.IsTrue(item.TryGetValue(out var update), item.Error?.Message);
            Assert.AreEqual("BTCUSDT", update.Symbol);
            Assert.IsGreaterThan(0m, update.MarkPrice);
            Assert.IsNotNull(update.IndexPrice);
            Assert.IsGreaterThan(0m, update.IndexPrice!.Value);
            Assert.IsNotNull(update.FundingRate);
            Assert.IsNotNull(update.NextFundingTime);
            return;
        }

        Assert.Fail("60 秒內沒有從 Testnet 收到任何標記價推送。");
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task OneCombinedStreamCarriesEverySubscribedSymbol()
    {
        // 組合串流的外層包裝是路徑決定的,不是標的數量決定的。若判讀只在單一標的下成立,
        // 加第二檔就會整組解析失敗 —— 而那是「訂閱成功、每一則都失敗」的樣子。
        // The combined-stream envelope follows the path rather than the number of symbols. A reader that only
        // works with one symbol breaks entirely on the second, which looks like "subscribed fine, every frame
        // fails".
        using var provider = Build();
        var feed = provider.GetRequiredService<IMarketDataFeed>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var item in feed.SubscribeMarkPricesAsync(BtcAndEth, cts.Token))
        {
            Assert.IsTrue(item.TryGetValue(out var update), item.Error?.Message);

            seen.Add(update.Symbol);

            if (seen.Count >= 2)
            {
                break;
            }
        }

        Assert.HasCount(2, seen, "組合串流沒有同時送出兩個標的。");
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task FetchesHistoricalKlinesAndMarksTheLastOneAsStillOpen()
    {
        using var provider = Build();
        var feed = provider.GetRequiredService<IMarketDataFeed>();

        var result = await feed.GetKlinesAsync(new KlineQuery
        {
            Symbol = "BTCUSDT",
            Interval = KlineInterval.OneMinute,
            Limit = 5,
        });

        Assert.IsTrue(result.TryGetValue(out var candles), result.Error?.Message);
        Assert.HasCount(5, candles);

        for (var index = 0; index < candles.Count - 1; index++)
        {
            Assert.IsTrue(candles[index].IsClosed, $"第 {index} 根的收盤時間早已過去,應判為已收盤。");
            Assert.IsGreaterThan(candles[index].OpenTime, candles[index + 1].OpenTime);
        }

        // 幣安回的最後一根是當前這根,還在跳動。把它當成已收盤就是把「查詢當下的最新價」
        // 寫成收盤價,存進歷史之後回測會用一根從未存在的 K 線。
        // The last candle Binance returns is the current one, still ticking. Treating it as closed records
        // "the latest price at query time" as a close, and a backtest later runs on a candle that never was.
        Assert.IsFalse(candles[^1].IsClosed, "最後一根還沒收盤,不可判為已收盤。");
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Testnet);
        services.AddBinanceMarketData();

        return services.BuildServiceProvider();
    }
}
