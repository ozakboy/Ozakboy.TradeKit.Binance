using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 需要真實 Testnet 憑證的整合測試。沒有憑證時判定為 Inconclusive,而不是通過。
/// Integration tests that need real Testnet credentials. Without them they report Inconclusive rather than
/// passing.
/// </summary>
/// <remarks>
/// <para>
/// 憑證一律從環境變數 <c>BINANCE_TESTNET_API_KEY</c> 與 <c>BINANCE_TESTNET_API_SECRET</c> 讀取,
/// 絕不寫進任何檔案。這幾條測試會實際連線,也會實際送單,因此只打
/// <see cref="BinanceEnvironment.Testnet"/> —— 主網的交易端點在任何情況下都不碰。
/// Credentials always come from the <c>BINANCE_TESTNET_API_KEY</c> and
/// <c>BINANCE_TESTNET_API_SECRET</c> environment variables and never from a file. These tests reach the network
/// and really do place orders, so they run against <see cref="BinanceEnvironment.Testnet"/> and nothing else:
/// the production trading endpoints are never touched.
/// </para>
/// <para>
/// 缺憑證時刻意<b>不</b>回報通過。一個「沒跑但綠燈」的測試比沒有測試更危險 ——
/// 它會讓「下單驗過了」這件事在完全沒有驗過的情況下被記成已完成。
/// A missing credential deliberately does <b>not</b> pass. A green test that never ran is worse than no test:
/// it records "order placement was verified" when nothing of the sort happened.
/// </para>
/// <para>
/// <b>測試單的紀律。</b> 每一張測試單都掛在遠離標記價的位置(買單在標記價下方約 4%)並使用 GTC,
/// 因此不會成交;每一張都在 <c>try/finally</c> 的 <c>finally</c> 裡撤掉,即使斷言失敗也一樣;
/// 每一張都帶 <c>pulsetrade-test-</c> 前綴,萬一真的留下來看得出來源。
/// 類別層級加上 <c>DoNotParallelize</c>,否則「檢查有沒有殘留掛單」會和還開著單的測試同時跑。
/// <b>Test-order discipline.</b> Every test order rests about 4% away from the mark price with GTC so that it
/// cannot fill; every one is cancelled in a <c>finally</c> block even when an assertion fails; and every one
/// carries the <c>pulsetrade-test-</c> prefix so that a stray order is traceable. The class is
/// <c>DoNotParallelize</c>, or the leftover check would run while another test still has an order resting.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class BinanceTestnetIntegrationTests
{
    private const string ApiKeyVariable = "BINANCE_TESTNET_API_KEY";
    private const string SecretVariable = "BINANCE_TESTNET_API_SECRET";

    /// <summary>
    /// 測試單的用戶端訂單編號前綴。留下孤兒單時,這是唯一能認出來源的線索。
    /// The client order id prefix for test orders: the only clue to their origin should one ever be left behind.
    /// </summary>
    private const string TestOrderPrefix = "pulsetrade-test-";

    /// <summary>
    /// 測試用的交易對。Testnet 上流動性最好、交易規則也最接近主網的一個。
    /// The symbol under test: the most liquid one on Testnet and the closest to production in its rules.
    /// </summary>
    private const string Symbol = "BTCUSDT";

    /// <summary>
    /// 掛單價相對標記價的比例。Testnet 上 BTCUSDT 的 <c>PERCENT_PRICE</c> 篩選器
    /// <c>multiplierDown</c> 是 0.95,所以 0.96 是「掛得上、又絕不會成交」的位置。
    /// The resting price as a fraction of the mark. The <c>PERCENT_PRICE</c> filter on Testnet BTCUSDT has a
    /// <c>multiplierDown</c> of 0.95, so 0.96 both passes the filter and cannot possibly fill.
    /// </summary>
    private const decimal RestingPriceRatio = 0.96m;

    private static bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SecretVariable));

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(options =>
        {
            options.Environment = BinanceEnvironment.Testnet;
            options.ApiKey = Environment.GetEnvironmentVariable(ApiKeyVariable)!;
            options.SecretKey = Environment.GetEnvironmentVariable(SecretVariable)!;
        });

        return services.BuildServiceProvider();
    }

    private static ServiceProvider? BuildOrSkip()
    {
        if (!HasCredentials)
        {
            Assert.Inconclusive(
                $"未設定 {ApiKeyVariable} 與 {SecretVariable},略過需要真實憑證的整合測試。Skipped: {ApiKeyVariable} and {SecretVariable} are not set.");
            return null;
        }

        return Build();
    }

    /// <summary>
    /// 取得 Testnet 的標記價。這是公開端點,不需要簽章。
    /// Reads the Testnet mark price from the public, unsigned endpoint.
    /// </summary>
    /// <remarks>
    /// 本階段的範圍是 REST 交易端點,行情查詢不在其中,所以這裡直接用一個裸的 <see cref="HttpClient"/>,
    /// 而不是為了一個呼叫端在套件裡開一個公開 API。掛單價必須算在真實的標記價上 ——
    /// 寫死一個價格會在市場走掉之後變成「不小心成交的測試單」。
    /// This stage covers the REST trading endpoints and not market data, so the price comes from a bare
    /// <see cref="HttpClient"/> rather than from a public API added to the package for one caller. The resting
    /// price has to be computed from the live mark: a hard-coded one becomes a test order that fills by
    /// accident once the market moves.
    /// </remarks>
    private static async Task<decimal> GetMarkPriceAsync()
    {
        using var http = new HttpClient
        {
            BaseAddress = BinanceEndpoints.Testnet.RestBaseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };

        var body = await http.GetStringAsync($"fapi/v1/premiumIndex?symbol={Symbol}");

        using var document = JsonDocument.Parse(body);

        return decimal.Parse(
            document.RootElement.GetProperty("markPrice").GetString()!,
            NumberStyles.Number,
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 算出一張「掛得上、但不會成交」的買單:價格在標記價下方約 4%,數量取剛好滿足最小名目價值的量。
    /// Builds a buy order that rests without filling: about 4% below the mark, sized to the minimum notional.
    /// </summary>
    private static async Task<(decimal Price, decimal Quantity)> BuildRestingOrderAsync(BinanceFuturesClient client)
    {
        var symbol = (await client.GetSymbolAsync(Symbol)).GetValueOrThrow();
        var mark = await GetMarkPriceAsync();

        Assert.IsGreaterThan(0m, mark, "取不到標記價,無法算出安全的掛單價。");

        var price = symbol.NormalizePrice(mark * RestingPriceRatio, PriceRounding.Down).GetValueOrThrow();
        var quantity = symbol.GetMinimumQuantity(price).GetValueOrThrow();

        return (price, quantity);
    }

    /// <summary>
    /// 產生一個測試單的用戶端訂單編號:前綴 + 毫秒時間戳 + 短後綴。
    /// Builds a client order id for a test order: the prefix, a millisecond timestamp, and a short suffix.
    /// </summary>
    /// <remarks>
    /// 後綴刻意很短。幣安的上限是 36 個字元,而前綴十六碼加時間戳十三碼再加一個連字號就已經用掉三十碼 ——
    /// 一個看起來很合理的 <c>"roundtrip"</c> 就會超過,而超過的下場是整張單在本地被擋下、測試紅得莫名其妙。
    /// The suffixes are deliberately terse. Binance allows 36 characters and the sixteen-character prefix plus
    /// a thirteen-digit timestamp plus a hyphen already spends thirty, so a reasonable-looking
    /// <c>"roundtrip"</c> overflows and the order is refused locally for reasons the test name does not hint at.
    /// </remarks>
    private static string NewClientOrderId(string suffix)
    {
        var id = TestOrderPrefix
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            + "-"
            + suffix;

        Assert.IsTrue(
            BinanceClientOrderId.IsValid(id),
            $"測試單的編號「{id}」共 {id.Length} 個字元,超過幣安的 {BinanceClientOrderId.MaxLength} 字上限,請縮短後綴。");

        return id;
    }

    // ── 唯讀端點 / Read-only endpoints ───────────────────────────────────────

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReadsTheTradingRulesFromTheTestnet()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var symbol = await client.GetSymbolAsync(Symbol);

        Assert.IsTrue(symbol.IsSuccess, symbol.Error?.Message);
        Assert.IsTrue(symbol.GetValueOrThrow().StepSize > 0m);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReadsTheAccountSnapshot()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var snapshot = await client.GetAccountSnapshotAsync();

        Assert.IsTrue(snapshot.IsSuccess, snapshot.Error?.Message);
        Assert.IsTrue(snapshot.GetValueOrThrow().Balances.Count > 0);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReadsPositions()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var positions = await client.GetPositionsAsync();

        Assert.IsTrue(positions.IsSuccess, positions.Error?.Message);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task LocalClockAgreesWithTheExchangeWithinTheRecvWindow()
    {
        // 時鐘偏移的症狀是每一個簽章請求都失敗,而錯誤訊息看起來像簽章問題。
        // 這條測試把它變成一個說得出原因的失敗。
        // Clock drift shows up as every signed request failing with what looks like a signature problem; this
        // turns it into a failure that says why.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();
        var options = provider!.GetRequiredService<BinanceOptions>();

        var serverTime = await client.GetServerTimeAsync();

        Assert.IsTrue(serverTime.IsSuccess, serverTime.Error?.Message);

        var drift = (DateTimeOffset.UtcNow - serverTime.GetValueOrThrow()).Duration();

        Assert.IsLessThanOrEqualTo(
            options.RecvWindow,
            drift,
            $"本機時鐘與交易所相差 {drift},超過 recvWindow {options.RecvWindow},簽章請求會全部失敗。");
    }

    // ── 交易端點 / Trading endpoints ─────────────────────────────────────────

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task PlacesQueriesAndCancelsAOneOffLimitOrder()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var (price, quantity) = await BuildRestingOrderAsync(client);
        var clientOrderId = NewClientOrderId("rt");

        var placed = await client.PlaceOrderAsync(new OrderRequest
        {
            Symbol = Symbol,
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = quantity,
            Price = price,
            TimeInForce = TimeInForce.GoodTilCanceled,
            ClientOrderId = clientOrderId,
        });

        Assert.IsTrue(placed.IsSuccess, placed.Error?.Message);

        var order = placed.GetValueOrThrow();

        try
        {
            Assert.AreEqual(Symbol, order.Symbol);
            Assert.AreEqual(clientOrderId, order.ClientOrderId);
            Assert.AreEqual(OrderStatus.New, order.Status);
            Assert.AreEqual(OrderType.Limit, order.OrderType);
            Assert.AreEqual(OrderSide.Buy, order.Side);
            Assert.AreEqual(price, order.Price);
            Assert.AreEqual(quantity, order.Quantity);
            Assert.AreEqual(0m, order.FilledQuantity, "測試單成交了,掛單價離標記價不夠遠。");
            Assert.IsNotNull(order.ExchangeOrderId);

            // 用交易所編號查得回來。
            // It can be found by exchange id.
            var byExchangeId = await client.GetOrderAsync(
                Symbol,
                OrderIdentifier.FromExchangeId(order.ExchangeOrderId!));

            Assert.IsTrue(byExchangeId.IsSuccess, byExchangeId.Error?.Message);
            Assert.AreEqual(order.ExchangeOrderId, byExchangeId.GetValueOrThrow().ExchangeOrderId);

            // 也用 clientOrderId 查得回來 —— 這正是下單逾時之後唯一能走的那條路。
            // And by clientOrderId, which is the only route available after a submission times out.
            var byClientId = await client.GetOrderAsync(Symbol, OrderIdentifier.FromClientId(clientOrderId));

            Assert.IsTrue(byClientId.IsSuccess, byClientId.Error?.Message);
            Assert.AreEqual(order.ExchangeOrderId, byClientId.GetValueOrThrow().ExchangeOrderId);

            // 掛單清單裡看得到它。
            // It appears in the open-orders listing.
            var open = await client.GetOpenOrdersAsync(Symbol);

            Assert.IsTrue(open.IsSuccess, open.Error?.Message);
            Assert.IsTrue(
                open.GetValueOrThrow().Any(candidate => candidate.ClientOrderId == clientOrderId),
                "掛單清單裡找不到剛送出去的那張單。");
        }
        finally
        {
            // 斷言失敗也要撤。留下孤兒單會污染後續測試,而且在 Testnet 上會一直掛著。
            // Cancelled even when an assertion fails: a stray order pollutes every later test and rests on
            // Testnet indefinitely.
            var cancelled = await client.CancelOrderAsync(Symbol, OrderIdentifier.FromClientId(clientOrderId));

            Assert.IsTrue(cancelled.IsSuccess, cancelled.Error?.Message);
            Assert.AreEqual(OrderStatus.Canceled, cancelled.GetValueOrThrow().Status);
        }
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task CancelsEveryOpenOrderOnASymbol()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var (price, quantity) = await BuildRestingOrderAsync(client);
        var symbol = (await client.GetSymbolAsync(Symbol)).GetValueOrThrow();

        try
        {
            // 兩張單,價格差一個跳動點,確認撤的是「全部」而不是「一張」。
            // Two orders one tick apart, to confirm that all of them go rather than one.
            foreach (var offset in new[] { 0, 1 })
            {
                var placed = await client.PlaceOrderAsync(new OrderRequest
                {
                    Symbol = Symbol,
                    Side = OrderSide.Buy,
                    OrderType = OrderType.Limit,
                    Quantity = quantity,
                    Price = price - (offset * symbol.TickSize),
                    TimeInForce = TimeInForce.GoodTilCanceled,
                    ClientOrderId = NewClientOrderId($"ca{offset}"),
                });

                Assert.IsTrue(placed.IsSuccess, placed.Error?.Message);
            }

            var before = await client.GetOpenOrdersAsync(Symbol);

            Assert.IsTrue(before.IsSuccess, before.Error?.Message);
            Assert.IsGreaterThanOrEqualTo(2, before.GetValueOrThrow().Count);
        }
        finally
        {
            var cancelled = await client.CancelAllOrdersAsync(Symbol);

            Assert.IsTrue(cancelled.IsSuccess, cancelled.Error?.Message);
        }

        var after = await client.GetOpenOrdersAsync(Symbol);

        Assert.IsTrue(after.IsSuccess, after.Error?.Message);
        Assert.HasCount(0, after.GetValueOrThrow());
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task CancellingWithNothingToCancelIsStillSuccess()
    {
        // 緊急出場會無條件先撤單再平倉。「本來就沒單」在那條路徑上是正常情況,不是失敗。
        // An emergency exit cancels before closing unconditionally, and "there was nothing there" is the normal
        // case on that path rather than a failure.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        Assert.IsTrue((await client.CancelAllOrdersAsync(Symbol)).IsSuccess);

        var result = await client.CancelAllOrdersAsync(Symbol);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task LooksUpAnOrderThatDoesNotExistAndSaysSo()
    {
        // 下單逾時之後要靠這個回答「那張單沒進去」。分不清「沒進去」與「查不到」,就不敢重下。
        // This is the answer that licenses a fresh submission after a timeout. Without telling "it never
        // landed" apart from "the lookup failed", nobody dares resend.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var result = await client.GetOrderAsync(
            Symbol,
            OrderIdentifier.FromClientId(TestOrderPrefix + "absent"));

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.OrderNotFound, result.Error!.Code);
        Assert.IsFalse(result.Error.IsTransient);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task SettingTheLeverageTwiceSucceedsBothTimes()
    {
        // 冪等:設成已經是的值不會出錯。這正是這個請求可以重試的理由。
        // Idempotent: setting it to what it already is is not an error, which is why the request may be retried.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var first = await client.SetLeverageAsync(Symbol, 20);
        var second = await client.SetLeverageAsync(Symbol, 20);

        Assert.IsTrue(first.IsSuccess, first.Error?.Message);
        Assert.IsTrue(second.IsSuccess, second.Error?.Message);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task AnUnacceptableLeverageIsRejectedByTheExchange()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var result = await client.SetLeverageAsync(Symbol, 500);

        Assert.IsTrue(result.IsFailure, "500 倍槓桿竟然被接受了,這條測試的假設要重新檢查。");
        Assert.AreEqual(TradeErrorCodes.LeverageNotAllowed, result.Error!.Code);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task SettingTheMarginModeToWhatItAlreadyIsCountsAsSuccess()
    {
        // 幣安對「模式沒有變」回的是 -4046 錯誤。實作把它吃掉,否則每次啟動都會冒出假的錯誤告警。
        // Binance answers "no change needed" with the error -4046. The implementation swallows it, or every
        // start-up raises a false alert.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var first = await client.SetMarginModeAsync(Symbol, MarginMode.Cross);
        var second = await client.SetMarginModeAsync(Symbol, MarginMode.Cross);

        Assert.IsTrue(first.IsSuccess, first.Error?.Message);
        Assert.IsTrue(second.IsSuccess, second.Error?.Message);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task AConditionalOrderIsRefusedByThisEndpointAsNotSupported()
    {
        // 2026-09-11 實測:幣安已把條件單移出 /fapi/v1/order,回 -4120 並要求改用 Algo Order 端點。
        // 這條測試把當下的事實釘住:哪天它又被接受了,這裡會紅,那正是該回頭補條件單支援的訊號。
        // Measured on 2026-09-11: Binance has moved conditional orders off /fapi/v1/order, answering -4120 and
        // pointing at the Algo Order endpoints. This pins the present reality down, and going red is exactly
        // the signal that conditional support is worth revisiting.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var (price, quantity) = await BuildRestingOrderAsync(client);
        var clientOrderId = NewClientOrderId("sm");

        var placed = await client.PlaceOrderAsync(new OrderRequest
        {
            Symbol = Symbol,
            Side = OrderSide.Sell,
            OrderType = OrderType.StopMarket,
            Quantity = quantity,
            StopPrice = price,
            ClientOrderId = clientOrderId,
        });

        if (placed.IsSuccess)
        {
            // 條件單又能用了。先撤掉,再讓測試紅,提醒這裡的假設已經過期。
            // Conditional orders work again: cancel it, then fail so the stale assumption gets noticed.
            _ = await client.CancelOrderAsync(Symbol, OrderIdentifier.FromClientId(clientOrderId));

            Assert.Fail("條件單竟然被 /fapi/v1/order 接受了,-4120 的限制已經解除,請重新評估條件單支援。");
        }

        Assert.AreEqual(TradeErrorCodes.NotSupported, placed.Error!.Code);
        Assert.IsFalse(placed.Error.IsTransient);
        Assert.IsTrue(placed.Error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var apiCode));
        Assert.AreEqual((long)BinanceApiErrorCodes.OrderTypeNotSupportedOnEndpoint, apiCode);

        // 就算失敗,編號也要回得來 —— 那張單有沒有進去只有它查得出來。
        // The id comes back even on failure: it is the only way to find out whether the order landed.
        Assert.IsTrue(placed.Error.TryGetData(BinanceErrorDataKeys.ClientOrderId, out var echoed));
        Assert.AreEqual(clientOrderId, echoed);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task AnOrderBelowTheMinimumNotionalNeverLeavesTheProcess()
    {
        // 校正後低於最小名目價值就在本地失敗,省一趟往返,也省一份限流額度。
        // Falling below the minimum notional fails locally, saving a round trip and a unit of quota.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var symbol = (await client.GetSymbolAsync(Symbol)).GetValueOrThrow();
        var (price, _) = await BuildRestingOrderAsync(client);

        var result = await client.PlaceOrderAsync(new OrderRequest
        {
            Symbol = Symbol,
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = symbol.StepSize,
            Price = price,
            ClientOrderId = NewClientOrderId("sml"),
        });

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.NotionalBelowMinimum, result.Error!.Code);
    }

    // ── 收尾:確認沒有殘留掛單 / Closing check: nothing left behind ────────────

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task NoTestOrderIsLeftBehind()
    {
        // 孤兒單會污染後續測試,而且在 Testnet 上會一直掛著。這條查的是<b>全部</b>商品,
        // 不只是本檔用到的那一個 —— 單一商品的檢查看不到的那些,正是最容易被忘記的。
        // A stray order pollutes every later test and rests on Testnet indefinitely. This checks <b>every</b>
        // symbol rather than only the one used here: the ones a single-symbol check cannot see are precisely
        // the ones that get forgotten.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var open = await client.GetOpenOrdersAsync();

        Assert.IsTrue(open.IsSuccess, open.Error?.Message);

        var leftovers = open.GetValueOrThrow()
            .Where(order => order.ClientOrderId.StartsWith(TestOrderPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.HasCount(
            0,
            leftovers,
            $"Testnet 上還留著 {leftovers.Count} 張測試單:{string.Join(", ", leftovers.Select(order => order.ClientOrderId))}");
    }

    /// <summary>
    /// 類別收尾:再確認一次沒有測試單殘留;有的話當場撤掉,並讓這個測試回合失敗。
    /// Class teardown: confirm once more that no test order survives, cancelling any that did and failing the
    /// run.
    /// </summary>
    /// <returns>非同步作業。The asynchronous operation.</returns>
    /// <remarks>
    /// 上面那條測試方法也做同一件事,但測試方法的執行順序沒有保證 —— 它可能排在還開著單的測試之前。
    /// 這裡才是真正的最後一道:它保證在這個類別的所有測試都跑完之後才執行。
    /// The test method above checks the same thing, but method order is not guaranteed and it may run before a
    /// test that still has an order resting. This is the one guaranteed to run last.
    /// </remarks>
    [ClassCleanup]
    public static async Task EnsureNothingIsLeftRestingAsync()
    {
        if (!HasCredentials)
        {
            return;
        }

        using var provider = Build();
        var client = provider.GetRequiredService<BinanceFuturesClient>();

        var open = await client.GetOpenOrdersAsync();

        if (!open.TryGetValue(out var orders))
        {
            throw new InvalidOperationException(
                $"收尾時查不到掛單清單,無法確認有沒有殘留:{open.Error!.Message}");
        }

        var leftovers = orders
            .Where(order => order.ClientOrderId.StartsWith(TestOrderPrefix, StringComparison.Ordinal))
            .ToList();

        if (leftovers.Count == 0)
        {
            return;
        }

        foreach (var symbol in leftovers.Select(order => order.Symbol).Distinct(StringComparer.Ordinal))
        {
            _ = await client.CancelAllOrdersAsync(symbol);
        }

        throw new InvalidOperationException(
            $"測試結束時仍有 {leftovers.Count} 張測試單掛在 Testnet 上,已在收尾時撤除,但這代表某條測試沒有撤乾淨:{string.Join(", ", leftovers.Select(order => order.ClientOrderId))}");
    }
}
