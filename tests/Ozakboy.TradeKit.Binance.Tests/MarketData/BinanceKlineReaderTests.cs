using System.Globalization;

using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests.MarketData;

/// <summary>
/// 歷史 K 線回應的判讀。
/// Reading the historical kline response.
/// </summary>
[TestClass]
public sealed class BinanceKlineReaderTests
{
    private static readonly DateTimeOffset ServerTime =
        DateTimeOffset.FromUnixTimeMilliseconds(MarketDataSamples.RestKlinesServerTimeMs);

    [TestMethod]
    public void PositionalElementsAreMappedInOrder()
    {
        var candles = Read(ServerTime);

        Assert.AreEqual(2, candles.Count);

        var first = candles[0];

        Assert.AreEqual("BTCUSDT", first.Symbol);
        Assert.AreEqual(KlineInterval.OneMinute, first.Interval);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789116060000L), first.OpenTime);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789116119999L), first.CloseTime);
        Assert.AreEqual(77322.40m, first.Open);
        Assert.AreEqual(77356.80m, first.High);
        Assert.AreEqual(77322.40m, first.Low);
        Assert.AreEqual(77356.80m, first.Close);
        Assert.AreEqual(478.3328m, first.Volume);
        Assert.AreEqual(37001617.536540m, first.QuoteVolume);
        Assert.AreEqual(195, first.TradeCount);
    }

    /// <summary>
    /// <b>REST 回應沒有收盤旗標,而最後一根通常還在跳動。</b>
    /// 這一條用實際呼叫當下的伺服器時間鎖住「前一根已收盤、最後一根未收盤」。
    /// 若把整串都當成已收盤,最後一根的收盤價其實只是查詢當下的最新價,存進歷史之後,
    /// 之後的回測會用一根永遠不會再出現的假 K 線,而且看起來完全正常。
    /// <b>The REST response has no closed flag and the last candle is usually still ticking.</b> This locks
    /// down "the earlier candle is closed, the last one is not" using the server time of the real call.
    /// Treating them all as closed makes the last close price merely the latest price at query time; stored as
    /// history, later backtests run on a candle that never existed and nothing about it looks wrong.
    /// </summary>
    [TestMethod]
    public void TheLastCandleIsStillOpenWhenItsCloseTimeHasNotPassed()
    {
        var candles = Read(ServerTime);

        Assert.IsTrue(candles[0].IsClosed, "第一根的收盤時間早於呼叫時間,應判為已收盤。");
        Assert.IsFalse(candles[1].IsClosed, "最後一根的收盤時間晚於呼叫時間,還在跳動,不可判為已收盤。");
    }

    [TestMethod]
    public void EveryCandleIsClosedOnceTimeHasMovedPastThemAll()
    {
        var candles = Read(DateTimeOffset.FromUnixTimeMilliseconds(1789116180000L));

        Assert.IsTrue(candles[0].IsClosed);
        Assert.IsTrue(candles[1].IsClosed);
    }

    [TestMethod]
    public void ACandleIsStillOpenAtTheExactCloseTimestamp()
    {
        // 幣安的收盤時間是「最後一毫秒」,那一毫秒本身還屬於這根 K 線。
        // The Binance close time is the last millisecond of the candle, and that millisecond still belongs to it.
        var candles = Read(DateTimeOffset.FromUnixTimeMilliseconds(1789116119999L));

        Assert.IsFalse(candles[0].IsClosed);
    }

    [TestMethod]
    public void AnEmptyResponseIsAnEmptyList()
    {
        var candles = BinanceKlineReader.Read("[]", "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsSuccess);
        Assert.AreEqual(0, candles.GetValueOrThrow().Count);
    }

    [TestMethod]
    public void UnparsableTextFails()
    {
        var candles = BinanceKlineReader.Read("{", "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, candles.Error?.Code);
    }

    [TestMethod]
    public void AResponseThatIsNotAnArrayFails()
    {
        var candles = BinanceKlineReader.Read(
            """{"code":-1121,"msg":"Invalid symbol."}""",
            "BTCUSDT",
            KlineInterval.OneMinute,
            ServerTime);

        Assert.IsTrue(candles.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, candles.Error?.Code);
    }

    [TestMethod]
    public void ACandleThatIsNotAnArrayFails()
    {
        var candles = BinanceKlineReader.Read("""[{"t":1}]""", "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, candles.Error?.Code);
    }

    [TestMethod]
    public void ATooShortCandleFails()
    {
        var candles = BinanceKlineReader.Read("[[1,2,3]]", "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, candles.Error?.Code);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    public void ACandleWithAnUnreadableElementFails(int index)
    {
        var elements = new string[]
        {
            "1789116060000", "\"77322.40\"", "\"77356.80\"", "\"77322.40\"", "\"77356.80\"",
            "\"478.3328\"", "1789116119999", "\"37001617.536540\"", "195",
        };

        elements[index] = "null";

        var json = string.Create(CultureInfo.InvariantCulture, $"[[{string.Join(',', elements)}]]");
        var candles = BinanceKlineReader.Read(json, "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsFailure, json);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, candles.Error?.Code);
        Assert.IsTrue(candles.Error!.TryGetData(BinanceErrorDataKeys.Field, out var field));
        Assert.AreEqual(string.Create(CultureInfo.InvariantCulture, $"[{index}]"), field);
    }

    [TestMethod]
    public void ANonPositiveTimestampFails()
    {
        // 幣安對「沒有時間」的表示是 0。轉成 1970-01-01 會讓一根不存在的 K 線混進歷史序列的最前面,
        // 而任何以時間排序的計算都會被它拉歪。
        // Binance expresses "no timestamp" as 0; turning that into 1970-01-01 slips a candle that never
        // existed into the front of the series and skews anything that sorts by time.
        var json = "[[0,\"1\",\"1\",\"1\",\"1\",\"1\",1789116119999,\"1\",1]]";
        var candles = BinanceKlineReader.Read(json, "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsFailure);
    }

    [TestMethod]
    public void NumericPricesAreAcceptedAsWellAsStrings()
    {
        // 幣安一律回字串,但代理或重播伺服器不一定照辦,而數值形式本身沒有精度問題。
        // Binance always returns strings, though a proxy or a replay server may not, and the numeric form
        // carries no precision problem of its own.
        var json = "[[1789116060000,77322.40,77356.80,77322.40,77356.80,478.3328,1789116119999,37001617.53654,195]]";
        var candles = BinanceKlineReader.Read(json, "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsSuccess, candles.Error?.Message);
        Assert.AreEqual(77322.40m, candles.GetValueOrThrow()[0].Open);
    }

    [TestMethod]
    public void ScientificNotationIsRejectedRatherThanSilentlyLosingScale()
    {
        // Precision.TryParsePlain 刻意拒絕科學記號。看似成功卻已經失真的數值,拿去算下單量最危險。
        // Precision.TryParsePlain deliberately rejects exponent notation: a value that parses successfully but
        // has already lost its scale is at its most dangerous feeding an order size.
        var json = "[[1789116060000,\"1E-8\",\"1\",\"1\",\"1\",\"1\",1789116119999,\"1\",1]]";
        var candles = BinanceKlineReader.Read(json, "BTCUSDT", KlineInterval.OneMinute, ServerTime);

        Assert.IsTrue(candles.IsFailure);
    }

    private static IReadOnlyList<Kline> Read(DateTimeOffset asOf) =>
        BinanceKlineReader
            .Read(MarketDataSamples.RestKlines, "BTCUSDT", KlineInterval.OneMinute, asOf)
            .GetValueOrThrow();
}
