namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 一則串流訊息被判讀成什麼。
/// What a stream message turned out to be.
/// </summary>
internal enum BinanceStreamReadKind
{
    /// <summary>
    /// 是一筆行情資料,<c>Value</c> 有值。
    /// A market data payload; <c>Value</c> is set.
    /// </summary>
    Payload = 0,

    /// <summary>
    /// 是控制訊息的成功回覆(訂閱受理、訂閱清單),不必交給消費端。
    /// A successful control reply — a subscribe acknowledgement or a subscription list — that the consumer has
    /// no use for.
    /// </summary>
    Ignored = 1,

    /// <summary>
    /// 讀不出來或是失敗回覆,<c>Error</c> 有值,必須交給消費端。
    /// Unreadable, or a rejection; <c>Error</c> is set and must reach the consumer.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// 判讀一則串流訊息的結果。
/// The outcome of reading one stream message.
/// </summary>
/// <typeparam name="T">行情資料的型別。The market data type.</typeparam>
/// <remarks>
/// 刻意不用 <see cref="Result{T}"/>:那個型別只有成功與失敗兩種狀態,表達不出「這則訊息不是資料,
/// 但也沒有出錯」。控制訊息的回覆每次訂閱與每次心跳都會出現,若被迫歸到失敗那一邊,
/// 消費端的 <c>await foreach</c> 會被例行的心跳回覆灌滿假警報,真正的失敗就淹沒在裡面了。
/// <see cref="Result{T}"/> is deliberately not used: it has only success and failure, with no room for "this
/// message is not data and nothing went wrong". Control replies arrive on every subscribe and every heartbeat,
/// and forcing them onto the failure side would flood the consumer's <c>await foreach</c> with routine false
/// alarms, burying the failures that matter.
/// </remarks>
internal readonly record struct BinanceStreamRead<T>
    where T : class
{
    private BinanceStreamRead(BinanceStreamReadKind kind, T? value, Error? error)
    {
        Kind = kind;
        Value = value;
        Error = error;
    }

    /// <summary>
    /// 這則訊息被判讀成什麼。
    /// What the message turned out to be.
    /// </summary>
    public BinanceStreamReadKind Kind { get; }

    /// <summary>
    /// 行情資料,僅在 <see cref="BinanceStreamReadKind.Payload"/> 時有值。
    /// The market data, set only for <see cref="BinanceStreamReadKind.Payload"/>.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// 失敗原因,僅在 <see cref="BinanceStreamReadKind.Failed"/> 時有值。
    /// The failure, set only for <see cref="BinanceStreamReadKind.Failed"/>.
    /// </summary>
    public Error? Error { get; }

    /// <summary>
    /// 建立「這是一筆行情資料」的結果。
    /// Creates a payload outcome.
    /// </summary>
    /// <param name="value">行情資料。The market data.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceStreamRead<T> Payload(T value) => new(BinanceStreamReadKind.Payload, value, null);

    /// <summary>
    /// 建立「這則訊息不必交給消費端」的結果。
    /// Creates an ignored outcome.
    /// </summary>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceStreamRead<T> Ignored() => new(BinanceStreamReadKind.Ignored, null, null);

    /// <summary>
    /// 建立「這則訊息是失敗」的結果。
    /// Creates a failed outcome.
    /// </summary>
    /// <param name="error">失敗原因。The failure.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceStreamRead<T> Failed(Error error) => new(BinanceStreamReadKind.Failed, null, error);
}
