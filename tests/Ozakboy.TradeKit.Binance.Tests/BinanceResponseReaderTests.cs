using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceResponseReaderTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 11, 7, 5, 53, TimeSpan.Zero);

    [TestMethod]
    public void ReadsServerTimeAsUtc()
    {
        var result = BinanceResponseReader.ReadServerTime(Fixtures.ServerTime);

        Assert.IsTrue(result.TryGetValue(out var serverTime));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789110429074L), serverTime);
        Assert.AreEqual(TimeSpan.Zero, serverTime.Offset);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("{oops")]
    [DataRow("{}")]
    [DataRow("""{"serverTime":0}""")]
    [DataRow("""{"serverTime":"not a number"}""")]
    public void RejectsAServerTimeItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadServerTime(json).IsFailure);
    }

    [TestMethod]
    public void EveryFieldTheParserNeedsExistsInTheRecordedTestnetResponse()
    {
        // 這一條的價值不在斷言的內容,而在資料的來源:fixture 是 Testnet 的實錄回應。
        // 只要有任何一個欄位名抄錯(例如把 unRealizedProfit 抄成小寫 r),解析就會在這裡失敗 ——
        // 那正是手寫 fixture 驗不出來的那一類錯誤。
        // The value here is not the assertion but the data behind it: the fixture is a live Testnet recording.
        // A single misspelled field name — the lower-case r of unRealizedProfit, say — fails the parse right
        // here, which is exactly the class of error a hand-written fixture cannot catch.
        var result = BinanceResponseReader.ReadPositions(Fixtures.PositionRisk, AsOf, includeFlat: true);

        Assert.IsTrue(result.TryGetValue(out var positions), result.Error?.Message);
        Assert.HasCount(6, positions);

        // 錄製時帳戶是空手的,所以數量全為零;但槓桿與保證金模式是真實值,證明那兩個欄位讀得到內容。
        // The account was flat when recorded, so every quantity is zero; leverage and margin mode carry real
        // values and prove those two fields are actually read.
        Assert.IsTrue(positions.All(position => position.IsFlat));
        Assert.IsTrue(positions.All(position => position.Leverage >= 1));
        Assert.IsTrue(positions.All(position => position.MarginMode == MarginMode.Cross));
        Assert.IsTrue(positions.All(position => position.Side == PositionSide.Both));
    }

    [TestMethod]
    public void TheRecordedHedgeModeResponseCarriesBothSides()
    {
        // 雙向模式的實錄:每個商品各有一筆 LONG 與一筆 SHORT。這份資料證明的是
        // positionSide 的字面值真的是 "LONG" 與 "SHORT",而不是文件上看起來像的東西。
        // The hedge-mode recording carries one LONG and one SHORT row per symbol, which proves the literals
        // really are "LONG" and "SHORT" rather than what the documentation appears to say.
        var positions = BinanceResponseReader
            .ReadPositions(Fixtures.PositionRiskHedge, AsOf, includeFlat: true)
            .GetValueOrThrow();

        Assert.HasCount(4, positions);
        Assert.HasCount(2, positions.Where(position => position.Side == PositionSide.Long).ToList());
        Assert.HasCount(2, positions.Where(position => position.Side == PositionSide.Short).ToList());
    }

    [TestMethod]
    public void ReadsPositionsAndDropsFlatOnesByDefault()
    {
        var result = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskOpen, AsOf);

        Assert.IsTrue(result.TryGetValue(out var positions));

        // fixture 有三筆,其中 DOGEUSDT 是空手的。
        // The fixture holds three rows, one of them flat.
        Assert.HasCount(2, positions);
        Assert.IsFalse(positions.Any(position => position.Symbol == "DOGEUSDT"));
    }

    [TestMethod]
    public void ReadsALongCrossPositionCompletely()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskOpen, AsOf).GetValueOrThrow();
        var btc = positions.Single(position => position.Symbol == "BTCUSDT");

        Assert.AreEqual(0.015m, btc.Quantity);
        Assert.IsTrue(btc.IsLong);
        Assert.AreEqual(62000.00m, btc.EntryPrice);
        Assert.AreEqual(62822.66666666m, btc.MarkPrice);
        Assert.AreEqual(12.33999999m, btc.UnrealizedPnl);
        Assert.AreEqual(51230.10m, btc.LiquidationPrice);
        Assert.AreEqual(10, btc.Leverage);
        Assert.AreEqual(MarginMode.Cross, btc.MarginMode);
        Assert.AreEqual(PositionSide.Both, btc.Side);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789109000000L), btc.UpdatedAt);

        // 名目價值是由 MarkPrice 算的 —— 這正是不能用帳戶端點那份沒有 markPrice 的持倉的原因。
        // The notional derives from MarkPrice, which is why the account endpoint's markPrice-less positions
        // cannot be used.
        Assert.IsGreaterThan(0m, btc.Notional);
    }

    [TestMethod]
    public void ReadsAShortIsolatedPositionCompletely()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskOpen, AsOf).GetValueOrThrow();
        var eth = positions.Single(position => position.Symbol == "ETHUSDT");

        Assert.AreEqual(-0.500m, eth.Quantity);
        Assert.IsTrue(eth.IsShort);
        Assert.AreEqual(OrderSide.Buy, eth.ClosingSide);
        Assert.AreEqual(MarginMode.Isolated, eth.MarginMode);
        Assert.AreEqual(240.00000000m, eth.Margin);
        Assert.AreEqual(5, eth.Leverage);
    }

    [TestMethod]
    public void AZeroLiquidationPriceBecomesNullRatherThanZero()
    {
        // 強平價 0 代表「沒有強平價」,不是「會在零元被強平」。留成 0 會讓風控看到一個近在眼前的強平價。
        // A zero means there is none, not that liquidation happens at zero; keeping it would show risk
        // management an imminent liquidation.
        var positions = BinanceResponseReader
            .ReadPositions(Fixtures.PositionRiskOpen, AsOf, includeFlat: true)
            .GetValueOrThrow();

        Assert.IsNull(positions.Single(position => position.Symbol == "DOGEUSDT").LiquidationPrice);
    }

    [TestMethod]
    public void AZeroUpdateTimeFallsBackToTheSnapshotMomentNotToNineteenSeventy()
    {
        var positions = BinanceResponseReader
            .ReadPositions(Fixtures.PositionRiskOpen, AsOf, includeFlat: true)
            .GetValueOrThrow();

        Assert.AreEqual(AsOf, positions.Single(position => position.Symbol == "DOGEUSDT").UpdatedAt);
    }

    [TestMethod]
    public void ReadsHedgeModeSides()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskHedgeOpen, AsOf).GetValueOrThrow();

        Assert.HasCount(2, positions);
        Assert.AreEqual(PositionSide.Long, positions[0].Side);
        Assert.AreEqual(PositionSide.Short, positions[1].Side);
    }

    [TestMethod]
    [DataRow("symbol")]
    [DataRow("positionAmt")]
    [DataRow("entryPrice")]
    [DataRow("markPrice")]
    [DataRow("unRealizedProfit")]
    [DataRow("leverage")]
    [DataRow("marginType")]
    public void AMissingPositionFieldFails(string field)
    {
        var json = Fixtures.PositionRiskOpen.Replace($"\"{field}\":", $"\"{field}_removed\":", StringComparison.Ordinal);
        var result = BinanceResponseReader.ReadPositions(json, AsOf);

        Assert.IsTrue(result.IsFailure, $"{field} 被拿掉之後應該解析失敗。");
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
        Assert.IsTrue(result.Error.TryGetData(BinanceErrorDataKeys.Field, out var named));
        StringAssert.Contains(named!, field, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheCapitalRSpellingIsWhatPositionRiskActuallyUses()
    {
        // unRealizedProfit(大寫 R)是 positionRisk 的拼法,已由 Testnet 實錄確認。抄成小寫不會報錯,
        // 只會讓浮動盈虧永遠是零 —— 所以這裡明確驗證「小寫拼法會被當成缺欄位」,而不是預設值 0。
        // The capital R of unRealizedProfit is what positionRisk really uses, confirmed against a Testnet
        // recording. Copying the account endpoint's lower-case spelling would not fail; it would silently zero
        // the P&L, so the lower-case form is asserted to read as a missing field rather than as a default.
        var json = Fixtures.PositionRisk.Replace(
            "\"unRealizedProfit\":",
            "\"unrealizedProfit\":",
            StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadPositions(json, AsOf, includeFlat: true).IsFailure);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{oops")]
    [DataRow("""{"not":"an array"}""")]
    [DataRow("""["not an object"]""")]
    public void RejectsAPositionPayloadItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadPositions(json, AsOf).IsFailure);
    }

    [TestMethod]
    public void RejectsANonPositiveLeverage()
    {
        var json = Fixtures.PositionRiskOpen.Replace("\"leverage\": \"10\"", "\"leverage\": \"0\"", StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadPositions(json, AsOf).IsFailure);
    }

    [TestMethod]
    public void ReadsAccountBalances()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskOpen, AsOf).GetValueOrThrow();
        var result = BinanceResponseReader.ReadAccountSnapshot(Fixtures.Account, positions, AsOf);

        Assert.IsTrue(result.TryGetValue(out var snapshot));
        Assert.HasCount(4, snapshot.Balances);
        Assert.AreEqual(AsOf, snapshot.TakenAt);
        Assert.IsTrue(snapshot.CanTrade);
        Assert.IsFalse(snapshot.IsHedgeMode);

        // 這幾個數字是 Testnet 帳戶當時的真實餘額,不是編出來的。
        // These are the Testnet account's real balances at recording time, not invented ones.
        Assert.IsTrue(snapshot.GetBalance("USDT").TryGetValue(out var usdt));
        Assert.AreEqual(5000.00000000m, usdt.WalletBalance);
        Assert.AreEqual(5000.00000000m, usdt.AvailableBalance);
        Assert.AreEqual(0m, usdt.UnrealizedPnl);
        Assert.AreEqual(5000.00000000m, usdt.MarginBalance);

        Assert.IsTrue(snapshot.GetBalance("BTC").TryGetValue(out var btc));
        Assert.AreEqual(0.01000000m, btc.WalletBalance);
    }

    [TestMethod]
    public void PositionsComeFromTheCallerNotFromTheAccountPayload()
    {
        // fixture 的 account.positions 有三筆(實錄,全部空手),而傳進來的是兩筆有部位的。
        // 快照必須用傳進來的那一份 —— 帳戶端點的持倉沒有 markPrice,拿它算出來的名目價值會是零。
        // The fixture's account.positions holds three recorded rows, all flat, while two open ones are passed
        // in. The snapshot has to use what was passed in: the account endpoint's positions carry no markPrice,
        // so a notional derived from them comes out zero.
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskOpen, AsOf).GetValueOrThrow();
        var snapshot = BinanceResponseReader
            .ReadAccountSnapshot(Fixtures.Account, positions, AsOf)
            .GetValueOrThrow();

        Assert.HasCount(2, snapshot.Positions);
        Assert.IsTrue(snapshot.GetPosition("BTCUSDT").IsSuccess);
        Assert.IsGreaterThan(0m, snapshot.GetPosition("BTCUSDT").GetValueOrThrow().Notional);
    }

    [TestMethod]
    public void TheAccountEndpointsOwnPositionsCarryNoMarkPrice()
    {
        // 這不是在測本套件的程式碼,而是把「為什麼要多打一次 positionRisk」釘成一條會失敗的斷言。
        // 哪天幣安替 account 的持倉補上 markPrice,這條會紅,那時才值得重新考慮省下那 5 點權重。
        // This does not test this package's code; it pins down why a second positionRisk call is worth five
        // extra weight. Should Binance ever add markPrice to the account endpoint's positions, this goes red
        // and the trade-off is worth revisiting.
        using var document = System.Text.Json.JsonDocument.Parse(Fixtures.Account);
        var positions = document.RootElement.GetProperty("positions");

        Assert.IsGreaterThan(0, positions.GetArrayLength());

        foreach (var position in positions.EnumerateArray())
        {
            Assert.IsFalse(position.TryGetProperty("markPrice", out _), "account 的持倉出現了 markPrice。");
            Assert.IsFalse(position.TryGetProperty("liquidationPrice", out _), "account 的持倉出現了 liquidationPrice。");
        }
    }

    [TestMethod]
    public void HedgeModeIsInferredFromThePositionSides()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskHedgeOpen, AsOf).GetValueOrThrow();
        var snapshot = BinanceResponseReader
            .ReadAccountSnapshot(Fixtures.Account, positions, AsOf)
            .GetValueOrThrow();

        Assert.IsTrue(snapshot.IsHedgeMode);
    }

    [TestMethod]
    [DataRow("asset")]
    [DataRow("walletBalance")]
    [DataRow("availableBalance")]
    [DataRow("unrealizedProfit")]
    public void AMissingBalanceFieldFails(string field)
    {
        var json = Fixtures.Account.Replace($"\"{field}\":", $"\"{field}_removed\":", StringComparison.Ordinal);
        var result = BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf);

        Assert.IsTrue(result.IsFailure, $"{field} 被拿掉之後應該解析失敗。");
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{oops")]
    [DataRow("[]")]
    [DataRow("""{"canTrade":true}""")]
    [DataRow("""{"assets":["not an object"]}""")]
    public void RejectsAnAccountPayloadItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf).IsFailure);
    }

    [TestMethod]
    public void AccountSnapshotRejectsNullPositions()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceResponseReader.ReadAccountSnapshot(Fixtures.Account, null!, AsOf));
    }

    [TestMethod]
    public void MissingCanTradeIsTreatedAsTradableRatherThanBlocking()
    {
        // canTrade 缺漏時預設可交易。反過來預設「不可交易」會讓一個解析瑕疵直接停掉整個系統,
        // 而這個欄位不是風控的最後一道防線。
        // A missing canTrade defaults to tradable; defaulting the other way would let one parsing quirk halt
        // the whole system, and this flag is not the last line of risk control.
        var json = Fixtures.Account.Replace("\"canTrade\":", "\"canTrade_removed\":", StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf).GetValueOrThrow().CanTrade);
    }

    [TestMethod]
    public void ADisabledAccountIsReportedAsSuch()
    {
        var json = Fixtures.Account.Replace("\"canTrade\": true", "\"canTrade\": false", StringComparison.Ordinal);

        Assert.IsFalse(BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf).GetValueOrThrow().CanTrade);
    }

    // ── 委託解析 / Order parsing ────────────────────────────────────────────

    [TestMethod]
    public void ReadsTheRecordedPlaceOrderReply()
    {
        var result = BinanceResponseReader.ReadOrder(Fixtures.OrderNew, AsOf);

        Assert.IsTrue(result.TryGetValue(out var order), result.Error?.Message);
        Assert.AreEqual("BTCUSDT", order.Symbol);
        Assert.AreEqual("28580357696", order.ExchangeOrderId);
        Assert.AreEqual("pulsetrade-rec-1789116070165", order.ClientOrderId);
        Assert.AreEqual(OrderStatus.New, order.Status);
        Assert.AreEqual(OrderSide.Buy, order.Side);
        Assert.AreEqual(OrderType.Limit, order.OrderType);
        Assert.AreEqual(TimeInForce.GoodTilCanceled, order.TimeInForce);
        Assert.AreEqual(PositionSide.Both, order.PositionSide);
        Assert.AreEqual(0.0008m, order.Quantity);
        Assert.AreEqual(0m, order.FilledQuantity);
        Assert.AreEqual(74260.40m, order.Price);
        Assert.IsFalse(order.ReduceOnly);
        Assert.IsFalse(order.ClosePosition);
        Assert.IsTrue(order.IsOpen);
        Assert.IsFalse(order.IsFinal);

        // 下單的回應沒有 time,只有 updateTime;建立時間以它充當,不會落在西元 0001 年。
        // The place-order reply carries no time, only updateTime, which stands in so the creation time is not
        // left at year 0001.
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789116070010L), order.CreatedAt);
        Assert.AreEqual(order.UpdatedAt, order.CreatedAt);
    }

    [TestMethod]
    public void ThePlaceOrderReplyCarriesNoAverageFillPriceOrNotional()
    {
        // 實錄確認:POST /fapi/v1/order 的回應<b>沒有</b> avgPrice 與 cumQuote(只有 cumQty),
        // 查單的回應才有。這條測試把這個落差釘住 —— 市價單送出後想知道成交均價,必須另外查單。
        // The recording shows the POST reply carries neither avgPrice nor cumQuote, only cumQty, while the
        // query reply carries both. Knowing the average fill price of a market order therefore means a second
        // lookup, and this pins that gap down.
        using var placed = System.Text.Json.JsonDocument.Parse(Fixtures.OrderNew);
        using var queried = System.Text.Json.JsonDocument.Parse(Fixtures.OrderQuery);

        Assert.IsFalse(placed.RootElement.TryGetProperty("avgPrice", out _));
        Assert.IsFalse(placed.RootElement.TryGetProperty("cumQuote", out _));
        Assert.IsTrue(queried.RootElement.TryGetProperty("avgPrice", out _));
        Assert.IsTrue(queried.RootElement.TryGetProperty("cumQuote", out _));

        Assert.AreEqual(0m, BinanceResponseReader.ReadOrder(Fixtures.OrderNew, AsOf).GetValueOrThrow().AverageFillPrice);
        Assert.AreEqual(0m, BinanceResponseReader.ReadOrder(Fixtures.OrderNew, AsOf).GetValueOrThrow().FilledNotional);
    }

    [TestMethod]
    public void ReadsTheRecordedQueryReplyIncludingItsCreationTime()
    {
        var order = BinanceResponseReader.ReadOrder(Fixtures.OrderQuery, AsOf).GetValueOrThrow();

        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789116070010L), order.CreatedAt);
        Assert.AreEqual(0m, order.AverageFillPrice);
        Assert.AreEqual(0m, order.FilledNotional);
        Assert.AreEqual(0.0008m, order.RemainingQuantity);
    }

    [TestMethod]
    public void ReadsTheRecordedCancelReplyAsATerminalOrder()
    {
        var order = BinanceResponseReader.ReadOrder(Fixtures.OrderCanceled, AsOf).GetValueOrThrow();

        Assert.AreEqual(OrderStatus.Canceled, order.Status);
        Assert.IsTrue(order.IsFinal);
        Assert.IsFalse(order.IsOpen);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789116070370L), order.UpdatedAt);
    }

    [TestMethod]
    public void AZeroStopPriceBecomesNullRatherThanAnOrderTriggeringAtZero()
    {
        // 限價單的回應仍帶 "stopPrice":"0.00"。照抄成 0 會讓上層看到一張「觸發價零元」的條件單。
        // A limit order's reply still carries "stopPrice":"0.00"; copying it through would show a conditional
        // order that triggers at zero.
        var order = BinanceResponseReader.ReadOrder(Fixtures.OrderNew, AsOf).GetValueOrThrow();

        Assert.IsNull(order.StopPrice);
    }

    [TestMethod]
    public void ReadsTheRecordedOpenOrdersList()
    {
        var result = BinanceResponseReader.ReadOrders(Fixtures.OpenOrders, AsOf);

        Assert.IsTrue(result.TryGetValue(out var orders), result.Error?.Message);
        Assert.HasCount(1, orders);
        Assert.AreEqual("28580357696", orders[0].ExchangeOrderId);
        Assert.IsTrue(orders[0].IsOpen);
    }

    [TestMethod]
    public void AnEmptyOpenOrdersListIsSuccessRatherThanFailure()
    {
        var result = BinanceResponseReader.ReadOrders("[]", AsOf);

        Assert.IsTrue(result.TryGetValue(out var orders));
        Assert.HasCount(0, orders);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{oops")]
    [DataRow("""{"not":"an array"}""")]
    [DataRow("""["not an object"]""")]
    public void RejectsAnOrderListItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadOrders(json, AsOf).IsFailure);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{oops")]
    [DataRow("[]")]
    public void RejectsAnOrderPayloadItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadOrder(json, AsOf).IsFailure);
    }

    [TestMethod]
    [DataRow("symbol")]
    [DataRow("clientOrderId")]
    [DataRow("status")]
    [DataRow("side")]
    [DataRow("origQty")]
    public void AMissingOrderFieldFails(string field)
    {
        var json = Fixtures.OrderNew.Replace($"\"{field}\":", $"\"{field}_removed\":", StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadOrder(json, AsOf).IsFailure, $"{field} 被拿掉之後應該解析失敗。");
    }

    [TestMethod]
    public void AnUnknownOrderStatusFailsRatherThanBecomingUnspecified()
    {
        // 既不算在簿上、也不算終態的委託,會讓部位追蹤永遠等不到結局 —— 那比整份查詢失敗難查得多。
        // An order that counts as neither live nor final leaves position tracking waiting for ever, which is
        // far harder to diagnose than an outright failure.
        var json = Fixtures.OrderNew.Replace("\"status\": \"NEW\"", "\"status\": \"WHAT_IS_THIS\"", StringComparison.Ordinal);
        var result = BinanceResponseReader.ReadOrder(json, AsOf);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
        StringAssert.Contains(result.Error.Message, "WHAT_IS_THIS", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnUnknownOrderSideFailsToo()
    {
        var json = Fixtures.OrderNew.Replace("\"side\": \"BUY\"", "\"side\": \"SIDEWAYS\"", StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadOrder(json, AsOf).IsFailure);
    }

    [TestMethod]
    public void AnUnknownOrderTypeIsReportedAsUnspecifiedRatherThanFailingTheWholeList()
    {
        // 掛單清單會一併回傳手動下的單,那裡可能出現本套件不模型化的類型。
        // 為了一張別人的單讓整份清單讀不到,代價遠大於一個誠實的「未指定」。
        // A listing includes orders placed by hand, which can carry types this package does not model. Losing
        // the whole list over someone else's order costs far more than one honest "unspecified".
        var json = Fixtures.OrderNew.Replace("\"origType\": \"LIMIT\"", "\"origType\": \"SOMETHING_NEW\"", StringComparison.Ordinal);
        var order = BinanceResponseReader.ReadOrder(json, AsOf).GetValueOrThrow();

        Assert.AreEqual(OrderType.Unspecified, order.OrderType);
    }

    [TestMethod]
    public void AnUnknownTimeInForceIsReportedAsUnspecifiedRatherThanGuessedAsGtc()
    {
        // 把一張有到期時間的單說成「掛到撤銷為止」,會讓上層以為它會一直在那裡。
        // Calling an order with an expiry "good til cancelled" tells the caller it will stay on the book.
        var json = Fixtures.OrderNew.Replace("\"timeInForce\": \"GTC\"", "\"timeInForce\": \"GTD\"", StringComparison.Ordinal);
        var order = BinanceResponseReader.ReadOrder(json, AsOf).GetValueOrThrow();

        Assert.AreEqual(TimeInForce.Unspecified, order.TimeInForce);
    }

    [TestMethod]
    public void FallsBackToTypeWhenOrigTypeIsAbsent()
    {
        var json = Fixtures.OrderNew.Replace("\"origType\":", "\"origType_removed\":", StringComparison.Ordinal);
        var order = BinanceResponseReader.ReadOrder(json, AsOf).GetValueOrThrow();

        Assert.AreEqual(OrderType.Limit, order.OrderType);
    }

    [TestMethod]
    public void AnOrderWithNeitherTypeFieldIsUnspecifiedRatherThanAMarketOrder()
    {
        // 對不上時絕不能退回 Market:那是唯一會立刻成交的類型,猜錯的代價是一個沒人要的部位。
        // Falling back to Market would be the one guess that fills immediately, and the cost of being wrong is
        // a position nobody asked for.
        var json = Fixtures.OrderNew
            .Replace("\"origType\":", "\"origType_removed\":", StringComparison.Ordinal)
            .Replace("\"type\":", "\"type_removed\":", StringComparison.Ordinal);

        Assert.AreEqual(
            OrderType.Unspecified,
            BinanceResponseReader.ReadOrder(json, AsOf).GetValueOrThrow().OrderType);
    }

    [TestMethod]
    public void AnOrderWithoutAnUpdateTimeFallsBackToTheSnapshotMoment()
    {
        var json = Fixtures.OrderNew.Replace("\"updateTime\":", "\"updateTime_removed\":", StringComparison.Ordinal);
        var order = BinanceResponseReader.ReadOrder(json, AsOf).GetValueOrThrow();

        Assert.AreEqual(AsOf, order.UpdatedAt);
        Assert.AreEqual(AsOf, order.CreatedAt);
    }

    [TestMethod]
    public void AnOrderWithoutAnExchangeIdFallsBackToTheClientId()
    {
        var json = Fixtures.OrderNew.Replace("\"orderId\":", "\"orderId_removed\":", StringComparison.Ordinal);
        var order = BinanceResponseReader.ReadOrder(json, AsOf).GetValueOrThrow();

        Assert.IsNull(order.ExchangeOrderId);
        Assert.AreEqual("pulsetrade-rec-1789116070165", order.GetIdentifier().ClientOrderId);
    }

    // ── 只回報成敗的端點 / Acknowledgement-only endpoints ────────────────────

    [TestMethod]
    public void TheRecordedCancelAllReplyCountsAsSuccess()
    {
        // 本文裡有 code 欄位,但 200 是成功而不是錯誤。
        // The body carries a code, but 200 there means success rather than an error.
        Assert.IsTrue(
            BinanceResponseReader.ReadAcknowledgement(Fixtures.CancelAll, BinanceApiPaths.AllOpenOrders).IsSuccess);
    }

    [TestMethod]
    public void TheRecordedLeverageReplyHasNoCodeAndStillCountsAsSuccess()
    {
        Assert.IsTrue(
            BinanceResponseReader.ReadAcknowledgement(Fixtures.Leverage, BinanceApiPaths.Leverage).IsSuccess);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not json at all")]
    public void AnUnreadableAcknowledgementBodyStillCountsAsSuccess(string json)
    {
        // 走到這裡代表 HTTP 已經是 2xx。本文讀不懂不足以推翻狀態碼,而把成功的撤單回報成失敗,
        // 會讓緊急出場流程停在第一步。
        // Reaching here means the status was already 2xx. An unreadable body does not overturn it, and
        // reporting a successful cancellation as a failure stops an emergency exit at its first step.
        Assert.IsTrue(
            BinanceResponseReader.ReadAcknowledgement(json, BinanceApiPaths.AllOpenOrders).IsSuccess);
    }

    [TestMethod]
    public void AnAcknowledgementCarryingARealErrorCodeFails()
    {
        var result = BinanceResponseReader.ReadAcknowledgement(
            Fixtures.MarginTypeNoChange,
            BinanceApiPaths.MarginType,
            BinanceEndpoints.Testnet);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TradeErrorCodes.MarginModeRejected, result.Error!.Code);
        Assert.IsTrue(result.Error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var apiCode));
        Assert.AreEqual(-4046L, apiCode);
    }
}
