namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 對映表本身的測試。<see cref="BinanceTradingTests"/> 驗的是「送出去的請求長什麼樣」,
/// 這裡驗的是那張表的每一格 —— 包含只在回應解析時才會走到的反向對映。
/// Tests for the mapping table itself. <see cref="BinanceTradingTests"/> asserts what an outgoing request
/// looks like; this file asserts every cell of the table, including the reverse direction that only response
/// parsing exercises.
/// </summary>
[TestClass]
public sealed class BinanceOrderMapperTests
{
    // ── 中立模型 → 幣安字面值 / Neutral model to Binance literals ─────────────

    [TestMethod]
    [DataRow(OrderSide.Buy, "BUY")]
    [DataRow(OrderSide.Sell, "SELL")]
    public void MapsEveryOrderSide(OrderSide side, string expected)
    {
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(side));
    }

    [TestMethod]
    [DataRow(PositionSide.Both, "BOTH")]
    [DataRow(PositionSide.Long, "LONG")]
    [DataRow(PositionSide.Short, "SHORT")]
    public void MapsEveryPositionSide(PositionSide positionSide, string expected)
    {
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(positionSide));
    }

    [TestMethod]
    [DataRow(OrderType.Limit, "LIMIT")]
    [DataRow(OrderType.Market, "MARKET")]
    [DataRow(OrderType.StopMarket, "STOP_MARKET")]
    [DataRow(OrderType.StopLimit, "STOP")]
    [DataRow(OrderType.TakeProfitMarket, "TAKE_PROFIT_MARKET")]
    [DataRow(OrderType.TakeProfitLimit, "TAKE_PROFIT")]
    [DataRow(OrderType.TrailingStopMarket, "TRAILING_STOP_MARKET")]
    public void MapsEveryOrderType(OrderType orderType, string expected)
    {
        // 停損限價單在幣安叫 STOP,停利限價單叫 TAKE_PROFIT —— 兩個都沒有 _LIMIT 後綴,
        // 而它們的市價版本才有 _MARKET。照直覺補上 _LIMIT 會換來 -1116。
        // A stop-limit is STOP and a take-profit-limit is TAKE_PROFIT: neither carries a _LIMIT suffix while
        // their market counterparts do carry _MARKET. Adding the intuitive suffix earns a -1116.
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(orderType));
    }

    [TestMethod]
    [DataRow(TimeInForce.GoodTilCanceled, "GTC")]
    [DataRow(TimeInForce.ImmediateOrCancel, "IOC")]
    [DataRow(TimeInForce.FillOrKill, "FOK")]
    [DataRow(TimeInForce.GoodTilCrossing, "GTX")]
    public void MapsEveryTimeInForce(TimeInForce timeInForce, string expected)
    {
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(timeInForce));
    }

    [TestMethod]
    [DataRow(TriggerPriceType.MarkPrice, "MARK_PRICE")]
    [DataRow(TriggerPriceType.LastPrice, "CONTRACT_PRICE")]
    public void MapsEveryTriggerPriceType(TriggerPriceType triggerPriceType, string expected)
    {
        // 幣安把「最新成交價」叫 CONTRACT_PRICE,不是 LAST_PRICE。字面照抄,不要照語意改寫。
        // Binance calls the last traded price CONTRACT_PRICE rather than LAST_PRICE; copy the literal.
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(triggerPriceType));
    }

    [TestMethod]
    [DataRow(MarginMode.Cross, "CROSSED")]
    [DataRow(MarginMode.Isolated, "ISOLATED")]
    public void MapsEveryMarginMode(MarginMode marginMode, string expected)
    {
        // 寫的時候是 CROSSED,讀回來的 marginType 卻是小寫 cross。兩個方向的字面值不同。
        // Writing uses CROSSED while the marginType read back is a lower-case cross: the literals differ by
        // direction.
        Assert.AreEqual(expected, BinanceOrderMapper.ToBinance(marginMode));
    }

    [TestMethod]
    public void AnUndefinedEnumValueThrowsRatherThanBeingFormattedIntoARequest()
    {
        // 對映表對不上時寧可擲例外,也不要把一個數字硬塞進查詢字串 —— 那會變成一張語意不明的真單。
        // 正常路徑走不到這裡:BuildPlaceOrder 會先把未定義的值擋成失敗。這是最後一道。
        // An unmapped value throws rather than formatting a number into the query string, which would become a
        // real order of unclear meaning. The normal path never reaches it, because BuildPlaceOrder rejects
        // undefined values first; this is the last line of defence.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceOrderMapper.ToBinance((OrderSide)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceOrderMapper.ToBinance((PositionSide)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceOrderMapper.ToBinance((OrderType)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceOrderMapper.ToBinance((TimeInForce)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceOrderMapper.ToBinance((TriggerPriceType)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceOrderMapper.ToBinance((MarginMode)99));
    }

    // ── 幣安字面值 → 中立模型 / Binance literals to the neutral model ──────────

    [TestMethod]
    [DataRow("LIMIT", OrderType.Limit)]
    [DataRow("MARKET", OrderType.Market)]
    [DataRow("STOP_MARKET", OrderType.StopMarket)]
    [DataRow("STOP", OrderType.StopLimit)]
    [DataRow("TAKE_PROFIT_MARKET", OrderType.TakeProfitMarket)]
    [DataRow("TAKE_PROFIT", OrderType.TakeProfitLimit)]
    [DataRow("TRAILING_STOP_MARKET", OrderType.TrailingStopMarket)]
    [DataRow("SOMETHING_ELSE", OrderType.Unspecified)]
    [DataRow(null, OrderType.Unspecified)]
    public void ParsesEveryOrderTypeAndFallsBackToUnspecified(string? value, OrderType expected)
    {
        Assert.AreEqual(expected, BinanceOrderMapper.ParseOrderType(value));
    }

    [TestMethod]
    [DataRow("BUY", OrderSide.Buy)]
    [DataRow("SELL", OrderSide.Sell)]
    [DataRow("SIDEWAYS", OrderSide.Unspecified)]
    [DataRow(null, OrderSide.Unspecified)]
    public void ParsesEveryOrderSide(string? value, OrderSide expected)
    {
        Assert.AreEqual(expected, BinanceOrderMapper.ParseOrderSide(value));
    }

    [TestMethod]
    [DataRow("NEW", OrderStatus.New)]
    [DataRow("PARTIALLY_FILLED", OrderStatus.PartiallyFilled)]
    [DataRow("FILLED", OrderStatus.Filled)]
    [DataRow("CANCELED", OrderStatus.Canceled)]
    [DataRow("REJECTED", OrderStatus.Rejected)]
    [DataRow("EXPIRED", OrderStatus.Expired)]
    [DataRow("EXPIRED_IN_MATCH", OrderStatus.Expired)]
    [DataRow("PENDING_CANCEL", OrderStatus.PendingCancel)]
    [DataRow("NEW_INSURANCE", OrderStatus.New)]
    [DataRow("NEW_ADL", OrderStatus.New)]
    [DataRow("WHAT_IS_THIS", OrderStatus.Unspecified)]
    [DataRow(null, OrderStatus.Unspecified)]
    public void ParsesEveryOrderStatus(string? value, OrderStatus expected)
    {
        // NEW_INSURANCE 與 NEW_ADL 來自強平與自動減倉,狀態上仍是「已接受、尚未成交」,
        // 因此算在簿上。當成終態會讓部位追蹤少算一筆還活著的委託。
        // NEW_INSURANCE and NEW_ADL come from liquidation and auto-deleveraging and are still "accepted,
        // unfilled", hence live. Treating them as terminal loses a working order in position tracking.
        Assert.AreEqual(expected, BinanceOrderMapper.ParseOrderStatus(value));
    }

    [TestMethod]
    [DataRow("NEW", true)]
    [DataRow("PARTIALLY_FILLED", true)]
    [DataRow("PENDING_CANCEL", true)]
    [DataRow("NEW_INSURANCE", true)]
    [DataRow("FILLED", false)]
    [DataRow("CANCELED", false)]
    public void TheParsedStatusAgreesWithWhetherTheOrderIsStillLive(string value, bool isOpen)
    {
        Assert.AreEqual(isOpen, BinanceOrderMapper.ParseOrderStatus(value).IsOpen());
    }

    [TestMethod]
    [DataRow("GTC", TimeInForce.GoodTilCanceled)]
    [DataRow("IOC", TimeInForce.ImmediateOrCancel)]
    [DataRow("FOK", TimeInForce.FillOrKill)]
    [DataRow("GTX", TimeInForce.GoodTilCrossing)]
    [DataRow("GTD", TimeInForce.Unspecified)]
    [DataRow(null, TimeInForce.Unspecified)]
    public void ParsesEveryTimeInForce(string? value, TimeInForce expected)
    {
        // GTD 有到期時間,本套件不送也不模型化。回報 Unspecified 而不是猜成 GTC:
        // 把一張會到期的單說成「掛到撤銷為止」,會讓上層以為它會一直在那裡。
        // GTD carries an expiry, which this package neither sends nor models. It reports Unspecified rather
        // than guessing GTC, because calling an expiring order "good til cancelled" tells the caller it will
        // stay on the book.
        Assert.AreEqual(expected, BinanceOrderMapper.ParseTimeInForce(value));
    }

    [TestMethod]
    [DataRow("LONG", PositionSide.Long)]
    [DataRow("SHORT", PositionSide.Short)]
    [DataRow("BOTH", PositionSide.Both)]
    [DataRow("", PositionSide.Both)]
    [DataRow(null, PositionSide.Both)]
    public void ParsesEveryPositionSideAndDefaultsToOneWayMode(string? value, PositionSide expected)
    {
        Assert.AreEqual(expected, BinanceOrderMapper.ParsePositionSide(value));
    }

    // ── 往返 / Round trips ───────────────────────────────────────────────────

    [TestMethod]
    [DataRow(OrderType.Limit)]
    [DataRow(OrderType.Market)]
    [DataRow(OrderType.StopMarket)]
    [DataRow(OrderType.StopLimit)]
    [DataRow(OrderType.TakeProfitMarket)]
    [DataRow(OrderType.TakeProfitLimit)]
    [DataRow(OrderType.TrailingStopMarket)]
    public void EveryOrderTypeSurvivesARoundTrip(OrderType orderType)
    {
        // 送出去的字面值要能原封不動地讀回同一個類型。這條擋的是「寫的時候對、讀的時候對映到別的」。
        // The literal sent must read back as the same type, which is what catches a table that is right in one
        // direction and wrong in the other.
        Assert.AreEqual(orderType, BinanceOrderMapper.ParseOrderType(BinanceOrderMapper.ToBinance(orderType)));
    }

    [TestMethod]
    [DataRow(OrderSide.Buy)]
    [DataRow(OrderSide.Sell)]
    public void EveryOrderSideSurvivesARoundTrip(OrderSide side)
    {
        Assert.AreEqual(side, BinanceOrderMapper.ParseOrderSide(BinanceOrderMapper.ToBinance(side)));
    }

    [TestMethod]
    [DataRow(TimeInForce.GoodTilCanceled)]
    [DataRow(TimeInForce.ImmediateOrCancel)]
    [DataRow(TimeInForce.FillOrKill)]
    [DataRow(TimeInForce.GoodTilCrossing)]
    public void EveryTimeInForceSurvivesARoundTrip(TimeInForce timeInForce)
    {
        Assert.AreEqual(
            timeInForce,
            BinanceOrderMapper.ParseTimeInForce(BinanceOrderMapper.ToBinance(timeInForce)));
    }

    [TestMethod]
    [DataRow(PositionSide.Both)]
    [DataRow(PositionSide.Long)]
    [DataRow(PositionSide.Short)]
    public void EveryPositionSideSurvivesARoundTrip(PositionSide positionSide)
    {
        Assert.AreEqual(
            positionSide,
            BinanceOrderMapper.ParsePositionSide(BinanceOrderMapper.ToBinance(positionSide)));
    }

    // ── 識別參數與帳戶設定 / Identifiers and account settings ─────────────────

    [TestMethod]
    public void AnOrderLookupPrefersTheExchangeIdWhenBothCouldBeUsed()
    {
        var result = BinanceOrderMapper.BuildOrderLookup("BTCUSDT", OrderIdentifier.FromExchangeId("123"));

        Assert.IsTrue(result.TryGetValue(out var builder));
        Assert.AreEqual(2, builder.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void AnOrderLookupRefusesABlankSymbol(string symbol)
    {
        var result = BinanceOrderMapper.BuildOrderLookup(symbol, OrderIdentifier.FromExchangeId("123"));

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
    }

    [TestMethod]
    public void AnOrderLookupRefusesAnEmptyIdentifier()
    {
        var result = BinanceOrderMapper.BuildOrderLookup("BTCUSDT", default);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
    }

    [TestMethod]
    public void TheLeverageAndMarginTypeParametersAreBuiltInOrder()
    {
        Assert.AreEqual(2, BinanceOrderMapper.BuildLeverage("BTCUSDT", 20).Count);
        Assert.AreEqual(2, BinanceOrderMapper.BuildMarginType("BTCUSDT", MarginMode.Isolated).Count);
    }

    // ── 幣安專屬的驗證 / The Binance-only validation ──────────────────────────

    [TestMethod]
    [DataRow(0.1, true)]
    [DataRow(10.0, true)]
    [DataRow(5.0, true)]
    [DataRow(0.09, false)]
    [DataRow(10.01, false)]
    [DataRow(100.0, false)]
    public void TheCallbackRateBoundsAreBinancesOwnNotTheAbstractions(double rate, bool accepted)
    {
        // 抽象層允許 0 到 100(所有交易所的聯集),幣安只收 0.1 到 10。
        // 兩端都驗:只驗中間值的話,把上限寫成 100 的錯誤照樣會通過。
        // The abstraction allows 0 to 100, the union across exchanges, while Binance takes 0.1 to 10. Both ends
        // are asserted, because a ceiling mistakenly left at 100 still passes a test of the middle.
        var request = new OrderRequest
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Sell,
            OrderType = OrderType.TrailingStopMarket,
            Quantity = 1m,
            CallbackRate = (decimal)rate,
        };

        var result = BinanceOrderMapper.ValidateForBinance(request, "ozk-1-aaaaaaaa");

        Assert.AreEqual(accepted, result.IsSuccess, $"回撤比例 {rate} 的判定不如預期:{result.Error?.Message}");
    }

    [TestMethod]
    public void ValidationRejectsANullRequest()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceOrderMapper.ValidateForBinance(null!, "id"));
    }

    [TestMethod]
    public void AnUndefinedOrderSideIsAFailureRatherThanAnException()
    {
        // (OrderSide)99 通過了抽象層的驗證 —— 它只擋 Unspecified。下單方法的契約是回傳 Result,
        // 所以這裡要變成失敗,而不是讓對映表擲例外穿過去。
        // A cast such as (OrderSide)99 passes the abstraction's validation, which only rejects Unspecified.
        // The placement contract returns a Result, so this becomes a failure rather than an exception escaping
        // through the mapping table.
        var request = new OrderRequest
        {
            Symbol = "BTCUSDT",
            Side = (OrderSide)99,
            OrderType = OrderType.Market,
            Quantity = 1m,
        };

        var result = BinanceOrderMapper.ValidateForBinance(request, "ozk-1-aaaaaaaa");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    [TestMethod]
    public void AnUndefinedOrderTypeIsAFailureRatherThanAnException()
    {
        var request = new OrderRequest
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = (OrderType)99,
            Quantity = 1m,
        };

        var result = BinanceOrderMapper.ValidateForBinance(request, "ozk-1-aaaaaaaa");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    [TestMethod]
    public void AnUndefinedTimeInForceIsAFailureOnLimitStyleOrders()
    {
        var request = new OrderRequest
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = 1m,
            Price = 100m,
            TimeInForce = (TimeInForce)99,
        };

        var result = BinanceOrderMapper.ValidateForBinance(request, "ozk-1-aaaaaaaa");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidOrderRequest, result.Error!.Code);
    }

    [TestMethod]
    public void AMarketOrderIgnoresAnUndefinedTimeInForceBecauseItNeverSendsOne()
    {
        // 市價單不送 timeInForce,所以那個欄位是什麼都無所謂。多擋一層只會讓合法的市價單被拒。
        // A market order never sends timeInForce, so its value is immaterial; validating it anyway would
        // reject perfectly good market orders.
        var request = new OrderRequest
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = OrderType.Market,
            Quantity = 1m,
            TimeInForce = (TimeInForce)99,
        };

        Assert.IsTrue(BinanceOrderMapper.ValidateForBinance(request, "ozk-1-aaaaaaaa").IsSuccess);
    }
}
