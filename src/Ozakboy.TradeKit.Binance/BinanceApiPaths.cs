namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約的 REST 端點路徑。
/// The REST endpoint paths of the Binance USDⓈ-M futures API.
/// </summary>
/// <remarks>
/// <para>
/// 路徑一律不帶開頭斜線。<see cref="HttpClient.BaseAddress"/> 設為
/// <see cref="BinanceEndpoints.RestBaseUri"/>(無結尾斜線),相對位址若以斜線開頭會被
/// <see cref="Uri"/> 當成「從主機根目錄重新開始」,在有路徑前綴的代理環境下就會悄悄打到錯的位址。
/// Paths never start with a slash. The base address is <see cref="BinanceEndpoints.RestBaseUri"/>, and a
/// relative URI that begins with a slash is treated by <see cref="Uri"/> as restarting from the host root,
/// which quietly targets the wrong address behind a proxy that carries a path prefix.
/// </para>
/// <para>
/// 帳戶與持倉刻意使用 v2 而不是 v3。v3 的回應不含 <c>leverage</c> 與 <c>marginType</c>,
/// 而 <see cref="Position.Leverage"/> 與 <see cref="Position.MarginMode"/> 是抽象層的必要欄位;
/// 用 v3 就只能填預設值,那正是 §12.3 明令禁止的「以預設值猜測」。
/// The account and position endpoints deliberately use v2 rather than v3. The v3 responses carry neither
/// <c>leverage</c> nor <c>marginType</c>, both of which the abstraction requires, and filling them with
/// defaults is exactly the guessing the specification forbids.
/// </para>
/// </remarks>
internal static class BinanceApiPaths
{
    /// <summary>連線測試。Test connectivity.</summary>
    public const string Ping = "fapi/v1/ping";

    /// <summary>伺服器時間。Server time.</summary>
    public const string ServerTime = "fapi/v1/time";

    /// <summary>交易規則與商品清單。Exchange trading rules and symbol list.</summary>
    public const string ExchangeInfo = "fapi/v1/exchangeInfo";

    /// <summary>帳戶資訊(V2)。Account information (V2).</summary>
    public const string Account = "fapi/v2/account";

    /// <summary>持倉風險(V2)。Position information (V2).</summary>
    public const string PositionRisk = "fapi/v2/positionRisk";
}
