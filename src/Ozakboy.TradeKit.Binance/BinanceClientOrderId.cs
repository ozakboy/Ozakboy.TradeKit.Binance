using System.Globalization;
using System.Security.Cryptography;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 產生與檢查幣安的用戶端訂單編號(<c>newClientOrderId</c>),它同時也是本套件的冪等識別碼。
/// Generates and validates the Binance client order id (<c>newClientOrderId</c>), which doubles as this
/// package's idempotency key.
/// </summary>
/// <remarks>
/// <para>
/// 這個編號是送單逾時之後<b>唯一</b>能查回「那張單到底進去了沒有」的線索。逾時不代表對方沒收到:
/// 請求可能已經成交,只是回應在路上掉了。因此正確處置是拿這個編號去
/// <see cref="BinanceFuturesClient.GetOrderAsync"/> 查,絕不是把同一張單再送一次 —— 重送的後果是兩倍的部位。
/// This id is the <b>only</b> handle back to an order whose submission timed out. A timeout does not mean the
/// exchange missed it: the order may already be live with the reply lost in transit. The correct response is to
/// look the id up through <see cref="BinanceFuturesClient.GetOrderAsync"/>, never to send the same order again,
/// because a re-send is a doubled position.
/// </para>
/// <para>
/// 因此下單一定要帶編號:呼叫端沒給就由這裡產生一個。應用層若想在送出<b>之前</b>就把編號寫進自己的
/// 委託紀錄(逾時後才查得到),請自行呼叫 <see cref="Generate()"/> 並填進
/// <see cref="OrderRequest.ClientOrderId"/>。
/// Every order therefore carries one, generated here when the caller supplies none. An application that wants
/// the id in its own order log <b>before</b> the request leaves — which is what makes a timeout recoverable —
/// should call <see cref="Generate()"/> itself and set <see cref="OrderRequest.ClientOrderId"/>.
/// </para>
/// </remarks>
public static class BinanceClientOrderId
{
    /// <summary>
    /// 幣安允許的最大長度。
    /// The longest id Binance accepts.
    /// </summary>
    public const int MaxLength = 36;

    /// <summary>
    /// 未指定時使用的前綴,用來在交易所的委託紀錄裡認出自家送出的單。
    /// The prefix used when none is given, so that this system's orders are recognisable in the exchange's
    /// order history.
    /// </summary>
    public const string DefaultPrefix = "ozk-";

    /// <summary>
    /// 幣安接受的字元集:英文大小寫、數字,以及 <c>. : / _ -</c> 這五個符號。
    /// The character set Binance accepts: letters, digits, and the five symbols <c>. : / _ -</c>.
    /// </summary>
    /// <remarks>
    /// 對應官方文件的 <c>^[\.A-Z\:/a-z0-9_-]{1,36}$</c>。這裡以字元判斷而非正規表示式:
    /// 規則簡單到一個 switch 就寫得完,而編譯一份正規表示式只為了檢查三十六個字元並不划算。
    /// This mirrors the documented <c>^[\.A-Z\:/a-z0-9_-]{1,36}$</c>. It is checked character by character
    /// rather than with a regular expression, since the rule fits in one switch and compiling a pattern to
    /// inspect at most thirty-six characters buys nothing.
    /// </remarks>
    private const string AllowedSymbols = ".:/_-";

    /// <summary>
    /// 以目前的 UTC 時間產生一個編號。
    /// Generates an id from the current UTC time.
    /// </summary>
    /// <returns>可直接送出的編號。An id ready to send.</returns>
    public static string Generate() => Generate(DateTimeOffset.UtcNow, DefaultPrefix);

    /// <summary>
    /// 以指定的時刻產生一個編號。
    /// Generates an id from a given instant.
    /// </summary>
    /// <param name="timestamp">用於編號中的時刻。The instant embedded in the id.</param>
    /// <param name="prefix">
    /// 前綴,<see langword="null"/> 時使用 <see cref="DefaultPrefix"/>。
    /// The prefix, or <see cref="DefaultPrefix"/> when <see langword="null"/>.
    /// </param>
    /// <returns>可直接送出的編號。An id ready to send.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="prefix"/> 含有幣安不接受的字元,或加上時間戳與亂數後超過
    /// <see cref="MaxLength"/> 時擲出。
    /// Thrown when <paramref name="prefix"/> contains a character Binance rejects, or when the finished id
    /// would exceed <see cref="MaxLength"/>.
    /// </exception>
    /// <remarks>
    /// 編號由「前綴 + 毫秒時間戳 + 八位亂數」組成。時間戳讓事後查 log 的人一眼看得出這張單是什麼時候送的,
    /// 亂數則負責唯一性 —— 同一毫秒內送出兩張單並不罕見,只靠時間戳會撞號,而撞號在幣安是 <c>-4015</c> 拒單。
    /// 亂數取自 <see cref="RandomNumberGenerator"/> 而不是 <c>Random</c>:這裡要的是不可預測的唯一值,
    /// 而 <c>Random</c> 在同一個處理序裡是可預測的序列。
    /// The id is a prefix, a millisecond timestamp, and eight random characters. The timestamp tells whoever
    /// reads the log later when the order went out; the randomness provides uniqueness, because two orders
    /// within one millisecond are not unusual and a collision is an outright <c>-4015</c> rejection. The random
    /// part comes from <see cref="RandomNumberGenerator"/> rather than <c>Random</c>, whose sequence is
    /// predictable within a process.
    /// </remarks>
    public static string Generate(DateTimeOffset timestamp, string? prefix = null)
    {
        var effectivePrefix = prefix ?? DefaultPrefix;

        if (!IsAllowed(effectivePrefix))
        {
            throw new ArgumentException(
                $"前綴「{effectivePrefix}」含有幣安不接受的字元,允許的是英數與 {AllowedSymbols}。The prefix \"{effectivePrefix}\" contains a character Binance rejects; only letters, digits, and {AllowedSymbols} are allowed.",
                nameof(prefix));
        }

        var id = string.Create(
            CultureInfo.InvariantCulture,
            $"{effectivePrefix}{timestamp.ToUnixTimeMilliseconds()}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}");

        return id.Length <= MaxLength
            ? id
            : throw new ArgumentException(
                $"前綴「{effectivePrefix}」太長:加上時間戳與亂數後共 {id.Length} 個字元,超過上限 {MaxLength}。The prefix \"{effectivePrefix}\" is too long: the finished id is {id.Length} characters, above the limit of {MaxLength}.",
                nameof(prefix));
    }

    /// <summary>
    /// 檢查編號是否符合幣安的格式要求。
    /// Checks an id against Binance's format rule.
    /// </summary>
    /// <param name="clientOrderId">要檢查的編號。The id to check.</param>
    /// <returns>
    /// 長度介於 1 與 <see cref="MaxLength"/> 之間且字元全部合法時為 <see langword="true"/>。
    /// <see langword="true"/> when the length is between 1 and <see cref="MaxLength"/> and every character is
    /// accepted.
    /// </returns>
    /// <remarks>
    /// 在本地檢查是為了省一趟往返:格式不符的編號送出去只會換回 <c>-4015</c>,而那一趟還是會吃掉限流額度。
    /// Checking locally saves a round trip: a malformed id earns nothing but a <c>-4015</c>, and the attempt
    /// still costs rate-limit quota.
    /// </remarks>
    public static bool IsValid(string? clientOrderId) =>
        !string.IsNullOrEmpty(clientOrderId)
        && clientOrderId.Length <= MaxLength
        && IsAllowed(clientOrderId);

    private static bool IsAllowed(string text)
    {
        foreach (var character in text)
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                || AllowedSymbols.Contains(character, StringComparison.Ordinal);

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
