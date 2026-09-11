using Ozakboy.Http;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 組出與正式註冊相同順序的處理器管線,但最內層換成 <see cref="StubHttpMessageHandler"/>。
/// Assembles the same handler order as the production registration, with a
/// <see cref="StubHttpMessageHandler"/> at the innermost position.
/// </summary>
/// <remarks>
/// 順序刻意與 <c>AddOzakboyHttpPipeline</c> 一致(簽章 → 限流 → 重試 → 內層),
/// 否則測到的就不是實際會跑的那條路徑 —— 例如簽章若不是最外層,限流器看到的 URL 就還沒被改寫,
/// 那種差異在測試裡看不出來,上線才會出問題。
/// The order deliberately matches <c>AddOzakboyHttpPipeline</c> — signing, rate limiting, retry, inner —
/// because otherwise the tests exercise a different path from the one that actually runs.
/// </remarks>
internal static class TestPipeline
{
    /// <summary>
    /// 建立測試用的設定:指向 Testnet 以外的環境時請自行覆寫,憑證一律是明顯的假值。
    /// Builds the test settings. Credentials are always obviously fake values.
    /// </summary>
    /// <summary>
    /// 不退避、最多三次的重試策略。用來驗證「哪些請求會被重試」,而不必真的等掉那幾百毫秒。
    /// A three-attempt policy with no back-off, so that "which requests get retried" can be asserted without
    /// waiting out the delays.
    /// </summary>
    public static RetryPolicy ImmediateRetries { get; } = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.Zero,
        Strategy = BackoffStrategy.None,
        JitterRatio = 0d,
    };

    public static BinanceOptions CreateOptions(
        BinanceEnvironment environment = BinanceEnvironment.Mainnet,
        bool withCredentials = true,
        RetryPolicy? retryPolicy = null)
    {
        var options = new BinanceOptions
        {
            Environment = environment,
            RecvWindow = TimeSpan.FromSeconds(5),
        };

        if (withCredentials)
        {
            // 明顯的假值。真實金鑰一律由環境變數注入,絕不出現在任何檔案裡。
            // Obviously fake. Real credentials come from environment variables and never appear in a file.
            options.ApiKey = "FAKE-API-KEY-NOT-A-REAL-CREDENTIAL";
            options.SecretKey = "FAKE-SECRET-NOT-A-REAL-CREDENTIAL";
        }

        // 預設關掉重試,避免一個失敗案例被重試三次而讓呼叫次數的斷言失準。
        // 專門要驗重試行為的測試自己傳 ImmediateRetries 進來。
        // Retries are off by default, or one failing case would become three calls and break the call-count
        // assertions. The tests that exist to exercise retrying pass ImmediateRetries in themselves.
        options.Retry.Policy = retryPolicy ?? RetryPolicy.NoRetry;

        return options;
    }

    public static (HttpPipelineClient Client, HttpClient Http) Create(
        BinanceOptions options,
        StubHttpMessageHandler stub,
        TimeProvider timeProvider)
    {
        var retry = new RetryHandler(options.Retry, options.Timeouts, timeProvider) { InnerHandler = stub };
        var rateLimiting = new RateLimitingHandler(options.CreateRateLimitOptions(), timeProvider)
        {
            InnerHandler = retry,
        };
        var signing = new SigningHandler(options.CreateSigningOptions()) { InnerHandler = rateLimiting };

        var http = new HttpClient(signing)
        {
            BaseAddress = options.ResolveEndpoints().RestBaseUri,
        };

        return (new HttpPipelineClient(http, options.Timeouts, timeProvider), http);
    }
}
