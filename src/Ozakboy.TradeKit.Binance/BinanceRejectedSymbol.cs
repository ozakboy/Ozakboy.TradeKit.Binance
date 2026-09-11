namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 一個交易規則讀不出來、因而被排除在快照之外的商品,以及它被排除的原因。
/// A symbol whose trading rules could not be read, excluded from the snapshot, together with why.
/// </summary>
/// <param name="Name">
/// 交易對代碼;連代碼都讀不出來時為 <c>#</c> 加上它在回應中的序號。
/// The symbol code, or <c>#</c> followed by its index in the response when even the code is unreadable.
/// </param>
/// <param name="Reason">
/// 被排除的原因,訊息裡會指名是哪一個欄位或哪一條規則。
/// Why it was excluded, naming the field or the rule in the message.
/// </param>
/// <remarks>
/// <para>
/// 這個型別的存在是為了在「絕不以預設值猜測」與「一顆壞蘋果不該倒掉整籃」之間取得正確的平衡。
/// 幣安會把<b>尚未上架</b>的商品也放進 <c>exchangeInfo</c>,而且填的是佔位值:2026-09-11 的 Testnet 快照裡,
/// <c>ELSAUSDT</c> 的狀態是 <c>PENDING_TRADING</c>,<c>tickSize</c> 是 <c>0</c>。
/// 若讓這一個商品把整份交易規則判定為失敗,系統就會因為一個根本不能交易的標的而完全無法運作。
/// This type exists to strike the right balance between never guessing a default and not throwing away the
/// whole basket for one bad apple. Binance lists <b>not-yet-launched</b> symbols in <c>exchangeInfo</c> with
/// placeholder values: in the Testnet snapshot of 2026-09-11, <c>ELSAUSDT</c> was <c>PENDING_TRADING</c> with a
/// <c>tickSize</c> of <c>0</c>. Letting that one symbol fail the entire rule set would stop the system over an
/// instrument nobody can trade anyway.
/// </para>
/// <para>
/// 被排除不等於被遺忘。這些商品不會出現在 <see cref="BinanceExchangeInfoSnapshot.Symbols"/> 裡,
/// 但查詢它們時回傳的是這裡記下的原因,而不是一句「查無此交易對」——
/// 後者會讓「幣安的規則有問題」看起來像「你打錯代碼了」。
/// Excluded is not forgotten. These never appear in <see cref="BinanceExchangeInfoSnapshot.Symbols"/>, but
/// looking one up returns the reason recorded here rather than a bare "no such symbol", which would make
/// "Binance's rules are broken" look like "you mistyped the code".
/// </para>
/// </remarks>
public sealed record BinanceRejectedSymbol(string Name, Error Reason);
