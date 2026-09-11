namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 本套件自有錯誤的建立方法,統一代碼、分類與訊息格式。
/// Factory methods for this package's own failures, keeping code, category, and message format consistent.
/// </summary>
/// <remarks>
/// 交易面的失敗請用 <see cref="TradeErrors"/>,那一組的代碼上層才認得。這裡只處理「這一側接線沒接好」。
/// Trading failures belong to <see cref="TradeErrors"/>, whose codes upper layers recognise. This type only
/// covers the cases where this side is miswired.
/// </remarks>
public static class BinanceErrors
{
    /// <summary>
    /// 建立「設定不合法」的錯誤。
    /// Creates an invalid-configuration failure.
    /// </summary>
    /// <param name="reason">
    /// 說明哪一項設定不合法,請一併寫成中英雙語。
    /// A bilingual explanation of which setting is invalid.
    /// </param>
    /// <returns>對應的錯誤。The corresponding error.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> 為空白時擲出。Thrown when <paramref name="reason"/> is blank.
    /// </exception>
    public static Error InvalidOptions(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return Error.Validation(BinanceErrorCodes.InvalidOptions, reason);
    }

    /// <summary>
    /// 建立「回應無法解析」的錯誤。
    /// Creates a malformed-response failure.
    /// </summary>
    /// <param name="reason">
    /// 說明哪裡看不懂,請一併寫成中英雙語。
    /// A bilingual explanation of what could not be read.
    /// </param>
    /// <returns>對應的錯誤。The corresponding error.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> 為空白時擲出。Thrown when <paramref name="reason"/> is blank.
    /// </exception>
    /// <remarks>
    /// 分類是 <see cref="ErrorCategory.Unexpected"/> 而不是 <see cref="ErrorCategory.Unavailable"/>:
    /// 看不懂的回應通常代表對方改了格式或這裡的對映寫錯了,重試只會再拿到一模一樣看不懂的回應。
    /// The category is <see cref="ErrorCategory.Unexpected"/> rather than
    /// <see cref="ErrorCategory.Unavailable"/>: an unreadable response usually means the format changed or the
    /// mapping is wrong, and a retry fetches exactly the same unreadable response again.
    /// </remarks>
    public static Error MalformedResponse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new Error(BinanceErrorCodes.MalformedResponse, reason, ErrorCategory.Unexpected);
    }

    /// <summary>
    /// 建立「回應缺少必要欄位」的錯誤。
    /// Creates a missing-field failure.
    /// </summary>
    /// <param name="field">缺少或無法解析的欄位名稱。The missing or unreadable field.</param>
    /// <param name="context">
    /// 這個欄位所在的位置,例如端點路徑或交易對代碼。
    /// Where the field belongs, such as an endpoint path or a symbol.
    /// </param>
    /// <returns>對應的錯誤,並把欄位名放進 <see cref="Error.Data"/>。The error, with the field in its data.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="field"/> 或 <paramref name="context"/> 為空白時擲出。
    /// Thrown when either argument is blank.
    /// </exception>
    public static Error MissingField(string field, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return MalformedResponse(
                $"{context} 的回應缺少欄位「{field}」或其值無法解析為十進位數值。The response for {context} is missing the field \"{field}\" or its value is not a plain decimal.")
            .WithData(BinanceErrorDataKeys.Field, field);
    }

    /// <summary>
    /// 建立「未設定 API 憑證」的錯誤。
    /// Creates a missing-credentials failure.
    /// </summary>
    /// <param name="operation">嘗試執行的操作名稱。The operation that was attempted.</param>
    /// <returns>對應的錯誤。The corresponding error.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> 為空白時擲出。Thrown when <paramref name="operation"/> is blank.
    /// </exception>
    /// <remarks>
    /// 在請求送出之前就攔下來。沒有金鑰的簽章請求送出去只會拿到 <c>-2014</c> 或 <c>-2015</c>,
    /// 那個錯誤訊息會把人帶去查 IP 白名單,而真正的原因只是設定沒讀到。
    /// This is caught before the request goes out. Sending an unsigned-but-signed-shaped request merely earns a
    /// <c>-2014</c> or <c>-2015</c>, whose message sends the reader off to check IP allowlists when the real
    /// cause is configuration that never loaded.
    /// </remarks>
    public static Error CredentialsMissing(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        return new Error(
            BinanceErrorCodes.CredentialsMissing,
            $"{operation} 需要簽章,但未設定 API 金鑰或密鑰,請求未送出。{operation} requires a signature but no API key or secret is configured; the request was not sent.",
            ErrorCategory.Unauthorized);
    }
}
