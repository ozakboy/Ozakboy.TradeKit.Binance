using System.Net;
using Ozakboy.Http;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 驗證幣安錯誤碼的對映,重點在<b>暫時性</b>的判定。
/// Verifies the Binance error mapping, with the emphasis on the <b>transience</b> verdict.
/// </summary>
/// <remarks>
/// <see cref="Error.IsTransient"/> 是上層決定重不重試的唯一依據,而標錯的兩種方向代價完全不同:
/// 該重試的放棄,是一次可自癒的塞車變成停擺;不該重試的一直重試,是把限流額度燒光之後被交易所封 IP。
/// 因此這裡逐碼釘死分類,而不是只測幾個代表性的代碼。
/// <see cref="Error.IsTransient"/> is the only thing callers consult before retrying, and the two ways of
/// getting it wrong cost differently: giving up on a recoverable failure turns a traffic jam into an outage,
/// while retrying an unrecoverable one burns the quota and earns an IP ban. Every code is therefore pinned
/// rather than a representative sample.
/// </remarks>
[TestClass]
public sealed class BinanceErrorMapperTests
{
    [TestMethod]
    [DataRow(-1001, TradeErrorCodes.ExchangeUnavailable, ErrorCategory.Unavailable, true)]
    [DataRow(-1003, TradeErrorCodes.RateLimited, ErrorCategory.RateLimited, true)]
    [DataRow(-1006, TradeErrorCodes.ExchangeUnavailable, ErrorCategory.Unavailable, true)]
    [DataRow(-1007, TradeErrorCodes.RequestTimeout, ErrorCategory.Timeout, true)]
    [DataRow(-1008, TradeErrorCodes.RateLimited, ErrorCategory.RateLimited, true)]
    [DataRow(-1015, TradeErrorCodes.RateLimited, ErrorCategory.RateLimited, true)]
    [DataRow(-1016, TradeErrorCodes.ExchangeUnavailable, ErrorCategory.Unavailable, true)]
    public void TransientCodesAreMarkedTransient(
        int apiCode,
        string expectedCode,
        ErrorCategory expectedCategory,
        bool expectedTransient)
    {
        var error = BinanceErrorMapper.Map(apiCode, "message");

        Assert.AreEqual(expectedCode, error.Code);
        Assert.AreEqual(expectedCategory, error.Category);
        Assert.AreEqual(expectedTransient, error.IsTransient);
    }

    [TestMethod]
    [DataRow(-1000, TradeErrorCodes.UnknownExchangeError, ErrorCategory.Unexpected)]
    [DataRow(-1002, TradeErrorCodes.PermissionDenied, ErrorCategory.Unauthorized)]
    [DataRow(-1013, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1014, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1020, TradeErrorCodes.NotSupported, ErrorCategory.NotSupported)]
    [DataRow(-1021, TradeErrorCodes.TimestampOutOfSync, ErrorCategory.Validation)]
    [DataRow(-1022, TradeErrorCodes.InvalidSignature, ErrorCategory.Unauthorized)]
    [DataRow(-1100, TradeErrorCodes.InvalidQuery, ErrorCategory.Validation)]
    [DataRow(-1102, TradeErrorCodes.InvalidQuery, ErrorCategory.Validation)]
    [DataRow(-1108, TradeErrorCodes.BalanceNotFound, ErrorCategory.NotFound)]
    [DataRow(-1109, TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized)]
    [DataRow(-1110, TradeErrorCodes.SymbolNotFound, ErrorCategory.NotFound)]
    [DataRow(-1111, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1112, TradeErrorCodes.MarketClosed, ErrorCategory.Conflict)]
    [DataRow(-1115, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1116, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1117, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1120, TradeErrorCodes.UnsupportedInterval, ErrorCategory.Validation)]
    [DataRow(-1121, TradeErrorCodes.SymbolNotFound, ErrorCategory.NotFound)]
    [DataRow(-1125, TradeErrorCodes.StreamDisconnected, ErrorCategory.Conflict)]
    [DataRow(-1127, TradeErrorCodes.InvalidQuery, ErrorCategory.Validation)]
    [DataRow(-1128, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-1130, TradeErrorCodes.InvalidQuery, ErrorCategory.Validation)]
    [DataRow(-1136, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-2010, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-2011, TradeErrorCodes.OrderNotCancelable, ErrorCategory.Conflict)]
    [DataRow(-2012, TradeErrorCodes.OrderNotCancelable, ErrorCategory.Conflict)]
    [DataRow(-2013, TradeErrorCodes.OrderNotFound, ErrorCategory.NotFound)]
    [DataRow(-2014, TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized)]
    [DataRow(-2015, TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized)]
    [DataRow(-2016, TradeErrorCodes.MarketClosed, ErrorCategory.Conflict)]
    [DataRow(-2017, TradeErrorCodes.InvalidCredentials, ErrorCategory.Unauthorized)]
    [DataRow(-2018, TradeErrorCodes.InsufficientBalance, ErrorCategory.Conflict)]
    [DataRow(-2019, TradeErrorCodes.InsufficientMargin, ErrorCategory.Conflict)]
    [DataRow(-2020, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-2021, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-2022, TradeErrorCodes.ReduceOnlyRejected, ErrorCategory.Conflict)]
    [DataRow(-2023, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-2024, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-2025, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-2026, TradeErrorCodes.ReduceOnlyRejected, ErrorCategory.Conflict)]
    [DataRow(-2027, TradeErrorCodes.LeverageNotAllowed, ErrorCategory.Conflict)]
    [DataRow(-2028, TradeErrorCodes.LeverageNotAllowed, ErrorCategory.Conflict)]
    [DataRow(-4003, TradeErrorCodes.InvalidQuantity, ErrorCategory.Validation)]
    [DataRow(-4013, TradeErrorCodes.PriceOutOfRange, ErrorCategory.Validation)]
    [DataRow(-4014, TradeErrorCodes.InvalidPrice, ErrorCategory.Validation)]
    [DataRow(-4015, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-4016, TradeErrorCodes.PriceOutOfRange, ErrorCategory.Validation)]
    [DataRow(-4028, TradeErrorCodes.LeverageNotAllowed, ErrorCategory.Conflict)]
    [DataRow(-4046, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4047, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4048, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4049, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4050, TradeErrorCodes.InsufficientBalance, ErrorCategory.Conflict)]
    [DataRow(-4051, TradeErrorCodes.InsufficientBalance, ErrorCategory.Conflict)]
    [DataRow(-4055, TradeErrorCodes.InvalidQuantity, ErrorCategory.Validation)]
    [DataRow(-4061, TradeErrorCodes.InvalidOrderRequest, ErrorCategory.Validation)]
    [DataRow(-4062, TradeErrorCodes.ReduceOnlyRejected, ErrorCategory.Conflict)]
    [DataRow(-4067, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4068, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4131, TradeErrorCodes.OrderRejected, ErrorCategory.Conflict)]
    [DataRow(-4141, TradeErrorCodes.SymbolNotTradable, ErrorCategory.Conflict)]
    [DataRow(-4164, TradeErrorCodes.NotionalBelowMinimum, ErrorCategory.Validation)]
    [DataRow(-4165, TradeErrorCodes.InvalidQuery, ErrorCategory.Validation)]
    [DataRow(-4183, TradeErrorCodes.MarginModeRejected, ErrorCategory.Conflict)]
    [DataRow(-4184, TradeErrorCodes.PriceOutOfRange, ErrorCategory.Validation)]
    public void NonTransientCodesAreNeverMarkedTransient(
        int apiCode,
        string expectedCode,
        ErrorCategory expectedCategory)
    {
        var error = BinanceErrorMapper.Map(apiCode, "message");

        Assert.AreEqual(expectedCode, error.Code);
        Assert.AreEqual(expectedCategory, error.Category);
        Assert.IsFalse(error.IsTransient, $"{apiCode} 被標成暫時性,重試它只會白白吃掉限流額度。");
    }

    [TestMethod]
    public void ClockDriftIsIdentifiableAndNotRetryable()
    {
        // -1021 必須能被上層辨識出「去校時」而不是「再試一次」。原封不動地重試只會再被拒一次。
        // -1021 must read as "re-synchronise the clock" rather than "try again"; replaying the same skewed
        // timestamp earns the same rejection.
        var error = BinanceErrorMapper.Map(
            BinanceApiErrorCodes.InvalidTimestamp,
            "Timestamp for this request is outside of the recvWindow.");

        Assert.AreEqual(TradeErrorCodes.TimestampOutOfSync, error.Code);
        Assert.IsFalse(error.IsTransient);
        StringAssert.Contains(error.Message, "recvWindow", StringComparison.Ordinal);
        StringAssert.Contains(error.Message, "校正", StringComparison.Ordinal);
        Assert.IsTrue(error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var raw));
        Assert.AreEqual(-1021L, raw);
    }

    [TestMethod]
    public void BadSignatureSaysRetryingWillNotHelp()
    {
        var error = BinanceErrorMapper.Map(
            BinanceApiErrorCodes.InvalidSignature,
            "Signature for this request is not valid.");

        Assert.AreEqual(TradeErrorCodes.InvalidSignature, error.Code);
        Assert.IsFalse(error.IsTransient);
        StringAssert.Contains(error.Message, "重試不會成功", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RejectedKeyPointsAtTheIpAllowlistFirst()
    {
        // 家用寬頻換 IP 之後,所有請求都會變成 -2015,而官方訊息會先把人帶去懷疑金鑰。
        // Every request turns into -2015 after a home connection changes address, and the official wording
        // sends the reader to suspect the key first.
        var error = BinanceErrorMapper.Map(
            BinanceApiErrorCodes.RejectedApiKey,
            "Invalid API-key, IP, or permissions for action.");

        Assert.AreEqual(TradeErrorCodes.InvalidCredentials, error.Code);
        Assert.IsFalse(error.IsTransient);
        StringAssert.Contains(error.Message, "白名單", StringComparison.Ordinal);
        StringAssert.Contains(error.Message, "allowlist", StringComparison.Ordinal);
    }

    [TestMethod]
    public void OutboundIpCanBeAttachedByTheApplicationLayer()
    {
        var error = BinanceErrorMapper.WithOutboundIpAddress(
            BinanceErrorMapper.Map(BinanceApiErrorCodes.RejectedApiKey, "Invalid API-key, IP, or permissions."),
            "203.0.113.7");

        StringAssert.Contains(error.Message, "203.0.113.7", StringComparison.Ordinal);
        Assert.IsTrue(error.TryGetData(BinanceErrorMapper.OutboundIpAddressDataKey, out var ip));
        Assert.AreEqual("203.0.113.7", ip);

        // 原有的診斷資料不能在補充時掉光。
        // The existing diagnostics must survive the enrichment.
        Assert.IsTrue(error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var raw));
        Assert.AreEqual(-2015L, raw);
    }

    [TestMethod]
    public void OutboundIpHelperValidatesItsArguments()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceErrorMapper.WithOutboundIpAddress(null!, "203.0.113.7"));

        Assert.ThrowsExactly<ArgumentException>(
            () => BinanceErrorMapper.WithOutboundIpAddress(BinanceErrorMapper.Map(-1000, "x"), "  "));
    }

    [TestMethod]
    public void InsufficientMarginTellsRiskManagementToSizeDown()
    {
        var error = BinanceErrorMapper.Map(BinanceApiErrorCodes.MarginNotSufficient, "Margin is insufficient.");

        Assert.AreEqual(TradeErrorCodes.InsufficientMargin, error.Code);
        Assert.IsFalse(error.IsTransient);
        StringAssert.Contains(error.Message, "縮小部位", StringComparison.Ordinal);
    }

    [TestMethod]
    public void MinimumNotionalCarriesTheKnownThresholds()
    {
        var error = BinanceErrorMapper.Map(BinanceApiErrorCodes.MinNotional, "Order's notional must be no smaller than 5.0.");

        Assert.AreEqual(TradeErrorCodes.NotionalBelowMinimum, error.Code);
        StringAssert.Contains(error.Message, "MIN_NOTIONAL", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UnmappedCodesAreSurfacedRatherThanSwallowed()
    {
        var error = BinanceErrorMapper.Map(-9999, "A code nobody has seen before.");

        Assert.AreEqual(TradeErrorCodes.UnknownExchangeError, error.Code);
        Assert.AreEqual(ErrorCategory.Unexpected, error.Category);
        Assert.IsFalse(error.IsTransient);
        Assert.IsTrue(error.TryGetInt64(BinanceErrorDataKeys.ApiCode, out var raw));
        Assert.AreEqual(-9999L, raw);
        Assert.IsTrue(error.TryGetData(BinanceErrorDataKeys.ApiMessage, out var message));
        Assert.AreEqual("A code nobody has seen before.", message);
    }

    [TestMethod]
    public void MissingMessagesDoNotProduceABlankError()
    {
        var error = BinanceErrorMapper.Map(-1121, null);

        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
        StringAssert.Contains(error.Message, "-1121", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EndpointAndEnvironmentTravelWithTheError()
    {
        // 同一個代碼在 Testnet 與主網的意義可能完全不同,少了環境就分不出來。
        // The same code can mean different things on Testnet and production.
        var error = BinanceErrorMapper.Map(-2019, "Margin is insufficient.", "fapi/v1/order", BinanceEndpoints.Testnet);

        Assert.IsTrue(error.TryGetData(BinanceErrorDataKeys.Endpoint, out var endpoint));
        Assert.AreEqual("fapi/v1/order", endpoint);
        Assert.IsTrue(error.TryGetData(BinanceErrorDataKeys.Environment, out var environment));
        Assert.AreEqual(BinanceEndpoints.Testnet.DisplayName, environment);
    }

    [TestMethod]
    public void ReadsTheBinanceErrorObjectOutOfAResponseBody()
    {
        Assert.IsTrue(BinanceErrorMapper.TryParseApiError(
            """{"code":-2019,"msg":"Margin is insufficient."}""",
            out var code,
            out var message));

        Assert.AreEqual(-2019, code);
        Assert.AreEqual("Margin is insufficient.", message);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not json at all")]
    [DataRow("[1,2,3]")]
    [DataRow("""{"msg":"no code here"}""")]
    [DataRow("""{"code":"-2019","msg":"code is a string"}""")]
    [DataRow("""{"code":-2019,"ms""")]
    public void ReturnsFalseForAnythingThatIsNotABinanceErrorObject(string? body)
    {
        Assert.IsFalse(BinanceErrorMapper.TryParseApiError(body, out _, out _));
    }

    [TestMethod]
    public void ParsesAnErrorObjectWithNoMessageField()
    {
        Assert.IsTrue(BinanceErrorMapper.TryParseApiError("""{"code":-1121}""", out var code, out var message));

        Assert.AreEqual(-1121, code);
        Assert.IsNull(message);
    }

    [TestMethod]
    public void HttpErrorsPreferTheBinanceCodeOverTheStatusCode()
    {
        // 幣安把業務錯誤放在 4xx 的本文裡。只看狀態碼會把「保證金不足」降級成「HTTP 400」。
        // Binance puts business errors in the body of a 4xx; reading only the status degrades "insufficient
        // margin" into "HTTP 400".
        var httpError = HttpErrorMapper.FromStatusCode(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"code":-2019,"msg":"Margin is insufficient."}""");

        var mapped = BinanceErrorMapper.MapHttpError(httpError, "fapi/v1/order", BinanceEndpoints.Mainnet);

        Assert.AreEqual(TradeErrorCodes.InsufficientMargin, mapped.Code);
        Assert.IsTrue(mapped.TryGetInt64(HttpErrorDataKeys.StatusCode, out var status));
        Assert.AreEqual(400L, status);
    }

    [TestMethod]
    public void RateLimitStatusesAreRecognisedWithoutABody()
    {
        var tooManyRequests = BinanceErrorMapper.MapHttpError(
            HttpErrorMapper.FromStatusCode(HttpStatusCode.TooManyRequests));

        Assert.AreEqual(TradeErrorCodes.RateLimited, tooManyRequests.Code);
        Assert.IsTrue(tooManyRequests.IsTransient);
    }

    [TestMethod]
    public void AutomaticBansAreTreatedAsRateLimitingNotAsAParameterProblem()
    {
        // HTTP 418 是幣安的自動封鎖。它落在 4xx,通用對映會判成 Validation,
        // 那會讓上層以為「改參數就好」,實際上要做的是停手等封鎖解除。
        // A 418 is Binance's automatic ban. It falls in the 4xx band where the generic mapping says Validation,
        // which tells the caller to fix its arguments when it must instead stop.
        var banned = BinanceErrorMapper.MapHttpError(HttpErrorMapper.FromStatusCode((HttpStatusCode)418));

        Assert.AreEqual(TradeErrorCodes.RateLimited, banned.Code);
        Assert.AreEqual(ErrorCategory.RateLimited, banned.Category);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, TradeErrorCodes.InvalidCredentials, false)]
    [DataRow(HttpStatusCode.Forbidden, TradeErrorCodes.InvalidCredentials, false)]
    [DataRow(HttpStatusCode.RequestTimeout, TradeErrorCodes.RequestTimeout, true)]
    [DataRow(HttpStatusCode.InternalServerError, TradeErrorCodes.ExchangeUnavailable, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, TradeErrorCodes.ExchangeUnavailable, true)]
    public void TransportStatusesMapToTheirNeutralEquivalents(
        HttpStatusCode statusCode,
        string expectedCode,
        bool expectedTransient)
    {
        var mapped = BinanceErrorMapper.MapHttpError(HttpErrorMapper.FromStatusCode(statusCode));

        Assert.AreEqual(expectedCode, mapped.Code);
        Assert.AreEqual(expectedTransient, mapped.IsTransient);
    }

    [TestMethod]
    [DataRow(HttpErrorCodes.Timeout, TradeErrorCodes.RequestTimeout, ErrorCategory.Timeout)]
    [DataRow(HttpErrorCodes.AttemptTimeout, TradeErrorCodes.RequestTimeout, ErrorCategory.Timeout)]
    [DataRow(HttpErrorCodes.Network, TradeErrorCodes.NetworkFailure, ErrorCategory.Network)]
    [DataRow(HttpErrorCodes.RateLimitTimeout, TradeErrorCodes.RateLimited, ErrorCategory.RateLimited)]
    [DataRow(HttpErrorCodes.RateLimitWeightTooLarge, TradeErrorCodes.RateLimited, ErrorCategory.RateLimited)]
    [DataRow(HttpErrorCodes.SigningSecretMissing, TradeErrorCodes.InvalidSignature, ErrorCategory.Unauthorized)]
    [DataRow(HttpErrorCodes.Cancelled, TradeErrorCodes.RequestTimeout, ErrorCategory.Cancelled)]
    public void PipelineLevelFailuresMapToTheirNeutralEquivalents(
        string httpCode,
        string expectedCode,
        ErrorCategory expectedCategory)
    {
        var mapped = BinanceErrorMapper.MapHttpError(new Error(httpCode, "pipeline failure", ErrorCategory.Internal));

        Assert.AreEqual(expectedCode, mapped.Code);
        Assert.AreEqual(expectedCategory, mapped.Category);
    }

    [TestMethod]
    public void ExhaustedRetriesStayExhaustedAfterTranslation()
    {
        // 「已經替你重試過了」必須傳下去,否則上層會在退避之上再疊一層退避。
        // "We already retried for you" has to survive, or the caller stacks a second backoff on the first.
        var exhausted = Error.Exhausted(HttpErrorCodes.ForStatus(HttpStatusCode.TooManyRequests), "gave up")
            .WithData(HttpErrorDataKeys.StatusCode, 429L)
            .WithData(HttpErrorDataKeys.Body, """{"code":-1003,"msg":"Too many requests."}""");

        var mapped = BinanceErrorMapper.MapHttpError(exhausted, "fapi/v1/exchangeInfo", BinanceEndpoints.Mainnet);

        Assert.AreEqual(TradeErrorCodes.RateLimited, mapped.Code);
        Assert.AreEqual(ErrorCategory.Exhausted, mapped.Category);
        Assert.IsFalse(mapped.IsTransient);
    }

    [TestMethod]
    public void RetryAfterSurvivesTheTranslation()
    {
        var httpError = HttpErrorMapper
            .FromStatusCode(HttpStatusCode.TooManyRequests)
            .WithData(HttpErrorDataKeys.RetryAfterSeconds, 30m);

        var mapped = BinanceErrorMapper.MapHttpError(httpError);

        Assert.IsTrue(mapped.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out var seconds));
        Assert.AreEqual(30m, seconds);
    }

    [TestMethod]
    public void TheOriginalExceptionIsNotLost()
    {
        var inner = new InvalidOperationException("socket closed");
        var httpError = new Error(HttpErrorCodes.Network, "connection failed", ErrorCategory.Network)
        {
            Exception = inner,
        };

        Assert.AreSame(inner, BinanceErrorMapper.MapHttpError(httpError).Exception);
    }

    [TestMethod]
    public void MapHttpErrorRejectsANullError()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => BinanceErrorMapper.MapHttpError(null!));
    }

    [TestMethod]
    public void EveryMappedCodeStaysInsideTheNeutralVocabulary()
    {
        // 上層只認得 trade. 開頭的代碼。對映若吐出別的前綴,那個錯誤在上層就等於沒有分類。
        // Upper layers only recognise trade. codes; any other prefix is uncategorised as far as they see.
        int[] codes =
        [
            -1000, -1001, -1002, -1003, -1006, -1007, -1008, -1013, -1014, -1015, -1016, -1020, -1021, -1022,
            -1100, -1101, -1102, -1103, -1104, -1105, -1106, -1108, -1109, -1110, -1111, -1112, -1114, -1115,
            -1116, -1117, -1118, -1119, -1120, -1121, -1125, -1127, -1128, -1130, -1136,
            -2010, -2011, -2012, -2013, -2014, -2015, -2016, -2017, -2018, -2019, -2020, -2021, -2022, -2023,
            -2024, -2025, -2026, -2027, -2028,
            -4003, -4013, -4014, -4015, -4016, -4028, -4046, -4047, -4048, -4049, -4050, -4051, -4055, -4061,
            -4062, -4067, -4068, -4131, -4141, -4164, -4165, -4183, -4184,
            -9999,
        ];

        foreach (var code in codes)
        {
            StringAssert.StartsWith(BinanceErrorMapper.Map(code, "message").Code, "trade.", StringComparison.Ordinal);
        }
    }
}
