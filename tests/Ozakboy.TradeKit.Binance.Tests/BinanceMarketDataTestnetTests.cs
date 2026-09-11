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
/// 一律連 Testnet(<c>wss://stream.binancefuture.com</c>),走 <c>/market/stream</c>。外層包裝的差異是在這裡實測出來的;
/// 走錯路由(例如把 K 線訂在 <c>/public</c> 上)的失敗方式是「連得上、訂閱受理、一筆資料都沒有」,
/// 從連線狀態完全看不出來,只能靠真的收到資料才算驗過。<b>Testnet 對行情仍相容不帶路由的舊位址,
/// 所以這一組抓不到路由錯誤</b>;那一半由 <see cref="BinanceMarketDataMainnetPublicTests"/> 在主網公開行情上負責。
/// The testnet is the only host used, on <c>/market/stream</c>. The envelope difference was established here by
/// measurement. A wrong route — klines subscribed on <c>/public</c>, say — behaves as "connects, acknowledges the
/// subscription, delivers nothing", a failure the connection state says nothing about, which is why only actually
/// receiving data counts as verification. <b>The testnet still honours the unprefixed old addresses for market
/// data, so these tests cannot catch a route mistake</b>; that half belongs to
/// <see cref="BinanceMarketDataMainnetPublicTests"/> on production public data.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceMarketDataTestnetTests
{
    private static readonly string[] Btc = ["BTCUSDT"];

    private static readonly string[] BtcAndEth = ["BTCUSDT", "ETHUSDT"];

    /// <summary>
    /// 歷史 K 線查詢的嘗試次數。The number of attempts a historical kline query is given.
    /// </summary>
    private const int BoundaryRetries = 2;

    /// <summary>
    /// 「安全窗」的起點,以每分鐘的第幾秒表示。The start of the safe window, as an offset into the minute.
    /// </summary>
    /// <remarks>
    /// 實測(2026-09-11,Testnet BTCUSDT):整分鐘翻過去之後,REST 的 <c>klines</c> 有好幾秒仍然只回到上一根,
    /// 最久的一次過了 10.7 秒新的一根都還沒出現。20 秒是留了餘裕的下限。
    /// Measured on the testnet BTCUSDT on 2026-09-11: for several seconds after a minute turns over, the REST
    /// <c>klines</c> response still ends at the previous candle — in the worst round observed, the new one had
    /// not appeared 10.7 seconds in. Twenty seconds is that bound with room to spare.
    /// </remarks>
    private static readonly TimeSpan SafeWindowStart = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 「安全窗」的終點,之後就太靠近下一個邊界了。The end of the safe window, past which the next boundary is too close.
    /// </summary>
    private static readonly TimeSpan SafeWindowEnd = TimeSpan.FromSeconds(55);

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

    /// <summary>
    /// 幣安回的最後一根是當前這根,還在跳動。把它當成已收盤就是把「查詢當下的最新價」
    /// 寫成收盤價,存進歷史之後回測會用一根從未存在的 K 線。
    /// The last candle Binance returns is the current one, still ticking. Treating it as closed records
    /// "the latest price at query time" as a close, and a backtest later runs on a candle that never was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 這條隨機失敗過一次。錯的不是判定,是「最後一根還開著」這個前提在分鐘邊界上不成立 ——
    /// 而且理由比「請求跨過邊界」更硬:實測整分鐘翻過去之後,幣安有好幾秒仍然只回到上一根,
    /// 那幾秒內查到的最後一根**真的已經收盤**,判它已收盤是對的。所以這裡不放寬斷言,改成把查詢
    /// 排開邊界:先等進「安全窗」(每分鐘的第 20 到 55 秒)再查。
    /// This failed once at random. The judgement was not wrong; the premise "the last candle is still open"
    /// does not hold near a minute boundary — for a harder reason than a request straddling it: measurement
    /// shows Binance keeps returning the previous candle for several seconds after the minute turns over, so
    /// the last candle really has closed and calling it closed is right. The assertion therefore stays as
    /// strict as it was, and the query is moved away from the boundary instead: it waits for the safe window,
    /// seconds 20 through 55 of the minute.
    /// </para>
    /// <para>
    /// 呼叫前後兩個時刻仍然記著,用來守住殘餘的競態:判定讀的是同一個本機時鐘,只要收盤時間晚於
    /// 「回應到手」的時刻,套件就沒有任何理由判它已收盤,這個斷言不依賴任何關於幣安的假設。
    /// 落在呼叫期間之內就是跨界,那一輪什麼也證明不了 —— 放寬斷言不行,這條擋的正是
    /// 「整串都標成已收盤」那種退化。
    /// The moments either side of the call are still recorded, to close the residual race: the judgement reads
    /// the same local clock, so once the close time is later than the arrival moment nothing licenses calling
    /// the candle closed, and that assertion rests on no assumption about Binance. A close time inside the
    /// call is a straddle and proves nothing that round. Relaxing the assertion is not an option — what this
    /// test guards against is precisely the degenerate "mark them all closed".
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("Testnet")]
    public async Task FetchesHistoricalKlinesAndMarksTheLastOneAsStillOpen()
    {
        using var provider = Build();
        var feed = provider.GetRequiredService<IMarketDataFeed>();

        var lastCloseTime = default(DateTimeOffset);
        var lastAfter = default(DateTimeOffset);

        for (var attempt = 1; attempt <= BoundaryRetries; attempt++)
        {
            await Task.Delay(DelayIntoSafeWindow(DateTimeOffset.UtcNow, mustAdvance: attempt > 1));

            var before = DateTimeOffset.UtcNow;

            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
                Limit = 5,
            });

            var after = DateTimeOffset.UtcNow;

            Assert.IsTrue(result.TryGetValue(out var candles), result.Error?.Message);
            Assert.HasCount(5, candles);

            for (var index = 0; index < candles.Count - 1; index++)
            {
                Assert.IsTrue(candles[index].IsClosed, $"第 {index} 根的收盤時間早已過去,應判為已收盤。");
                Assert.IsGreaterThan(candles[index].OpenTime, candles[index + 1].OpenTime);
            }

            var last = candles[^1];

            lastCloseTime = last.CloseTime;
            lastAfter = after;

            // 收盤時間不晚於回應到手的時刻:這一根在呼叫期間(或更早)就收了,判已收盤或未收盤都說得通,
            // 這一輪證明不了任何事。`before` 只用來讓訊息說得清楚是哪一種。
            // The close time is no later than the arrival moment: this candle closed during the call, or
            // before it, and either verdict is defensible, so the round proves nothing. `before` only serves
            // to say which of the two it was.
            if (last.CloseTime <= after)
            {
                Console.WriteLine(
                    last.CloseTime < before
                        ? $"第 {attempt} 輪:最後一根在請求送出前({before:O})就已於 {last.CloseTime:O} 收盤,重試。"
                        : $"第 {attempt} 輪:最後一根於 {last.CloseTime:O} 收盤,正好落在這次呼叫期間,重試。");

                continue;
            }

            // 收盤時間晚於回應到手的時刻,套件沒有任何理由判它已收盤。
            // The close time is later than the arrival moment; nothing licenses calling it closed.
            Assert.IsFalse(last.IsClosed, "最後一根還沒收盤,不可判為已收盤。");
            return;
        }

        Assert.Inconclusive(
            $"連續 {BoundaryRetries} 輪都拿不到還在跳動的最後一根(最後一次:收盤時間 {lastCloseTime:O}、"
            + $"回應到手 {lastAfter:O})。每一輪都排在安全窗內,所以不是分鐘邊界;剩下的可能是本機時鐘偏快,"
            + "或 Testnet 這段時間沒有成交。這不是套件的問題,但也表示這條這次沒有驗到東西。");
    }

    /// <summary>
    /// 算出還要等多久才進得了「安全窗」,已經在窗內就是零。
    /// The wait remaining before the safe window opens; zero when the moment is already inside it.
    /// </summary>
    /// <param name="now">當下時刻。The current moment.</param>
    /// <param name="mustAdvance">
    /// 為 <see langword="true"/> 時,即使已經在窗內也要前進到下一個窗 —— 重試若留在同一分鐘,
    /// 拿到的會是同一根 K 線,等於沒重試。
    /// When <see langword="true"/>, advance to the next window even from inside the current one: a retry that
    /// stays in the same minute reads the same candle and so retries nothing.
    /// </param>
    /// <returns>要等待的時間。The delay to apply.</returns>
    private static TimeSpan DelayIntoSafeWindow(DateTimeOffset now, bool mustAdvance)
    {
        var intoMinute = TimeSpan.FromTicks(now.UtcTicks % TimeSpan.TicksPerMinute);
        var inWindow = intoMinute >= SafeWindowStart && intoMinute < SafeWindowEnd;

        if (inWindow && !mustAdvance)
        {
            return TimeSpan.Zero;
        }

        return !inWindow && intoMinute < SafeWindowStart
            ? SafeWindowStart - intoMinute
            : TimeSpan.FromMinutes(1) - intoMinute + SafeWindowStart;
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Testnet);
        services.AddBinanceMarketData();

        return services.BuildServiceProvider();
    }
}
