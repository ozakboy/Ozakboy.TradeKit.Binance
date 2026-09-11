using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Ozakboy.Http;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 把幣安行情來源註冊進相依注入容器。
/// Registers the Binance market data source with the dependency injection container.
/// </summary>
/// <remarks>
/// <para>
/// 必須接在 <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/> 之後:
/// <see cref="BinanceOptions"/> 與 <see cref="HttpPipelineClient"/> 由那一步註冊,這裡只取用。
/// 分成兩個方法而不是併進 <c>AddBinanceFutures</c>,是因為行情串流會建立長命連線,
/// 而只想查交易規則或帳戶的宿主不該被迫帶上一個 WebSocket 客戶端。
/// This must follow <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>, which registers the
/// <see cref="BinanceOptions"/> and the <see cref="HttpPipelineClient"/> that this method only consumes. It is
/// a separate call rather than part of <c>AddBinanceFutures</c> because market streams open long-lived
/// connections, and a host that only wants trading rules or an account balance should not be made to carry a
/// WebSocket client it never uses.
/// </para>
/// </remarks>
public static class BinanceMarketDataServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 <see cref="BinanceMarketDataFeed"/> 與 <see cref="IMarketDataFeed"/>。
    /// Registers <see cref="BinanceMarketDataFeed"/> and <see cref="IMarketDataFeed"/>.
    /// </summary>
    /// <param name="services">服務集合。The service collection.</param>
    /// <param name="configure">
    /// 調整串流設定,未提供時採用預設值。
    /// Adjusts the stream settings; the defaults are used when none is supplied.
    /// </param>
    /// <returns>同一個服務集合,方便串接。The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 串流設定不合法時擲出。Thrown when the stream settings are invalid.
    /// </exception>
    public static IServiceCollection AddBinanceMarketData(
        this IServiceCollection services,
        Action<BinanceMarketStreamOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var streamOptions = new BinanceMarketStreamOptions();

        configure?.Invoke(streamOptions);

        // 在註冊當下就驗,而不是等到第一次訂閱。設定錯誤若延後到第一筆行情才爆,
        // 宿主會先看到「啟動成功」,幾秒後才在背景 Task 裡失敗。
        // Validated at registration rather than at the first subscription: deferring the check until the first
        // frame lets the host see a successful start-up and then fail seconds later inside a background task.
        var validation = streamOptions.Validate();

        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(configure));
        }

        services.AddSingleton(streamOptions);

        services.AddSingleton(provider => new BinanceMarketDataFeed(
            provider.GetRequiredService<HttpPipelineClient>(),
            provider.GetRequiredService<BinanceOptions>(),
            provider.GetRequiredService<BinanceMarketStreamOptions>(),
            provider.GetService<ILoggerFactory>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IWebSocketConnectionFactory>()));

        services.AddSingleton<IMarketDataFeed>(
            provider => provider.GetRequiredService<BinanceMarketDataFeed>());

        return services;
    }
}
