using System.Text.Json;

namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 判讀幣安行情串流的訊息:拆掉組合串流的外層、辨識控制訊息回覆、把推送轉成抽象層的型別。
/// Reads the frames of the Binance market streams: unwraps the combined-stream envelope, recognises control
/// replies, and turns pushes into the abstraction's types.
/// </summary>
/// <remarks>
/// <para>
/// <b>K 線推送的欄位對映</b>(以 2026-09-11 Testnet 實際收到的訊息核對):
/// <b>The kline push field mapping</b>, checked against frames actually received from the testnet on
/// 2026-09-11:
/// </para>
/// <list type="table">
/// <listheader>
/// <term>幣安欄位 / Binance field</term>
/// <description>對映到 / Mapped to</description>
/// </listheader>
/// <item><term><c>e</c></term><description>事件型別,必須是 <c>kline</c>。The event type; must be <c>kline</c>.</description></item>
/// <item><term><c>E</c></term><description>事件時間,不進 <see cref="Kline"/>。The event time; not carried on <see cref="Kline"/>.</description></item>
/// <item><term><c>k.s</c></term><description><see cref="Kline.Symbol"/></description></item>
/// <item><term><c>k.i</c></term><description><see cref="Kline.Interval"/></description></item>
/// <item><term><c>k.t</c></term><description><see cref="Kline.OpenTime"/></description></item>
/// <item><term><c>k.T</c></term><description><see cref="Kline.CloseTime"/></description></item>
/// <item><term><c>k.o</c></term><description><see cref="Kline.Open"/></description></item>
/// <item><term><c>k.h</c></term><description><see cref="Kline.High"/></description></item>
/// <item><term><c>k.l</c></term><description><see cref="Kline.Low"/></description></item>
/// <item><term><c>k.c</c></term><description><see cref="Kline.Close"/>(未收盤時為目前最新價 / the latest price while open)</description></item>
/// <item><term><c>k.v</c></term><description><see cref="Kline.Volume"/></description></item>
/// <item><term><c>k.q</c></term><description><see cref="Kline.QuoteVolume"/></description></item>
/// <item><term><c>k.n</c></term><description><see cref="Kline.TradeCount"/></description></item>
/// <item><term><c>k.x</c></term><description><see cref="Kline.IsClosed"/></description></item>
/// <item><term><c>k.f</c>、<c>k.L</c>、<c>k.V</c>、<c>k.Q</c>、<c>k.B</c></term><description>首尾成交編號、主動買入量額、忽略欄位,抽象層沒有對應欄位,不對映。First and last trade ids, taker buy volumes, and the ignore field; the abstraction has nowhere to put them.</description></item>
/// </list>
/// <para>
/// <b><c>k.x</c> 缺席時一律判為失敗,絕不填預設值。</b> 這個欄位是「這根 K 線收盤了沒」,
/// 填 <see langword="false"/> 會讓所有 K 線都被策略略過(功能靜默停擺),填 <see langword="true"/>
/// 則會讓策略在同一根 K 線內反覆進出場。兩種猜法都不會有任何錯誤訊息,而回測用的是收盤資料,
/// 連回測都不會亮紅燈。
/// <b>A missing <c>k.x</c> is a failure and is never defaulted.</b> The field says whether the candle has
/// closed: defaulting it to <see langword="false"/> makes a strategy skip every candle, which is a silent
/// shutdown, and defaulting it to <see langword="true"/> makes the strategy enter and exit repeatedly inside a
/// single candle. Neither guess raises anything, and a backtest — which runs on closed data — stays green
/// through both.
/// </para>
/// <para>
/// <b>標記價推送的欄位對映</b>(與 Testnet 的 <c>/fapi/v1/premiumIndex</c> 逐欄位交叉核對過,
/// 該端點的欄位有完整名稱,可以確認縮寫的意思):
/// <b>The mark price field mapping</b>, cross-checked field by field against the testnet's
/// <c>/fapi/v1/premiumIndex</c>, whose fully named fields pin down what the abbreviations mean:
/// </para>
/// <list type="table">
/// <listheader>
/// <term>幣安欄位 / Binance field</term>
/// <description>對映到 / Mapped to</description>
/// </listheader>
/// <item><term><c>e</c></term><description>事件型別,必須是 <c>markPriceUpdate</c>。The event type; must be <c>markPriceUpdate</c>.</description></item>
/// <item><term><c>E</c></term><description><see cref="MarkPriceUpdate.Timestamp"/></description></item>
/// <item><term><c>s</c></term><description><see cref="MarkPriceUpdate.Symbol"/></description></item>
/// <item><term><c>p</c></term><description><see cref="MarkPriceUpdate.MarkPrice"/>(對應 <c>premiumIndex</c> 的 <c>markPrice</c>)</description></item>
/// <item><term><c>i</c></term><description><see cref="MarkPriceUpdate.IndexPrice"/>(對應 <c>indexPrice</c>)</description></item>
/// <item><term><c>r</c></term><description><see cref="MarkPriceUpdate.FundingRate"/>(對應 <c>lastFundingRate</c>)</description></item>
/// <item><term><c>T</c></term><description><see cref="MarkPriceUpdate.NextFundingTime"/>(對應 <c>nextFundingTime</c>;交割合約為 0,對映成 <see langword="null"/>)</description></item>
/// <item><term><c>P</c>、<c>ap</c>、<c>st</c></term><description>預估結算價與另外兩個文件未載明的欄位,抽象層沒有對應欄位,不對映。The estimated settle price and two fields the documentation does not describe; the abstraction has nowhere to put them.</description></item>
/// </list>
/// </remarks>
internal static class BinanceStreamReader
{
    private const string EventTypeField = "e";

    private const string EventTimeField = "E";

    private const string SymbolField = "s";

    private const string KlineField = "k";

    private const string KlineOpenTimeField = "t";

    private const string KlineCloseTimeField = "T";

    private const string KlineIntervalField = "i";

    private const string KlineOpenField = "o";

    private const string KlineHighField = "h";

    private const string KlineLowField = "l";

    private const string KlineCloseField = "c";

    private const string KlineVolumeField = "v";

    private const string KlineQuoteVolumeField = "q";

    private const string KlineTradeCountField = "n";

    private const string KlineIsClosedField = "x";

    private const string MarkPriceField = "p";

    private const string IndexPriceField = "i";

    private const string FundingRateField = "r";

    private const string NextFundingTimeField = "T";

    private const string KlineContext = "K 線串流 / kline stream";

    private const string MarkPriceContext = "標記價串流 / mark price stream";

    /// <summary>
    /// 判讀一則 K 線串流訊息。
    /// Reads one kline stream frame.
    /// </summary>
    /// <param name="json">訊息原文。The frame text.</param>
    /// <param name="streamNames">這條連線訂了哪些串流,用於錯誤的診斷資料。The streams on this connection.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceStreamRead<Kline> ReadKline(
        string json,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints) =>
        Read<Kline>(json, BinanceStreamNames.KlineEventType, KlineContext, ReadKlinePayload, streamNames, endpoints);

    /// <summary>
    /// 判讀一則標記價串流訊息。
    /// Reads one mark price stream frame.
    /// </summary>
    /// <param name="json">訊息原文。The frame text.</param>
    /// <param name="streamNames">這條連線訂了哪些串流,用於錯誤的診斷資料。The streams on this connection.</param>
    /// <param name="endpoints">端點組合。The endpoint set.</param>
    /// <returns>判讀結果。The outcome.</returns>
    public static BinanceStreamRead<MarkPriceUpdate> ReadMarkPrice(
        string json,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints) =>
        Read<MarkPriceUpdate>(
            json,
            BinanceStreamNames.MarkPriceEventType,
            MarkPriceContext,
            ReadMarkPricePayload,
            streamNames,
            endpoints);

    private static BinanceStreamRead<T> Read<T>(
        string json,
        string expectedEventType,
        string context,
        Func<JsonElement, string, Result<T>> readPayload,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints)
        where T : class
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return BinanceStreamRead<T>.Failed(
                BinanceStreamErrors.MalformedStreamMessage(
                    $"{context} 收到的訊息不是合法的 JSON:{exception.Message}。The frame received on the {context} is not valid JSON: {exception.Message}.",
                    json,
                    streamNames,
                    endpoints));
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return BinanceStreamRead<T>.Failed(
                    BinanceStreamErrors.MalformedStreamMessage(
                        $"{context} 收到的訊息不是 JSON 物件。The frame received on the {context} is not a JSON object.",
                        json,
                        streamNames,
                        endpoints));
            }

            if (root.TryGetProperty(BinanceStreamCommands.ErrorProperty, out var errorElement))
            {
                return BinanceStreamRead<T>.Failed(ReadControlError(errorElement, streamNames, endpoints));
            }

            // 控制訊息的成功回覆(訂閱受理、LIST_SUBSCRIPTIONS 的心跳回覆)在這裡就結束旅程。
            // 它們沒有 "e" 欄位,若繼續往下走會變成一筆「缺少欄位 e」的假失敗,而心跳每隔幾十秒就來一次。
            // Successful control replies — subscribe acknowledgements and the LIST_SUBSCRIPTIONS heartbeat —
            // end here. They have no "e" field, and carrying on would manufacture a "missing field e" failure
            // every time the heartbeat fires.
            if (root.TryGetProperty(BinanceStreamCommands.ResultProperty, out _))
            {
                return BinanceStreamRead<T>.Ignored();
            }

            var payload = Unwrap(root);

            if (!BinanceJson.TryGetString(payload, EventTypeField, out var eventType))
            {
                return BinanceStreamRead<T>.Failed(
                    BinanceStreamErrors.MalformedStreamMessage(
                        $"{context} 收到的訊息缺少事件型別欄位「{EventTypeField}」。The frame received on the {context} has no event type field \"{EventTypeField}\".",
                        json,
                        streamNames,
                        endpoints));
            }

            if (!string.Equals(eventType, expectedEventType, StringComparison.Ordinal))
            {
                // 每個訂閱都用自己的連線、只訂同一種串流,所以這種事不該發生。真的發生就是協定變了,
                // 而協定變了要被看見 —— 安靜跳過會讓「資料變少」成為唯一的症狀。
                // Each subscription owns its connection and carries one kind of stream, so this cannot happen
                // in normal operation. If it does, the protocol has changed, and that has to be visible:
                // skipping quietly would leave "less data than before" as the only symptom.
                return BinanceStreamRead<T>.Failed(
                    BinanceStreamErrors.MalformedStreamMessage(
                        $"{context} 收到非預期的事件型別「{eventType}」,預期為「{expectedEventType}」。The {context} received the unexpected event type \"{eventType}\" where \"{expectedEventType}\" was expected.",
                        json,
                        streamNames,
                        endpoints));
            }

            var read = readPayload(payload, context);

            // Error 在失敗結果上必定有值,可空性標註表達不了這個前提。
            // A failed result always carries an Error; the nullability annotation cannot say so.
            return read.TryGetValue(out var value)
                ? BinanceStreamRead<T>.Payload(value)
                : BinanceStreamRead<T>.Failed(
                    BinanceStreamErrors.Decorate(read.Error!, streamNames, endpoints));
        }
    }

    /// <summary>
    /// 拆掉組合串流的外層包裝。
    /// Strips the combined-stream envelope.
    /// </summary>
    /// <remarks>
    /// 走 <c>/stream</c> 的訊息長成 <c>{"stream":"btcusdt@kline_1m","data":{…}}</c>,走 <c>/ws</c> 則直接是內層物件。
    /// 兩種都收,判讀就不會因為有人改了連線路徑而整個壞掉 —— 而那種壞法是「連得上、訂閱受理、
    /// 每則訊息都解析失敗」,從連線狀態完全看不出來。
    /// A frame from <c>/stream</c> looks like <c>{"stream":"btcusdt@kline_1m","data":{…}}</c> while one from
    /// <c>/ws</c> is the inner object itself. Accepting both means a change of connection path cannot break
    /// reading outright — and that break would look like "connected, subscription accepted, every frame fails
    /// to parse", which the connection state says nothing about.
    /// </remarks>
    private static JsonElement Unwrap(JsonElement root) =>
        root.TryGetProperty(BinanceStreamNames.EnvelopeStreamProperty, out var streamElement)
        && streamElement.ValueKind == JsonValueKind.String
        && root.TryGetProperty(BinanceStreamNames.EnvelopeDataProperty, out var dataElement)
        && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : root;

    private static Error ReadControlError(
        JsonElement errorElement,
        IReadOnlyList<string> streamNames,
        BinanceEndpoints endpoints)
    {
        var code = BinanceJson.TryGetInt32(errorElement, BinanceStreamCommands.ErrorCodeProperty, out var parsed)
            ? parsed
            : 0;

        var message = BinanceJson.TryGetString(errorElement, BinanceStreamCommands.ErrorMessageProperty, out var text)
            ? text
            : null;

        return BinanceStreamErrors.SubscriptionRejected(code, message, streamNames, endpoints);
    }

    private static Result<Kline> ReadKlinePayload(JsonElement payload, string context)
    {
        if (!payload.TryGetProperty(KlineField, out var candle) || candle.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MissingField(KlineField, context);
        }

        if (!BinanceJson.TryGetString(candle, SymbolField, out var symbol))
        {
            return BinanceErrors.MissingField(SymbolField, context);
        }

        if (!BinanceJson.TryGetString(candle, KlineIntervalField, out var intervalText))
        {
            return BinanceErrors.MissingField(KlineIntervalField, context);
        }

        var interval = KlineIntervals.Parse(intervalText);

        if (!interval.TryGetValue(out var parsedInterval))
        {
            return interval.ToFailure<Kline>();
        }

        if (!BinanceJson.TryGetTimestamp(candle, KlineOpenTimeField, out var openTime))
        {
            return BinanceErrors.MissingField(KlineOpenTimeField, context);
        }

        if (!BinanceJson.TryGetTimestamp(candle, KlineCloseTimeField, out var closeTime))
        {
            return BinanceErrors.MissingField(KlineCloseTimeField, context);
        }

        if (!BinanceJson.TryGetDecimal(candle, KlineOpenField, out var open))
        {
            return BinanceErrors.MissingField(KlineOpenField, context);
        }

        if (!BinanceJson.TryGetDecimal(candle, KlineHighField, out var high))
        {
            return BinanceErrors.MissingField(KlineHighField, context);
        }

        if (!BinanceJson.TryGetDecimal(candle, KlineLowField, out var low))
        {
            return BinanceErrors.MissingField(KlineLowField, context);
        }

        if (!BinanceJson.TryGetDecimal(candle, KlineCloseField, out var close))
        {
            return BinanceErrors.MissingField(KlineCloseField, context);
        }

        if (!BinanceJson.TryGetDecimal(candle, KlineVolumeField, out var volume))
        {
            return BinanceErrors.MissingField(KlineVolumeField, context);
        }

        if (!BinanceJson.TryGetDecimal(candle, KlineQuoteVolumeField, out var quoteVolume))
        {
            return BinanceErrors.MissingField(KlineQuoteVolumeField, context);
        }

        if (!BinanceJson.TryGetInt32(candle, KlineTradeCountField, out var tradeCount))
        {
            return BinanceErrors.MissingField(KlineTradeCountField, context);
        }

        // 收盤旗標沒有預設值,理由見類別註解。
        // The closed flag has no default; see the type remarks for why.
        if (!BinanceJson.TryGetBoolean(candle, KlineIsClosedField, out var isClosed))
        {
            return BinanceErrors.MissingField(KlineIsClosedField, context);
        }

        return new Kline
        {
            Symbol = symbol,
            Interval = parsedInterval,
            OpenTime = openTime,
            CloseTime = closeTime,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            QuoteVolume = quoteVolume,
            TradeCount = tradeCount,
            IsClosed = isClosed,
        };
    }

    private static Result<MarkPriceUpdate> ReadMarkPricePayload(JsonElement payload, string context)
    {
        if (!BinanceJson.TryGetString(payload, SymbolField, out var symbol))
        {
            return BinanceErrors.MissingField(SymbolField, context);
        }

        if (!BinanceJson.TryGetDecimal(payload, MarkPriceField, out var markPrice))
        {
            return BinanceErrors.MissingField(MarkPriceField, context);
        }

        if (!BinanceJson.TryGetTimestamp(payload, EventTimeField, out var timestamp))
        {
            return BinanceErrors.MissingField(EventTimeField, context);
        }

        return new MarkPriceUpdate
        {
            Symbol = symbol,
            MarkPrice = markPrice,
            Timestamp = timestamp,

            // 指數價、資金費率與下次結算時間在抽象層是可空的,交割合約本來就沒有資金費率。
            // 這裡不把缺漏當成失敗,但也不填零 —— 填零會讓「沒有資金費率」看起來像「費率剛好是零」。
            // The index price, funding rate, and next funding time are nullable in the abstraction because a
            // delivery contract genuinely has no funding. A missing value is not a failure here, but it is not
            // zero either: zero would make "no funding rate" look like "the rate happens to be zero".
            IndexPrice = BinanceJson.TryGetDecimal(payload, IndexPriceField, out var indexPrice) ? indexPrice : null,
            FundingRate = BinanceJson.TryGetDecimal(payload, FundingRateField, out var fundingRate) ? fundingRate : null,
            NextFundingTime = BinanceJson.TryGetTimestamp(payload, NextFundingTimeField, out var nextFunding)
                ? nextFunding
                : null,
        };
    }
}
