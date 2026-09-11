using System.Globalization;

using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 把連線層與幣安控制訊息的失敗,翻成交易所中立的錯誤。
/// Translates connection-layer and Binance control-message failures into exchange-neutral errors.
/// </summary>
/// <remarks>
/// <para>
/// 消費端要分得出兩件事:<b>「有缺口,還在重連」</b>與<b>「這條串流結束了」</b>。
/// 前者該記錄後繼續等,後者該重建訂閱或讓策略停手。分辨的方式是
/// <see cref="Error.IsTransient"/> —— <c>Ozakboy.WebSockets</c> 的終局失敗用
/// <see cref="ErrorCategory.Exhausted"/> 表達,這裡原樣保留分類,不做任何「都算網路問題」的壓平。
/// A consumer has to tell <b>"a gap, still reconnecting"</b> from <b>"this stream has ended"</b>: the first
/// means log and keep waiting, the second means rebuild the subscription or stand the strategy down. The test
/// is <see cref="Error.IsTransient"/>; <c>Ozakboy.WebSockets</c> marks its terminal failures with
/// <see cref="ErrorCategory.Exhausted"/>, and the category is carried across unchanged rather than flattened
/// into one catch-all network failure.
/// </para>
/// <para>
/// 原始的 <c>ws.*</c> 代碼留在 <see cref="WebSocketErrorDataKeys.InnerCode"/> 裡。翻譯之後只剩一句中立訊息,
/// 查問題時仍然需要知道到底是閒置逾時、重放訂閱失敗,還是握手就沒成功。
/// The original <c>ws.*</c> code stays in <see cref="WebSocketErrorDataKeys.InnerCode"/>. Translation leaves
/// only a neutral sentence, and diagnosis still needs to know whether this was an idle timeout, a failed
/// subscription replay, or a handshake that never completed.
/// </para>
/// </remarks>
internal static class BinanceStreamErrors
{
    /// <summary>
    /// 把 <c>Ozakboy.WebSockets</c> 的失敗翻成交易所中立的錯誤。
    /// Translates an <c>Ozakboy.WebSockets</c> failure into an exchange-neutral error.
    /// </summary>
    /// <param name="webSocketError">連線層的失敗。The connection-layer failure.</param>
    /// <param name="streamNames">這條連線訂了哪些串流。The streams this connection carries.</param>
    /// <param name="endpoints">端點組合,用於標示環境。The endpoint set, used to name the environment.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    public static Error FromWebSocketError(
        Error webSocketError,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(webSocketError);

        var code = webSocketError.Code switch
        {
            WebSocketErrorCodes.SubscriptionReplayFailed
                or WebSocketErrorCodes.SubscriptionNotFound
                or WebSocketErrorCodes.OptionsInvalid
                or WebSocketErrorCodes.InvalidState => TradeErrorCodes.SubscriptionFailed,
            WebSocketErrorCodes.ConnectFailed
                or WebSocketErrorCodes.ConnectTimeout
                or WebSocketErrorCodes.ConnectionLost
                or WebSocketErrorCodes.IdleTimeout
                or WebSocketErrorCodes.ReconnectExhausted
                or WebSocketErrorCodes.Unrecoverable
                or WebSocketErrorCodes.NotConnected
                or WebSocketErrorCodes.MessageTooLarge
                or WebSocketErrorCodes.SendFailed
                or WebSocketErrorCodes.Cancelled => TradeErrorCodes.StreamDisconnected,
            _ => TradeErrorCodes.UnknownExchangeError,
        };

        // 分類原樣沿用。這是「缺口 vs 結束」唯一的判斷依據,任何重新歸類都會讓 IsTransient 說謊。
        // The category is carried across untouched. It is the only thing separating a gap from an ending, and
        // any re-classification here makes IsTransient lie.
        var error = new Error(code, webSocketError.Message, webSocketError.Category)
        {
            Exception = webSocketError.Exception,
        };

        return Decorate(error, streamNames, endpoints)
            .WithData(WebSocketErrorDataKeys.InnerCode, webSocketError.Code);
    }

    /// <summary>
    /// 把幣安控制訊息的失敗回覆翻成錯誤。
    /// Translates a Binance control-message rejection into an error.
    /// </summary>
    /// <param name="apiCode">回覆裡的代碼。The code in the reply.</param>
    /// <param name="apiMessage">回覆裡的訊息。The message in the reply.</param>
    /// <param name="streamNames">這條連線訂了哪些串流。The streams this connection carries.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 先交給 <see cref="BinanceErrorMapper"/>,好處是訊息格式與 <c>binanceCode</c>／<c>binanceMessage</c>
    /// 這兩個診斷鍵與 REST 那一側完全一致。串流的控制訊息另有自己的代碼空間(實測 <c>code:2</c> 代表
    /// 請求格式不合法),REST 的對映表認不得它們而會落到
    /// <see cref="TradeErrorCodes.UnknownExchangeError"/>;只有在那種情況下才改標成
    /// <see cref="TradeErrorCodes.SubscriptionFailed"/> —— 認得出來的代碼(例如 <c>-1121</c> 沒有這個交易對)
    /// 保留原本更精確的對映,那比「訂閱失敗」有用得多。
    /// The mapping goes through <see cref="BinanceErrorMapper"/> first so that the message format and the
    /// <c>binanceCode</c> and <c>binanceMessage</c> diagnostic keys match the REST side exactly. Stream control
    /// messages have a code space of their own — <c>code:2</c> was measured to mean a malformed request — which
    /// the REST table does not recognise and therefore lands on
    /// <see cref="TradeErrorCodes.UnknownExchangeError"/>. Only in that case is the code re-labelled
    /// <see cref="TradeErrorCodes.SubscriptionFailed"/>; a recognised code such as <c>-1121</c> for an unknown
    /// symbol keeps its sharper mapping, which says far more than "the subscription failed".
    /// </remarks>
    public static Error SubscriptionRejected(
        int apiCode,
        string? apiMessage,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints)
    {
        var mapped = BinanceErrorMapper.Map(apiCode, apiMessage, BinanceStreamNames.CombinedStreamPath, endpoints);

        if (string.Equals(mapped.Code, TradeErrorCodes.UnknownExchangeError, StringComparison.Ordinal))
        {
            mapped = new Error(TradeErrorCodes.SubscriptionFailed, mapped.Message, ErrorCategory.Validation)
            {
                Exception = mapped.Exception,
                Data = mapped.Data,
            };
        }

        return Decorate(mapped, streamNames, endpoints);
    }

    /// <summary>
    /// 建立「串流訊息無法解析」的錯誤。
    /// Creates a failure for a stream message that could not be read.
    /// </summary>
    /// <param name="reason">說明為什麼讀不出來,中英雙語。A bilingual explanation of why.</param>
    /// <param name="message">原始訊息,會被截斷後放進診斷資料。The raw message, truncated into the data.</param>
    /// <param name="streamNames">這條連線訂了哪些串流。The streams this connection carries.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 解析失敗<b>一定</b>要浮上串流,不能就地丟掉。丟掉的話,幣安哪天多送一種事件或改了欄位名,
    /// 症狀會是「資料量慢慢變少」而不是任何錯誤 —— 而變少多少、從哪一天開始,事後查不出來。
    /// A parse failure <b>must</b> surface on the stream rather than being dropped. Dropping it means that the
    /// day Binance adds an event type or renames a field, the symptom is "slightly less data" rather than any
    /// error at all — with no way to tell afterwards how much less, or since when.
    /// </remarks>
    public static Error MalformedStreamMessage(
        string reason,
        string? message,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints)
    {
        var error = BinanceErrors.MalformedResponse(reason);

        if (!string.IsNullOrEmpty(message))
        {
            error = error.WithData(BinanceStreamErrorDataKeys.MessageSnippet, Snippet(message));
        }

        return Decorate(error, streamNames, endpoints);
    }

    /// <summary>
    /// 建立「佇列已滿,訊息被丟棄」的錯誤。
    /// Creates a failure reporting that messages were dropped because the queue was full.
    /// </summary>
    /// <param name="droppedSinceLastReport">自上次回報以來丟掉幾則。How many were dropped since the last report.</param>
    /// <param name="droppedTotal">這條連線累計丟掉幾則。The running total for this connection.</param>
    /// <param name="streamNames">這條連線訂了哪些串流。The streams this connection carries.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <remarks>
    /// <c>Ozakboy.WebSockets</c> 的丟棄本來只出現在事件與統計上,消費端在 <c>await foreach</c> 裡看不到。
    /// 但被丟掉的很可能正是一根已收盤的 K 線,而那是唯一會被策略拿去下單的那種;
    /// 少了它,策略不會報錯,只會少做一次該做的事。所以這裡把丟棄補成串流上的一筆失敗。
    /// Drops in <c>Ozakboy.WebSockets</c> surface only on an event and in the statistics, where an
    /// <c>await foreach</c> never sees them. Yet the message dropped may well be a closed candle — the only
    /// kind a strategy trades on — and losing one raises no error, it just silently skips a trade that should
    /// have happened. The drop is therefore re-published here as a failure on the stream itself.
    /// </remarks>
    public static Error MessagesDropped(
        long droppedSinceLastReport,
        long droppedTotal,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints)
    {
        var error = Error.Network(
            TradeErrorCodes.StreamDisconnected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"消費速度跟不上,佇列已滿並丟棄 {droppedSinceLastReport} 則行情訊息(這條連線累計 {droppedTotal} 則),資料有缺口。The consumer fell behind: {droppedSinceLastReport} market messages were dropped from a full queue ({droppedTotal} in total on this connection), so the data has a gap."));

        return Decorate(error, streamNames, endpoints);
    }

    /// <summary>
    /// 補上環境名稱與串流名稱這兩個診斷資料。
    /// Adds the environment name and the stream names as diagnostic data.
    /// </summary>
    /// <param name="error">原始錯誤。The original error.</param>
    /// <param name="streamNames">這條連線訂了哪些串流。The streams this connection carries.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>補過資料的錯誤。The error with the data attached.</returns>
    /// <remarks>
    /// 同一套程式碼會同時連 Testnet 與主網,也會同時開好幾條串流。錯誤訊息說不出是哪一邊、哪一條,
    /// 查問題就得先猜。
    /// The same code talks to both the testnet and production and keeps several streams open at once. A
    /// failure that cannot say which one leaves whoever reads it guessing first.
    /// </remarks>
    public static Error Decorate(Error error, IReadOnlyList<string> streamNames, BinanceEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(streamNames);
        ArgumentNullException.ThrowIfNull(endpoints);

        var decorated = error.WithData(BinanceErrorDataKeys.Environment, endpoints.DisplayName);

        return streamNames.Count == 0
            ? decorated
            : decorated.WithData(BinanceStreamErrorDataKeys.StreamNames, string.Join(',', streamNames));
    }

    private static string Snippet(string message) =>
        message.Length <= 256 ? message : string.Concat(message.AsSpan(0, 256), "…");
}
