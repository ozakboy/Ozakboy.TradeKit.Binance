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
/// <para>
/// 順序刻意與 <c>Ozakboy.Http</c> 0.3.0 的 <c>AddOzakboyHttpPipeline</c> 一致(重試 → 限流 → 簽章 → 內層),
/// 否則測到的就不是實際會跑的那條路徑 —— 例如重試若在簽章之外,每次重試都會沿用第一次的時間戳,
/// 那種差異在測試裡看不出來,上線才會以 <c>-1021</c> 出現。
/// The order deliberately matches <c>AddOzakboyHttpPipeline</c> in <c>Ozakboy.Http</c> 0.3.0 — retry, rate
/// limiting, signing, inner — because otherwise the tests exercise a different path from the one that actually
/// runs; with retry outside signing, for instance, every retry would reuse the first attempt's timestamp.
/// </para>
/// <para>
/// 0.1.1 以前這裡是「簽章 → 限流 → 重試」,那是照著 <c>Ozakboy.Http</c> 0.2.0 的實際順序抄的;0.3.0 修正了那個順序,
/// 這裡跟著改。簽章處理器拿的是注入的時鐘,時間戳的斷言才對得上假時鐘。
/// Up to 0.1.1 this read "signing, rate limiting, retry", copied from the actual order in <c>Ozakboy.Http</c>
/// 0.2.0; 0.3.0 corrected that order and this follows. The signing handler takes the injected clock so that
/// timestamp assertions line up with the fake one.
/// </para>
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
        var signing = new SigningHandler(options.CreateSigningOptions(), timeProvider) { InnerHandler = stub };
        var rateLimiting = new RateLimitingHandler(options.CreateRateLimitOptions(), timeProvider)
        {
            InnerHandler = signing,
        };
        var retry = new RetryHandler(options.Retry, options.Timeouts, timeProvider) { InnerHandler = rateLimiting };

        var http = new HttpClient(retry)
        {
            BaseAddress = options.ResolveEndpoints().RestBaseUri,
        };

        return (new HttpPipelineClient(http, options.Timeouts, timeProvider), http);
    }
}
