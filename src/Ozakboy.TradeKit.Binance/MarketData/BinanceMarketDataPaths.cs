namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 行情端點專用的 REST 路徑、查詢參數名與筆數限制。
/// The REST path, query parameter names, and limits used by the market data endpoints.
/// </summary>
/// <remarks>
/// 路徑不帶開頭斜線,理由與 <c>BinanceApiPaths</c> 相同:相對位址若以斜線開頭,
/// <see cref="Uri"/> 會把它當成「從主機根目錄重新開始」,在有路徑前綴的代理環境下會悄悄打到錯的位址。
/// 這裡另立一份而不是併進 <c>BinanceApiPaths</c>,是為了讓行情這一塊的新增不與其他同時進行的工作搶同一個檔案。
/// The path carries no leading slash, for the same reason as <c>BinanceApiPaths</c>: a relative URI beginning
/// with a slash restarts from the host root and quietly targets the wrong address behind a path-prefixed proxy.
/// It lives in its own file rather than in <c>BinanceApiPaths</c> so that market data additions do not contend
/// for the same file as other work in flight.
/// </remarks>
internal static class BinanceMarketDataPaths
{
    /// <summary>歷史 K 線。Historical klines.</summary>
    public const string Klines = "fapi/v1/klines";

    /// <summary>K 線週期的查詢參數名。The interval query parameter.</summary>
    public const string IntervalParameterName = "interval";

    /// <summary>起始時間的查詢參數名,值為毫秒 Unix epoch。The start time parameter, in Unix epoch milliseconds.</summary>
    public const string StartTimeParameterName = "startTime";

    /// <summary>結束時間的查詢參數名,值為毫秒 Unix epoch。The end time parameter, in Unix epoch milliseconds.</summary>
    public const string EndTimeParameterName = "endTime";

    /// <summary>筆數上限的查詢參數名。The limit parameter.</summary>
    public const string LimitParameterName = "limit";

    /// <summary>
    /// 未指定 <see cref="KlineQuery.Limit"/> 時幣安採用的筆數。
    /// The number of candles Binance returns when <see cref="KlineQuery.Limit"/> is omitted.
    /// </summary>
    /// <remarks>
    /// 這個值不會被送出去,只用來預先宣告請求權重:權重依筆數分級,不宣告就算不準。
    /// The value is never sent; it only pre-declares the request weight, which is banded by the number of
    /// candles and cannot be metered locally without it.
    /// </remarks>
    public const int DefaultKlineLimit = 500;

    /// <summary>
    /// 單次查詢的筆數上限,實測超過即回 <c>-1130</c>。
    /// The per-request ceiling; anything above it was measured to return <c>-1130</c>.
    /// </summary>
    /// <remarks>
    /// 2026-09-11 於 Testnet 實測:<c>limit=1500</c> 回 HTTP 200,<c>limit=1501</c> 回
    /// <c>{"code":-1130,"msg":"Data sent for parameter 'limit' is not valid."}</c>。
    /// Measured on the testnet on 2026-09-11: <c>limit=1500</c> returns HTTP 200 while <c>limit=1501</c>
    /// returns <c>{"code":-1130,"msg":"Data sent for parameter 'limit' is not valid."}</c>.
    /// </remarks>
    public const int MaxKlineLimit = 1500;
}
