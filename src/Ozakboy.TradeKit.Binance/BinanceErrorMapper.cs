using System.Globalization;
using System.Text.Json;
using Ozakboy.Http;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 把幣安的錯誤回應對映成交易所中立的 <see cref="Error"/>。
/// Maps Binance error responses onto exchange-neutral <see cref="Error"/> values.
/// </summary>
/// <remarks>
/// <para>
/// 這一層的責任有兩件,而第二件比第一件難也比第一件重要。
/// 第一件是把幣安的數字換成 <see cref="TradeErrorCodes"/>,讓上層不必寫
/// <c>if (code == -2011)</c>。第二件是判定<b>暫時性</b>:<see cref="Error.IsTransient"/> 是上層決定
/// 重不重試的唯一依據,標錯的代價是不對稱的 —— 把 <c>-1022</c>(簽章錯)標成暫時性,會讓一個永遠不會
/// 成功的請求重試到限流額度耗盡;把 <c>-1003</c>(限流)標成非暫時性,則會讓一次可自癒的塞車變成功能停擺。
/// This layer has two jobs, and the second is both harder and more important. The first is turning Binance's
/// numbers into <see cref="TradeErrorCodes"/> so no upper layer has to write <c>if (code == -2011)</c>. The
/// second is deciding <b>transience</b>: <see cref="Error.IsTransient"/> is the single source of truth for
/// whether callers retry, and the cost of getting it wrong is asymmetric. Marking <c>-1022</c> (bad signature)
/// transient retries a request that can never succeed until the rate-limit quota is gone; marking <c>-1003</c>
/// (rate limited) non-transient turns a self-healing traffic jam into an outage.
/// </para>
/// <para>
/// 暫時性不是逐碼設定的旗標,而是由 <see cref="ErrorCategory"/> 推得(見
/// <c>ErrorCategoryExtensions.IsTransient</c>):只有 <see cref="ErrorCategory.Timeout"/>、
/// <see cref="ErrorCategory.Network"/>、<see cref="ErrorCategory.RateLimited"/>、
/// <see cref="ErrorCategory.Unavailable"/> 是暫時性。因此對映表只要把分類選對,暫時性就自動正確,
/// 不會出現「代碼改了但旗標忘了跟著改」。
/// Transience is not a per-code flag but a consequence of <see cref="ErrorCategory"/> (see
/// <c>ErrorCategoryExtensions.IsTransient</c>): only Timeout, Network, RateLimited, and Unavailable are
/// transient. Getting the category right therefore makes transience right by construction, with no flag left
/// behind when a mapping changes.
/// </para>
/// <para>
/// 特別處理的三個代碼:<c>-1021</c> 對映成 <see cref="TradeErrorCodes.TimestampOutOfSync"/> 且刻意<b>不是</b>
/// 暫時性 —— 本機時鐘偏移時原封不動地重試只會再被拒一次,上層應該看到這個代碼就去校時(可用
/// <see cref="IExchangeInfoProvider.GetServerTimeAsync"/>)。<c>-1022</c> 同理,簽章錯不會自己變對。
/// <c>-2015</c> 則帶上 IP 白名單的提示:家用寬頻換 IP 之後所有請求都會變成這一碼,而它的官方訊息
/// 「Invalid API-key, IP, or permissions」會先把人帶去懷疑金鑰。
/// Three codes get special treatment. <c>-1021</c> maps to
/// <see cref="TradeErrorCodes.TimestampOutOfSync"/> and is deliberately <b>not</b> transient: replaying the same
/// skewed timestamp earns the same rejection, and the caller should re-synchronise instead (see
/// <see cref="IExchangeInfoProvider.GetServerTimeAsync"/>). <c>-1022</c> likewise: a bad signature does not
/// become correct on its own. <c>-2015</c> carries an IP-allowlist hint, because every request turns into this
/// code after a home broadband connection changes address, while its official wording sends the reader off to
/// suspect the key first.
/// </para>
/// </remarks>
public static class BinanceErrorMapper
{
    /// <summary>
    /// 幣安錯誤回應本文的欄位名:數值代碼。
    /// The field name carrying the numeric code in a Binance error body.
    /// </summary>
    private const string CodeField = "code";

    /// <summary>
    /// 幣安錯誤回應本文的欄位名:訊息。
    /// The field name carrying the message in a Binance error body.
    /// </summary>
    private const string MessageField = "msg";

    /// <summary>
    /// 把幣安的數值錯誤碼對映成交易所中立的錯誤。
    /// Maps a numeric Binance error code onto an exchange-neutral error.
    /// </summary>
    /// <param name="apiCode">幣安回傳的數值代碼,例如 <c>-2019</c>。The numeric Binance code.</param>
    /// <param name="apiMessage">幣安回傳的原始訊息。The raw message Binance returned.</param>
    /// <param name="endpoint">
    /// 觸發這個錯誤的端點路徑,會寫進 <see cref="Error.Data"/>。
    /// The endpoint path that produced it; written into <see cref="Error.Data"/>.
    /// </param>
    /// <param name="endpoints">
    /// 觸發這個錯誤的環境,會寫進 <see cref="Error.Data"/>。同一個代碼在 Testnet 與主網可能是完全不同的事。
    /// The environment it came from; the same code can mean different things on Testnet and production.
    /// </param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    public static Error Map(
        int apiCode,
        string? apiMessage,
        string? endpoint = null,
        BinanceEndpoints? endpoints = null)
    {
        var (code, category, explanation) = Classify(apiCode);
        var raw = string.IsNullOrWhiteSpace(apiMessage) ? "(交易所未附訊息 / no message)" : apiMessage;

        var message = explanation.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"幣安回報 {apiCode}:{raw} Binance returned {apiCode}: {raw}")
            : string.Create(CultureInfo.InvariantCulture, $"幣安回報 {apiCode}:{raw} {explanation} Binance returned {apiCode}: {raw}");

        var error = new Error(code, message, category)
            .WithData(BinanceErrorDataKeys.ApiCode, apiCode)
            .WithData(BinanceErrorDataKeys.ApiMessage, raw);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            error = error.WithData(BinanceErrorDataKeys.Endpoint, endpoint);
        }

        return endpoints is null
            ? error
            : error.WithData(BinanceErrorDataKeys.Environment, endpoints.DisplayName);
    }

    /// <summary>
    /// 把 <c>Ozakboy.Http</c> 管線回傳的 HTTP 錯誤,升級成帶有幣安語意的交易錯誤。
    /// Upgrades an HTTP-level error from the <c>Ozakboy.Http</c> pipeline into a Binance-aware trading error.
    /// </summary>
    /// <param name="httpError">管線回傳的錯誤。The error the pipeline returned.</param>
    /// <param name="endpoint">端點路徑。The endpoint path.</param>
    /// <param name="endpoints">環境。The environment.</param>
    /// <returns>對映後的錯誤。The mapped error.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpError"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="httpError"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 幣安把業務錯誤放在 4xx 的回應本文裡(<c>{"code":-2019,"msg":"..."}</c>),所以先試著解析本文;
    /// 解析得出來就以幣安的代碼為準,解析不出來才退回用 HTTP 狀態碼判斷。
    /// Binance puts business errors in the body of a 4xx response, so the body is parsed first and the Binance
    /// code wins when it is readable; the HTTP status is only a fallback.
    /// </para>
    /// <para>
    /// 若管線已經重試到放棄(<see cref="ErrorCategory.Exhausted"/>),分類一律保留 Exhausted,
    /// 只換代碼與訊息。原因是「已經替你重試過了」這件事必須傳下去:把它還原成
    /// <see cref="ErrorCategory.RateLimited"/> 會讓上層再重試一輪,等於把退避時間乘上兩層。
    /// When the pipeline already gave up (<see cref="ErrorCategory.Exhausted"/>) the category is preserved and
    /// only the code and message change. "We already retried for you" has to survive the translation:
    /// restoring it to <see cref="ErrorCategory.RateLimited"/> invites a second retry loop on top of the first.
    /// </para>
    /// </remarks>
    public static Error MapHttpError(Error httpError, string? endpoint = null, BinanceEndpoints? endpoints = null)
    {
        ArgumentNullException.ThrowIfNull(httpError);

        Error mapped;

        if (httpError.TryGetData(HttpErrorDataKeys.Body, out var body)
            && TryParseApiError(body, out var apiCode, out var apiMessage))
        {
            mapped = Map(apiCode, apiMessage, endpoint, endpoints);
        }
        else
        {
            mapped = MapTransportError(httpError, endpoint, endpoints);
        }

        // 例外物件與 Retry-After 這類診斷資料都留在原錯誤上,對映時要一併帶過來,
        // 否則事後查問題的人只剩一句翻譯過的訊息可看。
        // Diagnostics such as the exception and any Retry-After hint live on the original error and have to
        // come across, or whoever debugs this later is left with nothing but a translated sentence.
        mapped = mapped with { Exception = httpError.Exception };

        if (httpError.TryGetData(HttpErrorDataKeys.RetryAfterSeconds, out var retryAfter) && retryAfter is not null)
        {
            mapped = mapped.WithData(HttpErrorDataKeys.RetryAfterSeconds, retryAfter);
        }

        if (httpError.TryGetData(HttpErrorDataKeys.StatusCode, out var statusCode) && statusCode is not null)
        {
            mapped = mapped.WithData(HttpErrorDataKeys.StatusCode, statusCode);
        }

        return httpError.Category == ErrorCategory.Exhausted
            ? new Error(mapped.Code, mapped.Message, ErrorCategory.Exhausted)
            {
                Exception = mapped.Exception,
                Data = mapped.Data,
            }
            : mapped;
    }

    /// <summary>
    /// 嘗試從回應本文解析出幣安的錯誤代碼與訊息。
    /// Tries to read a Binance error code and message out of a response body.
    /// </summary>
    /// <param name="body">回應本文。The response body.</param>
    /// <param name="apiCode">解析出的數值代碼。The numeric code that was read.</param>
    /// <param name="apiMessage">解析出的訊息。The message that was read.</param>
    /// <returns>
    /// 解析成功時為 <see langword="true"/>。
    /// <see langword="true"/> when the body is a Binance error object.
    /// </returns>
    public static bool TryParseApiError(string? body, out int apiCode, out string? apiMessage)
    {
        apiCode = 0;
        apiMessage = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(CodeField, out var codeElement)
                || codeElement.ValueKind != JsonValueKind.Number
                || !codeElement.TryGetInt32(out apiCode))
            {
                return false;
            }

            apiMessage = document.RootElement.TryGetProperty(MessageField, out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;

            return true;
        }
        catch (JsonException)
        {
            // 錯誤路徑上拿到的本文常常是被截斷的片段(管線只保留前 512 個字元),
            // 解析失敗是預期中的事,不是異常狀況。
            // On the error path the body is often a truncated snippet — the pipeline keeps only the first 512
            // characters — so a parse failure here is expected rather than exceptional.
            return false;
        }
    }

    /// <summary>
    /// 把目前的對外 IP 附到錯誤上。<c>-2015</c> 幾乎都是 IP 白名單問題,而白名單該填什麼只有呼叫端查得到。
    /// Attaches the current outbound IP address to an error. <c>-2015</c> is almost always an allowlist problem,
    /// and only the caller can find out what the allowlist should contain.
    /// </summary>
    /// <param name="error">要補充的錯誤。The error to enrich.</param>
    /// <param name="outboundIpAddress">查到的對外 IP。The outbound IP address that was looked up.</param>
    /// <returns>補上 IP 說明的錯誤。The error with the address appended.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="error"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="error"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outboundIpAddress"/> 為空白時擲出。
    /// Thrown when <paramref name="outboundIpAddress"/> is blank.
    /// </exception>
    /// <remarks>
    /// 查詢對外 IP 必須向第三方服務發一次請求。函式庫不該在使用者不知情的狀況下對外連線,
    /// 因此這件事留給應用層決定何時做、對誰做,本套件只負責把結果接回錯誤裡。
    /// Discovering the outbound address means calling some third-party echo service. A library should not make
    /// outbound calls the user did not ask for, so when and where to do it is the application's decision; this
    /// package only folds the answer back into the error.
    /// </remarks>
    public static Error WithOutboundIpAddress(Error error, string outboundIpAddress)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundIpAddress);

        var message =
            $"{error.Message}(本機目前的對外 IP 為 {outboundIpAddress},請確認它在 API 金鑰的 IP 白名單內。The current outbound IP is {outboundIpAddress}; check that it is on the API key's IP allowlist.)";

        return new Error(error.Code, message, error.Category)
        {
            Exception = error.Exception,
            Data = error.Data,
        }.WithData(OutboundIpAddressDataKey, outboundIpAddress);
    }

    /// <summary>
    /// <see cref="WithOutboundIpAddress"/> 寫進 <see cref="Error.Data"/> 的鍵名。
    /// The key <see cref="WithOutboundIpAddress"/> writes into <see cref="Error.Data"/>.
    /// </summary>
    public const string OutboundIpAddressDataKey = "outboundIpAddress";

    private static Error MapTransportError(Error httpError, string? endpoint, BinanceEndpoints? endpoints)
    {
        var (code, category) = ClassifyTransport(httpError);

        var error = new Error(code, httpError.Message, category);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            error = error.WithData(BinanceErrorDataKeys.Endpoint, endpoint);
        }

        return endpoints is null
            ? error
            : error.WithData(BinanceErrorDataKeys.Environment, endpoints.DisplayName);
    }

    private static (string Code, ErrorCategory Category) ClassifyTransport(Error httpError)
    {
        // 先看 HTTP 狀態碼,再看管線自己的代碼。順序是重點:429 與 418 在
        // HttpErrorMapper 眼中分別是 RateLimited 與「4xx 一律 Validation」,而 418 是幣安的自動封鎖,
        // 把它當成參數錯誤會讓上層以為改參數就好,實際上是要停手等封鎖解除。
        // The status code is consulted before the pipeline's own code, and the order matters: to
        // HttpErrorMapper a 418 is just another 4xx and lands on Validation, yet 418 is Binance's automatic ban.
        // Treating it as a parameter problem tells the caller to fix its arguments when it must instead stop.
        if (httpError.TryGetInt64(HttpErrorDataKeys.StatusCode, out var status))
        {
            switch (status)
            {
                case 418:
                case 429:
                    return (TradeErrorCodes.RateLimited, ErrorCategory.RateLimited);
                case 401:
                case 403:
                    return (TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized);
                case 408:
                    return (TradeErrorCodes.RequestTimeout, ErrorCategory.Timeout);
                case >= 500 and <= 599:
                    return (TradeErrorCodes.ExchangeUnavailable, ErrorCategory.Unavailable);
                default:
                    break;
            }
        }

        return httpError.Code switch
        {
            HttpErrorCodes.Timeout or HttpErrorCodes.AttemptTimeout =>
                (TradeErrorCodes.RequestTimeout, ErrorCategory.Timeout),
            HttpErrorCodes.Network =>
                (TradeErrorCodes.NetworkFailure, ErrorCategory.Network),
            HttpErrorCodes.RateLimited or HttpErrorCodes.RateLimitTimeout or HttpErrorCodes.RateLimitWeightTooLarge =>
                (TradeErrorCodes.RateLimited, ErrorCategory.RateLimited),
            HttpErrorCodes.SigningSecretMissing or HttpErrorCodes.SigningFailed =>
                (TradeErrorCodes.InvalidSignature, ErrorCategory.Unauthorized),
            HttpErrorCodes.Cancelled =>
                (TradeErrorCodes.RequestTimeout, ErrorCategory.Cancelled),
            _ => (TradeErrorCodes.UnknownExchangeError, httpError.Category),
        };
    }

    /// <summary>
    /// 幣安代碼到中立代碼、錯誤分類與補充說明的對映表。
    /// The table mapping Binance codes onto neutral codes, categories, and extra guidance.
    /// </summary>
    /// <remarks>
    /// 第三個元素是要接在原始訊息後面的補充說明,只有「原始訊息會把人帶錯方向」的代碼才有。
    /// The third element is guidance appended after the raw message, present only for the codes whose official
    /// wording sends the reader in the wrong direction.
    /// </remarks>
    private static (string Code, ErrorCategory Category, string Explanation) Classify(int apiCode) => apiCode switch
    {
        // ── 10xx:伺服器與網路。這一段是唯一大量出現「暫時性」的區塊。 ──
        // ── 10xx: server and network. This is the only band where transient failures cluster. ──
        BinanceApiErrorCodes.Disconnected or BinanceApiErrorCodes.UnexpectedResponse
            or BinanceApiErrorCodes.ServiceShuttingDown =>
            (TradeErrorCodes.ExchangeUnavailable, ErrorCategory.Unavailable, string.Empty),

        BinanceApiErrorCodes.Timeout =>
            (TradeErrorCodes.RequestTimeout, ErrorCategory.Timeout, string.Empty),

        BinanceApiErrorCodes.TooManyRequests or BinanceApiErrorCodes.RequestThrottled
            or BinanceApiErrorCodes.TooManyOrders =>
            (TradeErrorCodes.RateLimited, ErrorCategory.RateLimited,
                "(已觸發交易所端限流,退避後才可重試;持續觸發會升級為 HTTP 418 封鎖 IP。Rate limited by the exchange; back off before retrying, as repeated offences escalate to an HTTP 418 IP ban.)"),

        BinanceApiErrorCodes.Unauthorized =>
            (TradeErrorCodes.PermissionDenied, ErrorCategory.Unauthorized, string.Empty),

        BinanceApiErrorCodes.UnsupportedOperation =>
            (TradeErrorCodes.NotSupported, ErrorCategory.NotSupported, string.Empty),

        BinanceApiErrorCodes.OrderTypeNotSupportedOnEndpoint =>
            (TradeErrorCodes.NotSupported, ErrorCategory.NotSupported,
                "(這個委託類型已不由 /fapi/v1/order 受理,幣安要求改用 Algo Order 專用端點;2026-09-11 在 Testnet 實測,STOP_MARKET 與 TRAILING_STOP_MARKET 都是這一碼。參數沒有錯,改參數也不會過。This order type is no longer accepted on /fapi/v1/order and Binance directs it to the Algo Order endpoints; measured on Testnet on 2026-09-11 for both STOP_MARKET and TRAILING_STOP_MARKET. The parameters are not at fault and editing them will not help.)"),

        BinanceApiErrorCodes.InvalidTimestamp =>
            (TradeErrorCodes.TimestampOutOfSync, ErrorCategory.Validation,
                "(本機時鐘與交易所時間偏移超過 recvWindow。重試不會成功,請先以伺服器時間校正本機時鐘或放寬 recvWindow。The local clock is outside recvWindow; retrying will not help, so re-synchronise against server time or widen recvWindow first.)"),

        BinanceApiErrorCodes.InvalidSignature =>
            (TradeErrorCodes.InvalidSignature, ErrorCategory.Unauthorized,
                "(簽章不符。重試不會成功:常見原因是參數順序與待簽字串不一致、編碼方式不同,或密鑰有誤。The signature did not match; retrying will not help. Usual causes are a parameter order that differs from the signed string, different encoding, or a wrong secret.)"),

        // ── 11xx:請求參數。全部非暫時性 —— 同樣的參數送幾次都是一樣的結果。 ──
        // ── 11xx: request parameters. All non-transient: the same arguments earn the same answer. ──
        BinanceApiErrorCodes.BadSymbol or BinanceApiErrorCodes.BadInstrumentType =>
            (TradeErrorCodes.SymbolNotFound, ErrorCategory.NotFound, string.Empty),

        BinanceApiErrorCodes.BadAsset =>
            (TradeErrorCodes.BalanceNotFound, ErrorCategory.NotFound, string.Empty),

        BinanceApiErrorCodes.BadAccount =>
            (TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized, string.Empty),

        BinanceApiErrorCodes.BadInterval =>
            (TradeErrorCodes.UnsupportedInterval, ErrorCategory.Validation, string.Empty),

        BinanceApiErrorCodes.NoDepth =>
            (TradeErrorCodes.MarketClosed, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.InvalidListenKey =>
            (TradeErrorCodes.StreamDisconnected, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.IllegalCharacters or BinanceApiErrorCodes.TooManyParameters
            or BinanceApiErrorCodes.MandatoryParameterMissing or BinanceApiErrorCodes.UnknownParameter
            or BinanceApiErrorCodes.UnreadParameters or BinanceApiErrorCodes.ParameterEmpty
            or BinanceApiErrorCodes.ParameterNotRequired or BinanceApiErrorCodes.LookupIntervalTooBig
            or BinanceApiErrorCodes.InvalidParameter or BinanceApiErrorCodes.InvalidTimeInterval =>
            (TradeErrorCodes.InvalidQuery, ErrorCategory.Validation, string.Empty),

        BinanceApiErrorCodes.InvalidMessage or BinanceApiErrorCodes.UnknownOrderComposition
            or BinanceApiErrorCodes.BadPrecision or BinanceApiErrorCodes.TimeInForceNotRequired
            or BinanceApiErrorCodes.InvalidTimeInForce or BinanceApiErrorCodes.InvalidOrderType
            or BinanceApiErrorCodes.InvalidSide or BinanceApiErrorCodes.EmptyNewClientOrderId
            or BinanceApiErrorCodes.EmptyOriginalClientOrderId
            or BinanceApiErrorCodes.BadOptionalParameterCombination
            or BinanceApiErrorCodes.InvalidNewOrderResponseType
            or BinanceApiErrorCodes.InvalidClientOrderIdLength
            or BinanceApiErrorCodes.PositionSideNotMatch =>
            (TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation, string.Empty),

        // ── 20xx:處理階段。多半是帳戶或市場狀態問題,不是參數問題。 ──
        // ── 20xx: processing. Mostly account or market state rather than argument problems. ──
        BinanceApiErrorCodes.NewOrderRejected or BinanceApiErrorCodes.UnableToFill
            or BinanceApiErrorCodes.OrderWouldImmediatelyTrigger or BinanceApiErrorCodes.UserInLiquidation
            or BinanceApiErrorCodes.PositionNotSufficient or BinanceApiErrorCodes.MaxOpenOrderExceeded
            or BinanceApiErrorCodes.MarketOrderRejected =>
            (TradeErrorCodes.OrderRejected, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.CancelRejected or BinanceApiErrorCodes.CancelAllFailed =>
            (TradeErrorCodes.OrderNotCancelable, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.NoSuchOrder =>
            (TradeErrorCodes.OrderNotFound, ErrorCategory.NotFound, string.Empty),

        BinanceApiErrorCodes.DuplicatedClientOrderId =>
            (TradeErrorCodes.DuplicateClientOrderId, ErrorCategory.Conflict,
                "(這個編號的委託先前已經送出並被交易所收下。冪等送單遇到它代表「那張單已經進去了」——正確反應是用同一個編號查單確認,不是換一個編號重送。The order carrying this id was already accepted by the exchange. Under idempotent submission that means it went through: look it up by the same id rather than re-sending under a fresh one.)"),

        BinanceApiErrorCodes.BadApiKeyFormat or BinanceApiErrorCodes.ApiKeysLocked =>
            (TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized, string.Empty),

        BinanceApiErrorCodes.RejectedApiKey =>
            (TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized,
                "(金鑰本身、來源 IP 或權限其中之一不被接受。最常見的原因不是金鑰而是 IP:家用寬頻換到新的對外 IP 之後,所有請求都會變成這一碼,請先確認白名單。One of the key, the source IP, or the permissions was rejected. The usual cause is the IP rather than the key: every request turns into this code after a home connection gets a new outbound address, so check the allowlist first.)"),

        BinanceApiErrorCodes.NoTradingWindow =>
            (TradeErrorCodes.MarketClosed, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.BalanceNotSufficient or BinanceApiErrorCodes.CrossBalanceInsufficient
            or BinanceApiErrorCodes.IsolatedBalanceInsufficient =>
            (TradeErrorCodes.InsufficientBalance, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.MarginNotSufficient =>
            (TradeErrorCodes.InsufficientMargin, ErrorCategory.Conflict,
                "(保證金不足以支應這張委託,風控應縮小部位或先降槓桿,重試同樣的規模不會成功。Margin will not cover this order; size down or reduce leverage, because retrying the same size cannot succeed.)"),

        BinanceApiErrorCodes.ReduceOnlyRejected or BinanceApiErrorCodes.ReduceOnlyOrderTypeNotSupported
            or BinanceApiErrorCodes.ReduceOnlyConflict =>
            (TradeErrorCodes.ReduceOnlyRejected, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.MaxLeverageRatio or BinanceApiErrorCodes.MinLeverageRatio
            or BinanceApiErrorCodes.InvalidLeverage =>
            (TradeErrorCodes.LeverageNotAllowed, ErrorCategory.Conflict, string.Empty),

        // ── 40xx:篩選器與帳戶設定。 ──
        // ── 40xx: filters and account settings. ──
        BinanceApiErrorCodes.QuantityLessThanZero or BinanceApiErrorCodes.AmountMustBePositive =>
            (TradeErrorCodes.InvalidQuantity, ErrorCategory.Validation, string.Empty),

        BinanceApiErrorCodes.PriceNotAlignedToTickSize =>
            (TradeErrorCodes.InvalidPrice, ErrorCategory.Validation,
                "(價格未對齊 tickSize。送單前應先以 SymbolInfo.NormalizePrice 校正,交易規則快取過期時也會出現這一碼。The price is not aligned to tickSize; normalise with SymbolInfo.NormalizePrice before sending, and note that a stale trading-rule cache produces this too.)"),

        BinanceApiErrorCodes.PriceLessThanMinPrice or BinanceApiErrorCodes.PriceAboveMultiplierCap
            or BinanceApiErrorCodes.PriceBelowStopMultiplierFloor =>
            (TradeErrorCodes.PriceOutOfRange, ErrorCategory.Validation, string.Empty),

        BinanceApiErrorCodes.MinNotional =>
            (TradeErrorCodes.NotionalBelowMinimum, ErrorCategory.Validation,
                "(委託名目價值低於該商品的 MIN_NOTIONAL。小面額標的多為 5 USDT,ETHUSDT 為 20、BTCUSDT 為 50。The notional is below the symbol's MIN_NOTIONAL, which is 5 USDT for most small-cap symbols but 20 for ETHUSDT and 50 for BTCUSDT.)"),

        BinanceApiErrorCodes.NoNeedToChangeMarginType or BinanceApiErrorCodes.MarginTypeChangeBlockedByOrders
            or BinanceApiErrorCodes.MarginTypeChangeBlockedByPosition
            or BinanceApiErrorCodes.AddIsolatedMarginRejected
            or BinanceApiErrorCodes.PositionModeChangeBlockedByOrders
            or BinanceApiErrorCodes.PositionModeChangeBlockedByPosition
            or BinanceApiErrorCodes.IsolatedRejectedWithJointMargin =>
            (TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict, string.Empty),

        BinanceApiErrorCodes.SymbolAlreadyClosed =>
            (TradeErrorCodes.SymbolNotTradable, ErrorCategory.Conflict, string.Empty),

        // 對不上的代碼不當成成功,也不猜分類:交給 Unexpected(非暫時性),
        // 原始代碼留在 Data 裡,這樣新出現的代碼會被看見而不是被默默重試。
        // An unmapped code is neither treated as success nor given a guessed category: it becomes Unexpected
        // (non-transient) with the raw code kept in Data, so a newly introduced code gets noticed rather than
        // quietly retried.
        _ => (TradeErrorCodes.UnknownExchangeError, ErrorCategory.Unexpected, string.Empty),
    };
}
