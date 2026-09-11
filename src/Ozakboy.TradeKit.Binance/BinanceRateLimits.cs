using Ozakboy.Http.RateLimiting;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約的限流門檻,以及由它們產生 <see cref="RateLimitOptions"/> 的方法。
/// The Binance USDⓈ-M rate-limit ceilings and the factory that turns them into a
/// <see cref="RateLimitOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// 限流本身由 <c>Ozakboy.Http</c> 的 <c>WeightedRateLimiter</c> 執行,本型別只負責「填幾號、填多少」。
/// 自研限流是明令禁止的:那顆輪子已經以 <c>TimeProvider</c> 驅動、可被測試,再寫一顆只會多一個
/// 沒人測過的節流器守在下單路徑上。
/// The limiting itself is performed by <c>WeightedRateLimiter</c> from <c>Ozakboy.Http</c>; this type only
/// supplies the numbers. Writing another limiter is forbidden: that wheel is already driven by
/// <c>TimeProvider</c> and covered by tests, and a second one would sit untested on the order path.
/// </para>
/// <para>
/// 只設一個 <c>REQUEST_WEIGHT</c> 桶,刻意<b>不</b>把下單速率(每分鐘 1200 張、每 10 秒 300 張)也設成桶。
/// 原因是 <c>WeightedRateLimiter</c> 會對<b>每一個</b>桶扣掉同一份權重,把下單桶加進來會讓查詢請求
/// 也吃掉下單額度,結果是行情查一查就下不了單 —— 那比沒有下單節流更危險。下單速率算在帳戶而非 IP 上,
/// 屬於下一階段的獨立機制。
/// Only one <c>REQUEST_WEIGHT</c> bucket is configured, deliberately leaving out the order-rate limits of 1200
/// per minute and 300 per ten seconds. <c>WeightedRateLimiter</c> deducts the same weight from <b>every</b>
/// bucket, so adding an order bucket would let ordinary queries eat the order allowance until no order can be
/// placed at all — worse than having no order throttle. Order rate counts against the account rather than the
/// IP and belongs to a separate mechanism in the next stage.
/// </para>
/// </remarks>
public static class BinanceRateLimits
{
    /// <summary>
    /// 正式環境宣告的每分鐘請求權重上限。
    /// The request-weight-per-minute ceiling production declares.
    /// </summary>
    /// <remarks>
    /// 2026-09-11 實測:<c>/fapi/v1/exchangeInfo</c> 回應的 <c>rateLimits</c> 為
    /// <c>REQUEST_WEIGHT / MINUTE / 1 / 2400</c>。
    /// Measured 2026-09-11: the <c>rateLimits</c> section of <c>/fapi/v1/exchangeInfo</c> reads
    /// <c>REQUEST_WEIGHT / MINUTE / 1 / 2400</c>.
    /// </remarks>
    public const int MainnetWeightPerMinute = 2400;

    /// <summary>
    /// 合約測試網宣告的每分鐘請求權重上限,僅供參考。
    /// The ceiling the futures testnet advertises, for reference only.
    /// </summary>
    /// <remarks>
    /// 2026-09-11 實測為 6000。<see cref="DefaultWeightPerMinute"/> 刻意不採用它,理由見該成員說明。
    /// Measured as 6000 on 2026-09-11. <see cref="DefaultWeightPerMinute"/> deliberately does not adopt it; see
    /// that member for why.
    /// </remarks>
    public const int TestnetAdvertisedWeightPerMinute = 6000;

    /// <summary>
    /// 請求權重桶的名稱,會出現在限流失敗的錯誤訊息中。
    /// The name of the request-weight bucket, which appears in rate-limit failure messages.
    /// </summary>
    public const string RequestWeightBucketName = "binance.request_weight.1m";

    /// <summary>
    /// 取得某個環境預設採用的每分鐘權重上限。
    /// Gets the weight ceiling used by default for an environment.
    /// </summary>
    /// <param name="environment">目標環境。The target environment.</param>
    /// <returns>每分鐘權重上限。The weight ceiling per minute.</returns>
    /// <remarks>
    /// 兩個環境都回傳主網的 2400,即使 Testnet 自己宣告 6000。理由與這個套件處處在防的那個陷阱一樣:
    /// 在 Testnet 跑得過的節奏必須在主網也跑得過。若採用 Testnet 較寬的額度,測試階段一切正常、
    /// 換到主網才開始被 429 —— 而那時系統已經在下真錢的單了。需要用滿 Testnet 額度時,
    /// 以 <see cref="BinanceOptions.RequestWeightPerMinute"/> 明確覆寫。
    /// Both environments get production's 2400 even though Testnet advertises 6000, for the same reason this
    /// package guards everywhere else: a pace that passes on Testnet must also pass on production. Adopting the
    /// looser allowance means everything is fine in testing and the 429s start on production — by which point
    /// the system is placing real orders. Override through
    /// <see cref="BinanceOptions.RequestWeightPerMinute"/> when the larger allowance is genuinely wanted.
    /// </remarks>
    public static int DefaultWeightPerMinute(BinanceEnvironment environment) => environment switch
    {
        BinanceEnvironment.Mainnet or BinanceEnvironment.Testnet => MainnetWeightPerMinute,
        _ => throw new ArgumentOutOfRangeException(
            nameof(environment),
            environment,
            "未定義的幣安環境。Undefined Binance environment."),
    };

    /// <summary>
    /// 建立請求權重的限流設定。
    /// Builds the request-weight rate-limit options.
    /// </summary>
    /// <param name="weightPerMinute">每分鐘權重上限。The weight ceiling per minute.</param>
    /// <param name="acquisitionTimeout">
    /// 等待額度的上限。超過這個時間仍排不到額度就直接失敗,而不是無限期地卡住下單路徑。
    /// How long to wait for permits before failing outright instead of blocking the order path indefinitely.
    /// </param>
    /// <returns>限流設定。The rate-limit options.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="weightPerMinute"/> 小於 1 時擲出。
    /// Thrown when <paramref name="weightPerMinute"/> is below 1.
    /// </exception>
    public static RateLimitOptions CreateOptions(int weightPerMinute, TimeSpan acquisitionTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(weightPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(acquisitionTimeout, TimeSpan.Zero);

        var options = new RateLimitOptions
        {
            DefaultWeight = BinanceRequestWeights.Default,
            AcquisitionTimeout = acquisitionTimeout,
        };

        options.Buckets.Add(new RateLimitBucket(RequestWeightBucketName, weightPerMinute, TimeSpan.FromMinutes(1)));

        return options;
    }
}
