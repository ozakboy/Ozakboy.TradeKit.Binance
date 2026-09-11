using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Http;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 把幣安用戶端註冊到相依性注入容器。
/// Registers the Binance client with a dependency injection container.
/// </summary>
public static class BinanceServiceCollectionExtensions
{
    /// <summary>
    /// 註冊幣安 USDⓈ-M 合約用戶端,含 <c>Ozakboy.Http</c> 的簽章、限流、重試與脫敏日誌管線。
    /// Registers the Binance USDⓈ-M futures client together with the <c>Ozakboy.Http</c> signing,
    /// rate-limiting, retry, and log-masking pipeline.
    /// </summary>
    /// <param name="services">服務集合。The service collection.</param>
    /// <param name="configure">
    /// 設定回呼。<b>金鑰請由此從環境變數或安全設定來源注入</b>,本套件不含任何預設憑證。
    /// The configuration callback. <b>Inject credentials here from environment variables or a secure
    /// configuration source</b>; this package ships none.
    /// </param>
    /// <returns>服務集合本身,便於串接。The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// 任一參數為 <see langword="null"/> 時擲出。Thrown when either argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the resulting configuration is invalid.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 註冊的內容:<see cref="BinanceOptions"/>、具名的 <see cref="HttpClient"/>
    /// (<see cref="BinanceConstants.HttpClientName"/>)、<see cref="HttpPipelineClient"/>、
    /// <see cref="BinanceExchangeInfoProvider"/>(同時滿足 <see cref="IExchangeInfoProvider"/>)與
    /// <see cref="BinanceFuturesClient"/>。
    /// Registered: the options, a named <see cref="HttpClient"/>, an <see cref="HttpPipelineClient"/>, the
    /// exchange-info provider (also serving <see cref="IExchangeInfoProvider"/>), and the futures client.
    /// </para>
    /// <para>
    /// 用戶端註冊為<b>單例</b>。交易規則快取掛在 <see cref="BinanceExchangeInfoProvider"/> 的實例上,
    /// 註冊成 scoped 或 transient 會讓每個請求各自重抓一次 <c>exchangeInfo</c> ——
    /// 那不只是浪費,一份約 1 MB 的回應每分鐘抓上幾十次很快就會撞到限流。
    /// The client is registered as a <b>singleton</b>. The trading-rule cache lives on the provider instance,
    /// so a scoped or transient registration would refetch <c>exchangeInfo</c> per request — not merely
    /// wasteful, since a response of roughly one megabyte fetched dozens of times a minute soon meets the rate
    /// limiter.
    /// </para>
    /// <para>
    /// 這個方法<b>只</b>註冊一個環境。同一個行程要同時連 Testnet 與主網時,不要呼叫兩次 ——
    /// 兩次註冊會讓後者覆蓋前者,而解析出來的是哪一個環境完全看註冊順序,那正是這個套件最不想留下的破口。
    /// 請改為各自建立 <see cref="BinanceFuturesClient"/>,由呼叫端明確持有兩個實例。
    /// This method registers <b>one</b> environment. Do not call it twice to reach Testnet and production from
    /// one process: the second registration overwrites the first and which environment resolves depends on
    /// registration order, which is exactly the hole this package exists to close. Construct a
    /// <see cref="BinanceFuturesClient"/> for each instead, so the caller holds both explicitly.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBinanceFutures(
        this IServiceCollection services,
        Action<BinanceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new BinanceOptions();
        configure(options);

        var validation = options.Validate();

        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(configure));
        }

        var endpoints = options.ResolveEndpoints();

        services.AddSingleton(options);

        services
            .AddHttpClient(BinanceConstants.HttpClientName, client => client.BaseAddress = endpoints.RestBaseUri)
            .AddOzakboyHttpPipeline(pipeline =>
            {
                pipeline.EnableSigning = true;
                pipeline.EnableRateLimiting = true;

                CopySigning(options, pipeline);
                CopyRateLimiting(options, pipeline);
                CopyRetry(options, pipeline);
                CopyTimeouts(options, pipeline);
                CopyLogging(options, pipeline);
            });

        services.AddSingleton(provider => new HttpPipelineClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(BinanceConstants.HttpClientName),
            options.Timeouts,
            provider.GetService<TimeProvider>()));

        services.AddSingleton(provider => new BinanceExchangeInfoProvider(
            provider.GetRequiredService<HttpPipelineClient>(),
            options,
            provider.GetService<TimeProvider>()));

        services.AddSingleton<IExchangeInfoProvider>(
            provider => provider.GetRequiredService<BinanceExchangeInfoProvider>());

        services.AddSingleton(provider => new BinanceFuturesClient(
            provider.GetRequiredService<HttpPipelineClient>(),
            options,
            provider.GetRequiredService<BinanceExchangeInfoProvider>(),
            provider.GetService<TimeProvider>()));

        // 以介面註冊,讓策略與風控只相依 IExchangeClient:回測時整個換成記憶體撮合器,
        // 應用層一行都不必改。
        // Registered by interface so that strategies and risk control depend on IExchangeClient alone; a
        // backtest swaps in an in-memory matching engine without the application changing a line.
        services.AddSingleton<IExchangeClient>(provider => provider.GetRequiredService<BinanceFuturesClient>());

        return services;
    }

    private static void CopySigning(BinanceOptions options, HttpPipelineOptions pipeline)
    {
        var signing = options.CreateSigningOptions();

        pipeline.Signing.ApiKey = signing.ApiKey;
        pipeline.Signing.SecretKey = signing.SecretKey;
        pipeline.Signing.ApiKeyHeaderName = signing.ApiKeyHeaderName;
        pipeline.Signing.SignatureParameterName = signing.SignatureParameterName;
        pipeline.Signing.SendApiKeyHeader = signing.SendApiKeyHeader;
        pipeline.Signing.Placement = signing.Placement;
        pipeline.Signing.Algorithm = signing.Algorithm;
    }

    private static void CopyRateLimiting(BinanceOptions options, HttpPipelineOptions pipeline)
    {
        var rateLimiting = options.CreateRateLimitOptions();

        pipeline.RateLimiting.DefaultWeight = rateLimiting.DefaultWeight;
        pipeline.RateLimiting.AcquisitionTimeout = rateLimiting.AcquisitionTimeout;
        pipeline.RateLimiting.Buckets.Clear();

        foreach (var bucket in rateLimiting.Buckets)
        {
            pipeline.RateLimiting.Buckets.Add(bucket);
        }
    }

    private static void CopyRetry(BinanceOptions options, HttpPipelineOptions pipeline)
    {
        pipeline.Retry.Policy = options.Retry.Policy;
        pipeline.Retry.RespectRetryAfter = options.Retry.RespectRetryAfter;
        pipeline.Retry.MaxRetryAfter = options.Retry.MaxRetryAfter;
        pipeline.Retry.ErrorBodySnippetLength = options.Retry.ErrorBodySnippetLength;
    }

    private static void CopyTimeouts(BinanceOptions options, HttpPipelineOptions pipeline)
    {
        pipeline.Timeouts.AttemptTimeout = options.Timeouts.AttemptTimeout;
        pipeline.Timeouts.OverallTimeout = options.Timeouts.OverallTimeout;
    }

    private static void CopyLogging(BinanceOptions options, HttpPipelineOptions pipeline)
    {
        var logging = options.CreateLoggingOptions();

        pipeline.Logging.LogRequestBody = logging.LogRequestBody;
        pipeline.Logging.LogResponseBody = logging.LogResponseBody;
        pipeline.Logging.MaxBodyLength = logging.MaxBodyLength;
        pipeline.Logging.AdditionalSensitiveParameterNames.Clear();

        foreach (var name in logging.AdditionalSensitiveParameterNames)
        {
            pipeline.Logging.AdditionalSensitiveParameterNames.Add(name);
        }
    }
}
