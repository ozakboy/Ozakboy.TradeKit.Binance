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
    public void CombinedStreamUriDialsTheMarketRoute()
    {
        // 不帶路由的 /stream 在主網上握手成功、零資料(2026-09-12 實測),K 線與標記價屬 market 類。
        // The unprefixed /stream completes the handshake on production and delivers nothing (measured on
        // 2026-09-12); klines and mark prices are market-class data.
        var uri = BinanceStreamNames.CombinedStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri);

        Assert.AreEqual("wss://stream.binancefuture.com/market/stream", uri.AbsoluteUri);
        Assert.AreEqual(
            "wss://fstream.binance.com/market/stream",
            BinanceStreamNames.CombinedStreamUri(BinanceEndpoints.Mainnet.WebSocketBaseUri).AbsoluteUri);
    }

    [TestMethod]
    public void CombinedStreamUriKeepsAnExistingPathPrefix()
    {
        // 指向代理或重播伺服器時,相對位址解析會把既有路徑當成檔名丟掉,結果靜默打到別的位址。
        // Against a proxy or a replay server, relative resolution would discard the existing path and quietly
        // dial somewhere else.
        var uri = BinanceStreamNames.CombinedStreamUri(new Uri("wss://proxy.example/binance", UriKind.Absolute));

        Assert.AreEqual("wss://proxy.example/binance/market/stream", uri.AbsoluteUri);
    }

    [TestMethod]
    public void CombinedStreamUriToleratesATrailingSlash()
    {
        var uri = BinanceStreamNames.CombinedStreamUri(new Uri("wss://proxy.example/binance/", UriKind.Absolute));

        Assert.AreEqual("wss://proxy.example/binance/market/stream", uri.AbsoluteUri);
    }

    [TestMethod]
    public void UserDataStreamUriUsesThePrivateRouteAndTheDocumentedQueryForm()
    {
        var uri = BinanceStreamNames.UserDataStreamUri(
            BinanceEndpoints.Testnet.WebSocketBaseUri,
            "abc123",
            ["ORDER_TRADE_UPDATE", "ACCOUNT_UPDATE"]);

        Assert.AreEqual(
            "wss://stream.binancefuture.com/private/ws?listenKey=abc123&events=ORDER_TRADE_UPDATE/ACCOUNT_UPDATE",
            uri.AbsoluteUri);
    }

    [TestMethod]
    public void UserDataStreamUriKeepsTheCredentialOutOfThePath()
    {
        // 文件沒寫的 /private/ws/<listenKey> 也連得上,但這裡用文件的查詢字串形式。
        // The undocumented /private/ws/<listenKey> also connects, but the documented query form is what is used.
        var uri = BinanceStreamNames.UserDataStreamUri(
            BinanceEndpoints.Testnet.WebSocketBaseUri,
            "abc123",
            ["ORDER_TRADE_UPDATE"]);

        Assert.AreEqual("/private/ws", uri.AbsolutePath);
        Assert.DoesNotContain("abc123", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [TestMethod]
    public void UserDataStreamUriEscapesTheCredentialAndEachEventName()
    {
        // 憑證若含 + / = 之類的字元,不編碼就會被伺服器讀成別的東西(+ 變空白、& 切斷參數)。
        // A credential holding + / = and the like would otherwise be read as something else by the server — a
        // + turning into a space, an & cutting the parameter short.
        var uri = BinanceStreamNames.UserDataStreamUri(
            BinanceEndpoints.Testnet.WebSocketBaseUri,
            "a+b/c=d&e",
            ["ORDER TRADE", "ACCOUNT_UPDATE"]);

        Assert.AreEqual(
            "wss://stream.binancefuture.com/private/ws?listenKey=a%2Bb%2Fc%3Dd%26e&events=ORDER%20TRADE/ACCOUNT_UPDATE",
            uri.AbsoluteUri);
    }

    [TestMethod]
    public void UserDataStreamUriKeepsAnExistingPathPrefix()
    {
        var uri = BinanceStreamNames.UserDataStreamUri(
            new Uri("wss://proxy.example/binance/", UriKind.Absolute),
            "abc123",
            ["ORDER_TRADE_UPDATE", "ACCOUNT_UPDATE"]);

        Assert.AreEqual(
            "wss://proxy.example/binance/private/ws?listenKey=abc123&events=ORDER_TRADE_UPDATE/ACCOUNT_UPDATE",
            uri.AbsoluteUri);
    }

    [TestMethod]
    public void UserDataStreamUriRejectsAnEmptyEventList()
    {
        // 省略 events 的連線收不收得到事件實測不一致,不可依賴,所以空清單在組位址時就擋下。
        // 例外訊息也不可以帶出憑證。
        // Whether a connection without events receives anything proved inconsistent, so an empty list is
        // refused when the address is built — and the exception must not carry the credential either.
        var thrown = Assert.ThrowsExactly<ArgumentException>(
            () => BinanceStreamNames.UserDataStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri, "secret-key-123", []));

        Assert.DoesNotContain("secret-key-123", thrown.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void UserDataStreamUriRejectsABlankEventName()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => BinanceStreamNames.UserDataStreamUri(
                BinanceEndpoints.Testnet.WebSocketBaseUri,
                "abc123",
                ["ORDER_TRADE_UPDATE", " "]));
    }

    [TestMethod]
    public void UserDataStreamUriRejectsABlankCredentialAndNullArguments()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => BinanceStreamNames.UserDataStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri, "  ", ["A"]));

        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceStreamNames.UserDataStreamUri(null!, "abc123", ["A"]));

        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceStreamNames.UserDataStreamUri(BinanceEndpoints.Testnet.WebSocketBaseUri, "abc123", null!));
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
