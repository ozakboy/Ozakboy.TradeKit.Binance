namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 一個幣安合約商品的交易規則:交易所中立的部分,加上抽象層放不下的幣安專屬欄位。
/// The trading rules of one Binance futures symbol: the exchange-neutral part plus the Binance-specific fields
/// the abstraction has no room for.
/// </summary>
/// <remarks>
/// <para>
/// 之所以需要這一層,關鍵是 <c>MARKET_LOT_SIZE</c>。幣安對<b>市價單</b>另有一組數量限制,
/// 而且它的 <c>maxQty</c> 與 <c>LOT_SIZE</c> 的 <c>maxQty</c> 幾乎必定不同 —— 2026-09-11 的主網快照裡
/// 897 個商品<b>每一個</b>都不同(<c>BTCUSDT</c> 為限價 1000、市價 120)。
/// <see cref="SymbolInfo.MaxQuantity"/> 只有一個欄位,填哪一個都會錯一半:填限價的值會讓市價單被拒,
/// 填市價的值會讓合法的限價單被本地攔下來。因此中立模型採<b>限價</b>的上限,市價的上限放在這裡。
/// This layer exists chiefly because of <c>MARKET_LOT_SIZE</c>. Binance imposes a separate quantity ceiling on
/// <b>market</b> orders, and it almost always differs from the <c>LOT_SIZE</c> ceiling — in the production
/// snapshot of 2026-09-11 it differed for <b>every one</b> of the 897 symbols (<c>BTCUSDT</c>: 1000 for limit,
/// 120 for market). <see cref="SymbolInfo.MaxQuantity"/> is a single field, so either choice is half wrong:
/// the limit figure gets market orders rejected, the market figure blocks legitimate limit orders locally. The
/// neutral model therefore carries the <b>limit</b> ceiling and the market one lives here.
/// </para>
/// <para>
/// <see cref="SymbolInfo.MaxLeverage"/> 不會被填。<c>exchangeInfo</c> 根本沒有這項資料,
/// 它在 <c>/fapi/v1/leverageBracket</c>(需要簽章)。由 <c>requiredMarginPercent</c> 反推出來的
/// 是「預設分層的槓桿」而不是上限(<c>BTCUSDT</c> 反推得 20,實際上限 125),
/// 那正是規格禁止的以預設值猜測,所以維持抽象層的預設 1,等下一階段接 <c>leverageBracket</c> 再補。
/// <see cref="SymbolInfo.MaxLeverage"/> is left alone. <c>exchangeInfo</c> does not carry it at all; it lives
/// behind the signed <c>/fapi/v1/leverageBracket</c>. Deriving it from <c>requiredMarginPercent</c> yields the
/// default tier's leverage rather than the ceiling (20 for <c>BTCUSDT</c>, whose real ceiling is 125), which is
/// precisely the guessing the specification forbids. It therefore keeps the abstraction's default of 1 until
/// the next stage wires up <c>leverageBracket</c>.
/// </para>
/// </remarks>
public sealed record BinanceSymbolDetail
{
    /// <summary>
    /// 交易所中立的交易規則。
    /// The exchange-neutral trading rules.
    /// </summary>
    public required SymbolInfo Info { get; init; }

    /// <summary>
    /// 幣安回報的商品狀態字串,例如 <c>TRADING</c>、<c>SETTLING</c>、<c>PENDING_TRADING</c>。
    /// The raw status Binance reports, such as <c>TRADING</c>, <c>SETTLING</c>, or <c>PENDING_TRADING</c>.
    /// </summary>
    /// <remarks>
    /// 只有 <c>TRADING</c> 會讓 <see cref="SymbolInfo.IsTradingEnabled"/> 為真。原始字串保留下來,
    /// 是因為「暫停」與「即將上架」在營運上要分開處理,而布林值分不出來。
    /// Only <c>TRADING</c> makes <see cref="SymbolInfo.IsTradingEnabled"/> true. The raw string is kept because
    /// "settling" and "about to list" call for different operational responses that a boolean cannot express.
    /// </remarks>
    public required string Status { get; init; }

    /// <summary>
    /// 合約類型,例如 <c>PERPETUAL</c>、<c>CURRENT_QUARTER</c>。
    /// The contract type, such as <c>PERPETUAL</c> or <c>CURRENT_QUARTER</c>.
    /// </summary>
    /// <remarks>
    /// 幣安的 USDⓈ-M 市場同時包含永續與交割合約。只做永續的策略必須自己篩,
    /// 本套件不代為過濾 —— 悄悄拿掉一半商品會讓「查不到這個交易對」變成無法解釋的現象。
    /// The USDⓈ-M market carries both perpetual and dated contracts. A perpetual-only strategy filters for
    /// itself; this package does not filter on its behalf, because quietly removing half the symbols turns
    /// "symbol not found" into an inexplicable event.
    /// </remarks>
    public required string ContractType { get; init; }

    /// <summary>
    /// 保證金資產,通常與計價幣相同。
    /// The margin asset, usually the same as the quote asset.
    /// </summary>
    public required string MarginAsset { get; init; }

    /// <summary>
    /// 市價單的最小數量(<c>MARKET_LOT_SIZE</c> 的 <c>minQty</c>)。
    /// The minimum market-order quantity from <c>MARKET_LOT_SIZE</c>.
    /// </summary>
    public required decimal MarketMinQuantity { get; init; }

    /// <summary>
    /// 市價單的最大數量(<c>MARKET_LOT_SIZE</c> 的 <c>maxQty</c>)。
    /// The maximum market-order quantity from <c>MARKET_LOT_SIZE</c>.
    /// </summary>
    public required decimal MarketMaxQuantity { get; init; }

    /// <summary>
    /// 市價單的數量步進(<c>MARKET_LOT_SIZE</c> 的 <c>stepSize</c>)。
    /// The market-order step size from <c>MARKET_LOT_SIZE</c>.
    /// </summary>
    /// <remarks>
    /// 2026-09-11 的主網快照中,這一項與 <see cref="SymbolInfo.StepSize"/> 在全部 897 個商品上都相同。
    /// 即使如此仍分開保留:「目前相同」不是「保證相同」,而把兩者混為一談的錯誤只會在幣安哪天調整時才浮現。
    /// In the production snapshot of 2026-09-11 this matched <see cref="SymbolInfo.StepSize"/> for all 897
    /// symbols. It is still kept separately: "identical today" is not "guaranteed identical", and conflating
    /// them only surfaces on the day Binance changes one.
    /// </remarks>
    public required decimal MarketStepSize { get; init; }

    /// <summary>
    /// 同一商品允許的最大掛單數(<c>MAX_NUM_ORDERS</c>);回應未提供時為 <see langword="null"/>。
    /// The maximum number of open orders on the symbol from <c>MAX_NUM_ORDERS</c>, or <see langword="null"/>
    /// when the response omits it.
    /// </summary>
    public int? MaxOpenOrders { get; init; }

    /// <summary>
    /// 幣安宣告的價格小數位數。
    /// The price precision Binance declares.
    /// </summary>
    /// <remarks>
    /// 這是<b>顯示</b>用的位數,不是下單校正的依據 —— 校正一律以 <see cref="SymbolInfo.TickSize"/> 為準。
    /// 兩者可能不一致(<c>BTCUSDT</c> 宣告 2 位,<c>tickSize</c> 為 <c>0.10</c> 即 1 位),
    /// 拿錯的那個去對齊價格就會收到 <c>-4014</c>。
    /// This is a <b>display</b> precision, not the basis for normalisation, which always uses
    /// <see cref="SymbolInfo.TickSize"/>. The two can disagree — <c>BTCUSDT</c> declares 2 while its
    /// <c>tickSize</c> of <c>0.10</c> implies 1 — and aligning to the wrong one earns a <c>-4014</c>.
    /// </remarks>
    public required int PricePrecision { get; init; }

    /// <summary>
    /// 幣安宣告的數量小數位數。與 <see cref="PricePrecision"/> 同理,校正請以
    /// <see cref="SymbolInfo.StepSize"/> 為準。
    /// The quantity precision Binance declares. As with <see cref="PricePrecision"/>, normalise against
    /// <see cref="SymbolInfo.StepSize"/> instead.
    /// </summary>
    public required int QuantityPrecision { get; init; }

    /// <summary>
    /// 交易對代碼,等同 <see cref="SymbolInfo.Name"/>。
    /// The symbol code, the same as <see cref="SymbolInfo.Name"/>.
    /// </summary>
    public string Name => Info.Name;

    /// <summary>
    /// 是否為永續合約。
    /// Whether this is a perpetual contract.
    /// </summary>
    public bool IsPerpetual => string.Equals(ContractType, "PERPETUAL", StringComparison.Ordinal);
}
