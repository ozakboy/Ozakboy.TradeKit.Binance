using System.Text.Json;

using Ozakboy.TradeKit.Binance.MarketData;

namespace Ozakboy.TradeKit.Binance.Tests.MarketData;

/// <summary>
/// 控制訊息的組裝。
/// Building the control messages.
/// </summary>
[TestClass]
public sealed class BinanceStreamCommandsTests
{
    [TestMethod]
    public void SubscribeMatchesTheShapeMeasuredOnTheTestnet()
    {
        var payload = BinanceStreamCommands.Subscribe(1, ["btcusdt@kline_1m", "ethusdt@markPrice@1s"]);

        Assert.AreEqual(
            """{"method":"SUBSCRIBE","params":["btcusdt@kline_1m","ethusdt@markPrice@1s"],"id":1}""",
            payload);
    }

    [TestMethod]
    public void UnsubscribeMatchesTheSameShape()
    {
        var payload = BinanceStreamCommands.Unsubscribe(7, ["btcusdt@kline_1m"]);

        Assert.AreEqual("""{"method":"UNSUBSCRIBE","params":["btcusdt@kline_1m"],"id":7}""", payload);
    }

    [TestMethod]
    public void ListSubscriptionsCarriesNoParameters()
    {
        // 這則同時是應用層心跳。多帶一個 params 欄位不會被拒,但每三十秒送一次的訊息沒有理由變胖。
        // This doubles as the application heartbeat. An extra params field would not be rejected, but a
        // message sent every thirty seconds has no reason to carry one.
        var payload = BinanceStreamCommands.ListSubscriptions(42);

        Assert.AreEqual("""{"method":"LIST_SUBSCRIPTIONS","id":42}""", payload);
    }

    [TestMethod]
    public void AnEmptyStreamListStillProducesAValidCommand()
    {
        var payload = BinanceStreamCommands.Subscribe(3, []);

        Assert.AreEqual("""{"method":"SUBSCRIBE","params":[],"id":3}""", payload);
    }

    [TestMethod]
    public void QuotesInsideAStreamNameAreEscaped()
    {
        // 名稱在上游已經過字元檢查,但控制訊息只要有一處引號沒跳脫,幣安就會回 error 並關掉連線,
        // 接著是一輪本來不需要的重連。用 JSON 寫入器產生,這件事就不可能發生。
        // Names are character-checked upstream, but one unescaped quote in a control message earns an error
        // reply and a closed connection, followed by a reconnect that need never have happened. Writing the
        // JSON with a writer makes that impossible.
        var payload = BinanceStreamCommands.Subscribe(1, ["""btc"usdt@kline_1m"""]);

        // 用解析回來的值斷言,而不是比對某一種跳脫寫法:重點是產出的仍是合法 JSON、
        // 而且原始字串一字不差,至於編碼器選了哪一種跳脫形式無關緊要。
        // The assertion reads the value back rather than matching one particular escape form: what matters is
        // that the output is still valid JSON carrying the exact original string, not which form the encoder
        // happened to choose.
        using var document = JsonDocument.Parse(payload);

        Assert.AreEqual(
            """btc"usdt@kline_1m""",
            document.RootElement.GetProperty("params")[0].GetString());
    }
}
