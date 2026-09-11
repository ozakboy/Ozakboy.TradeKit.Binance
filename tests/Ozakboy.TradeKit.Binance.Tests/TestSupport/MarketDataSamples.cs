namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 2026-09-11 從幣安 Testnet 行情串流實際收到的訊息,逐字保留。
/// Frames received verbatim from the Binance testnet market streams on 2026-09-11.
/// </summary>
/// <remarks>
/// <para>
/// 來源:<c>wss://stream.binancefuture.com/stream</c>,連線後以
/// <c>{"method":"SUBSCRIBE","params":["btcusdt@kline_1m","ethusdt@markPrice@1s"],"id":1}</c> 訂閱。
/// 公開行情端點,不需要憑證。內容<b>一字未改</b>,包含幣安自己在 <c>k</c> 物件裡留的空白 ——
/// 手動整理過的樣本會讓測試變成「驗自己寫的東西」,而真正會出錯的地方正是自己沒想到的那些格式細節。
/// Source: <c>wss://stream.binancefuture.com/stream</c>, subscribed with
/// <c>{"method":"SUBSCRIBE","params":["btcusdt@kline_1m","ethusdt@markPrice@1s"],"id":1}</c>. The endpoint is
/// public and needs no credentials. The text is <b>unedited</b>, down to the spaces Binance leaves inside the
/// <c>k</c> object: a tidied sample turns a test into a check of one's own typing, and the formatting details
/// nobody thought of are exactly the ones that break.
/// </para>
/// <para>
/// <see cref="KlineInProgressFirst"/>、<see cref="KlineInProgressSecond"/> 與 <see cref="KlineClosed"/>
/// 是<b>同一根</b> K 線(開盤時間都是 <c>1789117320000</c>)。前兩筆的 <c>x</c> 是 <see langword="false"/>,
/// 最後一筆是 <see langword="true"/>,而 OHLCV 完全相同 —— 這正是「收盤那一筆不會帶來新價格,
/// 只會把旗標翻過來」的實證,也是為什麼漏掉 <c>x</c> 的對映不會在任何數字上留下痕跡。
/// <see cref="KlineInProgressFirst"/>, <see cref="KlineInProgressSecond"/>, and <see cref="KlineClosed"/> are
/// the <b>same</b> candle, all opening at <c>1789117320000</c>. The first two carry <c>x</c> as
/// <see langword="false"/> and the last as <see langword="true"/>, with identical OHLCV — which is the evidence
/// that the closing push brings no new price, only the flipped flag, and therefore why getting the <c>x</c>
/// mapping wrong leaves no trace in any number.
/// </para>
/// </remarks>
internal static class MarketDataSamples
{
    /// <summary>訂閱受理的回覆。The subscribe acknowledgement.</summary>
    public const string SubscribeAck = """{"result":null,"id":1}""";

    /// <summary>列出訂閱的回覆,本套件用它當心跳。The list-subscriptions reply, used here as the heartbeat.</summary>
    public const string ListSubscriptionsReply =
        """{"result":["btcusdt@kline_1m","ethusdt@markPrice@1s"],"id":2}""";

    /// <summary>
    /// 控制訊息被拒的回覆。實測:送出一個不存在的 <c>method</c> 就會收到這個。
    /// A rejected control message, measured by sending a <c>method</c> that does not exist.
    /// </summary>
    public const string ControlError =
        """{"error":{"code":2,"msg":"Invalid request: unknown variant `NOT_A_METHOD`, expected one of `SUBSCRIBE`, `UNSUBSCRIBE`, `LIST_SUBSCRIPTIONS`, `SET_PROPERTY`, `GET_PROPERTY`"},"id":4}""";

    /// <summary>同一根 K 線的第一筆未收盤推送(組合串流,含外層包裝)。The first in-progress push of the candle.</summary>
    public const string KlineInProgressFirst =
        """{"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1789117378259,"s":"BTCUSDT","k":{"t":1789117320000, "T":1789117379999, "s":"BTCUSDT", "i":"1m", "f":536672051, "L":536672232, "o":"77332.00", "c":"77334.00", "h":"77366.20", "l":"77324.20", "v":"4.6831", "n":182, "x":false, "q":"362266.669720", "V":"3.3291", "Q":"257559.731160", "B":"0"}}}""";

    /// <summary>同一根 K 線的第二筆未收盤推送。The second in-progress push of the same candle.</summary>
    public const string KlineInProgressSecond =
        """{"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1789117378975,"s":"BTCUSDT","k":{"t":1789117320000, "T":1789117379999, "s":"BTCUSDT", "i":"1m", "f":536672051, "L":536672234, "o":"77332.00", "c":"77334.00", "h":"77366.20", "l":"77324.20", "v":"4.6853", "n":184, "x":false, "q":"362436.804520", "V":"3.3291", "Q":"257559.731160", "B":"0"}}}""";

    /// <summary>同一根 K 線的收盤推送,<c>x</c> 為 <see langword="true"/>。The closing push of the same candle.</summary>
    public const string KlineClosed =
        """{"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1789117380188,"s":"BTCUSDT","k":{"t":1789117320000, "T":1789117379999, "s":"BTCUSDT", "i":"1m", "f":536672051, "L":536672234, "o":"77332.00", "c":"77334.00", "h":"77366.20", "l":"77324.20", "v":"4.6853", "n":184, "x":true, "q":"362436.804520", "V":"3.3291", "Q":"257559.731160", "B":"0"}}}""";

    /// <summary>
    /// 走 <c>/ws</c> 原始串流時的 K 線推送,<b>沒有</b>外層包裝。
    /// A kline push from the raw <c>/ws</c> path, with <b>no</b> envelope.
    /// </summary>
    public const string KlineRawNoEnvelope =
        """{"e":"kline","E":1789116060236,"s":"BTCUSDT","k":{"t":1789116000000, "T":1789116059999, "s":"BTCUSDT", "i":"1m", "f":536668876, "L":536669005, "o":"77354.60", "c":"77354.60", "h":"77354.60", "l":"77322.40", "v":"0.3923", "n":130, "x":true, "q":"30342.256040", "V":"0.2671", "Q":"20661.398200", "B":"0"}}""";

    /// <summary>標記價推送(組合串流,含外層包裝)。A mark price push from the combined stream.</summary>
    public const string MarkPrice =
        """{"stream":"ethusdt@markPrice@1s","data":{"e":"markPriceUpdate","E":1789117371000,"s":"ETHUSDT","p":"2475.24000000","ap":"2475.24000000","P":"2477.68512984","i":"2476.33488372","r":"0.00007346","T":1789142400000,"st":1}}""";

    /// <summary>
    /// <c>GET /fapi/v1/klines?symbol=BTCUSDT&amp;interval=1m&amp;limit=2</c> 的回應,同樣是實際呼叫取得的。
    /// 呼叫當下的伺服器時間為 <c>1789116140000</c>,落在第二根的收盤時間 <c>1789116179999</c> <b>之前</b>
    /// —— 也就是最後一根還沒收盤。
    /// The response of <c>GET /fapi/v1/klines?symbol=BTCUSDT&amp;interval=1m&amp;limit=2</c>, also from a real
    /// call. The server time at that moment was <c>1789116140000</c>, <b>before</b> the second candle's close
    /// time of <c>1789116179999</c>: the last candle had not closed.
    /// </summary>
    public const string RestKlines =
        """
        [
         [1789116060000,"77322.40","77356.80","77322.40","77356.80","478.3328",1789116119999,"37001617.536540",195,"476.0681","36826505.347260","0"],
         [1789116120000,"77356.80","77366.20","77322.40","77366.20","1959.1654",1789116179999,"151563096.035780",145,"1958.6834","151525816.166760","0"]
        ]
        """;

    /// <summary>
    /// 這批樣本裡 K 線的開盤時間(毫秒 Unix epoch)。
    /// The open time of the candle in these samples, in Unix epoch milliseconds.
    /// </summary>
    public const long StreamCandleOpenTimeMs = 1789117320000L;

    /// <summary>
    /// 這批樣本裡 K 線的收盤時間(毫秒 Unix epoch)。
    /// The close time of the candle in these samples, in Unix epoch milliseconds.
    /// </summary>
    public const long StreamCandleCloseTimeMs = 1789117379999L;

    /// <summary>
    /// 呼叫 <c>klines</c> 當下的伺服器時間(毫秒 Unix epoch)。
    /// The server time at the moment the <c>klines</c> call was made, in Unix epoch milliseconds.
    /// </summary>
    public const long RestKlinesServerTimeMs = 1789116140000L;
}
