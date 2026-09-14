using System.Text.Json;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 讀取幣安 JSON 回應的輔助方法。
/// Helpers for reading Binance JSON responses.
/// </summary>
/// <remarks>
/// <para>
/// 幣安把所有價格、數量與金額都以<b>字串</b>回傳(<c>"tickSize":"0.10"</c>),
/// 而這裡一律用 <see cref="Precision.TryParsePlain"/> 解析。那個方法刻意拒絕科學記號,
/// 正是這裡要的:如果哪天回應裡出現 <c>1E-8</c>,寧可當場解析失敗被看見,
/// 也不要用一個看似成功、位數卻已經失真的數值去算下單量。
/// Binance returns every price, quantity, and amount as a <b>string</b>, and all of them are read through
/// <see cref="Precision.TryParsePlain"/>, which deliberately rejects exponent notation. That is exactly what is
/// wanted here: should a <c>1E-8</c> ever appear, an outright parse failure that someone notices beats a
/// seemingly successful value whose scale has already been lost feeding an order size.
/// </para>
/// <para>
/// 全程用 <see cref="JsonDocument"/> 而不是反序列化成 DTO。<c>exchangeInfo</c> 的 <c>filters</c> 陣列
/// 每個元素形狀都不同(<c>PRICE_FILTER</c> 有 <c>tickSize</c>、<c>LOT_SIZE</c> 有 <c>stepSize</c>),
/// 對映成 POCO 需要一整組多型設定或一個塞滿可空欄位的萬用型別;直接讀取樹狀結構反而更短、
/// 更容易在「缺欄位」時明確失敗,也不帶任何反射(對裁剪與 AOT 友善)。
/// Everything goes through <see cref="JsonDocument"/> rather than deserialising into DTOs. Each element of the
/// <c>filters</c> array has a different shape, so a POCO mapping needs either polymorphic configuration or one
/// catch-all type full of nullable fields. Reading the tree directly is shorter, fails more explicitly on a
/// missing field, and uses no reflection at all.
/// </para>
/// </remarks>
internal static class BinanceJson
{
    /// <summary>
    /// 讀取字串屬性。
    /// Reads a string property.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的值。The value that was read.</param>
    /// <returns>屬性存在且為非空白字串時為 <see langword="true"/>。<see langword="true"/> when present and non-blank.</returns>
    public static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>
    /// 讀取十進位屬性,字串與數值兩種形式都接受。
    /// Reads a decimal property, accepting both the string and the numeric form.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的值。The value that was read.</param>
    /// <returns>解析成功時為 <see langword="true"/>。<see langword="true"/> when parsed.</returns>
    /// <remarks>
    /// 兩種形式都接受,是因為幣安在不同端點上並不一致:交易規則全是字串,
    /// 某些欄位(例如 <c>MAX_NUM_ORDERS</c> 的 <c>limit</c>)卻是數值。數值形式改用
    /// <see cref="JsonElement.TryGetDecimal"/> 直接取,不經過字串,避免多一次格式化的機會出錯。
    /// Both forms are accepted because Binance is not consistent across endpoints: the trading rules are all
    /// strings while some fields arrive as numbers. The numeric form is read straight through
    /// <see cref="JsonElement.TryGetDecimal"/> rather than via a string, removing one chance to misformat.
    /// </remarks>
    public static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0m;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => Precision.TryParsePlain(property.GetString(), out value),
            JsonValueKind.Number => property.TryGetDecimal(out value),
            _ => false,
        };
    }

    /// <summary>
    /// 讀取十進位屬性,與 <see cref="TryGetDecimal"/> 相同,但字串形式<b>也接受科學記號</b>。
    /// Reads a decimal property like <see cref="TryGetDecimal"/>, except that the string form <b>also accepts
    /// exponent notation</b>.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的值。The value that was read.</param>
    /// <returns>解析成功時為 <see langword="true"/>。<see langword="true"/> when parsed.</returns>
    /// <remarks>
    /// <para>
    /// <b>只給 Algo Service 的回應用,是本類別「一律拒絕科學記號」的唯一例外。</b>
    /// 2026-09-14 Testnet 實測:<c>GET /fapi/v1/openAlgoOrders</c> 把數量 0.0007 寫成 <c>"7.0E-4"</c>
    /// (同一張單在 <c>POST</c> / <c>GET /fapi/v1/algoOrder</c> 與 <c>ALGO_UPDATE</c> 裡都是 <c>"0.0007"</c>),
    /// 價格也是 <c>"74457.1"</c>、<c>"0.0"</c> 這種浮點數轉字串的樣子;官方文件的範例則全是固定小數。
    /// 那是 Java <c>Double.toString</c> 的輸出:小於 10⁻³ 或大於等於 10⁷ 就改用科學記號,
    /// 所以數量上千萬的低價幣也會中。
    /// <b>For Algo Service responses only — the one exception to this class's refusal of exponents.</b> Measured
    /// on Testnet on 2026-09-14: <c>GET /fapi/v1/openAlgoOrders</c> writes a quantity of 0.0007 as
    /// <c>"7.0E-4"</c> (the same order reads <c>"0.0007"</c> on <c>POST</c> / <c>GET /fapi/v1/algoOrder</c> and on
    /// <c>ALGO_UPDATE</c>), and prices look like <c>"74457.1"</c> and <c>"0.0"</c>, while every example in the
    /// documentation is fixed-point. That is Java's <c>Double.toString</c>, which switches to exponent notation
    /// below 10⁻³ and at or above 10⁷, so a low-priced coin with a quantity in the tens of millions is affected
    /// too.
    /// </para>
    /// <para>
    /// 用 <see cref="TryGetDecimal"/> 讀,那個欄位會解析失敗而被當成缺欄位 —— 未結清單上的停損數量
    /// 變成 0,對帳會以為那張停損什麼都沒保護。<see cref="Precision.TryParsePlain"/> 的文件指明
    /// 「真的需要容忍的呼叫端自己指定 <see cref="System.Globalization.NumberStyles.Float"/>」,
    /// 這裡就是那個看得見的決定。<see cref="decimal"/> 解析科學記號是精確的,不經過二進位浮點數。
    /// Read through <see cref="TryGetDecimal"/> the field fails to parse and is treated as absent: a stop on the
    /// open listing reports a quantity of zero and reconciliation concludes it protects nothing. The documentation
    /// of <see cref="Precision.TryParsePlain"/> tells a caller that genuinely needs tolerance to ask for
    /// <see cref="System.Globalization.NumberStyles.Float"/> itself; this is that visible decision. Parsing an
    /// exponent into a <see cref="decimal"/> is exact and never passes through a binary floating-point value.
    /// </para>
    /// </remarks>
    public static bool TryGetDecimalAllowingExponent(JsonElement element, string propertyName, out decimal value)
    {
        value = 0m;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            JsonValueKind.Number => property.TryGetDecimal(out value),
            _ => false,
        };
    }

    /// <summary>
    /// 讀取 32 位元整數屬性,字串與數值兩種形式都接受。
    /// Reads a 32-bit integer property, accepting both the string and the numeric form.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的值。The value that was read.</param>
    /// <returns>解析成功時為 <see langword="true"/>。<see langword="true"/> when parsed.</returns>
    public static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }

    /// <summary>
    /// 讀取 64 位元整數屬性,字串與數值兩種形式都接受。
    /// Reads a 64-bit integer property, accepting both the string and the numeric form.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的值。The value that was read.</param>
    /// <returns>解析成功時為 <see langword="true"/>。<see langword="true"/> when parsed.</returns>
    public static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0L;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }

    /// <summary>
    /// 讀取布林屬性,字串(<c>"true"</c>／<c>"false"</c>)與布林兩種形式都接受。
    /// Reads a boolean property, accepting both the string and the boolean form.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的值。The value that was read.</param>
    /// <returns>解析成功時為 <see langword="true"/>。<see langword="true"/> when parsed.</returns>
    /// <remarks>
    /// 接受字串形式是必要的:<c>/fapi/v2/positionRisk</c> 的 <c>isAutoAddMargin</c> 回的是
    /// <c>"false"</c> 字串而不是 JSON 布林值。
    /// Accepting the string form is necessary: <c>isAutoAddMargin</c> on <c>/fapi/v2/positionRisk</c> comes
    /// back as the string <c>"false"</c> rather than a JSON boolean.
    /// </remarks>
    public static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(property.GetString(), out value);
            default:
                return false;
        }
    }

    /// <summary>
    /// 把毫秒 Unix epoch 轉成 UTC 的 <see cref="DateTimeOffset"/>。
    /// Converts Unix epoch milliseconds into a UTC <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <param name="element">來源物件。The source object.</param>
    /// <param name="propertyName">屬性名。The property name.</param>
    /// <param name="value">讀到的時間。The timestamp that was read.</param>
    /// <returns>解析成功時為 <see langword="true"/>。<see langword="true"/> when parsed.</returns>
    public static bool TryGetTimestamp(JsonElement element, string propertyName, out DateTimeOffset value)
    {
        value = default;

        if (!TryGetInt64(element, propertyName, out var milliseconds))
        {
            return false;
        }

        // 幣安對「沒有時間」的表示是 0,而不是省略欄位。轉成 1970-01-01 會讓「從未更新的持倉」
        // 看起來像 1970 年更新過一次,在時間軸上排序時特別容易誤導。
        // Binance expresses "no timestamp" as 0 rather than by omitting the field. Turning that into
        // 1970-01-01 makes a never-updated position look as though it was updated once in 1970, which is
        // especially misleading once anything sorts by time.
        if (milliseconds <= 0L)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }
}
