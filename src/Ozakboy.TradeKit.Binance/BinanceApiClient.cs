using Ozakboy.Http;
using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 送出幣安 REST 請求並回傳原始回應本文,負責時間戳、<c>recvWindow</c>、權重宣告與錯誤對映。
/// Sends Binance REST requests and returns the raw body, taking care of the timestamp, <c>recvWindow</c>,
/// weight declaration, and error mapping.
/// </summary>
/// <remarks>
/// <para>
/// 每個對外請求都必須經過這裡,沒有第二條路。集中的價值在於三件容易漏掉的事被做成強制:
/// 每個請求都宣告權重(漏了就換成交易所端的 429 與封鎖)、每個簽章請求都附上時間戳與
/// <c>recvWindow</c>(漏了就是 <c>-1021</c> 或 <c>-1102</c>)、每個失敗都經過
/// <see cref="BinanceErrorMapper"/>(漏了上層就看不到幣安的語意,只看得到 HTTP 400)。
/// Every outbound request goes through here and nowhere else. Centralising it makes three easily forgotten
/// things mandatory: declaring a weight on every request, attaching a timestamp and <c>recvWindow</c> to every
/// signed one, and translating every failure through <see cref="BinanceErrorMapper"/>.
/// </para>
/// <para>
/// 參數容器一律是 <see cref="QueryParameters"/> 這個<b>有序</b>容器,絕不是 <c>Dictionary</c>。
/// 幣安簽章的待簽字串就是實際送出的查詢字串,順序換掉簽章就不同;而字典的列舉順序沒有保證,
/// 用字典的後果是「隨機出現 <c>-1022</c>、重跑又好」這種最難查的失敗。
/// The parameter container is always the <b>ordered</b> <see cref="QueryParameters"/> and never a dictionary.
/// Binance signs the query string it actually receives, so a different order is a different signature — and
/// dictionary enumeration order is not guaranteed. Using one produces intermittent <c>-1022</c> failures that
/// pass on the next run, which is the hardest kind to chase.
/// </para>
/// </remarks>
internal sealed class BinanceApiClient
{
    private readonly HttpPipelineClient _http;
    private readonly BinanceOptions _options;
    private readonly BinanceEndpoints _endpoints;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 建立呼叫器。
    /// Creates the caller.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <exception cref="ArgumentNullException">
    /// 任一參數為 <see langword="null"/> 時擲出。Thrown when any argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> 不合法時擲出。Thrown when <paramref name="options"/> is invalid.
    /// </exception>
    public BinanceApiClient(HttpPipelineClient http, BinanceOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();

        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _http = http;
        _options = options;
        _endpoints = options.ResolveEndpoints();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 這個呼叫器連的是哪一組端點。
    /// Which endpoint set this caller talks to.
    /// </summary>
    public BinanceEndpoints Endpoints => _endpoints;

    /// <summary>
    /// 目前的 UTC 時間,取自注入的時間來源。
    /// The current UTC time from the injected time source.
    /// </summary>
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    /// <summary>
    /// 送出公開端點的 GET 請求。
    /// Sends a GET to a public endpoint.
    /// </summary>
    /// <param name="path">端點路徑,不帶開頭斜線。The endpoint path, without a leading slash.</param>
    /// <param name="query">查詢參數,沒有時傳 <see langword="null"/>。The query parameters, or <see langword="null"/>.</param>
    /// <param name="weight">這個請求的權重。The weight this request consumes.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>回應本文,或已對映的失敗。The response body, or a mapped failure.</returns>
    public async Task<Result<string>> GetPublicAsync(
        string path,
        QueryParameters? query,
        int weight,
        CancellationToken cancellationToken)
    {
        // 請求必須在送出完成之後才釋放,所以這裡一定要 await —— 直接回傳 Task 會讓 using 在
        // 非同步作業還在進行時就把 HttpRequestMessage 處理掉。
        // The request may only be disposed once the send has completed, so this must await: returning the task
        // directly would let the using block dispose the message while the operation is still in flight.
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        request.WithWeight(weight);

        if (query is not null)
        {
            request.WithQueryParameters(query);
        }

        return await SendAsync(request, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 送出需要簽章的 GET 請求,自動補上 <c>timestamp</c> 與 <c>recvWindow</c>。
    /// Sends a signed GET, appending <c>timestamp</c> and <c>recvWindow</c> automatically.
    /// </summary>
    /// <param name="path">端點路徑,不帶開頭斜線。The endpoint path, without a leading slash.</param>
    /// <param name="query">
    /// 已填好業務參數的建構器,沒有時傳 <see langword="null"/>。時間戳與 <c>recvWindow</c> 會接在最後。
    /// A builder already carrying the business parameters, or <see langword="null"/>. The timestamp and
    /// <c>recvWindow</c> are appended last.
    /// </param>
    /// <param name="weight">這個請求的權重。The weight this request consumes.</param>
    /// <param name="operation">操作名稱,用於憑證缺漏時的錯誤訊息。The operation name, used if credentials are missing.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>回應本文,或已對映的失敗。The response body, or a mapped failure.</returns>
    public async Task<Result<string>> GetSignedAsync(
        string path,
        QueryParametersBuilder? query,
        int weight,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!_options.HasCredentials)
        {
            // 在送出之前就擋下來。沒有憑證還是把請求送出去,拿到的會是 -2014 或 -2015,
            // 而那兩句訊息會把人帶去查 IP 白名單,真正的原因卻只是設定沒讀進來。
            // Stopped before it leaves. Sending anyway earns a -2014 or -2015 whose wording sends the reader
            // off to inspect IP allowlists when the real cause is configuration that never loaded.
            return BinanceErrors.CredentialsMissing(operation);
        }

        var builder = query ?? QueryParameters.CreateBuilder();

        builder
            .Add(BinanceConstants.RecvWindowParameterName, (long)_options.RecvWindow.TotalMilliseconds)
            .Add(BinanceConstants.TimestampParameterName, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        request
            .WithWeight(weight)
            .WithQueryParameters(builder.Build())
            .WithSignature();

        return await SendAsync(request, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<string>> SendAsync(
        HttpRequestMessage request,
        string path,
        CancellationToken cancellationToken)
    {
        var response = await _http.SendForStringAsync(request, cancellationToken).ConfigureAwait(false);

        return response.IsFailure
            ? BinanceErrorMapper.MapHttpError(response.Error, path, _endpoints)
            : response;
    }
}
