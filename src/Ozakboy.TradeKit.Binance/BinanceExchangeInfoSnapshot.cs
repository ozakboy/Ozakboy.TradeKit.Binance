namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 某個環境在某一刻的完整交易規則快照。
/// A complete snapshot of one environment's trading rules at one moment.
/// </summary>
/// <remarks>
/// <para>
/// 快照<b>自己記得自己來自哪個環境</b>。這不是為了方便,而是為了讓那個最難查的錯誤不可能發生:
/// Testnet 與主網的交易規則不同(實測 <c>BTCUSDT</c> 的 <c>stepSize</c> 分別是 <c>0.0001</c> 與 <c>0.001</c>),
/// 把 Testnet 驗過的數量送到主網會被拒單,而拒單訊息只會說「參數不合法」。
/// 有了 <see cref="Endpoints"/>,任何快取或持久化的快照在被取用前都能先比對來源,
/// 不必仰賴呼叫端自己記得哪一份是哪一份。
/// A snapshot <b>remembers which environment it came from</b>. That is not a convenience but a way of making
/// the hardest bug impossible: Testnet and production do not share trading rules — <c>BTCUSDT</c> reports a
/// <c>stepSize</c> of <c>0.0001</c> against <c>0.001</c> — so a quantity validated on Testnet is rejected on
/// production with nothing more than "invalid parameter". With <see cref="Endpoints"/> present, any cached or
/// persisted snapshot can be checked against its source before use, rather than relying on the caller to
/// remember which is which.
/// </para>
/// <para>
/// 這個型別可以直接序列化保存。§12.3 要求交易規則每日更新一次並保留快照,應用層把原始 JSON 存進資料庫之後,
/// 可以再用 <see cref="BinanceExchangeInfoParser.Parse"/> 重建這個型別,而重建時仍必須指定環境 ——
/// 所以「從資料庫讀回來的規則屬於哪個環境」同樣不會不見。
/// The type is meant to be persisted. The specification calls for a daily refresh with snapshots retained; an
/// application that stores the raw JSON can rebuild this type later with
/// <see cref="BinanceExchangeInfoParser.Parse"/>, which still demands an environment — so "which environment
/// were these rules from" survives the round trip through the database too.
/// </para>
/// </remarks>
public sealed class BinanceExchangeInfoSnapshot
{
    private readonly Dictionary<string, BinanceSymbolDetail> _bySymbol;

    private readonly Dictionary<string, BinanceRejectedSymbol> _rejected;

    internal BinanceExchangeInfoSnapshot(
        BinanceEndpoints endpoints,
        DateTimeOffset serverTime,
        DateTimeOffset fetchedAt,
        IReadOnlyList<BinanceSymbolDetail> symbols,
        IReadOnlyList<BinanceRejectedSymbol> rejectedSymbols)
    {
        Endpoints = endpoints;
        ServerTime = serverTime;
        FetchedAt = fetchedAt;
        Symbols = symbols;
        RejectedSymbols = rejectedSymbols;

        _rejected = new Dictionary<string, BinanceRejectedSymbol>(
            rejectedSymbols.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var rejection in rejectedSymbols)
        {
            _rejected[rejection.Name] = rejection;
        }

        SymbolInfos = [.. symbols.Select(static symbol => symbol.Info)];

        _bySymbol = new Dictionary<string, BinanceSymbolDetail>(symbols.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in symbols)
        {
            _bySymbol[symbol.Name] = symbol;
        }
    }

    /// <summary>
    /// 這份快照來自哪一組端點,也就是來自哪一個環境。
    /// Which endpoint set, and therefore which environment, this snapshot came from.
    /// </summary>
    public BinanceEndpoints Endpoints { get; }

    /// <summary>
    /// 交易所在產生這份回應時的時間。
    /// The exchange's own time when it produced the response.
    /// </summary>
    public DateTimeOffset ServerTime { get; }

    /// <summary>
    /// 本機取得這份快照的時間(UTC)。快取是否過期以這個時間為準。
    /// When this snapshot was fetched locally, in UTC; cache expiry is measured from here.
    /// </summary>
    /// <remarks>
    /// 刻意用本機時間而不是 <see cref="ServerTime"/>:過期判斷比較的是本機時鐘,
    /// 兩邊混用會讓時鐘偏移直接變成快取提早或延後過期。
    /// The local clock is used deliberately rather than <see cref="ServerTime"/>: expiry is compared against
    /// the local clock, and mixing the two turns any drift straight into a cache that expires early or late.
    /// </remarks>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>
    /// 全部商品的交易規則,順序與交易所回應相同。
    /// Every symbol's trading rules, in the order the exchange returned them.
    /// </summary>
    public IReadOnlyList<BinanceSymbolDetail> Symbols { get; }

    /// <summary>
    /// 交易所中立的商品清單,順序與 <see cref="Symbols"/> 相同。
    /// The exchange-neutral symbol list, in the same order as <see cref="Symbols"/>.
    /// </summary>
    public IReadOnlyList<SymbolInfo> SymbolInfos { get; }

    /// <summary>
    /// 交易規則讀不出來、因而未納入 <see cref="Symbols"/> 的商品。
    /// The symbols whose trading rules could not be read and are therefore absent from <see cref="Symbols"/>.
    /// </summary>
    /// <remarks>
    /// 正常情況下這是空的。非空時值得記一筆日誌:多半是幣安上架了新商品但規則還沒填好
    /// (狀態為 <c>PENDING_TRADING</c>),偶爾則代表對映該更新了。
    /// Normally empty. A non-empty list is worth a log line: usually Binance has announced a symbol whose rules
    /// are not filled in yet, occasionally it means the mapping needs updating.
    /// </remarks>
    public IReadOnlyList<BinanceRejectedSymbol> RejectedSymbols { get; }

    /// <summary>
    /// 依代碼尋找商品(不分大小寫)。
    /// Looks a symbol up by code, ignoring case.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol code.</param>
    /// <param name="detail">找到的商品。The symbol that was found.</param>
    /// <returns>找到時為 <see langword="true"/>。<see langword="true"/> when found.</returns>
    public bool TryGetSymbol(string symbol, out BinanceSymbolDetail? detail)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            detail = null;
            return false;
        }

        return _bySymbol.TryGetValue(symbol, out detail);
    }

    /// <summary>
    /// 查詢某個商品是否因為交易規則讀不出來而被排除,以及原因。
    /// Looks up whether a symbol was excluded for unreadable trading rules, and why.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol code.</param>
    /// <param name="rejection">排除的紀錄。The rejection record.</param>
    /// <returns>被排除時為 <see langword="true"/>。<see langword="true"/> when it was excluded.</returns>
    /// <remarks>
    /// 「這個代碼不存在」與「這個代碼的規則有問題」是兩件完全不同的事,前者該檢查拼字,
    /// 後者該去看交易所的公告。分不出來的話,一個上架中的新商品會被當成打錯字。
    /// "No such code" and "this code's rules are broken" call for entirely different responses — check the
    /// spelling versus check the exchange's announcements — and conflating them turns a symbol mid-launch into
    /// a suspected typo.
    /// </remarks>
    public bool TryGetRejection(string symbol, out BinanceRejectedSymbol? rejection)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            rejection = null;
            return false;
        }

        return _rejected.TryGetValue(symbol, out rejection);
    }

    /// <summary>
    /// 判斷這份快照在指定時間是否已經過期。
    /// Determines whether the snapshot has expired at a given moment.
    /// </summary>
    /// <param name="now">現在的時間。The current time.</param>
    /// <param name="lifetime">有效期。The lifetime.</param>
    /// <returns>已過期時為 <see langword="true"/>。<see langword="true"/> when stale.</returns>
    public bool IsExpired(DateTimeOffset now, TimeSpan lifetime) => now - FetchedAt >= lifetime;
}
