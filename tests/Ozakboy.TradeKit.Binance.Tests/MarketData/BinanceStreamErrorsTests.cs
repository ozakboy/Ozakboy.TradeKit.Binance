using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.MarketData;

/// <summary>
/// 連線層失敗到交易所中立錯誤的對映。
/// Mapping connection-layer failures onto exchange-neutral errors.
/// </summary>
[TestClass]
public sealed class BinanceStreamErrorsTests
{
    private static readonly string[] Streams = ["btcusdt@kline_1m", "ethusdt@kline_1m"];

    [TestMethod]
    [DataRow(WebSocketErrorCodes.SubscriptionReplayFailed)]
    [DataRow(WebSocketErrorCodes.SubscriptionNotFound)]
    [DataRow(WebSocketErrorCodes.OptionsInvalid)]
    [DataRow(WebSocketErrorCodes.InvalidState)]
    public void SubscriptionLevelFailuresMapToSubscriptionFailed(string code)
    {
        var mapped = Map(new Error(code, "訊息 / message", ErrorCategory.Network));

        Assert.AreEqual(TradeErrorCodes.SubscriptionFailed, mapped.Code, code);
    }

    [TestMethod]
    [DataRow(WebSocketErrorCodes.ConnectFailed)]
    [DataRow(WebSocketErrorCodes.ConnectTimeout)]
    [DataRow(WebSocketErrorCodes.ConnectionLost)]
    [DataRow(WebSocketErrorCodes.IdleTimeout)]
    [DataRow(WebSocketErrorCodes.ReconnectExhausted)]
    [DataRow(WebSocketErrorCodes.Unrecoverable)]
    [DataRow(WebSocketErrorCodes.NotConnected)]
    [DataRow(WebSocketErrorCodes.MessageTooLarge)]
    [DataRow(WebSocketErrorCodes.SendFailed)]
    [DataRow(WebSocketErrorCodes.Cancelled)]
    public void ConnectionLevelFailuresMapToStreamDisconnected(string code)
    {
        var mapped = Map(new Error(code, "訊息 / message", ErrorCategory.Network));

        Assert.AreEqual(TradeErrorCodes.StreamDisconnected, mapped.Code, code);
    }

    [TestMethod]
    public void AnUnknownConnectionCodeIsNotGuessedAt()
    {
        // 對不上的代碼不猜分類,也不硬塞進最近的那一格。猜錯的代價是消費端照著錯的語意做決定,
        // 而原始代碼還留在診斷資料裡,查得出來。
        // An unmapped code is neither guessed at nor forced into the nearest slot: guessing wrong has the
        // consumer acting on the wrong meaning, and the original code is still in the diagnostics either way.
        var mapped = Map(new Error("ws.something_new", "訊息 / message", ErrorCategory.Unexpected));

        Assert.AreEqual(TradeErrorCodes.UnknownExchangeError, mapped.Code);
        Assert.IsTrue(mapped.TryGetData(WebSocketErrorDataKeys.InnerCode, out var inner));
        Assert.AreEqual("ws.something_new", inner);
    }

    [TestMethod]
    public void TheCategoryIsCarriedAcrossUnchanged()
    {
        // 分類是「缺口」與「結束」唯一的判斷依據。重新歸類會讓 IsTransient 說謊,
        // 消費端就會對著一條已經死掉的串流永遠等下去,或是把一次普通的重連當成終局。
        // The category is the only thing separating a gap from an ending. Re-classifying makes IsTransient
        // lie, leaving the consumer waiting for ever on a dead stream, or treating a routine reconnect as the
        // end.
        var transient = Map(new Error(
            WebSocketErrorCodes.ConnectionLost,
            "斷線 / lost",
            ErrorCategory.Network));

        var terminal = Map(new Error(
            WebSocketErrorCodes.ReconnectExhausted,
            "放棄 / exhausted",
            ErrorCategory.Exhausted));

        Assert.IsTrue(transient.IsTransient);
        Assert.AreEqual(ErrorCategory.Network, transient.Category);

        Assert.IsFalse(terminal.IsTransient);
        Assert.AreEqual(ErrorCategory.Exhausted, terminal.Category);
    }

    [TestMethod]
    public void TheOriginalExceptionSurvivesTheTranslation()
    {
        var cause = new InvalidOperationException("底層原因 / underlying cause");

        var mapped = Map(new Error(WebSocketErrorCodes.ConnectFailed, "連不上 / failed", ErrorCategory.Network)
        {
            Exception = cause,
        });

        Assert.AreSame(cause, mapped.Exception);
    }

    [TestMethod]
    public void ADropReportSaysHowManyWereLostAndIsTransient()
    {
        // 丟棄在 Ozakboy.WebSockets 只出現在事件與統計上,await foreach 裡看不到。被丟掉的很可能
        // 正是一根已收盤的 K 線,而那是唯一會被策略拿去下單的那種。
        // A drop surfaces in Ozakboy.WebSockets only on an event and in the statistics, never inside an
        // await foreach. What was dropped may well be a closed candle, the only kind a strategy trades on.
        var error = BinanceStreamErrors.MessagesDropped(3, 12, Streams, BinanceEndpoints.Testnet);

        Assert.AreEqual(TradeErrorCodes.StreamDisconnected, error.Code);
        Assert.IsTrue(error.IsTransient);
        Assert.IsTrue(error.Message.Contains('3', StringComparison.Ordinal), error.Message);
        Assert.IsTrue(error.Message.Contains("12", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void EveryFailureNamesTheEnvironmentAndTheStreams()
    {
        var error = BinanceStreamErrors.MessagesDropped(1, 1, Streams, BinanceEndpoints.Testnet);

        Assert.IsTrue(error.TryGetData(BinanceErrorDataKeys.Environment, out var environment));
        Assert.AreEqual(BinanceEndpoints.Testnet.DisplayName, environment);
        Assert.IsTrue(error.TryGetData(BinanceStreamErrorDataKeys.StreamNames, out var streams));
        Assert.AreEqual("btcusdt@kline_1m,ethusdt@kline_1m", streams);
    }

    [TestMethod]
    public void AFailureWithNoStreamsCarriesNoStreamNames()
    {
        var error = BinanceStreamErrors.Decorate(
            Error.Network("test.code", "訊息 / message"),
            [],
            BinanceEndpoints.Mainnet);

        Assert.IsFalse(error.TryGetData(BinanceStreamErrorDataKeys.StreamNames, out _));
        Assert.IsTrue(error.TryGetData(BinanceErrorDataKeys.Environment, out var environment));
        Assert.AreEqual(BinanceEndpoints.Mainnet.DisplayName, environment);
    }

    [TestMethod]
    public void AMalformedMessageWithNoTextCarriesNoSnippet()
    {
        var error = BinanceStreamErrors.MalformedStreamMessage(
            "說明 / explanation",
            null,
            Streams,
            BinanceEndpoints.Testnet);

        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, error.Code);
        Assert.IsFalse(error.TryGetData(BinanceStreamErrorDataKeys.MessageSnippet, out _));
    }

    [TestMethod]
    public void NullArgumentsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceStreamErrors.FromWebSocketError(null!, Streams, BinanceEndpoints.Testnet));

        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceStreamErrors.Decorate(null!, Streams, BinanceEndpoints.Testnet));
    }

    private static Error Map(Error webSocketError) =>
        BinanceStreamErrors.FromWebSocketError(webSocketError, Streams, BinanceEndpoints.Testnet);
}
