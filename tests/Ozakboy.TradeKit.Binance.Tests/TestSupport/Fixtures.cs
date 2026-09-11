namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 讀取錄製下來的回應。來源與編輯狀況見 <c>Fixtures\PROVENANCE.md</c>。
/// Loads the recorded responses. Their provenance is documented in <c>Fixtures\PROVENANCE.md</c>.
/// </summary>
internal static class Fixtures
{
    public static string ExchangeInfoMainnet => Read("exchangeInfo-mainnet.json");

    public static string ExchangeInfoTestnet => Read("exchangeInfo-testnet.json");

    public static string ServerTime => Read("serverTime.json");

    public static string Account => Read("account.json");

    /// <summary>
    /// Testnet 實錄的單向模式持倉。錄製當時帳戶空手,所有數量為零。
    /// A verbatim one-way recording from Testnet. The account was flat, so every quantity is zero.
    /// </summary>
    public static string PositionRisk => Read("positionRisk.json");

    /// <summary>
    /// Testnet 實錄的雙向模式持倉:同一商品各有 <c>LONG</c> 與 <c>SHORT</c> 一筆。
    /// A verbatim hedge-mode recording from Testnet, with one <c>LONG</c> and one <c>SHORT</c> row per symbol.
    /// </summary>
    public static string PositionRiskHedge => Read("positionRisk-hedge.json");

    /// <summary>
    /// 結構取自實錄,數值改成「持有部位」的情境;供需要非零損益與名目價值的測試使用。
    /// The recorded structure with values substituted for an account that holds positions, for the tests that
    /// need a non-zero unrealised P&amp;L and notional.
    /// </summary>
    public static string PositionRiskOpen => Read("positionRisk-open.json");

    /// <summary>
    /// 同上,雙向模式下同一商品多空並存。
    /// The same, for a hedge-mode symbol holding both sides at once.
    /// </summary>
    public static string PositionRiskHedgeOpen => Read("positionRisk-hedge-open.json");

    /// <summary>下單成功的回應(<c>POST /fapi/v1/order</c>)。The place-order reply.</summary>
    public static string OrderNew => Read("order-new.json");

    /// <summary>查單的回應(<c>GET /fapi/v1/order</c>)。The order-query reply.</summary>
    public static string OrderQuery => Read("order-query.json");

    /// <summary>撤單的回應(<c>DELETE /fapi/v1/order</c>)。The cancel reply.</summary>
    public static string OrderCanceled => Read("order-canceled.json");

    /// <summary>未結訂單清單(<c>GET /fapi/v1/openOrders</c>)。The open-orders reply.</summary>
    public static string OpenOrders => Read("openOrders.json");

    /// <summary>撤銷全部掛單的回應。The cancel-all reply.</summary>
    public static string CancelAll => Read("cancelAll.json");

    /// <summary>調整槓桿的回應。The leverage reply.</summary>
    public static string Leverage => Read("leverage.json");

    /// <summary>條件單被 <c>/fapi/v1/order</c> 拒收(<c>-4120</c>)。The <c>-4120</c> rejection.</summary>
    public static string OrderTypeNotSupported => Read("orderTypeNotSupported.json");

    /// <summary>保證金模式不需變更(<c>-4046</c>)。The <c>-4046</c> "no change needed" reply.</summary>
    public static string MarginTypeNoChange => Read("marginTypeNoChange.json");

    /// <summary>查不到訂單(<c>-2013</c>)。The <c>-2013</c> "order does not exist" reply.</summary>
    public static string OrderNotFound => Read("orderNotFound.json");

    private static string Read(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"找不到測試 fixture。Test fixture not found: {path}", path);
    }
}
