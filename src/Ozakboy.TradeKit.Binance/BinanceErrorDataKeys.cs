namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 本套件寫進 <see cref="Error.Data"/> 的鍵名。
/// The keys this package writes into <see cref="Error.Data"/>.
/// </summary>
/// <remarks>
/// 對映之後原始的幣安代碼就從 <see cref="Error.Code"/> 消失了,但事後查 log 時它才是能對回官方文件的那一個。
/// 因此原始代碼與原始訊息一律隨錯誤帶走,而不是只留在被吞掉的回應本文裡。
/// After mapping, the original Binance code no longer appears in <see cref="Error.Code"/> — yet it is the one
/// that matches the official documentation when someone reads the log later. The raw code and message therefore
/// travel with the error instead of staying in a response body that has already been discarded.
/// </remarks>
public static class BinanceErrorDataKeys
{
    /// <summary>
    /// 幣安回傳的數值錯誤碼,例如 <c>-2015</c>。以整數字串儲存,可用
    /// <see cref="Error.TryGetInt64(string, out long)"/> 取回。
    /// The numeric Binance code such as <c>-2015</c>, stored as an integer string and readable through
    /// <see cref="Error.TryGetInt64(string, out long)"/>.
    /// </summary>
    public const string ApiCode = "binanceCode";

    /// <summary>
    /// 幣安回傳的原始訊息字串。
    /// The raw message string Binance returned.
    /// </summary>
    public const string ApiMessage = "binanceMessage";

    /// <summary>
    /// 觸發這個錯誤的端點路徑,例如 <c>/fapi/v2/positionRisk</c>。
    /// The endpoint path that produced the error, such as <c>/fapi/v2/positionRisk</c>.
    /// </summary>
    public const string Endpoint = "binanceEndpoint";

    /// <summary>
    /// 觸發這個錯誤的環境名稱。同一個代碼在 Testnet 與主網的意義可能完全不同,少了這一項就分不出來。
    /// The environment the error came from. The same code can mean quite different things on Testnet and on
    /// production, and without this there is no way to tell them apart.
    /// </summary>
    public const string Environment = "binanceEnvironment";

    /// <summary>
    /// 解析失敗時,缺少或無法解析的欄位名稱。
    /// The field that was missing or unparseable when a response could not be read.
    /// </summary>
    public const string Field = "binanceField";

    /// <summary>
    /// 解析失敗時,相關的交易對代碼。
    /// The symbol involved when a response could not be read.
    /// </summary>
    public const string Symbol = "binanceSymbol";
}
