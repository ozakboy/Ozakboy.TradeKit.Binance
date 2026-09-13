using Microsoft.Extensions.Logging;

namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 使用者資料串流憑證生命週期的日誌行。
/// The log lines of the user data stream credential's lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>這幾行沒有一行帶得到 listenKey。</b> 傳進來的只有時刻、次數、毫秒數與中立錯誤碼,
/// 型別上就沒有任何一個參數放得下憑證。本套件自己寫出去的日誌不經過 <c>Ozakboy.Http</c> 的遮罩器
/// (那一道遮的是它自己的請求日誌與錯誤),所以這一層唯一的保護就是「根本不把憑證傳進來」。
/// <b>Not one of these lines can carry the listenKey.</b> What comes in is instants, counts, milliseconds, and a
/// neutral error code; no parameter is even of a type that could hold the credential. Logs this package writes
/// itself do not pass through the <c>Ozakboy.Http</c> masker — that one covers its own request logs and errors —
/// so the only protection at this layer is never handing the credential over in the first place.
/// </para>
/// <para>
/// <b>為什麼一個值只出現一次,訊息卻是雙語的。</b> <see cref="LoggerMessage"/> 的 <c>Define</c> 最多六個參數,
/// 而把同一個值在中英兩半各寫一次會用掉雙倍的參數位(憑證失效那一行有六個值,寫兩次根本放不下),
/// 還會讓結構化輸出多出一組意義重複的欄位。因此格式一律是「中文標籤 / English label:值」,
/// 兩種語言共用同一個佔位符。
/// <b>Why each value appears once in a bilingual message.</b> <see cref="LoggerMessage"/>'s <c>Define</c> takes
/// at most six parameters, and repeating a value in both halves would double the slots — the expiry line has six
/// values and simply would not fit — besides adding a duplicated field to every structured sink. The shape is
/// therefore "Chinese label / English label: value", with both languages sharing one placeholder.
/// </para>
/// <para>
/// <b>事件編號。</b> 1000–1099 保留給使用者資料串流,這是本套件第一組認領的編號區間;
/// 其他區塊日後要寫日誌時,請往下接新的百位區間,不要插進這一段。
/// <b>Event ids.</b> 1000–1099 belong to the user data stream and are the first block this package claims;
/// anything else that starts logging later should take the next hundred rather than borrow from this range.
/// </para>
/// </remarks>
internal static class BinanceUserDataLog
{
    /// <summary>
    /// 憑證續期成功。
    /// A credential renewal succeeded.
    /// </summary>
    private static readonly Action<ILogger, int, long, double, Exception?> RenewalSucceededCore =
        LoggerMessage.Define<int, long, double>(
            LogLevel.Information,
            new EventId(1001, "ListenKeyRenewed"),
            "使用者資料串流憑證續期成功 / user data stream credential renewed。累計第 {RenewalCount} 次 / successful renewals so far;耗時 {ElapsedMilliseconds} ms / elapsed;距上次建立或續期 {MinutesSinceLastRenewal:F1} 分鐘 / minutes since created or last renewed。");

    /// <summary>
    /// 憑證續期失敗。
    /// A credential renewal failed.
    /// </summary>
    private static readonly Action<ILogger, int, string, long, string, Exception?> RenewalFailedCore =
        LoggerMessage.Define<int, string, long, string>(
            LogLevel.Warning,
            new EventId(1002, "ListenKeyRenewalFailed"),
            "使用者資料串流憑證續期失敗 / user data stream credential renewal failed。連續第 {ConsecutiveFailures} 次 / consecutive failures;錯誤碼 {ErrorCode} / neutral error code;耗時 {ElapsedMilliseconds} ms / elapsed;{NextAttempt}");

    /// <summary>
    /// 收到 <c>listenKeyExpired</c>。
    /// A <c>listenKeyExpired</c> arrived.
    /// </summary>
    private static readonly Action<ILogger, string, double, string, string, int, int, Exception?> CredentialExpiredCore =
        LoggerMessage.Define<string, double, string, string, int, int>(
            LogLevel.Warning,
            new EventId(1003, "ListenKeyExpired"),
            "使用者資料串流收到 listenKeyExpired,正在重建憑證與連線 / received listenKeyExpired, rebuilding the credential and the connection。憑證建立於 {CreatedAt} / created at;已存活 {AgeMinutes:F1} 分鐘 / age in minutes;最後一次成功續期 {LastRenewedAt} / last successful renewal;距今 {MinutesSinceLastRenewal} 分鐘 / minutes ago;累計成功續期 {RenewalCount} 次 / successful renewals so far;連續失敗 {ConsecutiveFailures} 次 / consecutive failures。");

    /// <summary>
    /// 記錄一次成功的續期。
    /// Records a successful renewal.
    /// </summary>
    /// <param name="logger">日誌輸出端,<see langword="null"/> 時什麼都不做。The sink; nothing happens when it is <see langword="null"/>.</param>
    /// <param name="renewalCount">成功續期的累計次數。The running count of successful renewals.</param>
    /// <param name="elapsedMilliseconds">這一次續期的往返耗時。How long this renewal's round trip took.</param>
    /// <param name="minutesSinceLastRenewal">距上次建立或成功續期幾分鐘。Minutes since the credential was created or last renewed.</param>
    public static void RenewalSucceeded(
        ILogger? logger,
        int renewalCount,
        long elapsedMilliseconds,
        double minutesSinceLastRenewal)
    {
        if (logger is not null)
        {
            RenewalSucceededCore(logger, renewalCount, elapsedMilliseconds, minutesSinceLastRenewal, null);
        }
    }

    /// <summary>
    /// 記錄一次失敗的續期。
    /// Records a failed renewal.
    /// </summary>
    /// <param name="logger">日誌輸出端,<see langword="null"/> 時什麼都不做。The sink; nothing happens when it is <see langword="null"/>.</param>
    /// <param name="consecutiveFailures">這是連續第幾次失敗。Which consecutive failure this is.</param>
    /// <param name="errorCode">
    /// 交易所中立的錯誤碼。<b>只有代碼,不含訊息</b> —— 續期端點的回應本體在正常情況下就是憑證,
    /// 而對映後的訊息可能夾帶交易所回傳的原文。
    /// The exchange-neutral error code. <b>The code alone, never the message</b>: the body of this endpoint is
    /// the credential in the normal case, and a mapped message can still quote what the exchange returned.
    /// </param>
    /// <param name="elapsedMilliseconds">這一次嘗試的往返耗時。How long this attempt's round trip took.</param>
    /// <param name="nextAttempt">接下來會怎麼做,雙語。What happens next, bilingual.</param>
    public static void RenewalFailed(
        ILogger? logger,
        int consecutiveFailures,
        string errorCode,
        long elapsedMilliseconds,
        string nextAttempt)
    {
        if (logger is not null)
        {
            RenewalFailedCore(logger, consecutiveFailures, errorCode, elapsedMilliseconds, nextAttempt, null);
        }
    }

    /// <summary>
    /// 記錄收到 <c>listenKeyExpired</c> 時憑證的完整履歷。
    /// Records the credential's whole history at the moment a <c>listenKeyExpired</c> arrives.
    /// </summary>
    /// <param name="logger">日誌輸出端,<see langword="null"/> 時什麼都不做。The sink; nothing happens when it is <see langword="null"/>.</param>
    /// <param name="createdAt">這一把憑證建立於何時。When this credential was created.</param>
    /// <param name="ageMinutes">它活了幾分鐘。How many minutes it lived.</param>
    /// <param name="lastRenewedAt">最後一次成功續期的時刻,沒有就寫明沒有。The last successful renewal, or a note saying there was none.</param>
    /// <param name="minutesSinceLastRenewal">距最後一次成功續期幾分鐘,沒有續期過就寫明沒有。Minutes since that renewal, or a note saying there was none.</param>
    /// <param name="renewalCount">成功續期的累計次數。The running count of successful renewals.</param>
    /// <param name="consecutiveFailures">目前連續失敗幾次。The current consecutive failure count.</param>
    /// <remarks>
    /// 這一行就是用來回答「30 分鐘一次的續期,為什麼憑證撐不到 60 分鐘」的:
    /// 最後一次成功續期才過幾分鐘、連續失敗是 0,那就是憑證本身有壽命上限,不是續期漏做;
    /// 連續失敗不是 0,就是那幾次 <c>PUT</c> 沒送成功。少了這一行,兩者在現場長得一模一樣。
    /// This line exists to answer why a credential renewed every 30 minutes still lapsed before 60. A recent
    /// successful renewal with no consecutive failures means the credential has a ceiling of its own rather
    /// than a renewal having been missed; a non-zero failure count means those <c>PUT</c>s did not get through.
    /// Without the line the two look identical from the outside.
    /// </remarks>
    public static void CredentialExpired(
        ILogger? logger,
        string createdAt,
        double ageMinutes,
        string lastRenewedAt,
        string minutesSinceLastRenewal,
        int renewalCount,
        int consecutiveFailures)
    {
        if (logger is not null)
        {
            CredentialExpiredCore(
                logger,
                createdAt,
                ageMinutes,
                lastRenewedAt,
                minutesSinceLastRenewal,
                renewalCount,
                consecutiveFailures,
                null);
        }
    }
}
