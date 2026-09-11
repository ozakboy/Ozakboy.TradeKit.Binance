using System.Globalization;
using Ozakboy.Http;
using Ozakboy.Http.Logging;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 連線幣安 USDⓈ-M 合約所需的設定。
/// The settings required to talk to the Binance USDⓈ-M futures API.
/// </summary>
/// <remarks>
/// <para>
/// <b>金鑰絕不落地。</b> 這個型別不含任何預設憑證,也不會把憑證寫進檔案、日誌或 <see cref="object.ToString"/>。
/// <see cref="ApiKey"/> 與 <see cref="SecretKey"/> 一律由宿主從環境變數或安全設定來源注入,
/// 並由 <c>Ozakboy.Http</c> 的 <see cref="SigningOptions"/> 承載;本套件只在簽章當下讀取它們。
/// <b>Credentials never land on disk.</b> This type carries no default credentials and writes none to files,
/// logs, or <see cref="object.ToString"/>. Both keys are injected by the host from environment variables or a
/// secure configuration source and are carried by <see cref="SigningOptions"/>; this package reads them only
/// at the moment of signing.
/// </para>
/// <para>
/// 環境是<b>成套</b>選的。<see cref="Environment"/> 一次決定 REST 與 WebSocket 兩個位址,
/// 沒有「分別填 URL」的入口;真要指向代理或重播伺服器時,用 <see cref="EndpointOverride"/> 這個明確的後門,
/// 而它同樣是一組不可拆開的 <see cref="BinanceEndpoints"/>。
/// The environment is chosen <b>as a set</b>. <see cref="Environment"/> decides the REST and WebSocket
/// addresses together and there is no per-URL entry point. Pointing at a proxy or a replay server goes through
/// the explicit <see cref="EndpointOverride"/> back door, which is itself an inseparable
/// <see cref="BinanceEndpoints"/>.
/// </para>
/// </remarks>
public sealed class BinanceOptions
{
    /// <summary>
    /// <see cref="RecvWindow"/> 允許的上限。幣安拒絕超過 60 秒的值。
    /// The ceiling <see cref="RecvWindow"/> may take; Binance rejects anything above 60 seconds.
    /// </summary>
    public static readonly TimeSpan MaxRecvWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 幣安省略 <c>recvWindow</c> 時的預設值。
    /// The value Binance assumes when <c>recvWindow</c> is omitted.
    /// </summary>
    public static readonly TimeSpan DefaultRecvWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 目標環境,預設為正式環境。
    /// The target environment; production by default.
    /// </summary>
    /// <remarks>
    /// 預設刻意是主網而不是 Testnet。預設值若是模擬盤,忘了設定的下場是「跑了一整天才發現一張真單都沒送出去」;
    /// 預設是主網,忘了設定的下場則是在第一次簽章請求就因為沒有主網金鑰而失敗,當場就會被發現。
    /// The default is deliberately production rather than Testnet. Defaulting to the simulator means a
    /// forgotten setting shows up as "a full day of trading that never reached the exchange"; defaulting to
    /// production means it shows up immediately, as the first signed request failing for want of a real key.
    /// </remarks>
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Mainnet;

    /// <summary>
    /// 端點覆寫(代理、鏡像站、離線重播伺服器)。設定後 <see cref="Environment"/> 不再決定位址。
    /// An endpoint override for a proxy, a mirror, or an offline replay server. When set,
    /// <see cref="Environment"/> no longer decides the addresses.
    /// </summary>
    /// <remarks>
    /// 這是後門,平常不要用。用 <see cref="BinanceEndpoints.CreateOverride"/> 建立,它一樣是成套的,
    /// 所以即使走後門也拆不開 REST 與 WebSocket。
    /// This is the back door and is not for everyday use. Build it with
    /// <see cref="BinanceEndpoints.CreateOverride"/>, which still produces a matched set, so even the back door
    /// cannot separate REST from WebSocket.
    /// </remarks>
    public BinanceEndpoints? EndpointOverride { get; set; }

    /// <summary>
    /// API 金鑰。公開端點不需要,簽章端點必填。
    /// The API key. Not needed for public endpoints and required for signed ones.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// API 密鑰。公開端點不需要,簽章端點必填。
    /// The API secret. Not needed for public endpoints and required for signed ones.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// 簽章請求的 <c>recvWindow</c>,預設 5 秒,上限 60 秒。
    /// The <c>recvWindow</c> for signed requests; 5 seconds by default and 60 at most.
    /// </summary>
    /// <remarks>
    /// 這是「本機時間戳可以比伺服器時間舊多久」的容忍度。調大能擋掉網路抖動造成的 <c>-1021</c>,
    /// 但同時也放寬了重放攻擊的時間窗,而且會讓真正的時鐘偏移延後被發現 —— 偏移不會自己好轉,
    /// 只會在某天超過放寬後的窗口時一次爆發。寧可維持小窗並定期校時。
    /// This is how stale a local timestamp may be. Widening it absorbs the <c>-1021</c> failures caused by
    /// network jitter, but it also widens the replay window and delays the discovery of genuine clock drift —
    /// which does not heal on its own and simply breaks out on the day it exceeds the wider window. Prefer a
    /// narrow window and periodic re-synchronisation.
    /// </remarks>
    public TimeSpan RecvWindow { get; set; } = DefaultRecvWindow;

    /// <summary>
    /// 交易規則快照的有效期,預設 24 小時。
    /// How long an exchange-info snapshot stays valid; 24 hours by default.
    /// </summary>
    /// <remarks>
    /// 幣安會調整交易規則(新增商品、變更 <c>stepSize</c>、下架商品),過期的規則會讓價量校正算出
    /// 交易所已經不接受的數值,而拒單訊息只會說「參數不合法」。每日更新是規格要求的節奏。
    /// Binance adjusts trading rules — new symbols, changed step sizes, delistings — and a stale rule set
    /// normalises to values the exchange no longer accepts, with a rejection that says only "invalid
    /// parameter". A daily refresh is the cadence the specification calls for.
    /// </remarks>
    public TimeSpan ExchangeInfoCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 每分鐘請求權重上限的覆寫值。未設定時取
    /// <see cref="BinanceRateLimits.DefaultWeightPerMinute(BinanceEnvironment)"/>。
    /// An override for the request-weight ceiling per minute; defaults to
    /// <see cref="BinanceRateLimits.DefaultWeightPerMinute(BinanceEnvironment)"/>.
    /// </summary>
    public int? RequestWeightPerMinute { get; set; }

    /// <summary>
    /// 等待限流額度的上限,預設 30 秒。
    /// How long to wait for rate-limit permits; 30 seconds by default.
    /// </summary>
    public TimeSpan RateLimitAcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 逾時設定。
    /// The timeout settings.
    /// </summary>
    public HttpTimeoutOptions Timeouts { get; } = new();

    /// <summary>
    /// 重試設定。
    /// The retry settings.
    /// </summary>
    /// <remarks>
    /// 預設保留 <c>Ozakboy.Http</c> 的行為:只有安全方法或明確標記冪等的請求會被重試,而且只在
    /// <see cref="Error.IsTransient"/> 為真時重試。<see cref="RetryOptions.ErrorBodySnippetLength"/>
    /// 預設改成 512,否則重試耗盡時的錯誤會少掉回應本文,而幣安的錯誤碼正是放在本文裡 ——
    /// 少了它,<c>-2019</c>(保證金不足)會退化成一句「HTTP 400」。
    /// The defaults keep the behaviour of <c>Ozakboy.Http</c>: only safe or explicitly idempotent requests are
    /// retried, and only while <see cref="Error.IsTransient"/> holds.
    /// <see cref="RetryOptions.ErrorBodySnippetLength"/> is raised to 512 because otherwise a
    /// retry-exhausted error loses the response body — which is precisely where Binance puts its error code,
    /// so <c>-2019</c> would degrade into a bare "HTTP 400".
    /// </remarks>
    public RetryOptions Retry { get; } = new() { ErrorBodySnippetLength = 512 };

    /// <summary>
    /// 日誌脫敏設定。
    /// The log-masking settings.
    /// </summary>
    /// <remarks>
    /// 幣安把簽章放在 <c>signature</c> 查詢參數、把金鑰放在 <c>X-MBX-APIKEY</c> 標頭。
    /// <see cref="CreateLoggingOptions"/> 會把這兩個名字加進脫敏清單,避免整串簽章請求原封不動地進 log。
    /// Binance puts the signature in a <c>signature</c> query parameter and the key in an <c>X-MBX-APIKEY</c>
    /// header. <see cref="CreateLoggingOptions"/> adds both names to the masking list so that a signed request
    /// does not land in the log verbatim.
    /// </remarks>
    public RequestLoggingOptions Logging { get; } = new();

    /// <summary>
    /// 是否已設定完整的 API 憑證。
    /// Whether a complete set of API credentials is configured.
    /// </summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>
    /// 取得實際要連線的端點組合。
    /// Gets the endpoint set actually in use.
    /// </summary>
    /// <returns>端點組合。The endpoint set.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Environment"/> 為未定義的值且未設定 <see cref="EndpointOverride"/> 時擲出。
    /// Thrown when <see cref="Environment"/> is undefined and no override is set.
    /// </exception>
    public BinanceEndpoints ResolveEndpoints() => EndpointOverride ?? BinanceEndpoints.For(Environment);

    /// <summary>
    /// 取得實際採用的每分鐘權重上限。
    /// Gets the weight ceiling actually in force.
    /// </summary>
    /// <returns>每分鐘權重上限。The weight ceiling per minute.</returns>
    public int ResolveWeightPerMinute() =>
        RequestWeightPerMinute ?? BinanceRateLimits.DefaultWeightPerMinute(
            EndpointOverride is null ? Environment : BinanceEnvironment.Mainnet);

    /// <summary>
    /// 檢查設定是否可用。
    /// Checks that the settings are usable.
    /// </summary>
    /// <returns>設定合法時為成功,否則為說明哪一項不合法的失敗。Success, or a failure naming the bad setting.</returns>
    /// <remarks>
    /// 刻意<b>不</b>檢查憑證是否存在:公開端點(交易規則、伺服器時間)不需要憑證,
    /// 而把憑證列為必填會讓「只想查交易規則」的用途也被迫準備金鑰。憑證缺漏在第一次簽章請求時才擋,
    /// 由 <see cref="BinanceErrors.CredentialsMissing"/> 明講原因。
    /// Credentials are deliberately <b>not</b> checked here: the public endpoints need none, and requiring them
    /// would force a caller who only wants trading rules to produce a key. A missing credential is caught at
    /// the first signed request instead, with <see cref="BinanceErrors.CredentialsMissing"/> saying so plainly.
    /// </remarks>
    public Result Validate()
    {
        if (EndpointOverride is null && !Enum.IsDefined(Environment))
        {
            return BinanceErrors.InvalidOptions(
                string.Create(CultureInfo.InvariantCulture, $"未定義的幣安環境:{(int)Environment}。Undefined Binance environment: {(int)Environment}."));
        }

        if (RecvWindow <= TimeSpan.Zero || RecvWindow > MaxRecvWindow)
        {
            return BinanceErrors.InvalidOptions(
                $"recvWindow 必須介於 0 與 {MaxRecvWindow} 之間,收到 {RecvWindow}。recvWindow must be between zero and {MaxRecvWindow} but was {RecvWindow}.");
        }

        if (ExchangeInfoCacheTtl <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions(
                "交易規則快取的有效期必須為正值。The exchange-info cache lifetime must be positive.");
        }

        if (RequestWeightPerMinute is { } weight && weight < 1)
        {
            return BinanceErrors.InvalidOptions(
                "每分鐘權重上限必須為正整數。The weight ceiling per minute must be a positive integer.");
        }

        if (RateLimitAcquisitionTimeout <= TimeSpan.Zero)
        {
            return BinanceErrors.InvalidOptions(
                "等待限流額度的上限必須為正值。The rate-limit acquisition timeout must be positive.");
        }

        return Timeouts.Validate()
            .Then(Retry.Validate)
            .Then(Logging.Validate);
    }

    /// <summary>
    /// 建立簽章設定。
    /// Builds the signing settings.
    /// </summary>
    /// <returns>簽章設定。The signing options.</returns>
    /// <remarks>
    /// <para>
    /// 參數一律放<b>查詢字串</b>,POST 也一樣,不使用「部分放 query、部分放表單」的混合模式。
    /// 混合模式在幣安合約文件的範例裡算不出官方刊登的簽章值(窮舉八個參數的全部排列都對不上),
    /// 而同型的現貨範例可完整重現,顯示問題出在那份範例而不是規則本身;全部放查詢字串則處處可重現。
    /// Parameters always go in the <b>query string</b>, including on POST; the mixed mode that splits them
    /// between query and form body is not used. That mixed example in the Binance futures documentation cannot
    /// be reproduced — every permutation of its eight parameters was tried and none yields the published
    /// signature — while the equivalent spot example reproduces exactly, which points at that one example
    /// rather than at the rule. Putting everything in the query reproduces everywhere.
    /// </para>
    /// <para>
    /// 簽章演算法固定 HMAC-SHA256、輸出小寫十六進位,由 <c>Ozakboy.Http</c> 提供且已對過官方黃金向量。
    /// The algorithm is HMAC-SHA256 with lower-case hexadecimal output, supplied by <c>Ozakboy.Http</c> and
    /// already checked against the official golden vectors.
    /// </para>
    /// </remarks>
    public SigningOptions CreateSigningOptions() => new()
    {
        ApiKey = ApiKey,
        SecretKey = SecretKey,
        ApiKeyHeaderName = BinanceConstants.ApiKeyHeaderName,
        SignatureParameterName = BinanceConstants.SignatureParameterName,
        SendApiKeyHeader = true,
        Placement = SignedPayloadPlacement.QueryString,
    };

    /// <summary>
    /// 建立限流設定。
    /// Builds the rate-limit settings.
    /// </summary>
    /// <returns>限流設定。The rate-limit options.</returns>
    public RateLimitOptions CreateRateLimitOptions() =>
        BinanceRateLimits.CreateOptions(ResolveWeightPerMinute(), RateLimitAcquisitionTimeout);

    /// <summary>
    /// 建立日誌脫敏設定,並補上幣安專屬的敏感參數名。
    /// Builds the log-masking settings with the Binance-specific sensitive names added.
    /// </summary>
    /// <returns>日誌設定。The logging options.</returns>
    public RequestLoggingOptions CreateLoggingOptions()
    {
        foreach (var name in BinanceConstants.SensitiveParameterNames)
        {
            if (!Logging.AdditionalSensitiveParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Logging.AdditionalSensitiveParameterNames.Add(name);
            }
        }

        return Logging;
    }
}
