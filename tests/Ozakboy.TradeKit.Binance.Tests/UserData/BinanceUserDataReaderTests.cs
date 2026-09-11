using Ozakboy.TradeKit.Binance.UserData;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流的欄位對映。
/// The field mapping of the user data stream.
/// </summary>
[TestClass]
public sealed class BinanceUserDataReaderTests
{
    // ── 委託與成交 / Orders and fills ──────────────────────────────────

    [TestMethod]
    public void AnOrderUpdateWithoutAFillProducesOnlyAnOrder()
    {
        var read = BinanceUserDataReader.Read(UserDataSamples.OrderNew);

        Assert.IsTrue(read.TryGetValue(out var evt), read.Error?.Message);
        Assert.AreEqual(BinanceUserDataEventKind.OrderTradeUpdate, evt.Kind);
        Assert.IsNotNull(evt.OrderUpdate);
        Assert.IsNull(evt.Fill, "沒有成交的事件不該憑空產生一筆成交。");

        var order = evt.OrderUpdate!;

        Assert.AreEqual("BTCUSDT", order.Symbol);
        Assert.AreEqual("pulsetrade-uds-1", order.ClientOrderId);
        Assert.AreEqual("8886774", order.ExchangeOrderId);
        Assert.AreEqual(OrderSide.Buy, order.Side);
        Assert.AreEqual(OrderType.Limit, order.OrderType);
        Assert.AreEqual(OrderStatus.New, order.Status);
        Assert.AreEqual(PositionSide.Both, order.PositionSide);
        Assert.AreEqual(TimeInForce.GoodTilCanceled, order.TimeInForce);
        Assert.AreEqual(0.002m, order.Quantity);
        Assert.AreEqual(0m, order.FilledQuantity);
        Assert.AreEqual(74000.00m, order.Price);
        Assert.IsFalse(order.ReduceOnly);
        Assert.IsFalse(order.ClosePosition);
    }

    [TestMethod]
    public void AZeroPriceIsReportedAsNoPriceRatherThanAsZero()
    {
        // 幣安對「沒有這個價格」的表示是 0,不是省略欄位。照抄會讓上層看到一張「限價零元」的委託。
        // Binance writes "no such price" as 0 rather than omitting the field; copying it through shows the
        // caller an order priced at zero.
        var order = BinanceUserDataReader.Read(UserDataSamples.OrderNew).GetValueOrThrow().OrderUpdate!;

        Assert.IsNull(order.StopPrice);
    }

    [TestMethod]
    public void TheSubmittedOrderTypeIsReportedRatherThanTheTriggeredOne()
    {
        // 條件單觸發之後 o 會變成實際掛出去的那一種。拿它回報等於把使用者下的 STOP_MARKET 說成 MARKET。
        // After a conditional trigger o becomes whatever was actually placed; reporting that turns the
        // caller's STOP_MARKET into a MARKET.
        var order = BinanceUserDataReader.Read(UserDataSamples.OrderTriggeredStop).GetValueOrThrow().OrderUpdate!;

        Assert.AreEqual(OrderType.StopMarket, order.OrderType);
        Assert.AreEqual(73000.00m, order.StopPrice);
        Assert.IsTrue(order.ReduceOnly);
    }

    [TestMethod]
    public void AnOrderUpdateCarryingAFillProducesBoth()
    {
        var evt = BinanceUserDataReader.Read(UserDataSamples.OrderPartiallyFilled).GetValueOrThrow();

        Assert.IsNotNull(evt.OrderUpdate);
        Assert.IsNotNull(evt.Fill);

        Assert.AreEqual(OrderStatus.PartiallyFilled, evt.OrderUpdate!.Status);
        Assert.AreEqual(0.001m, evt.OrderUpdate!.FilledQuantity);

        var fill = evt.Fill!;

        Assert.AreEqual("BTCUSDT", fill.Symbol);
        Assert.AreEqual("701001", fill.TradeId);
        Assert.AreEqual("8886774", fill.ExchangeOrderId);
        Assert.AreEqual("pulsetrade-uds-1", fill.ClientOrderId);
        Assert.AreEqual(OrderSide.Buy, fill.Side);
        Assert.AreEqual(73999.50m, fill.Price);
        Assert.AreEqual(0.001m, fill.Quantity);
        Assert.AreEqual(0.02959980m, fill.Fee);
        Assert.AreEqual("USDT", fill.FeeAsset);
        Assert.AreEqual(1.25000000m, fill.RealizedPnl);
        Assert.IsTrue(fill.IsMaker);
    }

    [TestMethod]
    public void TheFilledNotionalIsDerivedFromTheAveragePrice()
    {
        // 串流事件沒有 REST 那邊的 cumQuote。填零會讓「已成交但金額為零」的委託流到上層,
        // 而那個數字看起來完全合理。
        // The stream event has no cumQuote as REST does. Leaving it at zero publishes a filled order with zero
        // notional, and that number looks entirely reasonable.
        var order = BinanceUserDataReader.Read(UserDataSamples.OrderPartiallyFilled).GetValueOrThrow().OrderUpdate!;

        Assert.AreEqual(0.001m * 73999.50m, order.FilledNotional);
    }

    [TestMethod]
    public void TheFillCarriesTheOrderTradeTimeRatherThanTheEventTime()
    {
        var evt = BinanceUserDataReader.Read(UserDataSamples.OrderPartiallyFilled).GetValueOrThrow();

        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117381000L), evt.Fill!.ExecutedAt);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117381000L), evt.OrderUpdate!.UpdatedAt);
    }

    [TestMethod]
    public void AnUnmappableOrderStatusFailsInsteadOfBeingLetThrough()
    {
        // Unspecified 的 IsOpen 與 IsFinal 同時為 false:一張既沒結束也沒在簿上的單,
        // 會讓部位追蹤永遠等不到終態。
        // Unspecified reports both IsOpen and IsFinal as false, and an order that is neither live nor finished
        // leaves position tracking waiting for an outcome that never arrives.
        var read = BinanceUserDataReader.Read(UserDataSamples.OrderUnknownStatus);

        Assert.IsTrue(read.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Symbol, out var symbol));
        Assert.AreEqual("BTCUSDT", symbol);
    }

    // ── 帳戶增量 / Account deltas ──────────────────────────────────────

    [TestMethod]
    public void AnAccountUpdateCarriesTheChangedBalancesAndPositions()
    {
        var evt = BinanceUserDataReader.Read(UserDataSamples.AccountUpdate).GetValueOrThrow();

        Assert.AreEqual(BinanceUserDataEventKind.AccountUpdate, evt.Kind);

        var update = evt.Account!;

        Assert.AreEqual(AccountUpdateReason.Order, update.Reason);
        Assert.AreEqual("ORDER", update.RawReason);
        Assert.HasCount(1, update.Balances);
        Assert.AreEqual("USDT", update.Balances[0].Asset);
        Assert.AreEqual(15000.12345678m, update.Balances[0].WalletBalance);

        Assert.HasCount(1, update.Positions);
        Assert.AreEqual("BTCUSDT", update.Positions[0].Symbol);
        Assert.AreEqual(0.001m, update.Positions[0].Quantity);
        Assert.AreEqual(73999.50000m, update.Positions[0].EntryPrice);
        Assert.AreEqual(-0.00045000m, update.Positions[0].UnrealizedPnl);
        Assert.AreEqual(MarginMode.Cross, update.Positions[0].MarginMode);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117382003L), update.Timestamp);
    }

    [TestMethod]
    public void AnAccountUpdateThatOnlyMovedTheBalanceListsNoPositions()
    {
        // 增量只含有變動的項目。把它當快照用,會讓這一則看起來像「所有部位都被平掉了」。
        // A delta lists only what changed. Read as a snapshot, this one looks like every position was closed.
        var update = BinanceUserDataReader.Read(UserDataSamples.AccountUpdateFunding).GetValueOrThrow().Account!;

        Assert.AreEqual(AccountUpdateReason.FundingFee, update.Reason);
        Assert.HasCount(1, update.Balances);
        Assert.IsEmpty(update.Positions);
    }

    [TestMethod]
    public void AnUnknownReasonCodeKeepsTheRawValueInsteadOfFailing()
    {
        // 交易所日後新增代碼不該讓整則帳戶變動被丟掉 —— 丟掉一則,本地權益就從此差一截。
        // A code added later must not discard the whole account change: lose one and local equity stays out of
        // step from then on.
        var update = BinanceUserDataReader
            .Read(UserDataSamples.AccountUpdateUnknownReason)
            .GetValueOrThrow()
            .Account!;

        Assert.AreEqual(AccountUpdateReason.Unknown, update.Reason);
        Assert.AreEqual("A_REASON_ADDED_LATER", update.RawReason);
    }

    // ── 保證金追繳 / Margin calls ──────────────────────────────────────

    [TestMethod]
    public void AMarginCallCarriesTheWarnedPositionsAndTheirMarkPrice()
    {
        var evt = BinanceUserDataReader.Read(UserDataSamples.MarginCall).GetValueOrThrow();

        Assert.AreEqual(BinanceUserDataEventKind.MarginCall, evt.Kind);

        var call = evt.MarginWarning!;

        Assert.AreEqual(3.16812045m, call.CrossWalletBalance);
        Assert.HasCount(1, call.Positions);
        Assert.AreEqual("ETHUSDT", call.Positions[0].Symbol);
        Assert.AreEqual(PositionSide.Long, call.Positions[0].Side);
        Assert.AreEqual(1.327m, call.Positions[0].Quantity);
        Assert.AreEqual(7.10m, call.Positions[0].MarkPrice);
        Assert.AreEqual(-1.166074m, call.Positions[0].UnrealizedPnl);
    }

    [TestMethod]
    public void TheUpperCaseCrossedSpellingIsRecognisedAsCrossMargin()
    {
        // 同一個概念在不同地方拼法不同:ACCOUNT_UPDATE 回 cross,MARGIN_CALL 回 CROSSED。
        // 只認其中一種的下場不是解析失敗,而是把全倉部位讀成逐倉。
        // The same concept is spelled differently in different places: ACCOUNT_UPDATE answers cross and
        // MARGIN_CALL answers CROSSED. Recognising only one does not fail the parse, it reads a cross-margined
        // position as isolated.
        var call = BinanceUserDataReader.Read(UserDataSamples.MarginCall).GetValueOrThrow().MarginWarning!;

        Assert.AreEqual(MarginMode.Cross, call.Positions[0].MarginMode);
    }

    // ── 憑證與未知事件 / Credential and unknown events ─────────────────

    [TestMethod]
    public void TheCredentialExpiredEventIsRecognised()
    {
        var evt = BinanceUserDataReader
            .Read(UserDataSamples.ListenKeyExpired(UserDataSamples.ListenKey))
            .GetValueOrThrow();

        Assert.AreEqual(BinanceUserDataEventKind.ListenKeyExpired, evt.Kind);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(UserDataSamples.ListenKeyExpiredEventTimeMs),
            evt.EventTime);
    }

    [TestMethod]
    public void AnEventTypeThisPackageDoesNotModelIsIgnoredRatherThanFailed()
    {
        // 這條串流是多工的,交易所會持續新增事件型別。對未知型別判失敗,只會讓消費端被例行事件
        // 灌滿假警報,真正的失敗反而被淹掉。
        // The stream is multiplexed and the exchange keeps adding event types. Failing on an unknown one floods
        // the consumer with false alarms from routine events and buries the failures that matter.
        var read = BinanceUserDataReader.Read(UserDataSamples.AccountConfigUpdate);

        Assert.IsTrue(read.IsSuccess, read.Error?.Message);
        Assert.AreEqual(BinanceUserDataEventKind.Ignored, read.GetValueOrThrow().Kind);
    }

    // ── 讀不出來的訊息 / Unreadable frames ─────────────────────────────

    [TestMethod]
    public void AFrameThatIsNotJsonFailsWithoutQuotingItsContent()
    {
        var read = BinanceUserDataReader.Read("}{ not json");

        Assert.IsTrue(read.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
        Assert.DoesNotContain("not json", read.Error!.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AFrameWithoutAnEventTypeFails()
    {
        var read = BinanceUserDataReader.Read("""{"E":1789117380188}""");

        Assert.IsTrue(read.IsFailure);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("e", field);
    }

    [TestMethod]
    public void AFrameWithoutAnEventTimeFails()
    {
        var read = BinanceUserDataReader.Read("""{"e":"ACCOUNT_UPDATE","a":{"m":"ORDER"}}""");

        Assert.IsTrue(read.IsFailure);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("E", field);
    }

    [TestMethod]
    public void AFrameThatIsNotAJsonObjectFails()
    {
        var read = BinanceUserDataReader.Read("[1,2,3]");

        Assert.IsTrue(read.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
    }
}
