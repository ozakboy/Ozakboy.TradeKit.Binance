using Ozakboy.Http;
using Ozakboy.Http.Retry;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 成交紀錄查詢(<c>GET /fapi/v1/userTrades</c>)的離線測試:參數、欄位對映與拒絕條件。全程不連網。
/// Offline tests for the account trade list (<c>GET /fapi/v1/userTrades</c>): parameters, field mapping, and
/// the cases it refuses. Nothing touches the network.
/// </summary>
/// <remarks>
/// 回應樣本<b>不是</b>實錄,而是依官方 Account Trade List 端點的回應 schema 組出來的(擷取日期 2026-09-14)。
/// 因此這些測試驗的是「欄位對映有沒有照文件寫對」,「文件有沒有說對」由
/// <see cref="BinanceTestnetIntegrationTests"/> 那一組連線測試負責。
/// The sample responses are <b>not</b> recordings but constructed from the response schema of the official
/// Account Trade List endpoint, retrieved 2026-09-14. These tests therefore check that the field mapping
/// follows the documentation, while whether the documentation is right is the job of the live tests in
/// <see cref="BinanceTestnetIntegrationTests"/>.
/// </remarks>
[TestClass]
public sealed class BinanceUserTradesTests
{
    /// <summary>
    /// 兩筆成交:一筆吃單買進(手續費以 USDT 計),一筆掛單賣出平倉(手續費以 BNB 抵扣、帶已實現損益)。
    /// Two fills: a taker buy paying its fee in USDT, and a maker sell closing the position, paying in BNB and
    /// settling a realised P&amp;L.
    /// </summary>
    private const string UserTrades =
        """[{"symbol":"BTCUSDT","id":4059011,"orderId":28580357696,"side":"BUY","positionSide":"BOTH","price":"74260.40","qty":"0.008","quoteQty":"594.08320","realizedPnl":"0","commission":"0.23763328","commissionAsset":"USDT","maker":false,"buyer":true,"time":1789116070165},{"symbol":"BTCUSDT","id":4059012,"orderId":28580359001,"side":"SELL","positionSide":"BOTH","price":"74310.10","qty":"0.008","quoteQty":"594.48080","realizedPnl":"0.39760000","commission":"0.00019829","commissionAsset":"BNB","maker":true,"buyer":false,"time":1789116131402}]""";

    /// <summary>沒有成交時的回應:空陣列,不是錯誤。An empty array rather than an error.</summary>
    private const string NoUserTrades = "[]";

    /// <summary>只有 <c>buyer</c> 沒有 <c>side</c>:方向要退回布林欄位讀。Direction falls back to the boolean.</summary>
    private const string UserTradeWithoutSideField =
        """[{"symbol":"BTCUSDT","id":4059013,"orderId":28580360111,"positionSide":"BOTH","price":"74260.40","qty":"0.008","realizedPnl":"0","commission":"0.23763328","commissionAsset":"USDT","maker":false,"buyer":false,"time":1789116070165}]""";

    /// <summary>缺 <c>time</c>:成交時刻是補查的游標,不能用「現在」頂替。The cursor cannot be guessed.</summary>
    private const string UserTradeWithoutTime =
        """[{"symbol":"BTCUSDT","id":4059014,"orderId":28580360222,"side":"BUY","price":"74260.40","qty":"0.008","commission":"0.23763328","commissionAsset":"USDT","maker":false,"buyer":true}]""";

    /// <summary>有手續費卻沒有幣別:那個數字沒辦法用。A fee number with no currency attached.</summary>
    private const string UserTradeWithoutFeeAsset =
        """[{"symbol":"BTCUSDT","id":4059015,"orderId":28580360333,"side":"BUY","price":"74260.40","qty":"0.008","commission":"0.23763328","maker":false,"buyer":true,"time":1789116070165}]""";

    /// <summary>缺 <c>id</c>:沒有成交編號就沒辦法去重。Without the id there is nothing to de-duplicate on.</summary>
    private const string UserTradeWithoutId =
        """[{"symbol":"BTCUSDT","orderId":28580360444,"side":"BUY","price":"74260.40","qty":"0.008","commission":"0.23763328","commissionAsset":"USDT","maker":false,"buyer":true,"time":1789116070165}]""";

    /// <summary>
    /// 成交紀錄查詢實際送出的參數順序,也就是待簽字串的順序。
    /// The parameter order the trade list query is actually sent with, which is the order that gets signed.
    /// </summary>
    private static readonly string[] ExpectedUserTradesParameterOrder =
    [
        "symbol", "startTime", "limit", "recvWindow", "timestamp", "signature",
    ];

    private static (BinanceFuturesClient Client, StubHttpMessageHandler Stub, HttpClient Http) Create(
        string? userTradesBody = null,
        bool withCredentials = true,
        RetryPolicy? retryPolicy = null)
    {
        var handler = UserTradesStub(userTradesBody);
        var options = TestPipeline.CreateOptions(withCredentials: withCredentials, retryPolicy: retryPolicy);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, handler, clock);

        return (new BinanceFuturesClient(pipeline, options, clock), handler, http);
    }

    private static StubHttpMessageHandler UserTradesStub(string? userTradesBody = null) =>
        StubHttpMessageHandler.ByPath(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/fapi/v1/exchangeInfo"] = Fixtures.ExchangeInfoMainnet,
            ["/fapi/v1/userTrades"] = userTradesBody ?? UserTrades,
        });

    // ── 欄位對映 / Field mapping ─────────────────────────────────────────────

    [TestMethod]
    public async Task EveryReconciliationFieldOfAFillIsMapped()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);

            var trades = result.GetValueOrThrow();

            Assert.HasCount(2, trades);

            var taker = trades[0];

            Assert.AreEqual("BTCUSDT", taker.Symbol);
            Assert.AreEqual("4059011", taker.TradeId);
            Assert.AreEqual("28580357696", taker.ExchangeOrderId);
            Assert.AreEqual(OrderSide.Buy, taker.Side);
            Assert.AreEqual(PositionSide.Both, taker.PositionSide);
            Assert.AreEqual(74_260.40m, taker.Price);
            Assert.AreEqual(0.008m, taker.Quantity);
            Assert.AreEqual(0.23763328m, taker.Fee);
            Assert.AreEqual("USDT", taker.FeeAsset);
            Assert.AreEqual(0m, taker.RealizedPnl);
            Assert.IsFalse(taker.IsMaker);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789116070165), taker.ExecutedAt);

            // 這個端點不回 clientOrderId。留成 null,不要用空字串冒充「有這項但是空的」。
            // The endpoint reports no clientOrderId; null says "not reported" where an empty string would not.
            Assert.IsNull(taker.ClientOrderId);

            var maker = trades[1];

            Assert.AreEqual(OrderSide.Sell, maker.Side);
            Assert.IsTrue(maker.IsMaker);
            Assert.AreEqual(0.39760000m, maker.RealizedPnl);

            // 手續費以別的資產抵扣時,幣別必須跟著數字一起留下來 —— 把 0.00019829 BNB 當成計價幣
            // 從損益裡扣掉,對帳會差一截,而差多少要看當天的 BNB 價格。
            // When the fee is paid in another asset its currency has to travel with the number: treating
            // 0.00019829 BNB as quote currency leaves a gap whose size depends on the BNB price that day.
            Assert.AreEqual("BNB", maker.FeeAsset);
            Assert.AreEqual(0.00019829m, maker.Fee);
            Assert.AreEqual("BNB", maker.FeeAsMoney().Currency);
        }
    }

    [TestMethod]
    public async Task NoFillsIsAnEmptyListRatherThanAFailure()
    {
        // 「這段期間沒有成交」是對帳最常見的結果。它要是失敗,補查迴圈每 5 分鐘就會叫一次假警報。
        // "Nothing traded in that window" is reconciliation's most common outcome; as a failure it would raise
        // a false alarm every five minutes.
        var (client, _, http) = Create(NoUserTrades);

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.HasCount(0, result.GetValueOrThrow());
        }
    }

    [TestMethod]
    public async Task TheDirectionFallsBackToTheBuyerFlagWhenSideIsAbsent()
    {
        var (client, _, http) = Create(UserTradeWithoutSideField);

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.AreEqual(OrderSide.Sell, result.GetValueOrThrow()[0].Side);
        }
    }

    [TestMethod]
    public async Task AFillWithoutATimestampIsRefused()
    {
        // 成交時刻是下一次補查的起點。把缺席的時刻填成「現在」,起點就被推到未來,
        // 中間真正漏掉的成交從此再也查不回來,而帳上看起來完全正常。
        // The fill time is the next sweep's cursor. Substituting "now" for a missing one pushes the cursor into
        // the future, after which the genuinely missed fills are unrecoverable and the books look normal.
        var (client, _, http) = Create(UserTradeWithoutTime);

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
            StringAssert.Contains(result.Error!.Message, "time", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task AFeeWithoutItsCurrencyIsRefused()
    {
        var (client, _, http) = Create(UserTradeWithoutFeeAsset);

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
            StringAssert.Contains(result.Error!.Message, "commissionAsset", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task AFillWithoutATradeIdIsRefused()
    {
        var (client, _, http) = Create(UserTradeWithoutId);

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
            StringAssert.Contains(result.Error!.Message, "id", StringComparison.Ordinal);
        }
    }

    // ── 請求形狀 / Request shape ─────────────────────────────────────────────

    [TestMethod]
    public async Task TheQueryIsASignedIdempotentGetDeclaringItsWeight()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.GetUserTradesAsync("BTCUSDT", since: DateTimeOffset.FromUnixTimeMilliseconds(1789116000000), limit: 500);

            var request = stub.LastRequest;

            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual(RequestIdempotency.Idempotent, request.Idempotency);
            Assert.AreEqual(BinanceRequestWeights.UserTrades, request.Weight);
            CollectionAssert.AreEqual(ExpectedUserTradesParameterOrder, request.ParameterNames.ToArray());
            Assert.AreEqual("1789116000000", request.Parameter("startTime"));
            Assert.AreEqual("500", request.Parameter("limit"));
            Assert.IsNull(request.Parameter("fromId"));
        }
    }

    [TestMethod]
    public async Task TheStartingTradeIdIsSentAsFromId()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.GetUserTradesAsync("BTCUSDT", fromId: 4059011L);

            var request = stub.LastRequest;

            Assert.AreEqual("4059011", request.Parameter("fromId"));
            Assert.IsNull(request.Parameter("startTime"));
        }
    }

    // ── 送出之前就擋下來的請求 / Refused before anything is sent ──────────────

    [TestMethod]
    public async Task SupplyingBothCursorsIsRefusedWithoutSendingAnything()
    {
        // 幣安不接受 startTime 與 fromId 同時出現。靜默丟掉其中一個會讓補查的起點不是呼叫端以為的那一個,
        // 而那種錯誤在補完之後看起來就是「這段期間沒有成交」。
        // Binance does not accept startTime together with fromId. Quietly dropping one starts the sweep
        // somewhere the caller did not choose, and that mistake reads afterwards as "nothing traded".
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync(
                "BTCUSDT",
                since: DateTimeOffset.FromUnixTimeMilliseconds(1789116000000),
                fromId: 4059011L);

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            Assert.IsFalse(stub.Requests.Any(request =>
                request.RequestUri?.AbsolutePath.Contains("/fapi/v1/userTrades", StringComparison.Ordinal) == true));
        }
    }

    [TestMethod]
    public async Task ALimitAboveTheExchangeMaximumIsRefusedRatherThanClamped()
    {
        // 悄悄夾到 1000 的後果是補查以為缺口補完了,剩下的成交從此沒有人再查。
        // Clamping silently to 1000 lets the sweep conclude the gap is closed; nothing ever looks for the rest.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT", limit: 5000);

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            StringAssert.Contains(result.Error.Message, "1000", StringComparison.Ordinal);
            Assert.IsFalse(stub.Requests.Any(request =>
                request.RequestUri?.AbsolutePath.Contains("/fapi/v1/userTrades", StringComparison.Ordinal) == true));
        }
    }

    [TestMethod]
    public async Task ABlankSymbolIsRefused()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("   ");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task MissingCredentialsAreReportedBeforeTheRequestLeaves()
    {
        // 沒有憑證還是送出去,拿到的是 -2014 或 -2015,而那兩句訊息會把人帶去查 IP 白名單。
        // Sending without credentials earns a -2014 or -2015 whose wording sends the reader to inspect IP
        // allowlists when the real cause is configuration that never loaded.
        var (client, stub, http) = Create(withCredentials: false);

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
            Assert.IsFalse(stub.Requests.Any(request =>
                request.RequestUri?.AbsolutePath.Contains("/fapi/v1/userTrades", StringComparison.Ordinal) == true));
        }
    }

    [TestMethod]
    public async Task AMalformedBodyIsReportedRatherThanReadAsNoFills()
    {
        // 解析不出來絕不能變成「空清單」。那會讓補查安靜地什麼都不補,而且每一輪都安靜地什麼都不補。
        // An unreadable body must never become an empty list: the sweep would then quietly backfill nothing,
        // round after round.
        var (client, _, http) = Create("""{"code":-1121,"msg":"Invalid symbol."}""");

        using (client)
        using (http)
        {
            var result = await client.GetUserTradesAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
        }
    }
}
