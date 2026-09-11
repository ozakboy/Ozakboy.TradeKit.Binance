namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 行情串流錯誤附帶的診斷資料鍵。
/// The diagnostic data keys attached to market stream failures.
/// </summary>
/// <remarks>
/// 另立一組而不是併進 <c>BinanceErrorDataKeys</c>,理由與 <see cref="BinanceMarketDataPaths"/> 相同:
/// 串流專屬的新增不與其他同時進行的工作搶同一個檔案。連線層自己的鍵沿用
/// <c>Ozakboy.WebSockets</c> 的 <c>WebSocketErrorDataKeys</c>,不重新發明一套。
/// These live apart from <c>BinanceErrorDataKeys</c> for the same reason as
/// <see cref="BinanceMarketDataPaths"/>: stream-specific additions do not contend for a file that other work is
/// touching. Keys belonging to the connection layer are reused from the <c>WebSocketErrorDataKeys</c> of
/// <c>Ozakboy.WebSockets</c> rather than reinvented here.
/// </remarks>
public static class BinanceStreamErrorDataKeys
{
    /// <summary>
    /// 這筆失敗牽涉到的串流名稱,多個以逗號相接。
    /// The stream names involved in the failure, comma separated when there is more than one.
    /// </summary>
    public const string StreamNames = "binanceStreams";

    /// <summary>
    /// 無法解析的訊息片段,已截斷。
    /// A truncated snippet of the message that could not be parsed.
    /// </summary>
    /// <remarks>
    /// 只留片段而不是整則訊息:行情訊息本身不含憑證,但把整則原文塞進錯誤物件會讓日誌被單一則
    /// 深度快照灌爆,而查問題需要的只是前面那幾十個字元。
    /// A snippet rather than the whole message: market frames carry no credentials, but putting the full text
    /// into an error object lets a single depth snapshot flood the log, and the first few dozen characters are
    /// all that diagnosis needs.
    /// </remarks>
    public const string MessageSnippet = "binanceStreamMessage";
}
