namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約的執行環境。
/// The Binance USDⓈ-M futures environment to run against.
/// </summary>
/// <remarks>
/// <para>
/// 選環境而不是逐一填 URL,是為了讓「REST 指向 Testnet、WebSocket 指向主網」這種組合無法被寫出來。
/// 那種錯接不會有任何錯誤訊息:下單打到模擬盤、行情卻是真實市場,策略看起來完全正常地在賠真錢的反面。
/// The environment is chosen as a whole rather than URL by URL so that a mismatched pair — REST on Testnet
/// while the market stream is on production — cannot be expressed. Such a mix produces no error at all: orders
/// land on the simulator while prices come from the real market, and the strategy looks perfectly healthy.
/// </para>
/// <para>
/// Testnet 與主網的交易規則並不相同(實測 <c>BTCUSDT</c> 的 <c>stepSize</c> 在 Testnet 為
/// <c>0.0001</c>、主網為 <c>0.001</c>),因此交易規則快取一律以環境為鍵,見
/// <see cref="BinanceExchangeInfoProvider"/>。
/// Testnet and production do not share trading rules — <c>BTCUSDT</c> reports a <c>stepSize</c> of
/// <c>0.0001</c> on Testnet and <c>0.001</c> on production — so the trading-rule cache is always keyed by
/// environment; see <see cref="BinanceExchangeInfoProvider"/>.
/// </para>
/// </remarks>
public enum BinanceEnvironment
{
    /// <summary>
    /// 正式環境,以真實資金成交。
    /// Production, where orders trade real money.
    /// </summary>
    Mainnet = 0,

    /// <summary>
    /// 合約測試網,以模擬資金成交。
    /// The futures testnet, where orders trade simulated funds.
    /// </summary>
    Testnet = 1,
}
