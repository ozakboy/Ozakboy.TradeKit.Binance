using System.Globalization;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 各端點消耗的請求權重。
/// The request weight each endpoint consumes.
/// </summary>
/// <remarks>
/// <para>
/// 幣安以「權重」而非「次數」計算限流:一次 <c>/fapi/v1/exchangeInfo</c> 只算 1,
/// 一次不帶 <c>symbol</c> 的 <c>/fapi/v1/openOrders</c> 卻算 40。每個請求都要用
/// <c>WithWeight</c> 宣告自己的權重,本地限流器才算得準;少宣告的下場不是本地擋下來,
/// 而是交易所回 HTTP 429,再不停手就升級成 418 封鎖 IP。
/// Binance meters by weight rather than by request count: one <c>exchangeInfo</c> costs 1 while one
/// <c>openOrders</c> without a symbol costs 40. Every request declares its weight with <c>WithWeight</c> so the
/// local limiter can keep count; under-declaring is not caught locally but by an HTTP 429 from the exchange,
/// escalating to a 418 IP ban if the caller does not stop.
/// </para>
/// <para>
/// 來源:幣安官方文件 USDⓈ-M Futures 的 Market Data、Account、Trade 三個 REST API 頁面,擷取日期 2026-09-11。
/// <c>exchangeInfo</c> 與 <c>time</c> 兩項另以實際呼叫驗證:回應標頭 <c>x-mbx-used-weight-1m</c> 在單次呼叫後為 <c>1</c>。
/// Source: the Market Data, Account, and Trade REST API pages of the official USDⓈ-M Futures documentation,
/// retrieved 2026-09-11. The <c>exchangeInfo</c> and <c>time</c> figures were additionally verified by calling
/// them: the <c>x-mbx-used-weight-1m</c> response header read <c>1</c> after a single call.
/// </para>
/// </remarks>
public static class BinanceRequestWeights
{
    /// <summary><c>GET /fapi/v1/ping</c>。權重 1。Weight 1.</summary>
    public const int Ping = 1;

    /// <summary><c>GET /fapi/v1/time</c>。權重 1(已實測驗證)。Weight 1, measured.</summary>
    public const int ServerTime = 1;

    /// <summary><c>GET /fapi/v1/exchangeInfo</c>。權重 1(已實測驗證)。Weight 1, measured.</summary>
    public const int ExchangeInfo = 1;

    /// <summary><c>GET /fapi/v1/trades</c>。權重 5。Weight 5.</summary>
    public const int RecentTrades = 5;

    /// <summary><c>GET /fapi/v2/account</c>。權重 5。Weight 5.</summary>
    public const int Account = 5;

    /// <summary><c>GET /fapi/v2/balance</c>。權重 5。Weight 5.</summary>
    public const int Balance = 5;

    /// <summary>
    /// <c>GET /fapi/v2/positionRisk</c>。權重採 5。
    /// Weight taken as 5.
    /// </summary>
    /// <remarks>
    /// 官方文件在不同版面對這個端點標示過 1 與 5 兩種值。權重宣告過高只是讓本地限流保守一點,
    /// 宣告過低則會踩到交易所端的 429 與後續封鎖,兩種錯的代價完全不對稱,因此取較大的 5。
    /// The official documentation has carried both 1 and 5 for this endpoint. Over-declaring merely makes the
    /// local limiter conservative while under-declaring earns a 429 and the ban that follows, so the larger
    /// figure is used.
    /// </remarks>
    public const int PositionRisk = 5;

    /// <summary><c>GET /fapi/v1/allOrders</c>。權重 5。Weight 5.</summary>
    public const int AllOrders = 5;

    /// <summary><c>GET /fapi/v1/userTrades</c>。權重 5。Weight 5.</summary>
    public const int UserTrades = 5;

    /// <summary><c>GET /fapi/v1/leverageBracket</c>。權重 1。Weight 1.</summary>
    public const int LeverageBracket = 1;

    /// <summary><c>POST /fapi/v1/order</c>(新單)。取 1。Taken as 1.</summary>
    /// <remarks>
    /// 官方標示新單的 IP 權重為 0,另受獨立的下單速率限制。這裡仍取 1,有兩個理由:
    /// <c>WithWeight</c> 只接受正整數,而且每分鐘 1200 張、每 10 秒 300 張的下單速率是算在<b>帳戶</b>
    /// 而非 IP 上,權重桶擋不到它 —— 下單速率的節流屬於下一階段的獨立機制,絕不能靠這個常數代勞。
    /// The documentation gives new orders an IP weight of zero and meters them by a separate order rate. The
    /// value here is still 1, for two reasons: <c>WithWeight</c> only accepts positive integers, and the order
    /// rate of 1200 per minute and 300 per ten seconds counts against the <b>account</b> rather than the IP, so
    /// the weight bucket cannot police it. Throttling order rate is a separate mechanism for the next stage and
    /// must never lean on this constant.
    /// </remarks>
    public const int PlaceOrder = 1;

    /// <summary><c>GET /fapi/v1/order</c>(查單)。權重 1。Weight 1.</summary>
    public const int QueryOrder = 1;

    /// <summary><c>DELETE /fapi/v1/order</c>(撤單)。權重 1。Weight 1.</summary>
    public const int CancelOrder = 1;

    /// <summary><c>DELETE /fapi/v1/allOpenOrders</c>。權重 1。Weight 1.</summary>
    public const int CancelAllOpenOrders = 1;

    /// <summary>
    /// <c>POST /fapi/v1/leverage</c>、<c>POST /fapi/v1/marginType</c>、<c>POST /fapi/v1/positionMargin</c>。權重 1。
    /// Weight 1.
    /// </summary>
    /// <remarks>
    /// 官方 Trade REST API 頁面目前未標示這三個端點的權重,此處沿用歷史版本文件的 1。
    /// 2026-09-11 試過以回應標頭 <c>x-mbx-used-weight-1m</c> 實測覆核,但那個值是一分鐘滾動窗的<b>累計量</b>,
    /// 兩次呼叫之間相減會因為窗口滾動而出現負值,得不到穩定的單次權重 —— 因此這個數字仍然只有文件依據。
    /// 低估權重的代價是交易所端的 429 與後續封鎖,所以若要再調整,方向應該是往上而不是往下。
    /// The current Trade REST API page does not state a weight for these three; the value of 1 comes from older
    /// revisions. Verifying it against the <c>x-mbx-used-weight-1m</c> header was attempted on 2026-09-11 and
    /// did not work: that header is a <b>running total</b> over a rolling minute, so subtracting consecutive
    /// readings goes negative as the window rolls and yields no stable per-call figure. The number therefore
    /// still rests on documentation alone. Under-declaring earns a 429 and the ban that follows, so any future
    /// adjustment should move upwards rather than down.
    /// </remarks>
    public const int AccountSetting = 1;

    /// <summary>
    /// <c>POST</c>、<c>PUT</c>、<c>DELETE</c> <c>/fapi/v1/listenKey</c>(使用者資料串流憑證)。權重 1。
    /// Weight 1 for the user data stream credential on <c>POST</c>, <c>PUT</c>, and <c>DELETE</c>
    /// <c>/fapi/v1/listenKey</c>.
    /// </summary>
    /// <remarks>
    /// 三個操作共用一個常數,因為官方文件對三者標的都是 1。續期每 30 分鐘一次,對限流桶幾乎沒有影響;
    /// 這裡仍然宣告,是因為本套件的規矩是「每個請求都要宣告權重」—— 一個沒宣告的請求就是本地限流器算不到的請求。
    /// One constant covers all three because the documentation gives each of them a weight of 1. Renewal happens
    /// twice an hour and barely touches the bucket; it is declared anyway because the rule in this package is
    /// that every request declares its weight, and an undeclared request is one the local limiter cannot count.
    /// </remarks>
    public const int ListenKey = 1;

    /// <summary>
    /// 未宣告權重時的預設值。
    /// The weight assumed when a request declares none.
    /// </summary>
    public const int Default = 1;

    /// <summary>
    /// 算出 <c>GET /fapi/v1/klines</c> 在指定筆數上限下的權重。
    /// Returns the weight of <c>GET /fapi/v1/klines</c> for a given limit.
    /// </summary>
    /// <param name="limit">要求的 K 線筆數。The number of klines requested.</param>
    /// <returns>對應的權重。The matching weight.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> 小於 1 時擲出。Thrown when <paramref name="limit"/> is below 1.
    /// </exception>
    public static int Klines(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return limit switch
        {
            < 100 => 1,
            < 500 => 2,
            <= 1000 => 5,
            _ => 10,
        };
    }

    /// <summary>
    /// 算出 <c>GET /fapi/v1/depth</c> 在指定深度下的權重。
    /// Returns the weight of <c>GET /fapi/v1/depth</c> for a given depth.
    /// </summary>
    /// <param name="limit">要求的檔位數。The number of levels requested.</param>
    /// <returns>對應的權重。The matching weight.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> 小於 1 時擲出。Thrown when <paramref name="limit"/> is below 1.
    /// </exception>
    public static int Depth(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return limit switch
        {
            <= 50 => 2,
            <= 100 => 5,
            <= 500 => 10,
            _ => 20,
        };
    }

    /// <summary>
    /// 算出 <c>GET /fapi/v1/premiumIndex</c>(標記價)的權重。
    /// Returns the weight of <c>GET /fapi/v1/premiumIndex</c>.
    /// </summary>
    /// <param name="hasSymbol">
    /// 是否指定了 <c>symbol</c>。不指定會一次取回全市場,權重從 1 跳到 10。
    /// Whether a <c>symbol</c> was supplied; omitting it fetches the whole market and costs 10 instead of 1.
    /// </param>
    /// <returns>對應的權重。The matching weight.</returns>
    public static int MarkPrice(bool hasSymbol) => hasSymbol ? 1 : 10;

    /// <summary>
    /// 算出 <c>GET /fapi/v1/openOrders</c> 的權重。
    /// Returns the weight of <c>GET /fapi/v1/openOrders</c>.
    /// </summary>
    /// <param name="hasSymbol">
    /// 是否指定了 <c>symbol</c>。不指定的權重是 40,是指定時的四十倍。
    /// Whether a <c>symbol</c> was supplied; omitting it costs 40, forty times as much.
    /// </param>
    /// <returns>對應的權重。The matching weight.</returns>
    public static int OpenOrders(bool hasSymbol) => hasSymbol ? 1 : 40;

    /// <summary>
    /// 以人類可讀的形式列出固定權重表,供啟動時記錄或診斷使用。
    /// Renders the fixed weight table in human-readable form for start-up logging or diagnostics.
    /// </summary>
    /// <returns>權重表文字。The weight table as text.</returns>
    public static string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         exchangeInfo={ExchangeInfo}, time={ServerTime}, ping={Ping}, trades={RecentTrades},
         account={Account}, balance={Balance}, positionRisk={PositionRisk},
         allOrders={AllOrders}, userTrades={UserTrades}, leverageBracket={LeverageBracket},
         placeOrder={PlaceOrder}, queryOrder={QueryOrder}, cancelOrder={CancelOrder},
         cancelAllOpenOrders={CancelAllOpenOrders}, accountSetting={AccountSetting}
         """);
}
