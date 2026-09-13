namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 串流憑證生命週期的唯讀快照:何時建立、何時續期、成功與失敗各幾次。
/// A read-only snapshot of the stream credential's lifecycle: when it was created, when it was last renewed,
/// and how many renewals and rebuilds have happened.
/// </summary>
/// <param name="CreatedAt">
/// <b>目前這一把</b>憑證建立的時刻(UTC);還沒有建立過任何憑證時為 <see langword="null"/>。
/// 重建憑證會把它換成新那一把的建立時刻。
/// When the credential <b>currently in use</b> was created, in UTC, or <see langword="null"/> before any has
/// been created. A rebuild replaces it with the new credential's creation time.
/// </param>
/// <param name="LastRenewedAt">
/// 目前這一把憑證最後一次<b>成功</b>續期的時刻(UTC);還沒有成功續期過時為 <see langword="null"/>。
/// 重建憑證會把它清回 <see langword="null"/> —— 新的那一把還沒有續期過。
/// When the credential currently in use was last renewed <b>successfully</b>, in UTC, or
/// <see langword="null"/> when it has not been. A rebuild clears it, because the new credential has not been
/// renewed yet.
/// </param>
/// <param name="RenewalCount">
/// 成功續期的累計次數,涵蓋這個串流的整個生命週期(重建憑證<b>不會</b>歸零)。
/// The number of successful renewals over this feed's whole lifetime; a rebuild does <b>not</b> reset it.
/// </param>
/// <param name="ConsecutiveRenewalFailures">
/// 目前連續失敗幾次。任何一次成功都會歸零,建立新憑證也會歸零。
/// How many renewals have failed in a row. Any success resets it, and so does creating a new credential.
/// </param>
/// <param name="RebuildCount">
/// 因為 <c>listenKeyExpired</c> 而重建憑證與連線的累計次數。
/// How many times the credential and the connection have been rebuilt after a <c>listenKeyExpired</c>.
/// </param>
/// <remarks>
/// <para>
/// <b>這是給健康度呈現與事後對帳用的,不是控制流程用的。</b> 續期的成敗照舊以串流上的失敗元素通知消費端
/// (那是憑證過期前唯一的預告),這份快照回答的是另一種問題:
/// 「這 24 小時續期了幾次?有沒有失敗過?憑證撐了多久?」
/// 少了它,唯一觀測得到的只有「收到 <c>listenKeyExpired</c>、套件自動重建」這個結果,
/// 而「30 分鐘一次的續期為什麼撐不到 60 分鐘壽命」在日誌與畫面上都答不出來。
/// <b>This exists for health display and after-the-fact reconciliation, not for control flow.</b> A renewal's
/// outcome still reaches consumers as a failure element on the stream — the only advance warning before the
/// credential lapses — while this snapshot answers a different question: how many renewals happened over the
/// last day, whether any failed, and how long the credential actually lived. Without it the only observable
/// fact is that a <c>listenKeyExpired</c> arrived and the package rebuilt everything, which cannot explain why
/// a renewal every 30 minutes failed to keep a 60-minute credential alive.
/// </para>
/// <para>
/// <b>不含憑證的值,而且不可能含。</b> 這個型別沒有任何欄位放得下 listenKey,所以把快照整個寫進日誌、
/// 序列化進健康度端點都是安全的 —— 那正是它只放時刻與次數的理由。
/// <b>It carries no credential and has nowhere to put one.</b> No member of this type can hold a listenKey, so
/// writing the whole snapshot to a log or serialising it into a health endpoint is safe, which is exactly why
/// it holds nothing but instants and counts.
/// </para>
/// </remarks>
public sealed record BinanceListenKeyStatus(
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastRenewedAt,
    int RenewalCount,
    int ConsecutiveRenewalFailures,
    int RebuildCount);
