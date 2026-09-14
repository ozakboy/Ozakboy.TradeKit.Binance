using System.Globalization;
using System.Net;
using Ozakboy.Http;
using Ozakboy.Http.Retry;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 交易端點的離線測試:下單、撤單、查單與帳戶設定。全程以錄製的回應重播,不連網。
/// Offline tests for the trading endpoints — placement, cancellation, lookup, and account settings — replaying
/// recorded responses without touching the network.
/// </summary>
[TestClass]
public sealed class BinanceTradingTests
{
    private static (BinanceFuturesClient Client, StubHttpMessageHandler Stub, HttpClient Http) Create(
        StubHttpMessageHandler? stub = null,
        bool withCredentials = true,
        RetryPolicy? retryPolicy = null)
    {
        var handler = stub ?? TradingStub();
        var options = TestPipeline.CreateOptions(withCredentials: withCredentials, retryPolicy: retryPolicy);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, handler, clock);

        return (new BinanceFuturesClient(pipeline, options, clock), handler, http);
    }

    private static StubHttpMessageHandler TradingStub(string? orderBody = null) =>
        StubHttpMessageHandler.ByPath(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/fapi/v1/exchangeInfo"] = Fixtures.ExchangeInfoMainnet,
            ["/fapi/v1/allOpenOrders"] = Fixtures.CancelAll,
            ["/fapi/v1/openOrders"] = Fixtures.OpenOrders,
            ["/fapi/v1/order"] = orderBody ?? Fixtures.OrderNew,
            ["/fapi/v1/leverage"] = Fixtures.Leverage,
            ["/fapi/v1/marginType"] = """{"code":200,"msg":"success"}""",
        });

    private static OrderRequest LimitBuy(decimal price = 60000m, decimal quantity = 0.002m) => new()
    {
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        OrderType = OrderType.Limit,
        Quantity = quantity,
        Price = price,
    };

    private static decimal Decimal(string? text) =>
        decimal.Parse(text!, NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>
    /// 限價單實際送出的參數順序,也就是待簽字串的順序。
    /// The parameter order a limit order is actually sent with, which is the order that gets signed.
    /// </summary>
    private static readonly string[] ExpectedLimitOrderParameterOrder =
    [
        "symbol", "side", "positionSide", "type", "timeInForce", "quantity", "price",
        "newClientOrderId", "recvWindow", "timestamp", "signature",
    ];

    // ── 下單絕不重試 / An order is never retried ─────────────────────────────

    [TestMethod]
    public async Task AnOrderIsMarkedNonIdempotentAndPinnedToNoRetry()
    {
        // 這是整個套件最不能妥協的一條。逾時不代表對方沒收到,重送就是重複的部位。
        // 兩道防線都要在:標記擋的是 RetryHandler,策略擋的是「有人把預設策略換成看什麼都重試」。
        // The one rule this package will not bend. A timeout does not mean the exchange missed it, and a
        // re-send is a duplicated position. Both guards must be present: the marker stops RetryHandler, the
        // policy stops whoever swaps the default for one that retries everything.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy());

            var request = stub.LastRequest;

            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(RequestIdempotency.NonIdempotent, request.Idempotency);
            Assert.AreEqual(RetryPolicy.NoRetry, request.RetryPolicy);
            Assert.IsFalse(RetryHandler.IsRetryable(new HttpRequestMessage(HttpMethod.Post, "fapi/v1/order")
                .AsNonIdempotent()));
        }
    }

    [TestMethod]
    public async Task AFailingOrderIsSentExactlyOnceEvenWhenThePipelineRetriesEverythingElse()
    {
        // 行為層面的證據,而不只是旗標對不對:同一條管線上,撤單被重試三次,下單只送一次。
        // Behavioural proof rather than a flag check: on one pipeline the cancellation is attempted three
        // times and the order exactly once.
        var stub = StubHttpMessageHandler.Json(
            """{"code":-1001,"msg":"Internal error; unable to process your request."}""",
            HttpStatusCode.ServiceUnavailable);

        var (client, _, http) = Create(stub, retryPolicy: TestPipeline.ImmediateRetries);

        using (client)
        using (http)
        {
            var cancel = await client.CancelOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("1"));

            Assert.IsTrue(cancel.IsFailure);
            Assert.AreEqual(3, stub.CallCount);
        }

        var orderStub = StubHttpMessageHandler.ByPath(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/fapi/v1/exchangeInfo"] = Fixtures.ExchangeInfoMainnet,
        });

        var (orderClient, orderHandler, orderHttp) = Create(orderStub, retryPolicy: TestPipeline.ImmediateRetries);

        using (orderClient)
        using (orderHttp)
        {
            // exchangeInfo 打一次(交易規則),下單打一次,合計兩次。下單若被重試就會是三次以上。
            // One call for the trading rules and one for the order: two. A retried order would make it three.
            var placed = await orderClient.PlaceOrderAsync(LimitBuy());

            Assert.IsTrue(placed.IsFailure);
            Assert.AreEqual(2, orderHandler.CallCount);
        }
    }

    [TestMethod]
    public async Task AFailedOrderCarriesItsClientOrderIdSoItCanBeLookedUpInsteadOfResent()
    {
        // 自動產生的編號若隨著失敗一起消失,那張單就成了一個既查不到也撤不掉的部位。
        // An auto-generated id that vanishes with the failure leaves an order that can neither be looked up
        // nor cancelled.
        var stub = StubHttpMessageHandler.ByPath(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/fapi/v1/exchangeInfo"] = Fixtures.ExchangeInfoMainnet,
        });

        var (client, _, http) = Create(stub);

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy());

            Assert.IsTrue(result.IsFailure);
            Assert.IsTrue(result.Error!.TryGetData(BinanceErrorDataKeys.ClientOrderId, out var clientOrderId));
            Assert.IsTrue(BinanceClientOrderId.IsValid(clientOrderId));
            StringAssert.Contains(result.Error.Message, clientOrderId!, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task ACallerSuppliedClientOrderIdIsUsedVerbatim()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy() with { ClientOrderId = "my-own-id-42" });

            Assert.AreEqual("my-own-id-42", stub.LastRequest.Parameter("newClientOrderId"));
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task ABlankClientOrderIdCountsAsNoneAndOneIsGenerated(string clientOrderId)
    {
        // 空字串當成「沒給」而不是「給了一個不合法的」。設定沒讀到、變數沒填到都會長這樣,
        // 而這裡拒絕整張單並不會讓任何人更安全 —— 產生一個編號才是。
        // A blank id counts as none rather than as an invalid one. Unloaded configuration and unfilled
        // variables both look like this, and refusing the order makes nobody safer; generating one does.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy() with { ClientOrderId = clientOrderId });

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.IsTrue(BinanceClientOrderId.IsValid(stub.LastRequest.Parameter("newClientOrderId")));
        }
    }

    [TestMethod]
    public async Task AGeneratedClientOrderIdIsAlwaysSentAndAlwaysValid()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy());

            var generated = stub.LastRequest.Parameter("newClientOrderId");

            Assert.IsTrue(BinanceClientOrderId.IsValid(generated));
            StringAssert.StartsWith(generated!, BinanceClientOrderId.DefaultPrefix, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("with space")]
    [DataRow("bad#char")]
    [DataRow("way-too-long-0123456789012345678901234567890")]
    public async Task AClientOrderIdBinanceWouldRejectIsRefusedBeforeTheRequestLeaves(string clientOrderId)
    {
        // 格式不符的編號送出去只會換回 -4015,而那一趟還是吃掉限流額度。
        // A malformed id earns nothing but a -4015, and the attempt still costs quota.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy() with { ClientOrderId = clientOrderId });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);

            // 只打了 exchangeInfo 那一次,沒有送出委託。
            // Only the exchangeInfo call went out; no order was sent.
            Assert.IsFalse(stub.Requests.Any(request => request.Method == HttpMethod.Post));
        }
    }

    // ── 訂單類型對映 / Order type mapping ────────────────────────────────────

    [TestMethod]
    public async Task ALimitOrderCarriesPriceAndTimeInForceAndNothingConditional()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy());

            var request = stub.LastRequest;

            Assert.AreEqual("LIMIT", request.Parameter("type"));
            Assert.AreEqual("BUY", request.Parameter("side"));
            Assert.AreEqual("BOTH", request.Parameter("positionSide"));
            Assert.AreEqual("GTC", request.Parameter("timeInForce"));
            Assert.AreEqual(60000m, Decimal(request.Parameter("price")));
            Assert.AreEqual(0.002m, Decimal(request.Parameter("quantity")));

            // 多送一個不屬於這個類型的參數會被幣安以 -1106 拒絕。
            // Sending one parameter that does not belong to the type earns a -1106.
            Assert.IsNull(request.Parameter("stopPrice"));
            Assert.IsNull(request.Parameter("callbackRate"));
            Assert.IsNull(request.Parameter("workingType"));
            Assert.IsNull(request.Parameter("reduceOnly"));
            Assert.IsNull(request.Parameter("closePosition"));
        }
    }

    [TestMethod]
    public async Task AMarketOrderCarriesNeitherPriceNorTimeInForce()
    {
        // 市價單帶 timeInForce 會被回 -1114「TimeInForce 不需要」。
        // A market order carrying timeInForce earns a -1114, "time in force not required".
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.Market,
                Quantity = 0.002m,
            });

            var request = stub.LastRequest;

            Assert.AreEqual("MARKET", request.Parameter("type"));
            Assert.AreEqual("SELL", request.Parameter("side"));
            Assert.AreEqual(0.002m, Decimal(request.Parameter("quantity")));
            Assert.IsNull(request.Parameter("price"));
            Assert.IsNull(request.Parameter("timeInForce"));
            Assert.IsNull(request.Parameter("stopPrice"));
            Assert.IsNull(request.Parameter("workingType"));
        }
    }

    [TestMethod]
    public async Task AStopMarketOrderCarriesTheStopPriceAndTheWorkingTypeButNoPrice()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.StopMarket,
                Quantity = 0.002m,
                StopPrice = 55000m,
            });

            var request = stub.LastRequest;

            Assert.AreEqual("STOP_MARKET", request.Parameter("type"));
            Assert.AreEqual(55000m, Decimal(request.Parameter("stopPrice")));
            Assert.AreEqual("MARK_PRICE", request.Parameter("workingType"));
            Assert.IsNull(request.Parameter("price"));
            Assert.IsNull(request.Parameter("timeInForce"));
            Assert.IsNull(request.Parameter("callbackRate"));
        }
    }

    [TestMethod]
    public async Task AStopLimitOrderCarriesPriceStopPriceAndTimeInForce()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.StopLimit,
                Quantity = 0.002m,
                Price = 54900m,
                StopPrice = 55000m,
                TimeInForce = TimeInForce.GoodTilCrossing,
            });

            var request = stub.LastRequest;

            // 幣安的停損限價單叫 STOP,不是 STOP_LIMIT。
            // Binance calls a stop-limit order STOP rather than STOP_LIMIT.
            Assert.AreEqual("STOP", request.Parameter("type"));
            Assert.AreEqual(54900m, Decimal(request.Parameter("price")));
            Assert.AreEqual(55000m, Decimal(request.Parameter("stopPrice")));
            Assert.AreEqual("GTX", request.Parameter("timeInForce"));
        }
    }

    [TestMethod]
    public async Task ATakeProfitMarketOrderMapsToTakeProfitMarket()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.TakeProfitMarket,
                Quantity = 0.002m,
                StopPrice = 70000m,
                TriggerPriceType = TriggerPriceType.LastPrice,
            });

            var request = stub.LastRequest;

            Assert.AreEqual("TAKE_PROFIT_MARKET", request.Parameter("type"));
            Assert.AreEqual(70000m, Decimal(request.Parameter("stopPrice")));

            // 幣安把「最新成交價」叫 CONTRACT_PRICE,不是 LAST_PRICE。
            // Binance calls the last traded price CONTRACT_PRICE, not LAST_PRICE.
            Assert.AreEqual("CONTRACT_PRICE", request.Parameter("workingType"));
        }
    }

    [TestMethod]
    public async Task ATakeProfitLimitOrderMapsToTakeProfit()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.TakeProfitLimit,
                Quantity = 0.002m,
                Price = 70100m,
                StopPrice = 70000m,
            });

            Assert.AreEqual("TAKE_PROFIT", stub.LastRequest.Parameter("type"));
            Assert.AreEqual("GTC", stub.LastRequest.Parameter("timeInForce"));
        }
    }

    [TestMethod]
    public async Task ATrailingStopCarriesTheCallbackRateAndNoPrices()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.TrailingStopMarket,
                Quantity = 0.002m,
                CallbackRate = 1.5m,
            });

            var request = stub.LastRequest;

            Assert.AreEqual("TRAILING_STOP_MARKET", request.Parameter("type"));
            Assert.AreEqual(1.5m, Decimal(request.Parameter("callbackRate")));
            Assert.AreEqual("MARK_PRICE", request.Parameter("workingType"));
            Assert.IsNull(request.Parameter("price"));
            Assert.IsNull(request.Parameter("stopPrice"));
            Assert.IsNull(request.Parameter("timeInForce"));
        }
    }

    [TestMethod]
    public async Task AReduceOnlyFlagIsSentOnlyWhenItIsTrue()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy() with { ReduceOnly = true });

            Assert.AreEqual("true", stub.LastRequest.Parameter("reduceOnly"));
            Assert.IsNull(stub.LastRequest.Parameter("closePosition"));
        }
    }

    [TestMethod]
    public async Task AClosePositionOrderSendsNoQuantity()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.StopMarket,
                StopPrice = 55000m,
                ClosePosition = true,
            });

            Assert.AreEqual("true", stub.LastRequest.Parameter("closePosition"));
            Assert.IsNull(stub.LastRequest.Parameter("quantity"));
        }
    }

    [TestMethod]
    public async Task TheParameterOrderIsTheOrderThatGetsSigned()
    {
        // 幣安簽的就是實際送出的查詢字串,順序換掉簽章就不同。
        // Binance signs the query string it receives, so a different order is a different signature.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy());

            var names = stub.LastRequest.ParameterNames;

            CollectionAssert.AreEqual(ExpectedLimitOrderParameterOrder, names.ToArray());
        }
    }

    // ── 幣安比抽象層更嚴的規則 / Where Binance is stricter ────────────────────

    [TestMethod]
    public async Task ClosePositionIsRefusedOnOrderTypesBinanceDoesNotAllowItOn()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.TrailingStopMarket,
                CallbackRate = 1m,
                ClosePosition = true,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
            StringAssert.Contains(result.Error.Message, "closePosition", StringComparison.Ordinal);
            Assert.IsFalse(stub.Requests.Any(request => request.Method == HttpMethod.Post));
        }
    }

    [TestMethod]
    public async Task ReduceOnlyIsRefusedInHedgeModeBecauseBinanceRejectsIt()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy() with
            {
                PositionSide = PositionSide.Long,
                ReduceOnly = true,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
            StringAssert.Contains(result.Error.Message, "reduceOnly", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow(0.05)]
    [DataRow(50.0)]
    public async Task ACallbackRateOutsideTheBinanceRangeIsRefusedEvenThoughTheAbstractionAllowsIt(double rate)
    {
        // 抽象層允許 0 到 100(所有交易所的聯集),幣安的實際上限是 10。
        // The abstraction allows 0 to 100, the union across exchanges; the real Binance ceiling is 10.
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.TrailingStopMarket,
                Quantity = 0.002m,
                CallbackRate = (decimal)rate,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task AnUndefinedPositionSideIsRefusedRatherThanFormattedIntoTheRequest()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy() with { PositionSide = (PositionSide)99 });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task AnUndefinedTriggerPriceTypeIsRefused()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.StopMarket,
                Quantity = 0.002m,
                StopPrice = 55000m,
                TriggerPriceType = (TriggerPriceType)42,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task AnInvalidFieldCombinationIsRefusedWithoutEvenFetchingTheTradingRules()
    {
        // 欄位組合不合法的請求不該為了取得規則而多打一次網路。
        // A request with an invalid field combination should not cost a network call for rules it never uses.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Buy,
                OrderType = OrderType.Market,
                Quantity = 0.002m,
                Price = 60000m,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    // ── 價量校正 / Normalisation ─────────────────────────────────────────────

    [TestMethod]
    public async Task TheQuantityIsAlignedDownwardsNeverUpwards()
    {
        // 向上對齊會讓實際部位大於風控算出來的規模,那是風控破口而不是四捨五入問題。
        // Rounding up makes the real position larger than the size risk control calculated, which is a hole in
        // risk control rather than a rounding preference.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.PlaceOrderAsync(LimitBuy(quantity: 0.0119m));

            Assert.AreEqual(0.011m, Decimal(stub.LastRequest.Parameter("quantity")));
        }
    }

    [TestMethod]
    public async Task ThePriceIsAlignedToTheTickSize()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            // BTCUSDT 的 tickSize 是 0.10。
            // The tick size of BTCUSDT is 0.10.
            _ = await client.PlaceOrderAsync(LimitBuy(price: 60000.04m));

            Assert.AreEqual(60000.00m, Decimal(stub.LastRequest.Parameter("price")));
        }
    }

    [TestMethod]
    public async Task AQuantityBelowTheMinimumFailsLocallyInsteadOfBeingRejectedRemotely()
    {
        // 省的不只是一趟往返,還有一份限流額度。
        // That saves a round trip and a unit of rate-limit quota.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy(quantity: 0.0005m));

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.QuantityBelowMinimum, result.Error!.Code);
            Assert.IsFalse(stub.Requests.Any(request => request.Method == HttpMethod.Post));
        }
    }

    [TestMethod]
    public async Task ANotionalBelowTheMinimumFailsLocallyToo()
    {
        // BTCUSDT 的 MIN_NOTIONAL 是 50 USDT;0.001 顆 × 10000 只有 10。
        // The MIN_NOTIONAL of BTCUSDT is 50 USDT, and 0.001 at 10000 comes to 10.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy(price: 10000m, quantity: 0.001m));

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.NotionalBelowMinimum, result.Error!.Code);
            Assert.IsFalse(stub.Requests.Any(request => request.Method == HttpMethod.Post));
        }
    }

    [TestMethod]
    public async Task AnUnknownSymbolFailsBeforeAnythingIsSent()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy() with { Symbol = "NOTASYMBOL" });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.SymbolNotFound, result.Error!.Code);
            Assert.IsFalse(stub.Requests.Any(request => request.Method == HttpMethod.Post));
        }
    }

    [TestMethod]
    public async Task ASymbolThatIsNotTradingIsRefused()
    {
        // fixture 裡的 OMGUSDT 狀態是 SETTLING。
        // OMGUSDT is SETTLING in the fixture.
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "OMGUSDT",
                Side = OrderSide.Buy,
                OrderType = OrderType.Limit,
                Quantity = 10m,
                Price = 1m,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.SymbolNotTradable, result.Error!.Code);
        }
    }

    // ── 回應處理 / Response handling ─────────────────────────────────────────

    [TestMethod]
    public async Task APlacedOrderComesBackFullyMapped()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(LimitBuy());

            Assert.IsTrue(result.TryGetValue(out var order), result.Error?.Message);
            Assert.AreEqual("28580357696", order.ExchangeOrderId);
            Assert.AreEqual(OrderStatus.New, order.Status);
            Assert.AreEqual(OrderType.Limit, order.OrderType);
            Assert.IsTrue(order.IsOpen);
        }
    }

    [TestMethod]
    public async Task AConditionalOrderRejectedByTheEndpointSurfacesAsNotSupported()
    {
        // 2026-09-11 Testnet 實測:條件單送到 /fapi/v1/order 會得到 -4120。
        // 對映成 NotSupported 而不是參數錯誤 —— 參數再怎麼改都不會讓這個端點接受它。
        // Measured on Testnet on 2026-09-11: a conditional order sent to /fapi/v1/order earns a -4120. It maps
        // to NotSupported rather than to an argument error, because no edit to the arguments will help.
        // exchangeInfo 要成功(才有交易規則可校正),委託那一支要回 400。
        // The exchangeInfo call has to succeed so the rules exist to normalise against; the order call answers 400.
        var stub = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            return path.Contains("exchangeInfo", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Fixtures.ExchangeInfoMainnet, System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(Fixtures.OrderTypeNotSupported, System.Text.Encoding.UTF8, "application/json"),
                };
        });

        var (client, _, http) = Create(stub);

        using (client)
        using (http)
        {
            var result = await client.PlaceOrderAsync(new OrderRequest
            {
                Symbol = "BTCUSDT",
                Side = OrderSide.Sell,
                OrderType = OrderType.StopMarket,
                Quantity = 0.002m,
                StopPrice = 55000m,
            });

            Assert.IsTrue(result.IsFailure);
            // -4120 對映到條件單專屬的代碼,而不是籠統的「不支援」:收到它的人需要的是
            // 「改呼叫哪一個方法」,不是再一句「不被接受」。
            // -4120 maps to the conditional-specific code rather than a generic "not supported": whoever
            // reads it needs the method to call instead, not another way of saying "not accepted".
            Assert.AreEqual(TradeErrorCodes.ConditionalOrderPathRequired, result.Error!.Code);
            Assert.IsFalse(result.Error.IsTransient);
            StringAssert.Contains(result.Error.Message, "PlaceConditionalOrderAsync", StringComparison.Ordinal);

            // 就算失敗,編號也要回得來 —— 那張單有沒有進去只有它查得出來。
            // The id comes back even on failure: it is the only way to find out whether the order landed.
            Assert.IsTrue(result.Error.TryGetData(BinanceErrorDataKeys.ClientOrderId, out _));
        }
    }

    // ── 撤單與查單 / Cancellation and lookup ─────────────────────────────────

    [TestMethod]
    public async Task CancellingUsesDeleteAndIsRetryable()
    {
        var (client, stub, http) = Create(TradingStub(Fixtures.OrderCanceled));

        using (client)
        using (http)
        {
            var result = await client.CancelOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("28580357696"));

            Assert.IsTrue(result.TryGetValue(out var order), result.Error?.Message);
            Assert.AreEqual(OrderStatus.Canceled, order.Status);

            var request = stub.LastRequest;

            Assert.AreEqual(HttpMethod.Delete, request.Method);
            Assert.AreEqual(RequestIdempotency.Idempotent, request.Idempotency);
            Assert.AreEqual("28580357696", request.Parameter("orderId"));
            Assert.IsNull(request.Parameter("origClientOrderId"));
        }
    }

    [TestMethod]
    public async Task CancellingByClientOrderIdUsesOrigClientOrderId()
    {
        var (client, stub, http) = Create(TradingStub(Fixtures.OrderCanceled));

        using (client)
        using (http)
        {
            _ = await client.CancelOrderAsync("BTCUSDT", OrderIdentifier.FromClientId("ozk-1-abcdef01"));

            Assert.AreEqual("ozk-1-abcdef01", stub.LastRequest.Parameter("origClientOrderId"));
            Assert.IsNull(stub.LastRequest.Parameter("orderId"));
        }
    }

    [TestMethod]
    public async Task QueryingUsesGetAndTheQueryWeight()
    {
        var (client, stub, http) = Create(TradingStub(Fixtures.OrderQuery));

        using (client)
        using (http)
        {
            var result = await client.GetOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("28580357696"));

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.AreEqual(HttpMethod.Get, stub.LastRequest.Method);
            Assert.AreEqual(BinanceRequestWeights.QueryOrder, stub.LastRequest.Weight);
        }
    }

    [TestMethod]
    public async Task AnEmptyIdentifierIsRefusedWithoutCallingTheExchange()
    {
        // 兩個編號都不給,幣安會回 -1102;在本地擋下來省一趟往返。
        // Binance answers -1102 when neither id arrives; stopping locally saves the round trip.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            foreach (var result in new[]
            {
                (await client.GetOrderAsync("BTCUSDT", default)).ToResult(),
                (await client.CancelOrderAsync("BTCUSDT", default)).ToResult(),
            })
            {
                Assert.IsTrue(result.IsFailure);
                Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            }

            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task ABlankSymbolIsRefusedOnEveryTradingCall()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            foreach (var result in new[]
            {
                (await client.GetOrderAsync("  ", OrderIdentifier.FromExchangeId("1"))).ToResult(),
                (await client.CancelOrderAsync("  ", OrderIdentifier.FromExchangeId("1"))).ToResult(),
                await client.CancelAllOrdersAsync("  "),
                await client.SetLeverageAsync("  ", 10),
                await client.SetMarginModeAsync("  ", MarginMode.Cross),
            })
            {
                Assert.IsTrue(result.IsFailure);
                Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            }

            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task AMissingOrderIsReportedAsOrderNotFound()
    {
        var (client, _, http) = Create(StubHttpMessageHandler.Json(
            Fixtures.OrderNotFound,
            HttpStatusCode.BadRequest));

        using (client)
        using (http)
        {
            var result = await client.GetOrderAsync("BTCUSDT", OrderIdentifier.FromClientId("never-existed"));

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.OrderNotFound, result.Error!.Code);
            Assert.IsFalse(result.Error.IsTransient);
        }
    }

    // ── 撤銷全部掛單 / Cancel all ────────────────────────────────────────────

    [TestMethod]
    public async Task CancellingEverythingOnASymbolSucceedsOnTheRecordedReply()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.CancelAllOrdersAsync("BTCUSDT");

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.AreEqual(HttpMethod.Delete, stub.LastRequest.Method);
            Assert.AreEqual("BTCUSDT", stub.LastRequest.Parameter("symbol"));
            Assert.AreEqual(RequestIdempotency.Idempotent, stub.LastRequest.Idempotency);
        }
    }

    [TestMethod]
    public async Task ACancelAllThatReportsAnErrorCodeInTheBodyIsNotTreatedAsSuccess()
    {
        // 本文帶 code 200 是成功,帶別的就不是 —— 把失敗的撤單當成功,接下來就是帶著殘留掛單去平倉。
        // A body code of 200 is success and anything else is not: mistaking a failed cancellation for a
        // successful one means closing a position with orders still resting.
        var (client, _, http) = Create(StubHttpMessageHandler.ByPath(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/fapi/v1/allOpenOrders"] = """{"code":-2012,"msg":"Cancel all failed."}""",
            }));

        using (client)
        using (http)
        {
            var result = await client.CancelAllOrdersAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.OrderNotCancelable, result.Error!.Code);
        }
    }

    // ── 未結委託 / Open orders ───────────────────────────────────────────────

    [TestMethod]
    public async Task ListingOpenOrdersForOneSymbolCostsAWeightOfOne()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.GetOpenOrdersAsync("BTCUSDT");

            Assert.IsTrue(result.TryGetValue(out var orders), result.Error?.Message);
            Assert.HasCount(1, orders);
            Assert.AreEqual(1, stub.LastRequest.Weight);
            Assert.AreEqual("BTCUSDT", stub.LastRequest.Parameter("symbol"));
        }
    }

    [TestMethod]
    public async Task ListingOpenOrdersForEverySymbolCostsAWeightOfForty()
    {
        // 不帶商品代碼的權重是帶的四十倍。輪詢時務必指定商品。
        // Omitting the symbol costs forty times as much; always pass one when polling.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.GetOpenOrdersAsync();

            Assert.AreEqual(40, stub.LastRequest.Weight);
            Assert.IsNull(stub.LastRequest.Parameter("symbol"));
        }
    }

    [TestMethod]
    public async Task ABlankButNotNullSymbolIsReportedRatherThanWidenedToEverySymbol()
    {
        // 靜默改打全商品會把一個「變數沒填到」的 bug 變成四十倍的權重支出。
        // Silently widening it turns an unfilled variable into forty times the weight.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.GetOpenOrdersAsync("   ");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    // ── 帳戶設定 / Account settings ──────────────────────────────────────────

    [TestMethod]
    public async Task SettingLeverageUsesPostAndIsRetryable()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.SetLeverageAsync("BTCUSDT", 20);

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.AreEqual(HttpMethod.Post, stub.LastRequest.Method);
            Assert.AreEqual(RequestIdempotency.Idempotent, stub.LastRequest.Idempotency);
            Assert.AreEqual("20", stub.LastRequest.Parameter("leverage"));
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public async Task ALeverageBelowOneIsRefusedLocally(int leverage)
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.SetLeverageAsync("BTCUSDT", leverage);

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.LeverageNotAllowed, result.Error!.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task SettingTheMarginModeSendsCrossedNotCross()
    {
        // 幣安寫的是 CROSSED。持倉查詢回來的 marginType 卻是小寫的 cross ——
        // 讀與寫用的字面值不同,這是必須逐字照抄的地方。
        // Binance writes CROSSED here while the position query returns a lower-case cross: the literal differs
        // between reading and writing.
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.SetMarginModeAsync("BTCUSDT", MarginMode.Cross);

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.AreEqual("CROSSED", stub.LastRequest.Parameter("marginType"));
        }
    }

    [TestMethod]
    public async Task SettingTheMarginModeToIsolatedSendsIsolated()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            _ = await client.SetMarginModeAsync("BTCUSDT", MarginMode.Isolated);

            Assert.AreEqual("ISOLATED", stub.LastRequest.Parameter("marginType"));
        }
    }

    [TestMethod]
    public async Task AlreadyBeingInTheRequestedMarginModeCountsAsSuccess()
    {
        // 幣安對「模式沒有變」回的是 -4046 錯誤。不吃掉的話,每次啟動都會冒出一串假的錯誤告警。
        // Binance answers "no change needed" with the error -4046; not swallowing it means a row of false
        // alerts on every start-up.
        var (client, _, http) = Create(StubHttpMessageHandler.Json(
            Fixtures.MarginTypeNoChange,
            HttpStatusCode.BadRequest));

        using (client)
        using (http)
        {
            var result = await client.SetMarginModeAsync("BTCUSDT", MarginMode.Cross);

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        }
    }

    [TestMethod]
    public async Task AMarginModeChangeBlockedByAnOpenPositionStillFails()
    {
        // -4048 不是 -4046:有持倉擋著代表這次設定真的沒生效,不能當成成功。
        // -4048 is not -4046: an open position blocking the change means the setting did not take effect.
        var (client, _, http) = Create(StubHttpMessageHandler.Json(
            """{"code":-4048,"msg":"Cannot change margin type if you have open positions."}""",
            HttpStatusCode.BadRequest));

        using (client)
        using (http)
        {
            var result = await client.SetMarginModeAsync("BTCUSDT", MarginMode.Isolated);

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.MarginModeRejected, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task AnUndefinedMarginModeIsRefused()
    {
        var (client, stub, http) = Create();

        using (client)
        using (http)
        {
            var result = await client.SetMarginModeAsync("BTCUSDT", (MarginMode)7);

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    // ── 防呆 / Guard rails ───────────────────────────────────────────────────

    [TestMethod]
    public async Task EveryTradingCallIsRefusedWhenNoCredentialsAreConfigured()
    {
        var (client, _, http) = Create(withCredentials: false);

        using (client)
        using (http)
        {
            foreach (var result in new[]
            {
                (await client.CancelOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("1"))).ToResult(),
                (await client.GetOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("1"))).ToResult(),
                (await client.GetOpenOrdersAsync("BTCUSDT")).ToResult(),
                await client.CancelAllOrdersAsync("BTCUSDT"),
                await client.SetLeverageAsync("BTCUSDT", 10),
                await client.SetMarginModeAsync("BTCUSDT", MarginMode.Cross),
            })
            {
                Assert.IsTrue(result.IsFailure);
                Assert.AreEqual(BinanceErrorCodes.CredentialsMissing, result.Error!.Code);
                Assert.IsFalse(result.Error.IsTransient);
            }
        }
    }

    [TestMethod]
    public async Task PlacingAnOrderRejectsANullRequest()
    {
        var (client, _, http) = Create();

        using (client)
        using (http)
        {
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => client.PlaceOrderAsync(null!));
        }
    }

    [TestMethod]
    public async Task EveryTradingCallThrowsOnADisposedClient()
    {
        var (client, _, http) = Create();

        using (http)
        {
            client.Dispose();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.PlaceOrderAsync(LimitBuy()));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => client.CancelOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("1")));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => client.GetOrderAsync("BTCUSDT", OrderIdentifier.FromExchangeId("1")));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetOpenOrdersAsync("BTCUSDT"));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.CancelAllOrdersAsync("BTCUSDT"));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.SetLeverageAsync("BTCUSDT", 10));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => client.SetMarginModeAsync("BTCUSDT", MarginMode.Cross));
        }
    }

    [TestMethod]
    public void TheClientSatisfiesTheWholeExchangeInterface()
    {
        // 介面宣告得出來就要做得到。留一個擲「未實作」的成員,呼叫端會在編譯期看到它、執行期才撞牆。
        // Whatever the interface declares has to work. A member that throws "not implemented" binds at compile
        // time and fails at run time, which is far later and far more expensive.
        Assert.IsTrue(typeof(IExchangeClient).IsAssignableFrom(typeof(BinanceFuturesClient)));
    }
}
