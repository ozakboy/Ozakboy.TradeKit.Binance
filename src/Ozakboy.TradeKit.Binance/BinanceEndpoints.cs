namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 一組成套的幣安端點:REST 與 WebSocket 必定屬於同一個環境。
/// A matched set of Binance endpoints: the REST and WebSocket bases always belong to the same environment.
/// </summary>
/// <remarks>
/// <para>
/// 這個型別沒有公開建構式,只能由 <see cref="For(BinanceEnvironment)"/> 取得成套的組合,或由
/// <see cref="CreateOverride"/> 明確建立覆寫。目的是讓「REST 打 Testnet、行情接主網」這種組合
/// 無法在型別層面被組出來 —— 那種錯接不會拋任何例外,只會安靜地讓策略用錯市場的價格下錯市場的單。
/// The type has no public constructor: a set comes either from <see cref="For(BinanceEnvironment)"/> or from
/// an explicit <see cref="CreateOverride"/>. The point is that a REST-on-Testnet, stream-on-production mix
/// cannot be assembled at all — such a mix throws nothing and quietly trades one market on another's prices.
/// </para>
/// <para>
/// 這個型別同時是交易規則快取的鍵。它只描述「連到哪裡」,不含限流門檻或憑證之類會變動的設定,
/// 因此兩份端點相等就真的代表兩份交易規則可以互用;它是 <see langword="record"/>,具備值相等語意,
/// 所以「同一個環境」是可比對的事實,而不是呼叫端自己維護的字串。
/// This type doubles as the key of the trading-rule cache. It describes only where to connect — no rate-limit
/// ceilings, no credentials — so two equal endpoint sets really do mean two interchangeable sets of trading
/// rules. Being a record it has value equality, making "the same environment" a comparable fact rather than a
/// string the caller has to keep straight.
/// </para>
/// </remarks>
public sealed record BinanceEndpoints
{
    private BinanceEndpoints(string displayName, bool isTestnet, Uri restBaseUri, Uri webSocketBaseUri)
    {
        DisplayName = displayName;
        IsTestnet = isTestnet;
        RestBaseUri = restBaseUri;
        WebSocketBaseUri = webSocketBaseUri;
    }

    /// <summary>
    /// 正式環境的端點組合。
    /// The production endpoint set.
    /// </summary>
    /// <remarks>
    /// REST 主機於 2026-09-11 實際呼叫 <c>/fapi/v1/exchangeInfo</c> 與 <c>/fapi/v1/time</c> 驗證可用。
    /// WebSocket 主機於 2026-09-12 經 <c>/market/stream</c> 實際收到公開行情驗證(不帶任何憑證);
    /// 主網的 <c>/private</c> 使用者資料串流未實測 —— 本專案不使用主網憑證。
    /// The REST host was verified on 2026-09-11 by actually calling <c>/fapi/v1/exchangeInfo</c> and
    /// <c>/fapi/v1/time</c>. The WebSocket host was verified on 2026-09-12 by actually receiving public market data
    /// on <c>/market/stream</c> with no credential at all; the production <c>/private</c> user data stream has not
    /// been dialled, because this project uses no production credentials.
    /// </remarks>
    public static BinanceEndpoints Mainnet { get; } = new(
        "Binance USDⓈ-M Mainnet",
        isTestnet: false,
        new Uri("https://fapi.binance.com", UriKind.Absolute),
        new Uri("wss://fstream.binance.com", UriKind.Absolute));

    /// <summary>
    /// 合約測試網的端點組合。
    /// The futures testnet endpoint set.
    /// </summary>
    /// <remarks>
    /// REST 主機於 2026-09-11 實際呼叫 <c>/fapi/v1/exchangeInfo</c> 驗證可用,並確認其交易規則與主網不同
    /// (<c>BTCUSDT</c> 的 <c>stepSize</c> 在此為 <c>0.0001</c>,主網為 <c>0.001</c>)。
    /// The REST host was verified on 2026-09-11, including the confirmation that its trading rules differ from
    /// production: <c>BTCUSDT</c> reports a <c>stepSize</c> of <c>0.0001</c> here against <c>0.001</c> there.
    /// </remarks>
    public static BinanceEndpoints Testnet { get; } = new(
        "Binance USDⓈ-M Testnet",
        isTestnet: true,
        new Uri("https://testnet.binancefuture.com", UriKind.Absolute),
        new Uri("wss://stream.binancefuture.com", UriKind.Absolute));

    /// <summary>
    /// 人類可讀的環境名稱,用於日誌與錯誤訊息。
    /// A human-readable environment name for logs and error messages.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// 是否為測試網。用於在日誌與啟動檢查中明白標示「這不是真錢」或「這是真錢」。
    /// Whether this is the testnet, so logs and start-up checks can say plainly whether real money is at risk.
    /// </summary>
    public bool IsTestnet { get; }

    /// <summary>
    /// REST 基底位址,例如 <c>https://fapi.binance.com</c>,不含路徑。
    /// The REST base address such as <c>https://fapi.binance.com</c>, with no path.
    /// </summary>
    public Uri RestBaseUri { get; }

    /// <summary>
    /// WebSocket 基底位址,例如 <c>wss://fstream.binance.com</c>,不含路徑。
    /// The WebSocket base address such as <c>wss://fstream.binance.com</c>, with no path.
    /// </summary>
    /// <remarks>
    /// 行情串流(<see cref="BinanceMarketDataFeed"/>,走 <c>/market/stream</c>)與使用者資料串流
    /// (<see cref="BinanceUserDataFeed"/>,走 <c>/private/ws?listenKey=…</c>)都從這個位址撥號。它與 REST 位址放在
    /// 同一組端點裡,是為了讓兩種連線永遠指向同一個環境 —— 一旦分成兩處設定,「下單打 Testnet、
    /// 帳戶事件卻從主網來」這種錯接就又變成可能。這裡只提供主機,路由與路徑由各串流自己接上,
    /// 見 <see cref="MarketData.BinanceStreamNames"/>;以 <see cref="CreateOverride"/> 覆寫時同樣只給主機根位址
    /// (或代理前綴),不要把 <c>/market</c> 之類的路由寫進來。
    /// Both the market streams (<see cref="BinanceMarketDataFeed"/>, on <c>/market/stream</c>) and the user data
    /// stream (<see cref="BinanceUserDataFeed"/>, on <c>/private/ws?listenKey=…</c>) dial this address. It sits in the
    /// same set as the REST address so that both kinds of connection always point at one environment: the moment
    /// there are two places to configure, a mismatch such as orders on Testnet with account events from production
    /// becomes possible again. Only the host is fixed here; each stream appends its own route and path — see
    /// <see cref="MarketData.BinanceStreamNames"/>. An override through <see cref="CreateOverride"/> likewise supplies
    /// only the host root, or a proxy prefix, and never a route such as <c>/market</c>.
    /// </remarks>
    public Uri WebSocketBaseUri { get; }

    /// <summary>
    /// 取得指定環境的成套端點。
    /// Gets the matched endpoint set for an environment.
    /// </summary>
    /// <param name="environment">目標環境。The target environment.</param>
    /// <returns>該環境的端點組合。The endpoint set for that environment.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="environment"/> 為未定義的值時擲出。
    /// Thrown when <paramref name="environment"/> is an undefined value.
    /// </exception>
    public static BinanceEndpoints For(BinanceEnvironment environment) => environment switch
    {
        BinanceEnvironment.Mainnet => Mainnet,
        BinanceEnvironment.Testnet => Testnet,
        _ => throw new ArgumentOutOfRangeException(
            nameof(environment),
            environment,
            "未定義的幣安環境。Undefined Binance environment."),
    };

    /// <summary>
    /// 建立覆寫用的端點組合(代理、鏡像站、離線重播伺服器)。這是後門,平常請用
    /// <see cref="For(BinanceEnvironment)"/>。
    /// Creates an overriding endpoint set for a proxy, a mirror, or an offline replay server. This is the back
    /// door; prefer <see cref="For(BinanceEnvironment)"/>.
    /// </summary>
    /// <param name="displayName">環境名稱,會出現在日誌與錯誤訊息中。The name shown in logs and errors.</param>
    /// <param name="restBaseUri">REST 基底位址。The REST base address.</param>
    /// <param name="webSocketBaseUri">WebSocket 基底位址。The WebSocket base address.</param>
    /// <param name="isTestnet">
    /// 這組端點是否對應模擬資金。錯填會讓「這不是真錢」的提示說謊,請據實填寫。
    /// Whether this set trades simulated funds. Getting it wrong makes the "not real money" banner lie.
    /// </param>
    /// <returns>覆寫的端點組合,或說明哪一項不合法的失敗。The set, or a failure saying which value is invalid.</returns>
    public static Result<BinanceEndpoints> CreateOverride(
        string displayName,
        Uri restBaseUri,
        Uri webSocketBaseUri,
        bool isTestnet)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return BinanceErrors.InvalidOptions("環境名稱不可為空白。The environment name must not be blank.");
        }

        if (restBaseUri is null || !restBaseUri.IsAbsoluteUri)
        {
            return BinanceErrors.InvalidOptions("REST 基底位址必須是絕對位址。The REST base address must be absolute.");
        }

        return webSocketBaseUri is null || !webSocketBaseUri.IsAbsoluteUri
            ? BinanceErrors.InvalidOptions("WebSocket 基底位址必須是絕對位址。The WebSocket base address must be absolute.")
            : new BinanceEndpoints(displayName, isTestnet, restBaseUri, webSocketBaseUri);
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
