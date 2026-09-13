using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Ozakboy.Http;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 把幣安帳戶私有資料來源註冊進相依注入容器。
/// Registers the Binance private account data source with the dependency injection container.
/// </summary>
/// <remarks>
/// <para>
/// 必須接在 <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/> 之後:
/// <see cref="BinanceOptions"/> 與 <see cref="HttpPipelineClient"/> 由那一步註冊,這裡只取用。
/// 分成獨立的方法而不是併進 <c>AddBinanceFutures</c>,是因為這條串流會建立長命連線、
/// 會在背景每 30 分鐘打一次 REST 續期憑證,而且<b>必須有 API 憑證</b> ——
/// 只想查公開行情或交易規則的宿主不該被迫帶上這些。
/// This must follow <see cref="BinanceServiceCollectionExtensions.AddBinanceFutures"/>, which registers the
/// <see cref="BinanceOptions"/> and the <see cref="HttpPipelineClient"/> that this method only consumes. It is
/// a separate call rather than part of <c>AddBinanceFutures</c> because this stream opens a long-lived
/// connection, renews its credential over REST every 30 minutes in the background, and <b>requires API
/// credentials</b> — none of which a host that only wants public market data or trading rules should carry.
/// </para>
/// <para>
/// 這一步還會把 <c>AddBinanceFutures</c> 建立的那個遮罩器交給串流,讓執行期取得的 listenKey 一拿到就被登記成
/// 字面祕密。所以順序不只是「取得設定」而已:沒有前一步就沒有那個遮罩器,解析 <see cref="IUserDataFeed"/> 時會
/// 以 <see cref="InvalidOperationException"/> 當場失敗,而不是安靜地少掉一道保護。
/// This step also hands the stream the masker that <c>AddBinanceFutures</c> created, so that a listenKey obtained
/// at run time is registered as a literal secret the moment it arrives. The ordering is therefore about more than
/// settings: without the earlier call there is no such masker, and resolving <see cref="IUserDataFeed"/> fails
/// outright with an <see cref="InvalidOperationException"/> rather than quietly going one guard short.
/// </para>
/// <para>
/// 註冊為單例是設計的一部分,不是慣例:帳戶同時只有一把串流憑證,兩個實例會互相搶那一把
/// —— 後建立的那個會延長同一把憑證的效期,而任何一方 <c>DELETE</c> 都會把另一方的串流一起弄斷,
/// 而那一方只會看到「連線莫名其妙斷了」。
/// The singleton lifetime is part of the design rather than a convention: an account holds one stream
/// credential at a time and two instances would contend for it — the second extends the same key, and a
/// <c>DELETE</c> from either kills the other's stream, which the other sees only as a connection that dropped
/// for no apparent reason.
/// </para>
/// </remarks>
public static class BinanceUserDataServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 <see cref="BinanceUserDataFeed"/> 與 <see cref="IUserDataFeed"/>。
    /// Registers <see cref="BinanceUserDataFeed"/> and <see cref="IUserDataFeed"/>.
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
    public static IServiceCollection AddBinanceUserData(
        this IServiceCollection services,
        Action<BinanceUserDataStreamOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var streamOptions = new BinanceUserDataStreamOptions();

        configure?.Invoke(streamOptions);

        // 在註冊當下就驗,而不是等到第一次訂閱。設定錯誤若延後到第一則帳戶事件才爆,
        // 宿主會先看到「啟動成功」,而真正發現的時機是某一次成交沒有進來。
        // Validated at registration rather than at the first subscription: deferring the check lets the host see
        // a successful start-up, and the real discovery happens when some fill fails to arrive.
        var validation = streamOptions.Validate();

        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error!.Message, nameof(configure));
        }

        services.AddSingleton(streamOptions);

        // 遮罩器一定要從註冊處取回<b>同一個</b>實例:在別的遮罩器上登記 listenKey 會登記成功,
        // 但真正在遮日誌與錯誤的是這個具名用戶端的那一個,兩者不同等於完全沒有保護,而且沒有任何跡象。
        // The masker must be the very instance the registration created: registering the listenKey on a different
        // one succeeds, while the masker actually covering this client's logs and errors is the named client's, so
        // a mismatch leaves the credential entirely unprotected without a hint that anything is wrong.
        services.AddSingleton(provider => new BinanceUserDataFeed(
            provider.GetRequiredService<HttpPipelineClient>(),
            provider.GetRequiredService<BinanceOptions>(),
            provider.GetOzakboyHttpMasker(BinanceConstants.HttpClientName),
            provider.GetRequiredService<BinanceUserDataStreamOptions>(),
            provider.GetService<ILoggerFactory>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IWebSocketConnectionFactory>()));

        services.AddSingleton<IUserDataFeed>(
            provider => provider.GetRequiredService<BinanceUserDataFeed>());

        return services;
    }
}
