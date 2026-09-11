using System.Text.Json.Nodes;

using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests.MarketData;

/// <summary>
/// 串流訊息的判讀,樣本全部取自 Testnet 實際收到的訊息。
/// Reading stream frames, from samples actually received from the testnet.
/// </summary>
[TestClass]
public sealed class BinanceStreamReaderTests
{
    private static readonly string[] KlineStreams = ["btcusdt@kline_1m"];

    private static readonly string[] MarkPriceStreams = ["ethusdt@markPrice@1s"];

    /// <summary>
    /// <b>這一條鎖的是整個實作最容易出錯、錯了最難發現的一點。</b>
    /// 同一根 K 線在收盤前後的推送,除了 <c>x</c> 之外每個數字都一樣;<c>x</c> 若被填成定值,
    /// 價格、成交量、時間全部照樣正確,沒有任何斷言會掉 —— 而策略會在同一根 K 線內反覆進出場,
    /// 或永遠不進場。回測用的是收盤資料,所以回測一路綠燈。
    /// <b>This test locks down the single easiest thing to get wrong and the hardest to notice.</b> Across the
    /// pushes of one candle, everything but <c>x</c> is identical: hard-code the flag and the prices, volumes,
    /// and timestamps all stay correct and no other assertion fails — while the strategy either enters and
    /// exits repeatedly inside one candle or never enters at all. A backtest runs on closed data and stays
    /// green through both.
    /// </summary>
    [TestMethod]
    public void TheSameCandleIsOpenUntilItsClosingPushArrives()
    {
        var first = ReadKline(MarketDataSamples.KlineInProgressFirst);
        var second = ReadKline(MarketDataSamples.KlineInProgressSecond);
        var closed = ReadKline(MarketDataSamples.KlineClosed);

        var expectedOpenTime = DateTimeOffset.FromUnixTimeMilliseconds(MarketDataSamples.StreamCandleOpenTimeMs);

        Assert.AreEqual(expectedOpenTime, first.OpenTime);
        Assert.AreEqual(expectedOpenTime, second.OpenTime);
        Assert.AreEqual(expectedOpenTime, closed.OpenTime, "三筆推送必須是同一根 K 線,否則這條測試沒有意義。");

        Assert.IsFalse(first.IsClosed);
        Assert.IsFalse(second.IsClosed);
        Assert.IsTrue(closed.IsClosed);

        // 收盤那一筆沒有帶來任何新價格,只是把旗標翻過來。這就是為什麼漏掉 x 不會在數字上留下痕跡。
        // The closing push brings no new price at all; it only flips the flag. That is precisely why getting
        // x wrong leaves no trace in any number.
        Assert.AreEqual(second.Open, closed.Open);
        Assert.AreEqual(second.High, closed.High);
        Assert.AreEqual(second.Low, closed.Low);
        Assert.AreEqual(second.Close, closed.Close);
        Assert.AreEqual(second.Volume, closed.Volume);
        Assert.AreEqual(second.TradeCount, closed.TradeCount);
    }

    [TestMethod]
    public void KlineFieldsAreMappedFromTheAbbreviatedNames()
    {
        var candle = ReadKline(MarketDataSamples.KlineClosed);

        Assert.AreEqual("BTCUSDT", candle.Symbol);
        Assert.AreEqual(KlineInterval.OneMinute, candle.Interval);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(MarketDataSamples.StreamCandleOpenTimeMs),
            candle.OpenTime);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(MarketDataSamples.StreamCandleCloseTimeMs),
            candle.CloseTime);
        Assert.AreEqual(77332.00m, candle.Open);
        Assert.AreEqual(77366.20m, candle.High);
        Assert.AreEqual(77324.20m, candle.Low);
        Assert.AreEqual(77334.00m, candle.Close);
        Assert.AreEqual(4.6853m, candle.Volume);
        Assert.AreEqual(362436.804520m, candle.QuoteVolume);
        Assert.AreEqual(184, candle.TradeCount);
        Assert.IsTrue(candle.IsClosed);
    }

    [TestMethod]
    public void TimestampsAreUtc()
    {
        var candle = ReadKline(MarketDataSamples.KlineClosed);

        Assert.AreEqual(TimeSpan.Zero, candle.OpenTime.Offset);
        Assert.AreEqual(TimeSpan.Zero, candle.CloseTime.Offset);
    }

    [TestMethod]
    public void AFrameWithoutTheCombinedEnvelopeIsReadTheSameWay()
    {
        // 走 /ws 的訊息沒有外層包裝。兩種都收,判讀就不會因為有人改了連線路徑而整組壞掉,
        // 而那種壞法是「連得上、訂閱受理、每一則都解析失敗」。
        // Frames from /ws carry no envelope. Accepting both shapes means a change of path cannot break reading
        // outright — a break that would look like "connected, subscribed, every frame fails".
        var candle = ReadKline(MarketDataSamples.KlineRawNoEnvelope);

        Assert.AreEqual("BTCUSDT", candle.Symbol);
        Assert.IsTrue(candle.IsClosed);
        Assert.AreEqual(130, candle.TradeCount);
    }

    [TestMethod]
    public void MarkPriceFieldsAreMappedFromTheAbbreviatedNames()
    {
        var read = BinanceStreamReader.ReadMarkPrice(
            MarketDataSamples.MarkPrice,
            MarkPriceStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Payload, read.Kind);

        var update = read.Value!;

        Assert.AreEqual("ETHUSDT", update.Symbol);
        Assert.AreEqual(2475.24000000m, update.MarkPrice);
        Assert.AreEqual(2476.33488372m, update.IndexPrice);
        Assert.AreEqual(0.00007346m, update.FundingRate);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789142400000L), update.NextFundingTime);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789117371000L), update.Timestamp);
    }

    [TestMethod]
    public void AMarkPriceWithoutFundingKeepsNullRatherThanZero()
    {
        // 交割合約沒有資金費率。填零會讓「沒有資金費率」看起來像「費率剛好是零」,
        // 而持倉成本的估算會因此少算一整塊。
        // A delivery contract has no funding rate. Zero would make "no funding" look like "the rate happens to
        // be zero", and a carry-cost estimate would silently lose a whole component.
        var node = JsonNode.Parse(MarketDataSamples.MarkPrice)!;
        var data = node["data"]!.AsObject();

        data.Remove("r");
        data["T"] = 0L;

        var read = BinanceStreamReader.ReadMarkPrice(
            node.ToJsonString(),
            MarkPriceStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Payload, read.Kind);
        Assert.IsNull(read.Value!.FundingRate);
        Assert.IsNull(read.Value!.NextFundingTime);
    }

    [TestMethod]
    [DataRow("s")]
    [DataRow("p")]
    [DataRow("E")]
    public void AMarkPriceMissingARequiredFieldFails(string field)
    {
        var node = JsonNode.Parse(MarketDataSamples.MarkPrice)!;

        node["data"]!.AsObject().Remove(field);

        var read = BinanceStreamReader.ReadMarkPrice(
            node.ToJsonString(),
            MarkPriceStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind, field);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
    }

    [TestMethod]
    [DataRow("k")]
    [DataRow("s")]
    [DataRow("i")]
    [DataRow("t")]
    [DataRow("T")]
    [DataRow("o")]
    [DataRow("h")]
    [DataRow("l")]
    [DataRow("c")]
    [DataRow("v")]
    [DataRow("q")]
    [DataRow("n")]
    [DataRow("x")]
    public void AKlineMissingARequiredFieldFails(string field)
    {
        var read = BinanceStreamReader.ReadKline(
            WithoutKlineField(field),
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind, field);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
    }

    [TestMethod]
    public void AMissingClosedFlagIsAFailureRatherThanADefault()
    {
        // 這是上面那張表裡最重要的一格,單獨再寫一次:x 若被當成「沒有就算 false」,
        // 功能會靜默停擺(策略永遠等不到收盤的 K 線),而日誌與連線狀態一切正常。
        // The most important row of the table above, restated on its own: defaulting a missing x to false is a
        // silent shutdown — the strategy waits for a closed candle that never comes — with a healthy log and a
        // healthy connection throughout.
        var read = BinanceStreamReader.ReadKline(
            WithoutKlineField("x"),
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.IsNotNull(read.Error);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual("x", field);
    }

    [TestMethod]
    public void AnUnsupportedIntervalFails()
    {
        var node = JsonNode.Parse(MarketDataSamples.KlineClosed)!;

        node["data"]!["k"]!.AsObject()["i"] = "7m";

        var read = BinanceStreamReader.ReadKline(node.ToJsonString(), KlineStreams, BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(TradeErrorCodes.UnsupportedInterval, read.Error?.Code);
    }

    [TestMethod]
    public void ASubscribeAcknowledgementIsIgnored()
    {
        var read = BinanceStreamReader.ReadKline(
            MarketDataSamples.SubscribeAck,
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Ignored, read.Kind);
    }

    [TestMethod]
    public void TheHeartbeatReplyIsIgnored()
    {
        // 心跳每隔幾十秒就來一次。若它被歸到失敗那一邊,消費端的串流會被例行回覆灌滿假警報,
        // 真正的斷線就淹在裡面了。
        // The heartbeat fires every few dozen seconds. Putting its reply on the failure side would flood the
        // consumer's stream with routine false alarms and bury the real disconnects among them.
        var read = BinanceStreamReader.ReadKline(
            MarketDataSamples.ListSubscriptionsReply,
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Ignored, read.Kind);
    }

    [TestMethod]
    public void ARejectedControlMessageBecomesASubscriptionFailure()
    {
        var read = BinanceStreamReader.ReadKline(
            MarketDataSamples.ControlError,
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(TradeErrorCodes.SubscriptionFailed, read.Error?.Code);
        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.ApiCode, out var code));
        Assert.AreEqual("2", code);
    }

    [TestMethod]
    public void ARejectionWithAKnownBinanceCodeKeepsTheSharperMapping()
    {
        // 認得出來的代碼比「訂閱失敗」有用得多:-1121 直接說了是這個交易對不存在。
        // A recognised code says far more than "the subscription failed": -1121 names the missing symbol.
        var read = BinanceStreamReader.ReadKline(
            """{"error":{"code":-1121,"msg":"Invalid symbol."},"id":9}""",
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(TradeErrorCodes.SymbolNotFound, read.Error?.Code);
    }

    [TestMethod]
    public void ARejectionWithoutCodeOrMessageStillFails()
    {
        var read = BinanceStreamReader.ReadKline(
            """{"error":{},"id":9}""",
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(TradeErrorCodes.SubscriptionFailed, read.Error?.Code);
    }

    [TestMethod]
    public void AMarkPriceFrameOnAKlineStreamFails()
    {
        // 每個訂閱都有自己的連線,所以這不該發生;真的發生就是協定變了,而協定變了必須被看見。
        // 安靜跳過會讓「資料變少」成為唯一的症狀。
        // Each subscription owns its connection so this cannot happen normally; if it does, the protocol has
        // changed and that must be visible. Skipping quietly would leave "less data" as the only symptom.
        var read = BinanceStreamReader.ReadKline(
            MarketDataSamples.MarkPrice,
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
    }

    [TestMethod]
    public void AFrameWithoutAnEventTypeFails()
    {
        var read = BinanceStreamReader.ReadKline("""{"hello":"world"}""", KlineStreams, BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
    }

    [TestMethod]
    public void AFrameThatIsNotAnObjectFails()
    {
        var read = BinanceStreamReader.ReadKline("[1,2,3]", KlineStreams, BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
    }

    [TestMethod]
    public void UnparsableTextFailsRatherThanBeingDropped()
    {
        var read = BinanceStreamReader.ReadKline("not json at all", KlineStreams, BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, read.Error?.Code);
        Assert.IsTrue(read.Error!.TryGetData(BinanceStreamErrorDataKeys.MessageSnippet, out var snippet));
        Assert.AreEqual("not json at all", snippet);
    }

    [TestMethod]
    public void AVeryLongUnparsableFrameIsTruncatedInTheDiagnostics()
    {
        var read = BinanceStreamReader.ReadKline(
            new string('x', 4096),
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Failed, read.Kind);
        Assert.IsTrue(read.Error!.TryGetData(BinanceStreamErrorDataKeys.MessageSnippet, out var snippet));
        Assert.IsNotNull(snippet);
        Assert.IsLessThan(400, snippet!.Length);
    }

    [TestMethod]
    public void FailuresCarryTheEnvironmentAndTheStreamNames()
    {
        // 同一套程式碼會同時連 Testnet 與主網。錯誤訊息說不出是哪一邊,查問題就得先猜。
        // The same code talks to both the testnet and production; a failure that cannot say which one leaves
        // whoever reads it guessing first.
        var read = BinanceStreamReader.ReadKline(
            WithoutKlineField("x"),
            KlineStreams,
            BinanceEndpoints.Testnet);

        Assert.IsTrue(read.Error!.TryGetData(BinanceErrorDataKeys.Environment, out var environment));
        Assert.AreEqual(BinanceEndpoints.Testnet.DisplayName, environment);
        Assert.IsTrue(read.Error!.TryGetData(BinanceStreamErrorDataKeys.StreamNames, out var streams));
        Assert.AreEqual("btcusdt@kline_1m", streams);
    }

    private static Kline ReadKline(string json)
    {
        var read = BinanceStreamReader.ReadKline(json, KlineStreams, BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceStreamReadKind.Payload, read.Kind, read.Error?.Message);

        return read.Value!;
    }

    private static string WithoutKlineField(string field)
    {
        var node = JsonNode.Parse(MarketDataSamples.KlineClosed)!;
        var data = node["data"]!.AsObject();

        if (string.Equals(field, "k", StringComparison.Ordinal))
        {
            data.Remove("k");
        }
        else
        {
            data["k"]!.AsObject().Remove(field);
        }

        return node.ToJsonString();
    }
}
