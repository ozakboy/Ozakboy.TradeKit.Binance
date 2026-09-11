using System.Net.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 假連線要送出的一則訊息。
/// One frame for the fake connection to play.
/// </summary>
/// <remarks>
/// 文字訊息可以直接用字串寫,二進位訊息要明講。幣安的行情推送一律是文字,
/// 二進位只出現在「協定變了」或「連到了別的東西」的時候,而那正是需要被看見的情況。
/// A text frame can be written as a bare string while a binary one has to be spelled out. Binance market
/// pushes are always text; binary only appears when the protocol has changed or the client is talking to
/// something else entirely, which is exactly the case that has to be visible.
/// </remarks>
internal sealed record FakeWebSocketFrame(string Payload, WebSocketMessageType Type)
{
    public static implicit operator FakeWebSocketFrame(string payload) => Text(payload);

    /// <summary>
    /// 建立文字訊息。
    /// Creates a text frame.
    /// </summary>
    /// <param name="payload">訊息內容。The frame content.</param>
    /// <returns>文字訊息。The text frame.</returns>
    public static FakeWebSocketFrame Text(string payload) => new(payload, WebSocketMessageType.Text);

    /// <summary>
    /// 建立二進位訊息。
    /// Creates a binary frame.
    /// </summary>
    /// <param name="payload">訊息內容,會以 UTF-8 編碼送出。The content, sent as UTF-8 bytes.</param>
    /// <returns>二進位訊息。The binary frame.</returns>
    public static FakeWebSocketFrame Binary(string payload) => new(payload, WebSocketMessageType.Binary);
}
