using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Ozakboy.Http.Retry;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 以正式註冊(<see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>)組出的管線為對象的測試:
/// 重試時的時間戳,以及錯誤離開用戶端前的祕密遮罩。
/// Tests against the pipeline the real registration builds: the timestamp on a retry, and the masking of secrets
/// before an error leaves the client.
/// </summary>
/// <remarks>
/// <para>
/// 只換掉最內層的傳輸(<c>ConfigurePrimaryHttpMessageHandler</c>),重試、限流、簽章、日誌、遮罩器與門面
/// 全部是 <c>AddBinanceFutures</c> 註冊的那一份。這兩件事都取決於註冊時的設定 —— 簽章設定有沒有複製
/// <c>TimestampParameterName</c>、門面有沒有拿到這個用戶端的遮罩器 —— 用手組的 <see cref="TestPipeline"/> 驗不到。
/// Only the innermost transport is replaced; retry, rate limiting, signing, logging, the masker and the facade
/// are all the ones <c>AddBinanceFutures</c> registers. Both behaviours depend on registration-time settings —
/// whether the signing options copy <c>TimestampParameterName</c>, whether the facade receives this client's
/// masker — which the hand-built <see cref="TestPipeline"/> cannot reach.
/// </para>
/// <para>
/// <b>時間戳那一條被故意弄壞驗證過。</b>暫時拿掉 <c>CopySigning</c> 裡複製 <c>TimestampParameterName</c> 的那一行,
/// 第二次嘗試送出的仍是第一次的時間戳,<see cref="ARetryIsSignedWithTheTimeOfTheSecondAttemptAndTheSignatureStillVerifies"/>
/// 變紅;改回來即綠。
/// <b>The timestamp test has been verified by breaking it on purpose.</b> With the line in <c>CopySigning</c> that
/// copies <c>TimestampParameterName</c> removed, the second attempt went out with the first attempt's timestamp
/// and the test went red; restoring the line turned it green.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceHttpPipelineRegistrationTests
{
    // 明顯的假值,長度超過遮罩器登記的下限(8 個字元),所以會被登記成已知祕密。
    // Obviously fake, and longer than the masker's registration minimum of eight characters, so both are
    // registered as known secrets.
    private const string ApiKey = "FAKE-API-KEY-NOT-A-REAL-CREDENTIAL";
    private const string SecretKey = "FAKE-SECRET-NOT-A-REAL-CREDENTIAL";
    private const string Symbol = "BTCUSDT";

    [TestMethod]
    public void TheSigningOptionsRestampTheBinanceTimestampParameter()
    {
        // 手動組管線的呼叫端也拿 CreateSigningOptions,所以時間戳參數名必須在這裡就設好,不能只靠 DI 註冊時補。
        // Callers who assemble the pipeline by hand use CreateSigningOptions too, so the timestamp name has to be
        // set here rather than only patched in at DI registration.
        var signing = new BinanceOptions().CreateSigningOptions();

        Assert.AreEqual(BinanceConstants.TimestampParameterName, signing.TimestampParameterName);
    }

    [TestMethod]
    public async Task ARetryIsSignedWithTheTimeOfTheSecondAttemptAndTheSignatureStillVerifies()
    {
        var clock = TestClock.AtFixedInstant();
        var start = clock.GetUtcNow();

        // 兩次嘗試之間的間隔刻意比預設 recvWindow 長:沿用第一次時間戳的重試,在幣安上會以 -1021 被拒。
        // The gap between the attempts is deliberately longer than the default recvWindow: a retry reusing the
        // first attempt's timestamp would be rejected by Binance with -1021.
        var gap = BinanceOptions.DefaultRecvWindow + TimeSpan.FromSeconds(2);

        var stub = new StubHttpMessageHandler((_, attempt) =>
        {
            if (attempt == 1)
            {
                clock.Advance(gap);

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Service Unavailable"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Fixtures.CancelAll, Encoding.UTF8, "application/json"),
            };
        });

        using var provider = Build(stub, clock, TestPipeline.ImmediateRetries);

        var result = await provider.GetRequiredService<BinanceFuturesClient>().CancelAllOrdersAsync(Symbol);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(2, stub.CallCount, "503 之後應該恰好重試一次。A 503 should be retried exactly once.");

        var first = stub.Requests[0];
        var second = stub.Requests[1];

        Assert.AreEqual(
            UnixMilliseconds(start),
            first.Parameter(BinanceConstants.TimestampParameterName),
            "第一次嘗試的時間戳應該是當下時間。The first attempt should carry the current time.");

        Assert.AreEqual(
            UnixMilliseconds(start + gap),
            second.Parameter(BinanceConstants.TimestampParameterName),
            "重試沿用了第一次的時間戳,退避一久就會被幣安以 -1021 拒絕。The retry reused the first attempt's timestamp, which Binance rejects with -1021 once the backoff is long enough.");

        // 在原位換掉,不是附加在尾端:待簽字串的參數順序兩次完全相同。
        // Replaced in place rather than appended: the parameter order of the signed string is identical both times.
        CollectionAssert.AreEqual(
            first.ParameterNames.ToList(),
            second.ParameterNames.ToList(),
            "重試改變了參數順序。The retry changed the parameter order.");

        // 簽章是對「新的」待簽字串算的,拿同一把密鑰重算必須一致。
        // The signature covers the new canonical string; recomputing it with the same secret must agree.
        Assert.AreEqual(ExpectedSignature(first.Query), first.Parameter(BinanceConstants.SignatureParameterName));
        Assert.AreEqual(ExpectedSignature(second.Query), second.Parameter(BinanceConstants.SignatureParameterName));
        Assert.AreNotEqual(
            first.Parameter(BinanceConstants.SignatureParameterName),
            second.Parameter(BinanceConstants.SignatureParameterName),
            "時間戳換了,簽章卻沒換。The timestamp changed but the signature did not.");

        Assert.AreEqual(ApiKey, second.Headers[BinanceConstants.ApiKeyHeaderName]);
    }

    [TestMethod]
    public async Task AnApiKeyEchoedInAnErrorBodyIsMaskedBeforeTheErrorLeavesTheClient()
    {
        // 對方把金鑰 echo 回錯誤本文。本文摘要進 Error.Data、幣安訊息進 Error.Message,
        // 兩者都只有門面那一道遮罩攔得到 —— 前提是門面拿到的是這個用戶端的遮罩器。
        // The peer echoes the key back in the error body. The body snippet goes into Error.Data and Binance's
        // message into Error.Message, and only the facade's masking can catch either — provided the facade was
        // given this client's masker.
        var echoed = $$"""{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action. key={{ApiKey}} secret={{SecretKey}}"}""";
        var stub = StubHttpMessageHandler.Json(echoed, HttpStatusCode.Unauthorized);

        using var provider = Build(stub, TestClock.AtFixedInstant(), RetryPolicy.NoRetry);

        var result = await provider.GetRequiredService<BinanceFuturesClient>().CancelAllOrdersAsync(Symbol);

        // 先確認 canary 真的就是送出去的那把金鑰,否則「沒出現」什麼也證明不了。
        // First confirm the canary really is the key that was sent; otherwise "it never appeared" proves nothing.
        Assert.AreEqual(ApiKey, stub.LastRequest.Headers[BinanceConstants.ApiKeyHeaderName]);

        Assert.IsTrue(result.IsFailure);

        // 本文仍然讀得懂:遮罩換掉的是祕密,不是整段本文,-2015 的語意沒有丟。
        // The body is still readable: masking replaced the secrets, not the whole body, so -2015 survives.
        Assert.AreEqual(TradeErrorCodes.InvalidCredentials, result.Error!.Code);

        AssertCarriesNoCredential(result.Error);
    }

    [TestMethod]
    public async Task AnApiKeyInATransportExceptionIsMaskedBeforeTheErrorLeavesTheClient()
    {
        // 傳輸層例外的訊息常常帶著請求的細節。這裡讓它直接帶上送出的金鑰標頭值。
        // A transport exception's message often carries details of the request; here it carries the key header
        // that was actually sent.
        const string Marker = "transport-canary-marker";

        var stub = new StubHttpMessageHandler((request, _) => throw new HttpRequestException(
            $"{Marker}: connection reset while sending {request.Headers.GetValues(BinanceConstants.ApiKeyHeaderName).Single()}"));

        using var provider = Build(stub, TestClock.AtFixedInstant(), RetryPolicy.NoRetry);

        var result = await provider.GetRequiredService<BinanceFuturesClient>().CancelAllOrdersAsync(Symbol);

        Assert.IsTrue(result.IsFailure);

        // 例外訊息確實走到了錯誤裡 —— 金鑰不見是被遮掉,而不是整段訊息被丟掉。
        // The exception message did reach the error, so the key's absence is masking rather than the whole message
        // being dropped.
        Assert.Contains(
            Marker,
            result.Error!.Message,
            StringComparison.Ordinal,
            "例外訊息沒有進到錯誤,這條測試驗不到遮罩。The exception message never reached the error, so masking went untested.");

        AssertCarriesNoCredential(result.Error);
    }

    private static ServiceProvider Build(StubHttpMessageHandler stub, TimeProvider clock, RetryPolicy retryPolicy)
    {
        var services = new ServiceCollection();

        services.AddSingleton(clock);

        services.AddBinanceFutures(options =>
        {
            options.Environment = BinanceEnvironment.Testnet;
            options.ApiKey = ApiKey;
            options.SecretKey = SecretKey;
            options.Retry.Policy = retryPolicy;
        });

        // 只換掉最內層的傳輸,其餘全部是正式註冊的那一份。
        // Only the innermost transport is replaced; everything else is what the real registration built.
        services
            .AddHttpClient(BinanceConstants.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        return services.BuildServiceProvider();
    }

    private static string UnixMilliseconds(DateTimeOffset instant) =>
        instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 以測試用的假密鑰重算幣安簽章:HMAC-SHA256、小寫十六進位,待簽字串是 <c>signature</c> 之前的整段查詢字串。
    /// Recomputes the Binance signature with the fake test secret: HMAC-SHA256 in lower-case hex over the whole
    /// query string before <c>signature</c>.
    /// </summary>
    /// <param name="query">實際送出的查詢字串。The query string actually sent.</param>
    /// <returns>應有的簽章。The expected signature.</returns>
    private static string ExpectedSignature(string query)
    {
        var marker = $"&{BinanceConstants.SignatureParameterName}=";
        var index = query.LastIndexOf(marker, StringComparison.Ordinal);

        Assert.IsTrue(index > 0, "查詢字串裡找不到簽章。No signature in the query string.");

        var canonical = query[..index];
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// 斷言錯誤的訊息、每一筆資料與例外文字都不含 API 金鑰與密鑰。
    /// Asserts that neither the API key nor the secret appears in the error's message, any data entry, or the
    /// exception text.
    /// </summary>
    /// <param name="error">要檢查的錯誤。The error to inspect.</param>
    private static void AssertCarriesNoCredential(Error error)
    {
        foreach (var secret in new[] { ApiKey, SecretKey })
        {
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal, "Error.Message 含有已登記的祕密。");

            if (error.Data is not null)
            {
                foreach (var (key, value) in error.Data)
                {
                    Assert.DoesNotContain(
                        secret,
                        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                        StringComparison.Ordinal,
                        $"Error.Data[{key}] 含有已登記的祕密。");
                }
            }

            Assert.DoesNotContain(
                secret,
                error.Exception?.ToString() ?? string.Empty,
                StringComparison.Ordinal,
                "Error.Exception 含有已登記的祕密。");
        }
    }
}
