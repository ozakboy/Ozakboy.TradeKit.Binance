using Ozakboy.TradeKit.Binance.UserData;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// <c>ALGO_UPDATE</c>(條件單狀態變化)的欄位對映。
/// The field mapping of <c>ALGO_UPDATE</c>, the conditional order state change.
/// </summary>
[TestClass]
public sealed class BinanceAlgoUpdateReaderTests
{
    [TestMethod]
    public void AConditionalOrderEventMapsOntoTheNeutralModel()
    {
        var read = BinanceUserDataReader.Read(UserDataSamples.AlgoNew);

        Assert.IsTrue(read.TryGetValue(out var evt), read.Error?.Message);
        Assert.AreEqual(BinanceUserDataEventKind.AlgoUpdate, evt.Kind);
        Assert.IsNotNull(evt.ConditionalOrderUpdate);

        var order = evt.ConditionalOrderUpdate!.ConditionalOrder;

        Assert.AreEqual("BTCUSDT", order.Symbol);
        Assert.AreEqual("pulsetrade-algo-1", order.ClientConditionalOrderId);
        Assert.AreEqual("2148719", order.ExchangeConditionalOrderId);
        Assert.AreEqual(OrderSide.Sell, order.Side);
        Assert.AreEqual(ConditionalOrderType.StopMarket, order.ConditionalOrderType);
        Assert.AreEqual(ConditionalOrderStatus.New, order.Status);
        Assert.AreEqual(PositionSide.Both, order.PositionSide);
        Assert.AreEqual(TimeInForce.GoodTilCanceled, order.TimeInForce);
        Assert.AreEqual(0.002m, order.Quantity);
        Assert.AreEqual(37_000.0m, order.TriggerPrice);
        Assert.AreEqual(TriggerPriceType.MarkPrice, order.TriggerPriceType);
        Assert.IsTrue(order.ReduceOnly);
        Assert.IsFalse(order.ClosePosition);
    }

    [TestMethod]
    public void TheOrderTypeIsReadFromTheInnerORatherThanTheOuterOne()
    {
        // 外層的 o 是物件,內層還有一個 o 是委託類型字串。同名不同層,讀錯一層拿到的是一個型別不符的
        // 元素而不是例外 —— 這是整則事件最容易讀錯的一處。
        // The outer o is an object and the inner o is the order type string. Same name, two levels; reading
        // the wrong one yields an element of the wrong kind rather than an exception, and it is the easiest
        // thing in this event to get wrong.
        var order = BinanceUserDataReader.Read(UserDataSamples.AlgoNew)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!
            .ConditionalOrder;

        Assert.AreEqual(ConditionalOrderType.StopMarket, order.ConditionalOrderType);
    }

    [TestMethod]
    public void AnUntriggeredOrderHasNoTriggeredOrderIdAndNoTriggerTime()
    {
        var order = BinanceUserDataReader.Read(UserDataSamples.AlgoNew)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!
            .ConditionalOrder;

        // ai 未觸發時是空字串,tt 是 0。照抄會讓上層拿空字串去查單,並看到一個 1970 年的觸發時間。
        // ai is an empty string and tt is 0 before the trigger. Passing them through sends the caller to look
        // up "" and shows a trigger time in 1970.
        Assert.IsNull(order.TriggeredOrderId);
        Assert.IsNull(order.TriggeredAt);
        Assert.IsTrue(order.IsOpen);
        Assert.IsFalse(order.IsFinal);
    }

    [TestMethod]
    public void ATriggeredOrderCarriesTheRealOrderIdAndTheTriggerTime()
    {
        var order = BinanceUserDataReader.Read(UserDataSamples.AlgoTriggered)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!
            .ConditionalOrder;

        Assert.AreEqual(ConditionalOrderStatus.Triggered, order.Status);

        // 這個編號是把條件單接回一般委託與成交的唯一線索:成交、手續費、實際成交價都掛在它底下。
        // This id is the only link back to ordinary orders and fills: executions, fees, and the actual fill
        // price all hang off it.
        Assert.AreEqual("8886900", order.TriggeredOrderId);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117385000), order.TriggeredAt);
        Assert.IsTrue(order.IsOpen, "觸發後的委託還沒成交完,緊急出場仍需撤掉它。");
    }

    [TestMethod]
    public void ACancelledOrderReachesATerminalState()
    {
        var order = BinanceUserDataReader.Read(UserDataSamples.AlgoCanceled)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!
            .ConditionalOrder;

        Assert.AreEqual(ConditionalOrderStatus.Canceled, order.Status);
        Assert.IsTrue(order.IsFinal);
        Assert.IsFalse(order.IsOpen);
    }

    [TestMethod]
    public void ARejectionCarriesItsReasonBecauseNothingElseWill()
    {
        var update = BinanceUserDataReader.Read(UserDataSamples.AlgoRejected)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!;

        Assert.AreEqual(ConditionalOrderStatus.Rejected, update.ConditionalOrder.Status);

        // CONDITIONAL_ORDER_TRIGGER_REJECT 自 2025-12-15 起棄用,拒絕原因改放進這個事件的 rm。
        // 事後再查那張條件單只會得到一個「已拒絕」,看不出為什麼 —— 這個欄位只有這一次機會。
        // CONDITIONAL_ORDER_TRIGGER_REJECT was retired on 2025-12-15 and rejection reasons moved into this
        // event's rm. Looking the order up afterwards yields a bare "rejected"; this field has one chance.
        Assert.AreEqual("Reduce Only reject", update.RejectReason);
        Assert.AreEqual("REJECTED", update.RawStatus);
    }

    [TestMethod]
    public void AnEmptyRejectReasonIsReportedAsNoReason()
    {
        var update = BinanceUserDataReader.Read(UserDataSamples.AlgoNew)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!;

        // rm 在沒有被拒的事件上是空字串。空字串代表「沒有原因」,不是「原因是空的」。
        // rm is an empty string on an event that is not a rejection: that means there is no reason rather than
        // that the reason is blank.
        Assert.IsNull(update.RejectReason);
    }

    [TestMethod]
    public void TheRawStatusIsKeptSoAnUnmappedValueCanStillBeSeen()
    {
        var update = BinanceUserDataReader.Read(UserDataSamples.AlgoCanceled)
            .GetValueOrThrow()
            .ConditionalOrderUpdate!;

        Assert.AreEqual("CANCELED", update.RawStatus);
    }

    [TestMethod]
    public void AnUnmappableAlgoStatusFailsTheFrameRatherThanPassingThrough()
    {
        var read = BinanceUserDataReader.Read(UserDataSamples.AlgoUnknownStatus);

        // 既不算有效、也不算終態的停損會讓對帳永遠等不到結局,而帳面上看起來一切正常。
        // A stop that counts as neither live nor final leaves reconciliation waiting for an outcome that never
        // comes, while the books look entirely plausible.
        Assert.IsTrue(read.IsFailure);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("X", field);
    }

    [TestMethod]
    public void AConditionalOrderEventProducesNoOrderUpdateAndNoFill()
    {
        var evt = BinanceUserDataReader.Read(UserDataSamples.AlgoTriggered).GetValueOrThrow();

        // 觸發之後那張實際委託會由交易所另外推一則 ORDER_TRADE_UPDATE,兩者靠 TriggeredOrderId 接起來。
        // 在這裡順手生一張 Order 出來,會讓同一件事在兩條串流上各出現一次而且對不起來。
        // The real order arrives as its own ORDER_TRADE_UPDATE and the two are joined through
        // TriggeredOrderId. Manufacturing an Order here would publish one event twice, in two versions that
        // do not agree.
        Assert.IsNull(evt.OrderUpdate);
        Assert.IsNull(evt.Fill);
        Assert.IsNull(evt.Account);
        Assert.IsNull(evt.MarginWarning);
    }

    [TestMethod]
    public void TheEventTimeComesFromTheFrameRatherThanFromTheClock()
    {
        var evt = BinanceUserDataReader.Read(UserDataSamples.AlgoNew).GetValueOrThrow();

        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117383005), evt.EventTime);
        Assert.AreEqual(evt.EventTime, evt.ConditionalOrderUpdate!.Timestamp);
    }

    [TestMethod]
    public void AFrameWithoutTheAlgoObjectFails()
    {
        var read = BinanceUserDataReader.Read("""{"e":"ALGO_UPDATE","T":1789117383000,"E":1789117383005}""");

        Assert.IsTrue(read.IsFailure);
    }
}
