using System.Text.Json;

using Ozakboy.Http.Retry;

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
/// </remarks>
internal sealed class BinanceListenKeyClient
{
    private readonly BinanceApiClient _api;
    private readonly BinanceEndpoints _endpoints;

    /// <summary>
    /// 建立憑證管理器。
    /// Creates the credential manager.
    /// </summary>
    /// <param name="api">已組好管線的呼叫器。The caller with the pipeline already assembled.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="api"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="api"/> is <see langword="null"/>.
    /// </exception>
    public BinanceListenKeyClient(BinanceApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _endpoints = api.Endpoints;
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

        return body.TryGetValue(out var json) ? ReadListenKey(json) : body.ToFailure<string>();
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
        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAcknowledgement(json, BinanceUserDataPaths.ListenKey, _endpoints)
            : Result.Failure(body.Error!);
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
}
