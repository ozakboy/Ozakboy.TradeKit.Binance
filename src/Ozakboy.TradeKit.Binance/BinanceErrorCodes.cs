namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 本套件自有的錯誤代碼。交易面的失敗一律使用 <see cref="TradeErrorCodes"/>,這裡只放抽象層沒有對應項的情況。
/// The error codes this package owns. Trading failures always use <see cref="TradeErrorCodes"/>; only cases the
/// abstraction has no code for live here.
/// </summary>
/// <remarks>
/// <para>
/// 這三個代碼全都是「接線沒接好」而不是「這筆交易做不成」:設定錯、回應看不懂、金鑰沒給。
/// 它們一律非暫時性 —— 重試同樣的設定不會有不同結果,只會白白吃掉限流額度。
/// All three mean the wiring is wrong rather than the trade being impossible: bad configuration, an
/// unparseable response, missing credentials. They are always non-transient: retrying the same configuration
/// changes nothing and merely burns rate-limit quota.
/// </para>
/// <para>
/// 上層只認得 <c>trade.</c> 開頭的代碼,因此看到 <c>binance.</c> 開頭就代表「這不是交易所拒絕你,
/// 是這一側自己有問題」,應該當成部署或設定事故處理,而不是交易訊號。
/// Upper layers only recognise <c>trade.</c> codes, so a <c>binance.</c> code means the exchange did not reject
/// anything — this side is broken — and should be handled as a deployment or configuration incident.
/// </para>
/// </remarks>
public static class BinanceErrorCodes
{
    /// <summary>
    /// <see cref="BinanceOptions"/> 或端點設定不合法,請求根本沒有送出。
    /// The options or endpoint configuration is invalid and no request was sent.
    /// </summary>
    public const string InvalidOptions = "binance.options_invalid";

    /// <summary>
    /// 交易所回應的內容無法解析(不是合法 JSON、缺必要欄位、數值無法解析為十進位)。
    /// The exchange response could not be parsed: not valid JSON, a missing required field, or a value that is
    /// not a plain decimal.
    /// </summary>
    public const string MalformedResponse = "binance.malformed_response";

    /// <summary>
    /// 需要簽章的請求,但沒有設定 API 金鑰或密鑰。
    /// A signed request was attempted without an API key or secret configured.
    /// </summary>
    public const string CredentialsMissing = "binance.credentials_missing";
}
