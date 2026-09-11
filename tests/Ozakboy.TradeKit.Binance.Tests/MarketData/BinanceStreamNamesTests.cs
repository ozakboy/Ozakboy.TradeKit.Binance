using Ozakboy.TradeKit.Binance.MarketData;

namespace Ozakboy.TradeKit.Binance.Tests.MarketData;

/// <summary>
/// 串流名稱與路徑的組裝。
/// Building stream names and paths.
/// </summary>
[TestClass]
public sealed class BinanceStreamNamesTests
{
    [TestMethod]
    public void KlineStreamNameLowerCasesTheSymbol()
    {
        var name = BinanceStreamNames.Kline("BTCUSDT", KlineInterval.FifteenMinutes);

        Assert.AreEqual("btcusdt@kline_15m", name.GetValueOrThrow());
    }

    [TestMethod]
    public void KlineStreamNameKeepsTheUpperCaseMonthInterval()
    {
        // 整串轉小寫會把一個月的訂閱變成一分鐘,訂閱照樣成功、資料照樣進來,週期卻完全不對。
        // Lower-casing the whole name turns a monthly subscription into a one-minute one: it still succeeds and
        // still delivers, on an interval nobody asked for.
        var monthly = BinanceStreamNames.Kline("BTCUSDT", KlineInterval.OneMonth);
        var minutely = BinanceStreamNames.Kline("BTCUSDT", KlineInterval.OneMinute);

        Assert.AreEqual("btcusdt@kline_1M", monthly.GetValueOrThrow());
        Assert.AreEqual("btcusdt@kline_1m", minutely.GetValueOrThrow());
        Assert.AreNotEqual(monthly.GetValueOrThrow(), minutely.GetValueOrThrow());
    }

    [TestMethod]
    public void KlineStreamNameRejectsAnUnspecifiedInterval()
    {
        var name = BinanceStreamNames.Kline("BTCUSDT", KlineInterval.Unspecified);

        Assert.IsTrue(name.IsFailure);
        Assert.AreEqual(TradeErrorCodes.UnsupportedInterval, name.Error?.Code);
    }

    [TestMethod]
    public void KlineStreamNameRejectsAnUndefinedInterval()
    {
        var name = BinanceStreamNames.Kline("BTCUSDT", (KlineInterval)987);

        Assert.IsTrue(name.IsFailure);
        Assert.AreEqual(TradeErrorCodes.UnsupportedInterval, name.Error?.Code);
    }

    [TestMethod]
    public void KlineStreamNameReportsAnInvalidSymbol()
    {
        var name = BinanceStreamNames.Kline("BTC/USDT", KlineInterval.OneMinute);

        Assert.IsTrue(name.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, name.Error?.Code);
    }

    [TestMethod]
    public void MarkPriceStreamNameDefaultsToTheOneSecondVariant()
    {
        Assert.AreEqual("btcusdt@markPrice@1s", BinanceStreamNames.MarkPrice("BTCUSDT").GetValueOrThrow());
        Assert.AreEqual(
            "btcusdt@markPrice",
            BinanceStreamNames.MarkPrice("BTCUSDT", fastUpdates: false).GetValueOrThrow());
    }

    [TestMethod]
    public void MarkPriceStreamNameReportsAnInvalidSymbol()
    {
        var name = BinanceStreamNames.MarkPrice("  ");

        Assert.IsTrue(name.IsFailure);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, name.Error?.Code);
    }

    [TestMethod]
    public void SymbolNormalizationAcceptsDeliveryContracts()
    {
        // 交割合約的代碼帶底線,擋掉底線會讓整個季度合約無法訂閱。
        // Delivery contracts carry an underscore in the symbol; rejecting it would make every quarterly
        // contract unsubscribable.
        Assert.AreEqual("btcusdt_250926", BinanceStreamNames.NormalizeSymbol("BTCUSDT_250926").GetValueOrThrow());
    }

    [TestMethod]
    [DataRow("BTC@USDT")]
    [DataRow("BTC/USDT")]
    [DataRow("BTC USDT")]
    [DataRow("BTC-USDT")]
    public void SymbolNormalizationRejectsSeparatorCharacters(string symbol)
    {
        // @ 與 / 是串流名稱與串流清單的分隔符號,混進代碼會把一個訂閱悄悄變成兩個或一個不存在的。
        // The @ and / characters separate stream names and stream lists; letting one through quietly turns one
        // subscription into two, or into one that does not exist.
        var normalized = BinanceStreamNames.NormalizeSymbol(symbol);

        Assert.IsTrue(normalized.IsFailure, symbol);
        Assert.AreEqual(TradeErrorCodes.InvalidQuery, normalized.Error?.Code);
    }

    [TestMethod]
    public void SymbolNormalizationRejectsBlankInput()
    {
        Assert.IsTrue(BinanceStreamNames.NormalizeSymbol(string.Empty).IsFailure);
        Assert.IsTrue(BinanceStreamNames.NormalizeSymbol("   ").IsFailure);
    }

    [TestMethod]
    public void CombinedStreamUriAppendsTheVerifiedPath()
    {
        var uri = BinanceStreamNames.CombinedStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri);

        Assert.AreEqual("wss://stream.binancefuture.com/stream", uri.AbsoluteUri);
    }

    [TestMethod]
    public void CombinedStreamUriKeepsAnExistingPathPrefix()
    {
        // 指向代理或重播伺服器時,相對位址解析會把既有路徑當成檔名丟掉,結果靜默打到別的位址。
        // Against a proxy or a replay server, relative resolution would discard the existing path and quietly
        // dial somewhere else.
        var uri = BinanceStreamNames.CombinedStreamUri(new Uri("wss://proxy.example/binance", UriKind.Absolute));

        Assert.AreEqual("wss://proxy.example/binance/stream", uri.AbsoluteUri);
    }

    [TestMethod]
    public void CombinedStreamUriToleratesATrailingSlash()
    {
        var uri = BinanceStreamNames.CombinedStreamUri(new Uri("wss://proxy.example/binance/", UriKind.Absolute));

        Assert.AreEqual("wss://proxy.example/binance/stream", uri.AbsoluteUri);
    }

    [TestMethod]
    public void RawStreamUriCarriesTheStreamNameInThePath()
    {
        var uri = BinanceStreamNames.RawStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri, "btcusdt@kline_1m");

        Assert.AreEqual("wss://stream.binancefuture.com/ws/btcusdt@kline_1m", uri.AbsoluteUri);
    }

    [TestMethod]
    public void RawStreamUriRejectsABlankStreamName()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => BinanceStreamNames.RawStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri, "  "));
    }

    [TestMethod]
    public void StreamUriBuildersRejectANullBase()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => BinanceStreamNames.CombinedStreamUri(null!));
    }
}
