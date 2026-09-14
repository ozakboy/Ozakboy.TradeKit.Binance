using System.Net;
using Ozakboy.Http;
using Ozakboy.Http.Retry;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 條件單端點的離線測試:送單、撤單、查單與清單。全程以回應樣本重播,不連網。
/// Offline tests for the conditional order endpoints — placement, cancellation, lookup, and listing —
/// replaying sample responses without touching the network.
/// </summary>
/// <remarks>
/// 回應樣本<b>不是</b>實錄,而是依官方 New Algo Order / Query Algo Order 端點的回應 schema 組出來的
/// (擷取日期 2026-09-14)。因此這些測試驗的是「欄位對映有沒有照文件寫對」,
/// 而「文件有沒有說對」由 <see cref="BinanceTestnetIntegrationTests"/> 那一組連線測試負責。
/// The sample responses are <b>not</b> recordings but constructed from the response schemas of the official
/// New Algo Order and Query Algo Order endpoints, retrieved 2026-09-14. These tests therefore check that the
/// field mapping follows the documentation, while whether the documentation is right is the job of the live
/// tests in <see cref="BinanceTestnetIntegrationTests"/>.
/// </remarks>
[TestClass]
public sealed class BinanceConditionalOrderTradingTests
{
    /// <summary>送出條件單的回應。刻意<b>不含</b> <c>actualOrderId</c>:那張單還沒觸發。</summary>
    private const string AlgoOrderNew =
        """{"algoId":2148719,"clientAlgoId":"pt-algo-1","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.002","algoStatus":"NEW","triggerPrice":"37000.0","price":"0","icebergQuantity":"null","selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":true,"activatePrice":"","callbackRate":"","createTime":1789117383000,"updateTime":1789117383000,"triggerTime":0,"goodTillDate":0}""";

    /// <summary>查回來的同一張條件單,已被撤銷。</summary>
    private const string AlgoOrderCanceled =
        """{"algoId":2148719,"clientAlgoId":"pt-algo-1","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.002","algoStatus":"CANCELED","actualOrderId":"","actualPrice":"0","triggerPrice":"37000.0","price":"0","selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":true,"createTime":1789117383000,"updateTime":1789117384000,"triggerTime":0,"goodTillDate":0}""";

    /// <summary>撤銷單張條件單的回應。<c>code</c> 在這個端點是<b>字串</b>。</summary>
    private const string AlgoCancelAck =
        """{"algoId":2148719,"clientAlgoId":"pt-algo-1","code":"200","msg":"success"}""";

    /// <summary>撤銷全部條件單的回應。<c>code</c> 在這個端點是<b>數值</b>。</summary>
    private const string AlgoCancelAllAck =
        """{"code":200,"msg":"The operation of cancel all open order is done."}""";

    /// <summary>未結條件單清單。</summary>
    private const string OpenAlgoOrders =
        """[{"algoId":2148719,"clientAlgoId":"pt-algo-1","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.002","algoStatus":"NEW","actualOrderId":"","actualPrice":"0","triggerPrice":"37000.0","price":"0","selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":true,"createTime":1789117383000,"updateTime":1789117383000,"triggerTime":0,"goodTillDate":0}]""";

    /// <summary>已觸發的條件單,撮合引擎裡生出了一張實際委託。</summary>
    private const string AlgoOrderTriggered =
        """{"algoId":2148719,"clientAlgoId":"pt-algo-1","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.002","algoStatus":"TRIGGERED","actualOrderId":"8886900","actualPrice":"36999.50","actualType":"MARKET","triggerPrice":"37000.0","price":"0","selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":true,"createTime":1789117383000,"updateTime":1789117385000,"triggerTime":1789117385000,"goodTillDate":0}""";

    // ── 2026-09-14 Testnet 實錄 / Recorded on Testnet on 2026-09-14 ─────────────
    //
    // 以下三份是實錄,逐字取自同一張 STOP_MARKET(數量 0.0007、觸發價 74457.1、未觸發即撤)的原始回應。
    // 與上面依文件組的樣本相比,不符之處:POST 的 icebergQuantity 是 JSON null 而不是字串 "null";
    // GET 多出文件沒提的 tpOrderType;openAlgoOrders 以 actualQty 取代 actualPrice,
    // 而且數值是浮點數轉字串 —— 數量寫成 "7.0E-4"、價格寫成 "0.0"。
    // The three below are recordings, verbatim, of one STOP_MARKET (quantity 0.0007, trigger 74457.1, cancelled
    // untriggered). Against the documentation-built samples above: POST's icebergQuantity is a JSON null rather
    // than the string "null"; GET adds an undocumented tpOrderType; openAlgoOrders carries actualQty instead of
    // actualPrice and formats numbers as floating-point strings — a quantity of "7.0E-4" and a price of "0.0".

    /// <summary>實錄:<c>POST /fapi/v1/algoOrder</c> 的回應。Recorded placement response.</summary>
    private const string MeasuredPlaced =
        """{"algoId":1000000204743716,"clientAlgoId":"pulsetrade-test-1789363069217-pr","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.0007","algoStatus":"NEW","triggerPrice":"74457.10","price":"0.00","icebergQuantity":null,"selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":false,"createTime":1789363069186,"updateTime":1789363069186,"triggerTime":0,"goodTillDate":0}""";

    /// <summary>
    /// 實錄:撤單成功之後緊接著的 <c>GET /fapi/v1/algoOrder</c>,狀態<b>仍是 NEW</b>、<c>updateTime</c> 也沒變。
    /// Recorded: the <c>GET /fapi/v1/algoOrder</c> right after a successful cancellation, <b>still NEW</b> with
    /// the old <c>updateTime</c>.
    /// </summary>
    private const string MeasuredQueryStillNew =
        """{"algoId":1000000204743716,"clientAlgoId":"pulsetrade-test-1789363069217-pr","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.0007","algoStatus":"NEW","actualOrderId":"","actualPrice":"0.000000","triggerPrice":"74457.10","price":"0.00","icebergQuantity":null,"tpOrderType":"","selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":false,"createTime":1789363069186,"updateTime":1789363069186,"triggerTime":0,"goodTillDate":0}""";

    /// <summary>實錄:約 1.2 秒後同一個查詢,狀態才是 CANCELED。Recorded: the same query about 1.2 s later.</summary>
    private const string MeasuredQueryCanceled =
        """{"algoId":1000000204743716,"clientAlgoId":"pulsetrade-test-1789363069217-pr","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.0007","algoStatus":"CANCELED","actualOrderId":"","actualPrice":"0.000000","triggerPrice":"74457.10","price":"0.00","icebergQuantity":null,"tpOrderType":"","selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":false,"createTime":1789363069186,"updateTime":1789363071983,"triggerTime":0,"goodTillDate":0}""";

    /// <summary>實錄:<c>GET /fapi/v1/openAlgoOrders</c>。數量是 <c>"7.0E-4"</c>。Recorded open listing.</summary>
    private const string MeasuredOpenAlgoOrders =
        """[{"algoId":1000000204743716,"clientAlgoId":"pulsetrade-test-1789363069217-pr","algoType":"CONDITIONAL","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"7.0E-4","algoStatus":"NEW","actualOrderId":"","actualQty":"0.0","triggerPrice":"74457.1","price":"0.0","icebergQuantity":null,"selfTradePreventionMode":"EXPIRE_MAKER","workingType":"MARK_PRICE","priceMatch":"NONE","closePosition":false,"priceProtect":false,"reduceOnly":false,"createTime":1789363069186,"updateTime":1789363069186,"triggerTime":0,"goodTillDate":0}]""";

    /// <summary>實錄:<c>DELETE /fapi/v1/algoOrder</c> 的回應。Recorded cancel acknowledgement.</summary>
    private const string MeasuredCancelAck =
        """{"algoId":1000000204743716,"clientAlgoId":"pulsetrade-test-1789363069217-pr","code":"200","msg":"success"}""";

    /// <summary>實錄:查不到條件單(撤單剛成功時的回查也曾回這個)。Recorded: conditional order not found.</summary>
    private const string MeasuredNotFound = """{"code":-2013,"msg":"Order does not exist."}""";

    private static (BinanceFuturesClient Client, StubHttpMessageHandler Stub, HttpClient Http) Create(
        StubHttpMessageHandler? stub = null,
        RetryPolicy? retryPolicy = null)
    {
        var handler = stub ?? AlgoStub();
        var options = TestPipeline.CreateOptions(withCredentials: true, retryPolicy: retryPolicy);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, handler, clock);

        return (new BinanceFuturesClient(pipeline, options, clock), handler, http);
    }

    /// <summary>
    /// 依「方法 + 路徑」回應。條件單的下單、查單與撤單共用同一個路徑,只看路徑分不出來。
    /// Replies by method and path together: placement, lookup, and cancellation share one conditional order
    /// path, and the path alone cannot tell them apart.
    /// </summary>
    private static StubHttpMessageHandler AlgoStub(
        string? placeBody = null,
        string? queryBody = null,
        string? cancelBody = null) =>
        new((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            var body = (path, request.Method.Method) switch
            {
                var (p, _) when p.Contains("/fapi/v1/exchangeInfo", StringComparison.Ordinal) =>
                    Fixtures.ExchangeInfoMainnet,
                var (p, _) when p.Contains("/fapi/v1/openAlgoOrders", StringComparison.Ordinal) => OpenAlgoOrders,
                var (p, _) when p.Contains("/fapi/v1/algoOpenOrders", StringComparison.Ordinal) => AlgoCancelAllAck,
                ({ } algo, "POST") when algo.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal) =>
                    placeBody ?? AlgoOrderNew,
                ({ } algo, "GET") when algo.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal) =>
                    queryBody ?? AlgoOrderCanceled,
                ({ } algo, "DELETE") when algo.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal) =>
                    cancelBody ?? AlgoCancelAck,
                _ => null,
            };

            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"code":-1121,"msg":"Invalid symbol."}"""),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
        });

    /// <summary>
    /// 停損市價單實際送出的參數順序,也就是待簽字串的順序。
    /// The parameter order a stop-market order is actually sent with, which is the order that gets signed.
    /// </summary>
    private static readonly string[] ExpectedStopMarketParameterOrder =
    [
        "algoType", "symbol", "side", "positionSide", "type", "quantity", "triggerPrice",
        "workingType", "reduceOnly", "clientAlgoId", "recvWindow", "timestamp", "signature",
    ];

    private static ConditionalOrderRequest StopMarket() => new()
    {
        Symbol = "BTCUSDT",
        Side = OrderSide.Sell,
        ConditionalOrderType = ConditionalOrderType.StopMarket,
        Quantity = 0.002m,
        TriggerPrice = 37_000m,
        ReduceOnly = true,
        ClientConditionalOrderId = "pt-algo-1",
    };

    // ── 條件單絕不重試 / A conditional order is never retried ────────────────

    [TestMethod]
    public async Task AConditionalOrderIsMarkedNonIdempotentAndPinnedToNoRetry()
    {
        // 與一般下單同樣不能妥協,而且後果更糟:重送留下兩張停損,其中一張在部位被另一張平掉之後
        // 會反手開出一個沒人要的反向部位。
        // As non-negotiable as an ordinary order, with a worse failure mode: a re-send leaves two stops, and
        // once one closes the position the other opens an unwanted inverted one.
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(StopMarket());

            Assert.IsTrue(placed.IsSuccess, placed.Error?.Message);

            var request = stub.Requests.Single(snapshot =>
                snapshot.Method == HttpMethod.Post
                && snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal));

            Assert.AreEqual(RequestIdempotency.NonIdempotent, request.Idempotency);
            Assert.AreSame(RetryPolicy.NoRetry, request.RetryPolicy);
        }
    }

    [TestMethod]
    public async Task TheLookupAndTheCancellationStayRetryable()
    {
        // 撤單是冪等的。不重試的代價是留下一張以為已撤、實際還活著的停損,那危險得多。
        // Cancellation is idempotent, and not retrying risks a stop believed cancelled but still live.
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            _ = await client.GetConditionalOrderAsync("BTCUSDT", ConditionalOrderIdentifier.FromClientId("pt-algo-1"));
            _ = await client.CancelAllConditionalOrdersAsync("BTCUSDT");

            foreach (var request in stub.Requests.Where(snapshot =>
                snapshot.Method != HttpMethod.Post))
            {
                Assert.AreEqual(RequestIdempotency.Idempotent, request.Idempotency);
            }
        }
    }

    // ── 送出的請求 / The outgoing request ────────────────────────────────────

    [TestMethod]
    public async Task AConditionalOrderGoesToTheAlgoEndpointAndNeverToTheOrderEndpoint()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(StopMarket());

            Assert.IsTrue(placed.IsSuccess, placed.Error?.Message);

            var paths = stub.Requests.Select(snapshot => snapshot.RequestUri!.AbsolutePath).ToList();

            Assert.Contains("/fapi/v1/algoOrder", paths);

            // 送到舊端點會換回 -4120。這條斷言把「走對路徑」釘在單元測試層,不必等到連線才發現。
            // The old endpoint answers -4120. This pins the routing down at unit-test level rather than
            // leaving it to be discovered on the wire.
            Assert.DoesNotContain("/fapi/v1/order", paths);
        }
    }

    [TestMethod]
    public async Task TheParameterOrderIsTheOrderThatGetsSigned()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            _ = await client.PlaceConditionalOrderAsync(StopMarket());

            var request = stub.Requests.Single(snapshot =>
                snapshot.Method == HttpMethod.Post
                && snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal));

            // 幣安簽的是實際送出的查詢字串,順序換掉簽章就不同。這一串是刻意釘死的。
            // Binance signs the query string it receives, so a different order is a different signature. This
            // sequence is deliberately pinned.
            CollectionAssert.AreEqual(ExpectedStopMarketParameterOrder, request.ParameterNames.ToArray());
        }
    }

    [TestMethod]
    public async Task ThePriceAndQuantityAreNormalisedBeforeTheRequestLeaves()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var request = StopMarket() with { TriggerPrice = 37_000.17m, Quantity = 0.0025m };

            _ = await client.PlaceConditionalOrderAsync(request);

            var sent = stub.Requests.Single(snapshot =>
                snapshot.Method == HttpMethod.Post
                && snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal));

            // 數量一律向下對齊:向上會讓實際部位大於風控算出來的規模。
            // Quantities always align downwards: rounding up makes the real position larger than the size risk
            // management calculated.
            Assert.AreEqual("0.002", sent.Parameter("quantity"));
            Assert.AreEqual("37000.2", sent.Parameter("triggerPrice"));
        }
    }

    [TestMethod]
    public async Task AClientAlgoIdIsGeneratedWhenTheCallerSuppliesNone()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            _ = await client.PlaceConditionalOrderAsync(StopMarket() with { ClientConditionalOrderId = null });

            var sent = stub.Requests.Single(snapshot =>
                snapshot.Method == HttpMethod.Post
                && snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal));

            var generated = sent.Parameter("clientAlgoId");

            Assert.IsNotNull(generated);
            Assert.IsTrue(BinanceClientOrderId.IsValid(generated), $"產生的編號不符幣安格式:{generated}");
        }
    }

    // ── 回應對映 / Response mapping ──────────────────────────────────────────

    [TestMethod]
    public async Task ThePlacementResponseMapsOntoTheNeutralModel()
    {
        var (client, _, http) = Create();

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(StopMarket());

            Assert.IsTrue(placed.TryGetValue(out var order), placed.Error?.Message);
            Assert.AreEqual("BTCUSDT", order.Symbol);
            Assert.AreEqual("pt-algo-1", order.ClientConditionalOrderId);
            Assert.AreEqual("2148719", order.ExchangeConditionalOrderId);
            Assert.AreEqual(OrderSide.Sell, order.Side);
            Assert.AreEqual(ConditionalOrderType.StopMarket, order.ConditionalOrderType);
            Assert.AreEqual(ConditionalOrderStatus.New, order.Status);
            Assert.AreEqual(0.002m, order.Quantity);
            Assert.AreEqual(37_000.0m, order.TriggerPrice);
            Assert.AreEqual(TriggerPriceType.MarkPrice, order.TriggerPriceType);
            Assert.IsTrue(order.ReduceOnly);
            Assert.IsTrue(order.IsOpen);
            Assert.IsFalse(order.IsFinal);
        }
    }

    [TestMethod]
    public async Task AnUntriggeredOrderHasNoTriggeredOrderIdAndNoTriggerTime()
    {
        var (client, _, http) = Create();

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(StopMarket());

            Assert.IsTrue(placed.TryGetValue(out var order), placed.Error?.Message);

            // actualOrderId 未觸發時是空字串,triggerTime 是 0。照抄會讓上層拿空字串去查單,
            // 並且看到一個 1970 年的觸發時間。
            // actualOrderId is an empty string and triggerTime is 0 before the trigger. Passing them through
            // sends the caller to look up "" and shows a trigger time in 1970.
            Assert.IsNull(order.TriggeredOrderId);
            Assert.IsNull(order.TriggeredAt);
        }
    }

    [TestMethod]
    public async Task ATriggeredOrderCarriesTheRealOrderIdAndTheTriggerTime()
    {
        var (client, _, http) = Create(AlgoStub(queryBody: AlgoOrderTriggered));

        using (http)
        using (client)
        {
            var found = await client.GetConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromClientId("pt-algo-1"));

            Assert.IsTrue(found.TryGetValue(out var order), found.Error?.Message);
            Assert.AreEqual(ConditionalOrderStatus.Triggered, order.Status);

            // 這個編號是接回一般委託與成交的唯一線索。
            // This id is the only link back to ordinary orders and fills.
            Assert.AreEqual("8886900", order.TriggeredOrderId);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117385000), order.TriggeredAt);
            Assert.IsTrue(order.IsOpen, "觸發後的委託還沒成交完,緊急出場仍需撤掉它。");
        }
    }

    [TestMethod]
    public async Task AMarketStyleConditionalOrderHasNoPriceRatherThanAPriceOfZero()
    {
        var (client, _, http) = Create();

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(StopMarket());

            Assert.IsTrue(placed.TryGetValue(out var order), placed.Error?.Message);

            // 幣安對「沒有這個價格」的表示是 "0"。照抄會讓上層看到一張「限價零元」的停損。
            // Binance writes "no such price" as "0". Copying it through shows a stop priced at zero.
            Assert.IsNull(order.Price);
            Assert.IsNull(order.CallbackRate, "空字串的 callbackRate 不該變成 0。");
            Assert.IsNull(order.ActivationPrice, "空字串的 activatePrice 不該變成 0。");
        }
    }

    [TestMethod]
    public async Task AnUnmappableAlgoStatusFailsTheParseRatherThanPassingThrough()
    {
        const string unknownStatus =
            """{"algoId":2148719,"clientAlgoId":"pt-algo-1","orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL","quantity":"0.002","algoStatus":"NOT_A_REAL_STATUS","triggerPrice":"37000.0","createTime":1789117383000,"updateTime":1789117383000,"triggerTime":0}""";

        var (client, _, http) = Create(AlgoStub(queryBody: unknownStatus));

        using (http)
        using (client)
        {
            var found = await client.GetConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromClientId("pt-algo-1"));

            // 既不算有效、也不算終態的停損,會讓對帳永遠等不到結局,而畫面上看起來一切正常。
            // A stop that counts as neither live nor final leaves reconciliation waiting for an outcome that
            // never comes, while everything on screen looks fine.
            Assert.IsTrue(found.IsFailure);
            Assert.IsTrue(found.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
            Assert.AreEqual("algoStatus", field);
        }
    }

    // ── 撤單 / Cancellation ──────────────────────────────────────────────────

    [TestMethod]
    public async Task CancellingReadsTheOrderBackBecauseTheCancelResponseIsNotEnough()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var cancelled = await client.CancelConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromClientId("pt-algo-1"));

            Assert.IsTrue(cancelled.TryGetValue(out var order), cancelled.Error?.Message);
            Assert.AreEqual(ConditionalOrderStatus.Canceled, order.Status);

            // 撤單的回應只有 algoId、clientAlgoId、code、msg,湊不出方向與類型,所以撤完要再查一次。
            // The cancel response carries only algoId, clientAlgoId, code and msg, which cannot produce a side
            // or a type, so the order is read back afterwards.
            var algoRequests = stub.Requests
                .Where(snapshot =>
                    snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(HttpMethod.Delete, algoRequests[0].Method);
            Assert.AreEqual(HttpMethod.Get, algoRequests[1].Method);

            // 回查用的是撤單回應給的 algoId,不是呼叫端傳進來的 clientAlgoId。
            // The re-read uses the algoId the cancel response returned rather than the caller's clientAlgoId.
            Assert.AreEqual("2148719", algoRequests[1].Parameter("algoId"));
        }
    }

    /// <summary>
    /// 撤單固定回實錄的成功回應,回查依序回給定的內容(用完就重複最後一個)。
    /// Answers the cancellation with the recorded acknowledgement and the read-backs with the given replies in
    /// order, repeating the last one once they run out.
    /// </summary>
    private static StubHttpMessageHandler LaggingReadBackStub(params (HttpStatusCode Status, string Body)[] readBacks)
    {
        var reads = 0;

        return new((request, _) =>
        {
            var (status, body) = request.Method == HttpMethod.Delete
                ? (HttpStatusCode.OK, MeasuredCancelAck)
                : readBacks[Math.Min(Interlocked.Increment(ref reads) - 1, readBacks.Length - 1)];

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });
    }

    private static int ReadBackCount(StubHttpMessageHandler stub) =>
        stub.Requests.Count(snapshot =>
            snapshot.Method == HttpMethod.Get
            && snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal));

    [TestMethod]
    public async Task CancellingWaitsForTheQueryEndpointToCatchUpRatherThanReportingNew()
    {
        // 2026-09-14 Testnet 實測:撤單回 "200" 之後,緊接著的回查仍是 NEW,一秒多後才是 CANCELED。
        // 把第一個回查結果照抄回去,撤單成功卻回報「還掛著」—— 整合測試就是這樣紅的。
        // Measured on Testnet on 2026-09-14: after the cancellation answered "200", the immediate read-back was
        // still NEW and only showed CANCELED over a second later. Echoing the first read-back reports a successful
        // cancellation as "still resting", which is exactly how the integration test went red.
        var (client, stub, http) = Create(LaggingReadBackStub(
            (HttpStatusCode.OK, MeasuredQueryStillNew),
            (HttpStatusCode.OK, MeasuredQueryStillNew),
            (HttpStatusCode.OK, MeasuredQueryCanceled)));

        using (http)
        using (client)
        {
            client.CancelReadBackDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];

            var cancelled = await client.CancelConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromClientId("pulsetrade-test-1789363069217-pr"));

            Assert.IsTrue(cancelled.TryGetValue(out var order), cancelled.Error?.Message);
            Assert.AreEqual(ConditionalOrderStatus.Canceled, order.Status);
            Assert.AreEqual("1000000204743716", order.ExchangeConditionalOrderId);
            Assert.AreEqual(3, ReadBackCount(stub));
        }
    }

    [TestMethod]
    public async Task CancellingReadsAgainThroughATransientNotFound()
    {
        // 另一次實測:撤單成功之後緊接著的回查回 -2013。那不是撤單失敗,是查詢端點還沒同步。
        // Another measured run: the read-back right after a successful cancellation answered -2013. That is not
        // a failed cancellation but a query endpoint that has not caught up.
        var (client, stub, http) = Create(LaggingReadBackStub(
            (HttpStatusCode.BadRequest, MeasuredNotFound),
            (HttpStatusCode.OK, MeasuredQueryCanceled)));

        using (http)
        using (client)
        {
            client.CancelReadBackDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];

            var cancelled = await client.CancelConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromExchangeId("1000000204743716"));

            Assert.IsTrue(cancelled.TryGetValue(out var order), cancelled.Error?.Message);
            Assert.AreEqual(ConditionalOrderStatus.Canceled, order.Status);
            Assert.AreEqual(2, ReadBackCount(stub));
        }
    }

    [TestMethod]
    public async Task ACancellationTheQueryNeverCatchesUpWithFailsTransientlyAndSaysItWentThrough()
    {
        // 等完預算仍是 NEW:不照抄(那會說停損還掛著)、也不改寫成 Canceled(那是替交易所說話),
        // 而是回報暫時性失敗,並講明撤單本身已經成功。
        // Still NEW after the whole budget: neither echoed (that says the stop still rests) nor rewritten as
        // Canceled (that speaks for the exchange), but a transient failure stating the cancellation went through.
        var (client, stub, http) = Create(LaggingReadBackStub((HttpStatusCode.OK, MeasuredQueryStillNew)));

        using (http)
        using (client)
        {
            client.CancelReadBackDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];

            var cancelled = await client.CancelConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromExchangeId("1000000204743716"));

            Assert.IsTrue(cancelled.IsFailure);
            Assert.AreEqual(TradeErrorCodes.ExchangeUnavailable, cancelled.Error!.Code);
            Assert.IsTrue(cancelled.Error.IsTransient);
            Assert.Contains("撤單本身已經成功", cancelled.Error.Message);

            // 第一次立刻查,之後每個間隔各查一次,不多也不少。
            // One immediate read-back and one per delay, no more and no fewer.
            Assert.AreEqual(3, ReadBackCount(stub));
        }
    }

    [TestMethod]
    public async Task ACancellationThatStaysNotFoundKeepsTheConditionalCodeAndSaysItWentThrough()
    {
        var (client, stub, http) = Create(LaggingReadBackStub((HttpStatusCode.BadRequest, MeasuredNotFound)));

        using (http)
        using (client)
        {
            client.CancelReadBackDelays = [TimeSpan.FromMilliseconds(1)];

            var cancelled = await client.CancelConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromExchangeId("1000000204743716"));

            Assert.IsTrue(cancelled.IsFailure);
            Assert.AreEqual(TradeErrorCodes.ConditionalOrderNotFound, cancelled.Error!.Code);
            Assert.Contains("撤單本身已經成功", cancelled.Error.Message);
            Assert.AreEqual(2, ReadBackCount(stub));
        }
    }

    [TestMethod]
    public async Task AReadBackThatIsAlreadyFinalIsNotRepeated()
    {
        // 交易所已經同步的正常情況,不該為了保險多查幾次去吃權重。
        // When the exchange has already caught up, nothing is re-read just in case.
        var (client, stub, http) = Create(LaggingReadBackStub((HttpStatusCode.OK, MeasuredQueryCanceled)));

        using (http)
        using (client)
        {
            var cancelled = await client.CancelConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromExchangeId("1000000204743716"));

            Assert.IsTrue(cancelled.IsSuccess, cancelled.Error?.Message);
            Assert.AreEqual(1, ReadBackCount(stub));
        }
    }

    [TestMethod]
    public async Task CancellingAllUsesTheDedicatedAlgoEndpointRatherThanTheOrdinaryOne()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var cancelled = await client.CancelAllConditionalOrdersAsync("BTCUSDT");

            Assert.IsTrue(cancelled.IsSuccess, cancelled.Error?.Message);

            var paths = stub.Requests.Select(snapshot => snapshot.RequestUri!.AbsolutePath).ToList();

            // 兩個端點互不涵蓋:撤一般掛單撤不掉條件單,反之亦然。
            // Neither endpoint covers the other: cancelling ordinary orders leaves the conditional ones, and
            // the reverse holds too.
            Assert.Contains("/fapi/v1/algoOpenOrders", paths);
            Assert.DoesNotContain("/fapi/v1/allOpenOrders", paths);
        }
    }

    // ── 清單 / Listing ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task TheOpenListingAsksOnlyForConditionalOrders()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var open = await client.GetOpenConditionalOrdersAsync("BTCUSDT");

            Assert.IsTrue(open.TryGetValue(out var orders), open.Error?.Message);
            Assert.HasCount(1, orders);
            Assert.AreEqual("pt-algo-1", orders[0].ClientConditionalOrderId);

            var sent = stub.Requests.Single(snapshot =>
                snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/openAlgoOrders", StringComparison.Ordinal));

            // Algo Service 底下還有策略單與網格單。不限定 algoType,清單會混進本套件沒有模型化的類型,
            // 而解析會在 orderType 那一關失敗,失敗原因還指向一張根本不是本套件掛的單。
            // The Algo Service also carries strategy and grid orders. Without narrowing by algoType the
            // listing mixes in types this package does not model, and the parse fails at orderType pointing at
            // an order this package never placed.
            Assert.AreEqual("CONDITIONAL", sent.Parameter("algoType"));
            Assert.AreEqual("BTCUSDT", sent.Parameter("symbol"));
        }
    }

    [TestMethod]
    public async Task TheOpenListingReadsAQuantityWrittenInExponentNotation()
    {
        // 2026-09-14 Testnet 實測:openAlgoOrders 把 0.0007 寫成 "7.0E-4"。以固定小數的讀法解析會失敗,
        // 數量落回 0 —— 未結清單上的停損看起來什麼都沒保護,而清單本身照樣「成功」。
        // Measured on Testnet on 2026-09-14: openAlgoOrders writes 0.0007 as "7.0E-4". Parsed as fixed-point it
        // fails and the quantity falls back to 0 — the stop on the listing appears to protect nothing while the
        // listing itself still "succeeds".
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MeasuredOpenAlgoOrders, System.Text.Encoding.UTF8, "application/json"),
        });

        var (client, _, http) = Create(stub);

        using (http)
        using (client)
        {
            var open = await client.GetOpenConditionalOrdersAsync("BTCUSDT");

            Assert.IsTrue(open.TryGetValue(out var orders), open.Error?.Message);
            Assert.HasCount(1, orders);

            var order = orders[0];

            Assert.AreEqual(0.0007m, order.Quantity);
            Assert.AreEqual(74_457.1m, order.TriggerPrice);

            // "0.0" 一樣代表沒有限價。"0.0" likewise means no limit price.
            Assert.IsNull(order.Price);
            Assert.IsNull(order.TriggeredOrderId);
            Assert.IsNull(order.TriggeredAt);
            Assert.AreEqual(ConditionalOrderStatus.New, order.Status);
            Assert.AreEqual(TriggerPriceType.MarkPrice, order.TriggerPriceType);
        }
    }

    [TestMethod]
    [DataRow("7.0E-4", "0.0007")]
    [DataRow("1.0E7", "10000000")]
    [DataRow("1.2345E-5", "0.000012345")]
    [DataRow("0.0", "0")]
    public void TheAlgoReaderParsesExponentNotationExactly(string written, string expected)
    {
        // Java Double.toString 在小於 10⁻³ 或大於等於 10⁷ 時改用科學記號,兩端都要接得住,而且是精確的十進位值。
        // Java's Double.toString switches to exponents below 10⁻³ and at or above 10⁷; both ends must parse, and
        // into the exact decimal value.
        using var document = System.Text.Json.JsonDocument.Parse($$"""{"quantity":"{{written}}"}""");

        Assert.IsTrue(BinanceJson.TryGetDecimalAllowingExponent(document.RootElement, "quantity", out var value));
        Assert.AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);

        // 一般讀法維持拒絕,例外只開在 Algo Service 這一條。
        // The ordinary reader keeps refusing; the exception is confined to the Algo Service path.
        if (written.Contains('E', StringComparison.Ordinal))
        {
            Assert.IsFalse(BinanceJson.TryGetDecimal(document.RootElement, "quantity", out _));
        }
    }

    [TestMethod]
    public async Task TheRecordedPlacementAndQueryShapesParse()
    {
        // 實錄的 POST 沒有 actualOrderId、icebergQuantity 是 JSON null;GET 多了 tpOrderType 與 actualPrice。
        // The recorded POST lacks actualOrderId and has a JSON null icebergQuantity; GET adds tpOrderType and
        // actualPrice.
        var (client, _, http) = Create(AlgoStub(placeBody: MeasuredPlaced, queryBody: MeasuredQueryStillNew));

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(StopMarket());

            Assert.IsTrue(placed.TryGetValue(out var order), placed.Error?.Message);
            Assert.AreEqual("1000000204743716", order.ExchangeConditionalOrderId);
            Assert.AreEqual(0.0007m, order.Quantity);
            Assert.AreEqual(74_457.1m, order.TriggerPrice);
            Assert.IsNull(order.Price);
            Assert.IsNull(order.TriggeredOrderId);
            Assert.IsNull(order.TriggeredAt);
            Assert.IsFalse(order.ReduceOnly);

            var found = await client.GetConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromExchangeId("1000000204743716"));

            Assert.IsTrue(found.TryGetValue(out var queried), found.Error?.Message);
            Assert.AreEqual(ConditionalOrderStatus.New, queried.Status);
            Assert.IsNull(queried.TriggeredOrderId, "actualOrderId 的空字串代表還沒有實際委託。");
            Assert.AreEqual(order.Quantity, queried.Quantity);
            Assert.AreEqual(order.TriggerPrice, queried.TriggerPrice);
        }
    }

    [TestMethod]
    public async Task TheOpenListingDeclaresTheHeavierWeightWhenNoSymbolIsGiven()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            _ = await client.GetOpenConditionalOrdersAsync("BTCUSDT");
            _ = await client.GetOpenConditionalOrdersAsync();

            var requests = stub.Requests
                .Where(snapshot =>
                    snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/openAlgoOrders", StringComparison.Ordinal))
                .ToList();

            // 不帶商品代碼的權重是 40,四十倍。少宣告的下場不是本地擋下來,而是交易所回 429。
            // Omitting the symbol costs 40, forty times as much. Under-declaring is not caught locally but by
            // an HTTP 429 from the exchange.
            Assert.AreEqual(1, requests[0].Weight);
            Assert.AreEqual(40, requests[1].Weight);
        }
    }

    [TestMethod]
    public async Task ABlankSymbolIsNotTreatedAsEverySymbol()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var result = await client.GetOpenConditionalOrdersAsync("   ");

            // 空白幾乎都是變數沒填到,靜默改打全商品會讓一個 bug 變成四十倍的權重支出。
            // A blank almost always means an unfilled variable, and silently widening it turns one bug into
            // forty times the weight.
            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            Assert.IsEmpty(stub.Requests);
        }
    }

    // ── 錯誤對映 / Error mapping ─────────────────────────────────────────────

    [TestMethod]
    public async Task AMissingConditionalOrderIsReportedUnderItsOwnCode()
    {
        // 幣安的 algo 端點沒有自己的錯誤碼,查不到條件單回的是一般的 -2013 NO_SUCH_ORDER。
        // 區分只能在呼叫端做 —— 共用代碼會讓上層分不出「停損不見了」與「進場單不見了」,
        // 而前者代表部位正在裸奔。
        // The algo endpoints have no error codes of their own and a missing conditional order answers the
        // ordinary -2013 NO_SUCH_ORDER. The distinction can only be made at the call site: sharing the code
        // leaves callers unable to tell "the stop is gone" from "the entry is gone", and the first means a
        // position is unprotected.
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"code":-2013,"msg":"Order does not exist."}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var (client, _, http) = Create(stub);

        using (http)
        using (client)
        {
            var found = await client.GetConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromClientId("pt-algo-missing"));

            Assert.IsTrue(found.IsFailure);
            Assert.AreEqual(TradeErrorCodes.ConditionalOrderNotFound, found.Error!.Code);
            Assert.AreNotEqual(TradeErrorCodes.OrderNotFound, found.Error.Code);

            // 原始幣安代碼仍然跟著錯誤走,事後查 log 才對得回官方文件。
            // The raw Binance code still travels with the error so a log can be matched to the documentation.
            Assert.IsTrue(found.Error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var apiCode));
            Assert.AreEqual(-2013L, apiCode);
        }
    }

    [TestMethod]
    [DataRow(-2013, TradeErrorCodes.ConditionalOrderNotFound)]
    [DataRow(-4116, TradeErrorCodes.DuplicateClientConditionalOrderId)]
    [DataRow(-2025, TradeErrorCodes.ConditionalOrderLimitExceeded)]
    public async Task EachGenericCodeIsRelabelledForTheConditionalPath(int apiCode, string expected)
    {
        // 這三碼在一般委託上有各自的中立代碼,條件單路徑上要換成條件單專屬的那一組。
        // -2025 特別值得分開:條件單的上限是全帳戶合計 200 張,與「保證金不足」這種同樣落在
        // OrderRejected 的原因完全不是一回事,而呼叫端對兩者的處置也不同。
        // The three have their own neutral codes for ordinary orders and need the conditional counterparts
        // here. -2025 is worth separating in particular: the conditional ceiling is 200 across the whole
        // account, which is nothing like "insufficient margin" even though both land on OrderRejected, and a
        // caller responds to them differently.
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                $$"""{"code":{{apiCode}},"msg":"rejected."}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var (client, _, http) = Create(stub);

        using (http)
        using (client)
        {
            var found = await client.GetConditionalOrderAsync(
                "BTCUSDT",
                ConditionalOrderIdentifier.FromClientId("pt-algo-1"));

            Assert.IsTrue(found.IsFailure);
            Assert.AreEqual(expected, found.Error!.Code);
        }
    }

    [TestMethod]
    public async Task AFailedPlacementStillHandsBackTheClientAlgoId()
    {
        var stub = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("/fapi/v1/exchangeInfo", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        Fixtures.ExchangeInfoMainnet,
                        System.Text.Encoding.UTF8,
                        "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"code":-2019,"msg":"Margin is insufficient."}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });

        var (client, _, http) = Create(stub);

        using (http)
        using (client)
        {
            var placed = await client.PlaceConditionalOrderAsync(
                StopMarket() with { ClientConditionalOrderId = null });

            Assert.IsTrue(placed.IsFailure);

            // 編號由本套件產生時,呼叫端手上原本沒有它。失敗一起弄丟編號,那張停損就成了一個
            // 既查不到也撤不掉、卻會在某個價位真的動用部位的東西。
            // When the id is generated here the caller never held it. Losing it with the failure leaves a stop
            // that can be neither looked up nor cancelled and that will still move the position at some price.
            Assert.IsTrue(placed.Error!.TryGetData(BinanceErrorDataKeys.ClientAlgoId, out var echoed));
            Assert.IsNotNull(echoed);
            Assert.IsTrue(BinanceClientOrderId.IsValid(echoed));
        }
    }

    [TestMethod]
    public async Task AnOrderBelowTheMinimumNotionalNeverLeavesTheProcess()
    {
        var (client, stub, http) = Create();

        using (http)
        using (client)
        {
            var result = await client.PlaceConditionalOrderAsync(StopMarket() with { Quantity = 0.001m });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.NotionalBelowMinimum, result.Error!.Code);
            Assert.IsFalse(
                stub.Requests.Any(snapshot =>
                    snapshot.RequestUri!.AbsolutePath.Contains("/fapi/v1/algoOrder", StringComparison.Ordinal)),
                "校正後下不了單的請求不該送出去換一次拒單。");
        }
    }
}
