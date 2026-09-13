using System.Text.Json;

using Ozakboy.Http.Retry;
using Ozakboy.Security.Masking;

namespace Ozakboy.TradeKit.Binance.UserData;

/// <summary>
/// 使用者資料串流憑證(listenKey)的建立、續期與關閉。
/// Creates, renews, and closes the user data stream credential — the listenKey.
/// </summary>
/// <remarks>
/// <para>
/// <b>三個操作全部走既有的簽章管線。</b> 官方文件說這組端點「只要帶 X-MBX-APIKEY」,實測
/// (2026-09-11,Testnet)「只帶金鑰」與「額外帶 timestamp + signature」兩種形式都回 200。
/// 走簽章管線是刻意的選擇:它順帶把限流宣告與重試策略一併套上,而為了省一個簽章另開一條
/// 「只帶金鑰」的請求路徑,等於讓這三個請求繞過本地限流器 —— 那是 429 與後續封 IP 的來源。
/// <b>All three operations go through the existing signing pipeline.</b> The documentation says these endpoints
/// need only the <c>X-MBX-APIKEY</c> header, and both forms — header only, and header plus
/// <c>timestamp</c>/<c>signature</c> — were measured to return 200 on the testnet on 2026-09-11. Using the
/// signed path is deliberate: it brings the weight declaration and the retry policy with it, whereas adding a
/// key-only request path to save one signature would let these three requests bypass the local rate limiter,
/// which is where a 429 and the IP ban that follows come from.
/// </para>
/// <para>
/// <b><see cref="KeepAliveAsync"/> 與 <see cref="DeleteAsync"/> 不需要、也不接受 listenKey。</b>
/// USDⓈ-M 合約的這兩個端點是從 API 金鑰認出憑證的。這不只是省一個參數 ——
/// 方法簽章裡沒有那個字串,就沒有任何一條路能讓它流進查詢字串、日誌或錯誤訊息。
/// 現貨的同名端點要求帶參數,照現貨抄過來就會把憑證寫進 URL。
/// <b><see cref="KeepAliveAsync"/> and <see cref="DeleteAsync"/> neither need nor accept the listenKey.</b> On
/// USDⓈ-M futures the exchange identifies the credential from the API key. That is more than one parameter
/// saved: with the string absent from the signature, there is no route by which it can reach a query string, a
/// log, or an error message. The spot endpoints of the same name do take the parameter, and copying their shape
/// would put the credential into the URL.
/// </para>
/// <para>
/// <b>建立與續期的回應本體都含有完整的 listenKey</b>(實測:<c>PUT</c> 回的不是官方文件說的空物件
/// <c>{}</c>,而是帶著完整憑證的物件)。因此這裡的回應處理一律只取出需要的部分,
/// 任何失敗訊息都不會附上回應本體。
/// <b>The create and renew responses both carry the full listenKey</b> — measured: the <c>PUT</c> does not
/// return the empty <c>{}</c> the documentation describes but an object holding the whole credential. Response
/// handling here therefore extracts only what is needed, and no failure message ever attaches the body.
/// </para>
/// <para>
/// <b>憑證一拿到手就登記成字面祕密。</b> 建立與續期一旦讀出 listenKey,這裡立刻以
/// <see cref="SecretMasker.RegisterKnownSecret"/> 把它登記到<b>這個具名用戶端的</b>遮罩器上。
/// 欄位名規則(<see cref="BinanceConstants.SensitiveParameterNames"/>)只作用在結構化的 payload,
/// 攔不到沒有欄位名的位置 —— 撥號位址的路徑段、第三方套件已經格式化好的訊息、例外文字。
/// 字面替換這一道不管值從哪條路徑流出去都攔得到,那正是它存在的理由。
/// <b>The credential is registered as a literal secret the moment it is obtained.</b> As soon as a create or a
/// renewal yields a listenKey it is registered on <b>this named client's</b> masker with
/// <see cref="SecretMasker.RegisterKnownSecret"/>. The field-name rule in
/// <see cref="BinanceConstants.SensitiveParameterNames"/> applies to structured payloads alone and cannot reach
/// places that have no field name — a path segment of the dialled address, a message some other package has
/// already formatted, exception text. Literal replacement catches the value whichever route it leaves by, which
/// is the whole point of having it.
/// </para>
/// </remarks>
internal sealed class BinanceListenKeyClient
{
    private readonly BinanceApiClient _api;
    private readonly BinanceEndpoints _endpoints;
    private readonly SecretMasker? _masker;

    /// <summary>
    /// 建立憑證管理器。
    /// Creates the credential manager.
    /// </summary>
    /// <param name="api">已組好管線的呼叫器。The caller with the pipeline already assembled.</param>
    /// <param name="masker">
    /// 這個具名用戶端的遮罩器,取得的 listenKey 會登記到它上面。傳 <see langword="null"/> 就不登記 ——
    /// 憑證屆時只剩結構上的保護,任何以字面值流出的路徑都攔不住。
    /// The named client's masker, onto which every listenKey obtained is registered. With
    /// <see langword="null"/> nothing is registered and the credential keeps only its structural protection,
    /// leaving every route that carries it as a literal unguarded.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="api"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="api"/> is <see langword="null"/>.
    /// </exception>
    public BinanceListenKeyClient(BinanceApiClient api, SecretMasker? masker = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _endpoints = api.Endpoints;
        _masker = masker;
    }

    /// <summary>
    /// 建立一把新的串流憑證,或取回帳戶目前那一把並順便延長它。
    /// Creates a stream credential, or returns the account's current one and extends it.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 憑證字串,或已對映的失敗。<b>呼叫端必須把回傳值當成祕密處理。</b>
    /// The credential, or a mapped failure. <b>The caller must treat the value as a secret.</b>
    /// </returns>
    /// <remarks>
    /// 冪等性宣告為 <see cref="RequestIdempotency.Idempotent"/>,允許重試。這不是「POST 通常可以重試」的推定,
    /// 而是這個端點的實際語意:帳戶同時只有一把 listenKey,重複呼叫拿回的是同一把並延長效期,
    /// 不會多出第二把。反過來把它標成不可重試的代價很具體 —— 一次網路抖動就讓整個帳戶串流啟動失敗。
    /// The idempotency is declared <see cref="RequestIdempotency.Idempotent"/> so the request may be retried.
    /// That is not the usual "a POST is probably safe" guess but this endpoint's actual behaviour: an account
    /// holds one listenKey at a time, and calling again returns the same one with its validity extended rather
    /// than creating a second. Declaring it non-retryable has a concrete cost instead — one network hiccup and
    /// the whole account stream fails to start.
    /// </remarks>
    public async Task<Result<string>> CreateAsync(CancellationToken cancellationToken)
    {
        var body = await _api
            .SendSignedAsync(
                HttpMethod.Post,
                BinanceUserDataPaths.ListenKey,
                query: null,
                BinanceRequestWeights.ListenKey,
                BinanceUserDataPaths.CreateOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        if (!body.TryGetValue(out var json))
        {
            return body.ToFailure<string>();
        }

        var listenKey = ReadListenKey(json);

        // 登記在「回傳給呼叫端之前」。晚一步就有一整段憑證已經在手上、遮罩器卻還不認得它的空窗,
        // 而那段空窗正是撥號位址被組出來的地方。
        // Registered before the value goes back to the caller. A later registration leaves a window in which the
        // credential is in hand and the masker does not yet know it — and that window is exactly where the
        // dialled address gets built.
        if (listenKey.TryGetValue(out var value))
        {
            RegisterKnownCredential(value);
        }

        return listenKey;
    }

    /// <summary>
    /// 延長目前這把憑證的效期。
    /// Extends the validity of the current credential.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>成功,或已對映的失敗。Success, or a mapped failure.</returns>
    /// <remarks>
    /// 憑證只活 60 分鐘,過期之後交易所會送出 <c>listenKeyExpired</c> 並斷開串流,期間發生的委託與成交
    /// 一概不補送。續期失敗<b>不可以</b>被吞掉:它是「再過不到一小時,帳戶事件就會全部消失」的唯一預告。
    /// A credential lives 60 minutes; once it expires the exchange sends <c>listenKeyExpired</c>, drops the
    /// stream, and replays nothing that happened meanwhile. A failed renewal must <b>not</b> be swallowed: it is
    /// the only advance warning that account events will stop arriving within the hour.
    /// </remarks>
    public async Task<Result> KeepAliveAsync(CancellationToken cancellationToken)
    {
        var body = await _api
            .SendSignedAsync(
                HttpMethod.Put,
                BinanceUserDataPaths.ListenKey,
                query: null,
                BinanceRequestWeights.ListenKey,
                BinanceUserDataPaths.KeepAliveOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        // 成功的回應本體就是憑證本身,所以只看成敗、不看內容。
        // ReadAcknowledgement 只讀 code 與 msg 兩個欄位,不會把本體帶進任何錯誤。
        // A successful body is the credential itself, so only the outcome is inspected. ReadAcknowledgement
        // reads only the code and msg fields and never carries the body into an error.
        if (!body.TryGetValue(out var json))
        {
            return Result.Failure(body.Error!);
        }

        // 續期的回應帶回的<b>可能</b>是另一把憑證(實測 PUT 回的不是文件說的空物件,而是完整的一把)。
        // 只讀出來登記,不往外傳、也不影響這次續期的成敗。
        // A renewal <b>may</b> come back with a different credential — measured: the PUT returns a full one
        // rather than the empty object the documentation describes. It is read only to be registered; it goes
        // nowhere else and does not affect whether this renewal succeeded.
        RegisterRenewedCredential(json);

        return BinanceResponseReader.ReadAcknowledgement(json, BinanceUserDataPaths.ListenKey, _endpoints);
    }

    /// <summary>
    /// 關閉目前這把憑證,串流隨即失效。
    /// Closes the current credential; the stream stops immediately.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>成功,或已對映的失敗。Success, or a mapped failure.</returns>
    /// <remarks>
    /// 實測:<c>DELETE</c> 之後那把憑證立刻失效,再 <c>PUT</c> 會回
    /// <c>400 {"code":-1125,"msg":"This listenKey does not exist."}</c>。因此關閉是<b>不可逆</b>的,
    /// 只有在確定不再需要這條串流時才呼叫。
    /// Measured: after the <c>DELETE</c> the credential is immediately invalid and a following <c>PUT</c>
    /// answers <c>400 {"code":-1125,"msg":"This listenKey does not exist."}</c>. Closing is therefore
    /// <b>irreversible</b> and belongs only where the stream is genuinely finished with.
    /// </remarks>
    public async Task<Result> DeleteAsync(CancellationToken cancellationToken)
    {
        var body = await _api
            .SendSignedAsync(
                HttpMethod.Delete,
                BinanceUserDataPaths.ListenKey,
                query: null,
                BinanceRequestWeights.ListenKey,
                BinanceUserDataPaths.DeleteOperation,
                RequestIdempotency.Idempotent,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAcknowledgement(json, BinanceUserDataPaths.ListenKey, _endpoints)
            : Result.Failure(body.Error!);
    }

    /// <summary>
    /// 從建立回應取出憑證。
    /// Extracts the credential from a create response.
    /// </summary>
    /// <param name="json">回應本體。The response body.</param>
    /// <returns>憑證,或說不出內容的失敗。The credential, or a failure that says nothing about the content.</returns>
    /// <remarks>
    /// 解析失敗的訊息刻意不含回應本體。一般端點會把讀不懂的本體截一段進錯誤資料,那對查問題很有用;
    /// 但這個端點的本體在正常情況下<b>就是</b>憑證,截進去等於把憑證寫進日誌。
    /// The failure message deliberately omits the body. Elsewhere a truncated body is valuable when a response
    /// cannot be read; here the body <b>is</b> the credential in the normal case, and truncating it into the
    /// error writes the credential into the log.
    /// </remarks>
    private Result<string> ReadListenKey(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BinanceUserDataErrors.ListenKeyMissing(_endpoints);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 連 JsonException.Message 都不轉述:它會引用出錯位置附近的字元。
            // Not even JsonException.Message is relayed: it quotes characters from around the failure point.
            return BinanceUserDataErrors.MalformedEvent(
                $"{BinanceUserDataPaths.Context} 的憑證回應不是合法的 JSON;內容不列出,因為它正常情況下就是憑證本身。The credential response for {BinanceUserDataPaths.Context} is not valid JSON; the content is not shown because in the normal case it is the credential itself.",
                _endpoints);
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                && BinanceJson.TryGetString(document.RootElement, BinanceUserDataPaths.ListenKeyField, out var key)
                    ? Result.Success(key)
                    : BinanceUserDataErrors.ListenKeyMissing(_endpoints);
        }
    }

    /// <summary>
    /// 從續期回應裡把憑證讀出來登記,讀不到就算了。
    /// Registers the credential carried by a renewal response, and does nothing when there is none.
    /// </summary>
    /// <param name="json">續期的回應本體。The renewal response body.</param>
    /// <remarks>
    /// 這裡刻意不產生任何失敗。續期的成敗由 <see cref="BinanceResponseReader.ReadAcknowledgement"/> 判定,
    /// 讀不出憑證只代表這次回應沒帶著一把(文件說的空物件就是這個形狀),不是續期失敗 ——
    /// 把它當失敗會讓一次無害的形狀差異變成「帳戶事件即將消失」的假警報。
    /// No failure is produced here. Whether the renewal succeeded is <see cref="BinanceResponseReader.ReadAcknowledgement"/>'s
    /// call, and a body without a credential merely means this response carried none — the empty object the
    /// documentation describes has exactly that shape. Treating it as a failure would turn a harmless difference
    /// in shape into a false alarm that account events are about to stop.
    /// </remarks>
    private void RegisterRenewedCredential(string json)
    {
        if (_masker is null || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 連 JsonException.Message 都不轉述:它會引用出錯位置附近的字元,而這裡的本體正常情況下就是憑證。
            // Not even JsonException.Message is relayed: it quotes characters from around the failure point, and
            // this body is the credential in the normal case.
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && BinanceJson.TryGetString(document.RootElement, BinanceUserDataPaths.ListenKeyField, out var key))
            {
                RegisterKnownCredential(key);
            }
        }
    }

    /// <summary>
    /// 把一把憑證登記成遮罩器的已知祕密。
    /// Registers one credential as a known secret on the masker.
    /// </summary>
    /// <param name="listenKey">剛取得的憑證。The credential just obtained.</param>
    /// <remarks>
    /// <para>
    /// <b>換發之後不移除舊的那一把。</b> <see cref="SecretMasker"/> 只有清空全部的
    /// <see cref="SecretMasker.ClearKnownSecrets"/>,沒有移除單一項的 API;就算有也不會用它 ——
    /// 一把已經失效的憑證被多遮一次沒有任何壞處,而少遮一次就是外流。
    /// 每次續期最多多一筆,帳戶的憑證一小時才換一次,清單不會長到值得擔心。
    /// <b>The previous key is not removed after a rotation.</b> <see cref="SecretMasker"/> offers only
    /// <see cref="SecretMasker.ClearKnownSecrets"/>, which clears everything, and no per-value removal; were
    /// there one it would still go unused, because masking a lapsed credential once more costs nothing while
    /// masking it once less is a leak. A renewal adds at most one entry and an account rotates its credential
    /// once an hour, so the list never grows enough to matter.
    /// </para>
    /// <para>
    /// 長度不足的值直接略過,不讓 <see cref="SecretMasker.RegisterKnownSecret"/> 擲出的
    /// <see cref="ArgumentException"/> 打到啟動路徑上。這不是妥協:短到那個地步的字串,全域字面替換會把大量
    /// 正常日誌一起遮掉,而遮罩器拒絕它正是為了這件事。真實的 listenKey 有數十個字元,
    /// 走到這一支代表回應的形狀本來就不對了。
    /// A value that is too short is skipped rather than letting the <see cref="ArgumentException"/> from
    /// <see cref="SecretMasker.RegisterKnownSecret"/> reach the start-up path. That is not a compromise: a
    /// literal that short would mask great swathes of ordinary log text, which is precisely why the masker
    /// refuses it. A real listenKey is dozens of characters, so reaching this branch already means the response
    /// had the wrong shape.
    /// </para>
    /// </remarks>
    private void RegisterKnownCredential(string listenKey)
    {
        if (_masker is null || listenKey.Length < SecretMasker.MinimumKnownSecretLength)
        {
            return;
        }

        _ = _masker.RegisterKnownSecret(listenKey);
    }
}
