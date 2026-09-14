using Microsoft.Extensions.DependencyInjection;

using Ozakboy.Http;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 組出「不帶憑證、關掉簽章」的公開行情 REST 用戶端,組法與下游混接主網公開行情時相同。
/// Assembles a credential-free, signing-off public market data REST client the way a downstream application does
/// when it mixes in production public market data.
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要另外有這一份。</b><see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/> 一律以
/// <c>EnableSigning = true</c> 註冊管線,手組的 <see cref="TestPipeline"/> 也一律掛著 <c>SigningHandler</c>;
/// 而在 <c>Ozakboy.Http</c> 0.3.2 以前,把 <c>WithQueryParameters</c> 的參數寫進位址的<b>只有</b>簽章處理器。
/// 本套件自己的兩條組法因此都碰不到「簽章關掉時 query 參數整個消失」這件事,只有照下游那樣
/// 以 <c>AddOzakboyHttpPipeline</c> 關掉簽章自己組,才走得到那條路徑。
/// <b>Why this exists separately.</b> <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/> always
/// registers the pipeline with <c>EnableSigning = true</c>, and the hand-built <see cref="TestPipeline"/> always
/// includes <c>SigningHandler</c>; before <c>Ozakboy.Http</c> 0.3.3 the signing handler was the <b>only</b> thing
/// that wrote <c>WithQueryParameters</c> into the URI. Neither of this package's own assemblies can therefore reach
/// "switching signing off drops the query parameters"; only assembling a pipeline with signing off through
/// <c>AddOzakboyHttpPipeline</c>, as the downstream application does, travels that path.
/// </para>
/// <para>
/// 設定值照 <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/> 內部的複製方法抄一遍(那幾個方法是
/// 私有的):權重表、重試政策、逾時與脫敏名單。門面也與下游相同,以已取得的 <see cref="HttpClient"/> 建立。
/// The settings are copied the way the private copy helpers inside
/// <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/> do it — weight table, retry policy, timeouts
/// and sensitive names — and the facade is built over an obtained <see cref="HttpClient"/>, as downstream does.
/// </para>
/// </remarks>
internal static class UnsignedPublicPipeline
{
    /// <summary>
    /// 這條管線的具名用戶端名稱,刻意不與 <see cref="BinanceConstants.HttpClientName"/> 同名。
    /// The named client for this pipeline, deliberately distinct from <see cref="BinanceConstants.HttpClientName"/>.
    /// </summary>
    public const string HttpClientName = "Ozakboy.TradeKit.Binance.Tests.UnsignedPublicMarketData";

    /// <summary>
    /// 建立服務容器與行情來源。
    /// Builds the service provider and the market data feed.
    /// </summary>
    /// <param name="options">行情用的設定;<b>不可帶憑證</b>。The market data options; <b>must carry no credential</b>.</param>
    /// <param name="primaryHandler">
    /// 最內層傳輸;<see langword="null"/> 時走真正的網路。
    /// The innermost transport; the real network when <see langword="null"/>.
    /// </param>
    /// <param name="timeProvider">時間來源;<see langword="null"/> 時為系統時鐘。The time source; the system clock when <see langword="null"/>.</param>
    /// <returns>服務容器(呼叫端負責釋放)與行情來源。The provider, which the caller disposes, and the feed.</returns>
    public static (ServiceProvider Provider, BinanceMarketDataFeed Feed) Create(
        BinanceOptions options,
        HttpMessageHandler? primaryHandler = null,
        TimeProvider? timeProvider = null)
    {
        Assert.IsTrue(
            string.IsNullOrEmpty(options.ApiKey) && string.IsNullOrEmpty(options.SecretKey),
            "公開行情的管線不可帶任何憑證。The public market data pipeline must carry no credential.");

        var services = new ServiceCollection();

        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        var builder = services
            .AddHttpClient(HttpClientName, client => client.BaseAddress = options.ResolveEndpoints().RestBaseUri);

        if (primaryHandler is not null)
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => primaryHandler);
        }

        builder.AddOzakboyHttpPipeline(pipeline =>
        {
            // 關鍵的一行:與下游相同,不開簽章、連簽章處理器都不掛。
            // The line that matters: as downstream, signing is off and the signing handler is not attached.
            pipeline.EnableSigning = false;
            pipeline.EnableRateLimiting = true;

            var rateLimiting = options.CreateRateLimitOptions();
            pipeline.RateLimiting.DefaultWeight = rateLimiting.DefaultWeight;
            pipeline.RateLimiting.AcquisitionTimeout = rateLimiting.AcquisitionTimeout;
            pipeline.RateLimiting.Buckets.Clear();

            foreach (var bucket in rateLimiting.Buckets)
            {
                pipeline.RateLimiting.Buckets.Add(bucket);
            }

            pipeline.Retry.Policy = options.Retry.Policy;
            pipeline.Retry.RespectRetryAfter = options.Retry.RespectRetryAfter;
            pipeline.Retry.MaxRetryAfter = options.Retry.MaxRetryAfter;
            pipeline.Retry.ErrorBodySnippetLength = options.Retry.ErrorBodySnippetLength;

            pipeline.Timeouts.AttemptTimeout = options.Timeouts.AttemptTimeout;
            pipeline.Timeouts.OverallTimeout = options.Timeouts.OverallTimeout;

            var logging = options.CreateLoggingOptions();
            pipeline.Logging.LogRequestBody = logging.LogRequestBody;
            pipeline.Logging.LogResponseBody = logging.LogResponseBody;
            pipeline.Logging.MaxBodyLength = logging.MaxBodyLength;
            pipeline.Logging.AdditionalSensitiveParameterNames.Clear();

            foreach (var name in logging.AdditionalSensitiveParameterNames)
            {
                pipeline.Logging.AdditionalSensitiveParameterNames.Add(name);
            }
        });

        var provider = services.BuildServiceProvider();

        var feed = new BinanceMarketDataFeed(
            new HttpPipelineClient(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                options.Timeouts,
                timeProvider),
            options,
            timeProvider: timeProvider);

        return (provider, feed);
    }
}
