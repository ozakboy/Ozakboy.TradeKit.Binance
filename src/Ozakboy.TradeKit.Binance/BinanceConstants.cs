namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 API 的協定常數:標頭名、參數名與註冊用的 <see cref="HttpClient"/> 名稱。
/// Protocol constants of the Binance API: header names, parameter names, and the registered
/// <see cref="HttpClient"/> name.
/// </summary>
public static class BinanceConstants
{
    /// <summary>
    /// 承載 API 金鑰的標頭名。幣安用的是 <c>X-MBX-APIKEY</c>,不是常見的 <c>X-API-Key</c>。
    /// The header carrying the API key. Binance uses <c>X-MBX-APIKEY</c>, not the more common
    /// <c>X-API-Key</c>.
    /// </summary>
    public const string ApiKeyHeaderName = "X-MBX-APIKEY";

    /// <summary>
    /// 簽章的查詢參數名。
    /// The query parameter carrying the signature.
    /// </summary>
    public const string SignatureParameterName = "signature";

    /// <summary>
    /// 時間戳的查詢參數名,值為毫秒 Unix epoch。
    /// The timestamp query parameter, in Unix epoch milliseconds.
    /// </summary>
    public const string TimestampParameterName = "timestamp";

    /// <summary>
    /// <c>recvWindow</c> 的查詢參數名,值為毫秒。
    /// The <c>recvWindow</c> query parameter, in milliseconds.
    /// </summary>
    public const string RecvWindowParameterName = "recvWindow";

    /// <summary>
    /// 交易對的查詢參數名。
    /// The symbol query parameter.
    /// </summary>
    public const string SymbolParameterName = "symbol";

    /// <summary>
    /// 註冊在 <c>IHttpClientFactory</c> 的具名用戶端名稱。
    /// The name under which the client is registered with <c>IHttpClientFactory</c>.
    /// </summary>
    public const string HttpClientName = "Ozakboy.TradeKit.Binance";

    /// <summary>
    /// 記錄請求時必須遮蔽的參數與標頭名。
    /// The parameter and header names that must be masked when a request is logged.
    /// </summary>
    /// <remarks>
    /// <c>signature</c> 洩漏本身不足以偽造下一個請求(每個請求的待簽字串都不同),但它會連同完整的
    /// 查詢字串一起出現在 log 裡,等於把一組合法請求的全文留在磁碟上;<c>X-MBX-APIKEY</c> 更是直接的身分憑證。
    /// A leaked <c>signature</c> alone cannot forge the next request, since every request signs a different
    /// string, but it appears alongside the full query string and so leaves a complete valid request on disk;
    /// <c>X-MBX-APIKEY</c> is an identity credential outright.
    /// </remarks>
    public static IReadOnlyList<string> SensitiveParameterNames { get; } =
    [
        SignatureParameterName,
        ApiKeyHeaderName,
    ];
}
