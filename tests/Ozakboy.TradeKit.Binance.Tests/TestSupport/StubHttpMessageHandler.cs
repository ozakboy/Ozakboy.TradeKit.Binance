using System.Net;
using Ozakboy.Http;
using Ozakboy.Http.Retry;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 假的最內層處理器:記錄收到的請求,並依腳本回傳回應。測試全程不連網。
/// A stub innermost handler that records the requests it receives and replies from a script. Tests never touch
/// the network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _callCount;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>
    /// 收到的請求快照,依收到順序排列。
    /// Snapshots of the requests received, in arrival order.
    /// </summary>
    public List<RequestSnapshot> Requests { get; } = [];

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// 最後一次收到的請求。
    /// The most recent request.
    /// </summary>
    public RequestSnapshot LastRequest
    {
        get
        {
            lock (Requests)
            {
                return Requests[^1];
            }
        }
    }

    public static StubHttpMessageHandler Json(string content, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
        });

    /// <summary>
    /// 依序回傳內容;腳本用完後重複最後一則。
    /// Replies with the given bodies in order, repeating the last once the script runs out.
    /// </summary>
    public static StubHttpMessageHandler Sequence(params string[] bodies) =>
        new((_, attempt) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                bodies[Math.Min(attempt - 1, bodies.Length - 1)],
                System.Text.Encoding.UTF8,
                "application/json"),
        });

    /// <summary>
    /// 依請求路徑挑選回應。用於「一次操作打兩個端點」的情境。
    /// Picks a reply by request path, for operations that call two endpoints.
    /// </summary>
    public static StubHttpMessageHandler ByPath(IReadOnlyDictionary<string, string> bodies) =>
        new((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            foreach (var (fragment, body) in bodies)
            {
                if (path.Contains(fragment, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"code\":-1121,\"msg\":\"Invalid symbol.\"}"),
            };
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _callCount);

        lock (Requests)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase),
                request.GetWeight(),
                request.GetIdempotency(),
                request.GetRetryPolicy()));
        }

        return Task.FromResult(_responder(request, attempt));
    }

    /// <summary>
    /// 一次請求的快照。除了方法與位址,也記下宣告的權重與冪等性 ——「下單不可重試」這件事
    /// 藏在 <see cref="HttpRequestMessage.Options"/> 裡,不撈出來就驗不到。
    /// A snapshot of one request. Besides the method and address it records the declared weight and
    /// idempotency: "an order must never be retried" lives in the request options, and what is not extracted
    /// cannot be asserted.
    /// </summary>
    internal sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? RequestUri,
        Dictionary<string, string> Headers,
        int? Weight,
        RequestIdempotency Idempotency,
        RetryPolicy? RetryPolicy)
    {
        /// <summary>
        /// 查詢字串,已去掉開頭的問號。
        /// The query string with its leading question mark removed.
        /// </summary>
        public string Query => RequestUri?.Query.TrimStart('?') ?? string.Empty;

        /// <summary>
        /// 查詢字串的參數名,順序與實際送出的一致(也就是待簽字串的順序)。
        /// The parameter names in the order they were sent, which is the order that was signed.
        /// </summary>
        public IReadOnlyList<string> ParameterNames =>
            [.. Query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=')[0])];

        /// <summary>
        /// 取出某個查詢參數的值(已解碼);找不到時回傳 <see langword="null"/>。
        /// Returns one decoded query parameter, or <see langword="null"/> when it is absent.
        /// </summary>
        /// <param name="name">參數名。The parameter name.</param>
        /// <returns>參數值。The value.</returns>
        public string? Parameter(string name)
        {
            foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);

                if (separator > 0 && part[..separator] == name)
                {
                    return Uri.UnescapeDataString(part[(separator + 1)..]);
                }
            }

            return null;
        }
    }
}
