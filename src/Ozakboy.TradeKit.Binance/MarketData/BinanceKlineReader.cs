using System.Globalization;
using System.Text.Json;

namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 判讀 <c>GET /fapi/v1/klines</c> 的回應。
/// Reads the response of <c>GET /fapi/v1/klines</c>.
/// </summary>
/// <remarks>
/// <para>
/// 回應是陣列的陣列,每根 K 線十二個元素,<b>沒有欄位名</b>,只能靠位置。索引如下
/// (2026-09-11 於 Testnet 實際呼叫核對):
/// The response is an array of arrays with twelve positional elements per candle and <b>no field names</b>.
/// The indices, checked against an actual testnet call on 2026-09-11, are:
/// </para>
/// <list type="table">
/// <listheader><term>索引 / Index</term><description>內容 / Meaning</description></listheader>
/// <item><term>0</term><description>開盤時間(毫秒)。Open time in milliseconds.</description></item>
/// <item><term>1</term><description>開盤價。Open.</description></item>
/// <item><term>2</term><description>最高價。High.</description></item>
/// <item><term>3</term><description>最低價。Low.</description></item>
/// <item><term>4</term><description>收盤價。Close.</description></item>
/// <item><term>5</term><description>成交量(基礎幣)。Volume in base units.</description></item>
/// <item><term>6</term><description>收盤時間(毫秒)。Close time in milliseconds.</description></item>
/// <item><term>7</term><description>成交額(計價幣)。Quote asset volume.</description></item>
/// <item><term>8</term><description>成交筆數。Number of trades.</description></item>
/// <item><term>9</term><description>主動買入成交量。Taker buy base volume.</description></item>
/// <item><term>10</term><description>主動買入成交額。Taker buy quote volume.</description></item>
/// <item><term>11</term><description>忽略。Ignore.</description></item>
/// </list>
/// <para>
/// <b>回應<u>沒有</u>「這根收盤了沒」的旗標,而最後一根通常還沒收盤。</b>
/// 實測:<c>limit=2</c> 取回的第二根,其收盤時間<b>晚於</b>當下的伺服器時間 —— 也就是還在跳動的那一根。
/// 若把整串都當成已收盤,最後一根的收盤價其實只是「查詢當下的最新價」,拿去算指標就得到一個
/// 每次查詢都不一樣的訊號;更糟的是把它存進歷史資料庫,之後的回測會用一根永遠不會再出現的假 K 線,
/// 而且看起來完全正常。因此這裡用 <c>asOf</c> 與收盤時間比對逐根判定
/// <see cref="Kline.IsClosed"/>。
/// <b>The response carries <u>no</u> closed flag, and the last candle is usually still open.</b> Measured: with
/// <c>limit=2</c>, the second candle's close time is <b>later</b> than the server time at that moment — it is
/// the candle still ticking. Treating the whole list as closed makes that last close price merely "the latest
/// price when the query ran", which produces an indicator value that differs on every query; worse, storing it
/// as history makes later backtests run on a candle that never existed, and nothing about it looks wrong.
/// <see cref="Kline.IsClosed"/> is therefore decided candle by candle by comparing the close time against
/// <c>asOf</c>.
/// </para>
/// <para>
/// 這個判定依賴本機時鐘。同一份設定已經要求本機時鐘與交易所的偏差在 <c>recvWindow</c>(預設 5 秒)之內
/// ——超過的話每一個簽章請求都會被拒 ——所以偏差本來就被壓在秒級;真正的風險只剩「時鐘快了幾秒,
/// 剛好落在收盤那一瞬間」,那一根會提早一點被標成已收盤。宿主要做的事沒有變:定期校時。
/// The decision leans on the local clock. The same settings already require it to sit within
/// <c>recvWindow</c> — five seconds by default — of the exchange, since anything further gets every signed
/// request rejected, so the drift is already held to seconds. The residual risk is a clock running a few
/// seconds fast at the exact moment of a close, which marks that one candle closed slightly early. The host's
/// obligation is unchanged: keep the clock synchronised.
/// </para>
/// </remarks>
internal static class BinanceKlineReader
{
    private const int OpenTimeIndex = 0;

    private const int OpenIndex = 1;

    private const int HighIndex = 2;

    private const int LowIndex = 3;

    private const int CloseIndex = 4;

    private const int VolumeIndex = 5;

    private const int CloseTimeIndex = 6;

    private const int QuoteVolumeIndex = 7;

    private const int TradeCountIndex = 8;

    private const int RequiredElementCount = 9;

    private const string Context = "歷史 K 線 / historical klines";

    /// <summary>
    /// 把回應轉成 K 線清單。
    /// Turns the response into a list of candles.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="symbol">查詢用的交易對代碼。回應本身不帶代碼,只能由呼叫端補上。The symbol queried; the response does not carry one.</param>
    /// <param name="interval">查詢用的週期。回應本身不帶週期,只能由呼叫端補上。The interval queried; the response does not carry one.</param>
    /// <param name="asOf">
    /// 判定「這根收盤了沒」的當下時間,應取自注入的時間來源。
    /// The moment used to decide whether a candle has closed; it should come from the injected time source.
    /// </param>
    /// <returns>依開盤時間由舊到新的 K 線,或說明哪裡讀不出來的失敗。The candles oldest first, or a failure.</returns>
    public static Result<IReadOnlyList<Kline>> Read(
        string json,
        string symbol,
        KlineInterval interval,
        DateTimeOffset asOf)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return BinanceErrors.MalformedResponse(
                $"{Context} 的回應不是合法的 JSON:{exception.Message}。The response for {Context} is not valid JSON: {exception.Message}.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MalformedResponse(
                    $"{Context} 的回應不是 JSON 陣列。The response for {Context} is not a JSON array.");
            }

            var candles = new List<Kline>(root.GetArrayLength());

            foreach (var element in root.EnumerateArray())
            {
                var candle = ReadCandle(element, symbol, interval, asOf);

                if (!candle.TryGetValue(out var value))
                {
                    return candle.ToFailure<IReadOnlyList<Kline>>();
                }

                candles.Add(value);
            }

            return candles;
        }
    }

    private static Result<Kline> ReadCandle(
        JsonElement element,
        string symbol,
        KlineInterval interval,
        DateTimeOffset asOf)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < RequiredElementCount)
        {
            return BinanceErrors.MalformedResponse(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Context} 的單根 K 線不是至少 {RequiredElementCount} 個元素的陣列。A candle in the response for {Context} is not an array of at least {RequiredElementCount} elements."));
        }

        if (!TryGetTimestamp(element, OpenTimeIndex, out var openTime))
        {
            return MissingElement(OpenTimeIndex, "開盤時間 / open time");
        }

        if (!TryGetTimestamp(element, CloseTimeIndex, out var closeTime))
        {
            return MissingElement(CloseTimeIndex, "收盤時間 / close time");
        }

        if (!TryGetDecimal(element, OpenIndex, out var open))
        {
            return MissingElement(OpenIndex, "開盤價 / open");
        }

        if (!TryGetDecimal(element, HighIndex, out var high))
        {
            return MissingElement(HighIndex, "最高價 / high");
        }

        if (!TryGetDecimal(element, LowIndex, out var low))
        {
            return MissingElement(LowIndex, "最低價 / low");
        }

        if (!TryGetDecimal(element, CloseIndex, out var close))
        {
            return MissingElement(CloseIndex, "收盤價 / close");
        }

        if (!TryGetDecimal(element, VolumeIndex, out var volume))
        {
            return MissingElement(VolumeIndex, "成交量 / volume");
        }

        if (!TryGetDecimal(element, QuoteVolumeIndex, out var quoteVolume))
        {
            return MissingElement(QuoteVolumeIndex, "成交額 / quote volume");
        }

        if (!TryGetInt32(element, TradeCountIndex, out var tradeCount))
        {
            return MissingElement(TradeCountIndex, "成交筆數 / trade count");
        }

        return new Kline
        {
            Symbol = symbol,
            Interval = interval,
            OpenTime = openTime,
            CloseTime = closeTime,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            QuoteVolume = quoteVolume,
            TradeCount = tradeCount,

            // 收盤時間已經過去,這根才算收盤。回應本身沒有旗標,見類別註解。
            // A candle counts as closed only once its close time is in the past; the response carries no flag.
            IsClosed = closeTime < asOf,
        };
    }

    private static Error MissingElement(int index, string name) =>
        BinanceErrors.MalformedResponse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Context} 的 K 線第 {index} 個元素({name})無法解析。Element {index} ({name}) of a candle in the response for {Context} could not be read."))
            .WithData(BinanceErrorDataKeys.Field, string.Create(CultureInfo.InvariantCulture, $"[{index}]"));

    private static bool TryGetDecimal(JsonElement element, int index, out decimal value)
    {
        var item = element[index];

        return item.ValueKind switch
        {
            JsonValueKind.String => Precision.TryParsePlain(item.GetString(), out value),
            JsonValueKind.Number => item.TryGetDecimal(out value),
            _ => Fail(out value),
        };

        static bool Fail(out decimal value)
        {
            value = 0m;
            return false;
        }
    }

    private static bool TryGetInt32(JsonElement element, int index, out int value)
    {
        value = 0;
        var item = element[index];

        // 先看 ValueKind 再取值。JsonElement 的 TryGetInt32 在型別不符時是<b>擲例外</b>而不是回傳
        // false,少了這道檢查,回應裡一個 null 就會讓整個解析改走例外路徑。
        // The kind is checked before reading: JsonElement.TryGetInt32 throws rather than returning false when
        // the kind does not match, and without this a single null in the response would send the whole parse
        // down the exception path.
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }

    private static bool TryGetTimestamp(JsonElement element, int index, out DateTimeOffset value)
    {
        value = default;

        if (element[index].ValueKind != JsonValueKind.Number || !element[index].TryGetInt64(out var milliseconds))
        {
            return false;
        }

        if (milliseconds <= 0L)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }
}
