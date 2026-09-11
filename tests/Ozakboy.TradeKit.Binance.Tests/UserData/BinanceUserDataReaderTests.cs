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
    public void AnAccountUpdateMapsTheOptionalBalanceAndPositionFields()
    {
        var update = BinanceUserDataReader.Read(UserDataSamples.AccountUpdate).GetValueOrThrow().Account!;

        var balance = update.Balances[0];

        Assert.AreEqual(15000.12345678m, balance.CrossWalletBalance);
        Assert.AreEqual(0m, balance.NonTradingChange, "bc 是 \"0\" 時是零,不是 null —— 交易所明確說了沒有非交易變動。");

        var position = update.Positions[0];

        Assert.AreEqual(PositionSide.Both, position.Side);
        Assert.AreEqual(0m, position.AccumulatedRealizedPnl);

        // 全倉部位的 iw 一律是 "0"。照抄會得到一個「逐倉保證金為零」的全倉部位。
        // A cross position's iw is always "0"; copying it gives a cross position an isolated margin of zero.
        Assert.IsNull(position.IsolatedMargin, "全倉部位的逐倉保證金應為 null。");
    }

    [TestMethod]
    public void AnIsolatedPositionCarriesItsIsolatedMarginAndRealizedPnl()
    {
        var update = BinanceUserDataReader.Read(UserDataSamples.AccountUpdateIsolated).GetValueOrThrow().Account!;

        var position = update.Positions[0];

        Assert.AreEqual("ETHUSDT", position.Symbol);
        Assert.AreEqual(-0.500m, position.Quantity);
        Assert.AreEqual(PositionSide.Short, position.Side);
        Assert.AreEqual(2500.00m, position.EntryPrice);
        Assert.AreEqual(1.20000000m, position.UnrealizedPnl);
        Assert.AreEqual(-3.25000000m, position.AccumulatedRealizedPnl);
        Assert.AreEqual(MarginMode.Isolated, position.MarginMode);
        Assert.AreEqual(125.40000000m, position.IsolatedMargin);
        Assert.AreEqual(14864.60000000m, update.Balances[0].CrossWalletBalance);
    }

    [TestMethod]
    public void OptionalAccountFieldsThatAreAbsentAreNullRatherThanZero()
    {
        // 零是一個說得出口的數字,「交易所沒說」不是。bc 缺席時填零,會讓一筆入金被當成策略賺的。
        // Zero is a number one can state and "the exchange did not say" is not. Defaulting bc to zero counts a
        // deposit as something the strategy earned.
        var update = BinanceUserDataReader
            .Read(UserDataSamples.AccountUpdateWithoutOptionalFields)
            .GetValueOrThrow()
            .Account!;

        Assert.IsNull(update.Balances[0].CrossWalletBalance, "cw 缺席應為 null。");
        Assert.IsNull(update.Balances[0].NonTradingChange, "bc 缺席應為 null。");
        Assert.IsNull(update.Positions[0].AccumulatedRealizedPnl, "cr 缺席應為 null。");
        Assert.IsNull(update.Positions[0].IsolatedMargin, "逐倉部位的 iw 缺席應為 null。");
        Assert.AreEqual(MarginMode.Isolated, update.Positions[0].MarginMode);
    }

    [TestMethod]
    public void AnAccountPositionWithoutAnEntryPriceFailsInsteadOfDefaultingToZero()
    {
        // PositionChange.EntryPrice 的零是留給「已平倉」的。缺了就填零,一個還開著的部位會看起來像沒有進場價。
        // Zero is reserved for a closed position; defaulting a missing entry price gives an open position none.
        var read = BinanceUserDataReader.Read(UserDataSamples.AccountUpdateWithoutEntryPrice);

        Assert.IsTrue(read.IsFailure);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("ep", field);
    }

    [TestMethod]
    [DataRow(nameof(UserDataSamples.AccountUpdateWithoutMarginType), "BTCUSDT")]
    [DataRow(nameof(UserDataSamples.MarginCallWithoutMarginType), "ETHUSDT")]
    public void AMissingMarginTypeFailsInsteadOfDefaultingToIsolated(string sampleName, string expectedSymbol)
    {
        // 缺席曾經會落到「不是 cross 就是逐倉」:全倉部位被讀成逐倉,保證金與強平的計算整個走錯邊,
        // 而欄位看起來完全正常。REST 持倉查詢缺這個欄位本來就判失敗,兩邊要一致。
        // An absent value used to fall through to isolated, sending a cross position's margin and liquidation
        // maths down the wrong branch while looking normal. The REST reader already fails here; so must this.
        var sample = (string)typeof(UserDataSamples).GetField(sampleName)!.GetValue(null)!;

        var read = BinanceUserDataReader.Read(sample);

        Assert.IsTrue(read.IsFailure, "缺少保證金模式卻解析成功 —— 那個值是猜出來的。");
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("mt", field);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Symbol, out var symbol));
        Assert.AreEqual(expectedSymbol, symbol);
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

    [TestMethod]
    public void AMarginCallCarriesTheMaintenanceMarginAndARealNotional()
    {
        // 標記價是真的,所以名目價值也是真的 —— 這正是追繳部位與帳戶增量部位的差別。
        // The mark price is real, so the notional is too — which is exactly how a margin call position differs
        // from an account delta position.
        var position = BinanceUserDataReader
            .Read(UserDataSamples.MarginCall)
            .GetValueOrThrow()
            .MarginWarning!
            .Positions[0];

        Assert.AreEqual(1.614445m, position.MaintenanceMargin);
        Assert.AreEqual(1.327m * 7.10m, position.Notional);
        Assert.IsNull(position.IsolatedMargin, "全倉部位的逐倉保證金應為 null。");
    }

    [TestMethod]
    public void AMarginCallWithoutAMarkPriceFails()
    {
        // 少了標記價,這則警告就沒有意義,名目價值也會跟著變成零。
        // Without the mark price the warning means nothing, and the notional collapses to zero with it.
        var read = BinanceUserDataReader.Read(UserDataSamples.MarginCallWithoutMarkPrice);

        Assert.IsTrue(read.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("mp", field);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Symbol, out var symbol));
        Assert.AreEqual("ETHUSDT", symbol);
    }

    [TestMethod]
    public void AMarginCallWithoutOptionalFieldsLeavesThemNull()
    {
        var call = BinanceUserDataReader
            .Read(UserDataSamples.MarginCallIsolatedWithoutMaintenanceMargin)
            .GetValueOrThrow()
            .MarginWarning!;

        Assert.IsNull(call.CrossWalletBalance, "cw 缺席應為 null。");

        var position = call.Positions[0];

        Assert.IsNull(position.MaintenanceMargin, "mm 缺席應為 null。");
        Assert.AreEqual(MarginMode.Isolated, position.MarginMode);
        Assert.AreEqual(12.50m, position.IsolatedMargin);
        Assert.AreEqual(PositionSide.Short, position.Side);
        Assert.AreEqual(2600.00m, position.MarkPrice);
    }

    // ── 心跳回覆 / Heartbeat replies ───────────────────────────────────

    [TestMethod]
    public void AHeartbeatReplyIsIgnored()
    {
        // 回覆的 result 裡就是憑證。判成失敗等於每 30 秒送一則錯誤給消費端,而錯誤會進日誌。
        // The reply's result is the credential. Failing it would hand the consumer an error every 30 seconds,
        // and errors end up in logs.
        var read = BinanceUserDataReader.Read(UserDataSamples.HeartbeatReply(UserDataSamples.ListenKey, 1));

        Assert.IsTrue(read.IsSuccess, read.Error?.Message);
        Assert.AreEqual(BinanceUserDataEventKind.Ignored, read.GetValueOrThrow().Kind);
    }

    [TestMethod]
    public void ARejectedOrEmptyCommandReplyIsIgnoredToo()
    {
        foreach (var reply in new[]
                 {
                     UserDataSamples.HeartbeatRejection(UserDataSamples.ListenKey),
                     """{"result":null,"id":7}""",
                 })
        {
            var read = BinanceUserDataReader.Read(reply);

            Assert.IsTrue(read.IsSuccess, read.Error?.Message);
            Assert.AreEqual(BinanceUserDataEventKind.Ignored, read.GetValueOrThrow().Kind);
        }
    }

    [TestMethod]
    public void AnObjectWithNeitherAnIdNorAnEventTypeStillFailsWithoutQuotingIt()
    {
        // 既不是事件也不是回覆,是協定變了 —— 要浮上來。但失敗只說缺哪個欄位,不說收到了什麼。
        // Neither an event nor a reply means the protocol changed, so it must surface — naming the missing field
        // and never what arrived.
        var read = BinanceUserDataReader.Read($$"""{"result":["{{UserDataSamples.ListenKey}}"]}""");

        Assert.IsTrue(read.IsFailure);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("e", field);

        var described = string.Join(
            '|',
            new[] { read.Error.Code, read.Error.Message }
                .Concat(read.Error.Data?.Select(pair => $"{pair.Key}={pair.Value}") ?? []));

        Assert.DoesNotContain(UserDataSamples.ListenKey, described, StringComparison.OrdinalIgnoreCase);
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
