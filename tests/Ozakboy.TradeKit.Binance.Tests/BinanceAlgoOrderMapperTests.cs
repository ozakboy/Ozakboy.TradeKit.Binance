namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 條件單對映表的測試。<see cref="BinanceConditionalOrderTradingTests"/> 驗的是「送出去的請求長什麼樣」,
/// 這裡驗的是那張表的每一格,包含只在回應解析時才走到的反向對映。
/// Tests for the conditional order mapping table. <see cref="BinanceConditionalOrderTradingTests"/> asserts
/// what an outgoing request looks like; this file asserts every cell of the table, including the reverse
/// direction that only response parsing exercises.
/// </summary>
[TestClass]
public sealed class BinanceAlgoOrderMapperTests
{
    private static ConditionalOrderRequest StopMarket() => new()
    {
        Symbol = "BTCUSDT",
        Side = OrderSide.Sell,
        ConditionalOrderType = ConditionalOrderType.StopMarket,
        Quantity = 0.002m,
        TriggerPrice = 37_000m,
        ReduceOnly = true,
    };

    // ── 中立模型 → 幣安字面值 / Neutral model to Binance literals ─────────────

    [TestMethod]
    [DataRow(ConditionalOrderType.StopMarket, "STOP_MARKET")]
    [DataRow(ConditionalOrderType.StopLimit, "STOP")]
    [DataRow(ConditionalOrderType.TakeProfitMarket, "TAKE_PROFIT_MARKET")]
    [DataRow(ConditionalOrderType.TakeProfitLimit, "TAKE_PROFIT")]
    [DataRow(ConditionalOrderType.TrailingStopMarket, "TRAILING_STOP_MARKET")]
    public void MapsEveryConditionalOrderType(ConditionalOrderType type, string expected)
    {
        // 字面值與一般委託那一張表相同:Algo Service 換的是端點,不是 type 字串。
        // The literals match the ordinary order table: the Algo Service changed the endpoint, not the type
        // strings.
        Assert.AreEqual(expected, BinanceAlgoOrderMapper.ToBinance(type));
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(type.ToOrderType()));
    }

    [TestMethod]
    public void AnUndefinedConditionalOrderTypeThrowsRatherThanGuessing()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BinanceAlgoOrderMapper.ToBinance(ConditionalOrderType.Unspecified));
    }

    // ── 幣安字面值 → 中立模型 / Binance literals to the neutral model ─────────

    [TestMethod]
    [DataRow("STOP_MARKET", ConditionalOrderType.StopMarket)]
    [DataRow("STOP", ConditionalOrderType.StopLimit)]
    [DataRow("TAKE_PROFIT_MARKET", ConditionalOrderType.TakeProfitMarket)]
    [DataRow("TAKE_PROFIT", ConditionalOrderType.TakeProfitLimit)]
    [DataRow("TRAILING_STOP_MARKET", ConditionalOrderType.TrailingStopMarket)]
    [DataRow("LIMIT", ConditionalOrderType.Unspecified)]
    [DataRow("NOT_A_REAL_TYPE", ConditionalOrderType.Unspecified)]
    [DataRow(null, ConditionalOrderType.Unspecified)]
    public void ParsesEveryConditionalOrderType(string? value, ConditionalOrderType expected)
    {
        Assert.AreEqual(expected, BinanceAlgoOrderMapper.ParseConditionalOrderType(value));
    }

    [TestMethod]
    [DataRow("NEW", ConditionalOrderStatus.New)]
    [DataRow("TRIGGERING", ConditionalOrderStatus.Triggering)]
    [DataRow("TRIGGERED", ConditionalOrderStatus.Triggered)]
    [DataRow("FINISHED", ConditionalOrderStatus.Finished)]
    [DataRow("CANCELED", ConditionalOrderStatus.Canceled)]
    [DataRow("EXPIRED", ConditionalOrderStatus.Expired)]
    [DataRow("REJECTED", ConditionalOrderStatus.Rejected)]
    public void ParsesEveryAlgoStatus(string value, ConditionalOrderStatus expected)
    {
        // 七個值取自官方 user-data-streams 頁面,擷取日期 2026-09-14。
        // The seven values come from the official user-data-streams page, retrieved 2026-09-14.
        Assert.AreEqual(expected, BinanceAlgoOrderMapper.ParseAlgoStatus(value));
    }

    [TestMethod]
    public void FinishedIsNotReadAsFilled()
    {
        // 幣安的 FINISHED 原文是「filled or canceled in the matching engine」—— 它涵蓋兩種結局。
        // 讀成成交會讓一張觸發後被撤掉的停損在帳上變成一次不存在的平倉。
        // The official wording of FINISHED is "filled or canceled in the matching engine", covering both
        // outcomes. Reading it as a fill turns a stop cancelled after triggering into a close that never
        // happened.
        Assert.AreEqual(ConditionalOrderStatus.Finished, BinanceAlgoOrderMapper.ParseAlgoStatus("FINISHED"));
        Assert.AreNotEqual(ConditionalOrderStatus.Filled, BinanceAlgoOrderMapper.ParseAlgoStatus("FINISHED"));
    }

    [TestMethod]
    [DataRow("CANCELLED")]
    [DataRow("WORKING")]
    [DataRow("FILLED")]
    [DataRow("NOT_A_REAL_STATUS")]
    [DataRow(null)]
    public void AnUnknownAlgoStatusIsReportedAsUnspecified(string? value)
    {
        // CANCELLED(兩個 L)、WORKING 與 FILLED 都不是條件單的狀態 —— 前者是拼錯,後兩者屬於
        // GRID_UPDATE 與 STRATEGY_UPDATE。憑印象多收一個分支,會讓「對映漏了」變成看起來正常的狀態。
        // CANCELLED with two Ls is a misspelling, and WORKING and FILLED belong to GRID_UPDATE and
        // STRATEGY_UPDATE. Accepting one of them from memory turns "the mapping missed something" into a
        // state that looks perfectly normal.
        Assert.AreEqual(ConditionalOrderStatus.Unspecified, BinanceAlgoOrderMapper.ParseAlgoStatus(value));
    }

    // ── 送出的參數 / The parameters that go out ──────────────────────────────

    [TestMethod]
    public void EveryConditionalOrderCarriesTheConditionalAlgoType()
    {
        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(StopMarket(), "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        // algoType 是必填。少了它,Algo Service 根本不知道要建哪一種單。
        // algoType is mandatory: without it the Algo Service does not know which kind of order to create.
        Assert.Contains("algoType=CONDITIONAL", query);
    }

    [TestMethod]
    public void TheTrailingStopUsesActivatePriceRatherThanActivationPrice()
    {
        var request = new ConditionalOrderRequest
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Sell,
            ConditionalOrderType = ConditionalOrderType.TrailingStopMarket,
            Quantity = 0.002m,
            CallbackRate = 1.5m,
            ActivationPrice = 80_000m,
            ReduceOnly = true,
        };

        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(request, "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        // 幣安的參數名是動詞 activatePrice,抽象層的屬性是名詞 ActivationPrice。
        // 拼成 activationPrice 會換來 -1104,而錯誤訊息不會說是哪一個參數多了。
        // The Binance parameter is the verb activatePrice while the neutral property is the noun
        // ActivationPrice. Spelling it activationPrice earns a -1104 whose message never names the parameter.
        Assert.Contains("activatePrice=80000", query);
        Assert.DoesNotContain("activationPrice", query);
        Assert.Contains("callbackRate=1.5", query);
    }

    [TestMethod]
    public void TheTriggerPriceIsSentAsTriggerPriceNotStopPrice()
    {
        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(StopMarket(), "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        // Algo 端點用 triggerPrice,不是舊端點的 stopPrice。照舊名送出去的是一個不被讀取的參數。
        // The algo endpoint uses triggerPrice rather than the old endpoint's stopPrice. Sending the old name
        // sends a parameter nothing reads.
        Assert.Contains("triggerPrice=37000", query);
        Assert.DoesNotContain("stopPrice", query);
    }

    [TestMethod]
    public void TheWorkingTypeIsAlwaysSentBecauseTheExchangeDefaultDiffers()
    {
        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(StopMarket(), "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        // 幣安的預設是 CONTRACT_PRICE,本套件的預設是 MARK_PRICE。不送這個參數,
        // 一張以為看標記價的停損實際上會看成交價,然後被一根影線掃掉。
        // The Binance default is CONTRACT_PRICE while this package defaults to MARK_PRICE. Omitting it leaves
        // a stop believed to watch the mark watching traded prices instead, to be taken out by a single wick.
        Assert.Contains("workingType=MARK_PRICE", builder.Build().ToString());
    }

    [TestMethod]
    public void CloseAllSendsNoQuantity()
    {
        var request = StopMarket() with { ReduceOnly = false, ClosePosition = true, Quantity = 0m };

        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(request, "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        Assert.Contains("closePosition=true", query);
        Assert.DoesNotContain("quantity=", query);
        Assert.DoesNotContain("reduceOnly", query);
    }

    [TestMethod]
    public void FlagsThatAreFalseAreOmittedRatherThanSentAsFalse()
    {
        var request = StopMarket() with { ReduceOnly = false };

        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(request, "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        // 送出 false 與不送語意相同,少送一個參數就少一種被拒的方式。
        // Sending false means the same as omitting it, and omitting removes one way to be rejected.
        Assert.DoesNotContain("reduceOnly", query);
        Assert.DoesNotContain("closePosition", query);
    }

    [TestMethod]
    public void TheClientAlgoIdIsTheLastBusinessParameter()
    {
        var built = BinanceAlgoOrderMapper.BuildPlaceAlgoOrder(StopMarket(), "pt-algo-1");

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        Assert.Contains("clientAlgoId=pt-algo-1", query);
        Assert.DoesNotContain("newClientOrderId", query);
    }

    // ── 幣安比抽象層更嚴的那幾條 / Where Binance is stricter than the abstraction ──

    [TestMethod]
    public void AClientAlgoIdThatBreaksTheBinanceFormatIsRejectedLocally()
    {
        // clientAlgoId 的規則與 newClientOrderId 相同:^[\.A-Z\:/a-z0-9_-]{1,36}$。
        // 大括號與加號都不在字元集裡,GUID 的 "B" 格式會直接被拒。
        // clientAlgoId follows the same rule as newClientOrderId. Braces and plus signs are outside the
        // character set, so a GUID in "B" format is refused outright.
        var result = BinanceAlgoOrderMapper.ValidateForBinance(StopMarket(), "{not-allowed}");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    [TestMethod]
    public void TheCallbackRateCeilingIsTenRatherThanOneHundred()
    {
        var request = new ConditionalOrderRequest
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Sell,
            ConditionalOrderType = ConditionalOrderType.TrailingStopMarket,
            Quantity = 0.002m,
            CallbackRate = 50m,
            ReduceOnly = true,
        };

        // 抽象層只要求 0 到 100(所有交易所的聯集),幣安的實際上限是 10。
        // 這一檔差距若不在本地擋下,就是送出去換一次 -1102 與一份限流額度。
        // The abstraction requires only 0 to 100, the union across exchanges, while the real Binance ceiling
        // is 10. Not catching the gap locally costs a round trip, a -1102, and quota.
        Assert.IsTrue(request.Validate().IsSuccess, "抽象層應該放行,這一條是幣安專屬的限制。");

        var result = BinanceAlgoOrderMapper.ValidateForBinance(request, "pt-algo-1");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    [TestMethod]
    public void HedgeModeRefusesReduceOnly()
    {
        var request = StopMarket() with { PositionSide = PositionSide.Long, Side = OrderSide.Sell };

        var result = BinanceAlgoOrderMapper.ValidateForBinance(request, "pt-algo-1");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    [TestMethod]
    public void AnUndefinedEnumValueBecomesAFailureRatherThanAnException()
    {
        // (ConditionalOrderType)99 通過得了抽象層的驗證(那裡只擋 Unspecified),而送單方法的契約是
        // 回傳 Result。擲例外會讓呼叫端的 try/catch 與 Result 判斷各漏一半。
        // A cast such as (ConditionalOrderType)99 passes the abstraction's validation, which only rejects
        // Unspecified, and the contract here is to return a Result: throwing leaves callers catching in one
        // place and checking in another.
        var request = StopMarket() with { ConditionalOrderType = (ConditionalOrderType)99 };

        var result = BinanceAlgoOrderMapper.ValidateForBinance(request, "pt-algo-1");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    // ── 查單與撤單的識別參數 / The identifying parameters for lookup and cancel ──

    [TestMethod]
    public void TheLookupUsesAlgoIdWhenTheExchangeIdIsKnown()
    {
        var built = BinanceAlgoOrderMapper.BuildAlgoOrderLookup(
            "BTCUSDT",
            ConditionalOrderIdentifier.FromExchangeId("2148719"));

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        Assert.Contains("algoId=2148719", query);
        Assert.DoesNotContain("clientAlgoId", query);
        Assert.DoesNotContain("orderId=", query);
    }

    [TestMethod]
    public void TheLookupFallsBackToClientAlgoId()
    {
        var built = BinanceAlgoOrderMapper.BuildAlgoOrderLookup(
            "BTCUSDT",
            ConditionalOrderIdentifier.FromClientId("pt-algo-1"));

        Assert.IsTrue(built.TryGetValue(out var builder), built.Error?.Message);

        var query = builder.Build().ToString();

        Assert.Contains("clientAlgoId=pt-algo-1", query);
        Assert.DoesNotContain("algoId=", query);

        // 舊端點的 origClientOrderId 在這裡不適用,送過去不會被讀取。
        // The old endpoint's origClientOrderId does not apply here and would go unread.
        Assert.DoesNotContain("origClientOrderId", query);
    }

    [TestMethod]
    public void AnEmptyIdentifierIsStoppedBeforeTheRequestLeaves()
    {
        var built = BinanceAlgoOrderMapper.BuildAlgoOrderLookup("BTCUSDT", default);

        Assert.IsTrue(built.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, built.Error!.Code);
    }

    [TestMethod]
    public void ABlankSymbolIsStoppedBeforeTheRequestLeaves()
    {
        var built = BinanceAlgoOrderMapper.BuildAlgoOrderLookup(
            "   ",
            ConditionalOrderIdentifier.FromClientId("pt-algo-1"));

        Assert.IsTrue(built.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, built.Error!.Code);
    }
}
