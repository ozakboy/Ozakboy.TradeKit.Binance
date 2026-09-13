using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;

using Ozakboy.Security.Masking;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 把使用者資料串流接上既有的假連線與假 HTTP 處理器,讓整條路徑在完全不碰網路的情況下被驗證。
/// Wires the user data stream to the existing fake connection and fake HTTP handler so the whole path can be
/// exercised without touching the network.
/// </summary>
/// <remarks>
/// 這裡<b>沒有</b>新的測試替身。<c>FakeWebSocketConnection</c>、<c>FakeWebSocketConnectionFactory</c>、
/// <c>StubHttpMessageHandler</c>、<c>TestClock</c>、<c>TestPipeline</c> 全部沿用 <c>TestSupport</c> 那一份;
/// 這個型別只負責把它們依這條串流的需要組起來,並提供兩個等待輔助。
/// There are <b>no</b> new test doubles here. <c>FakeWebSocketConnection</c>,
/// <c>FakeWebSocketConnectionFactory</c>, <c>StubHttpMessageHandler</c>, <c>TestClock</c>, and
/// <c>TestPipeline</c> all come from <c>TestSupport</c>; this type only assembles them the way this stream
/// needs and adds two waiting helpers.
/// </remarks>
internal static class UserDataFixture
{
    /// <summary>
    /// 所有等待的上限。串流測試絕不可以無限等 —— 掛住的測試比紅燈的測試難查得多。
    /// The ceiling on every wait. A stream test must never wait for ever: a hung test is harder to diagnose
    /// than a red one.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 建立一個接上假連線的使用者資料串流。
    /// Creates a user data stream wired to the fake connection.
    /// </summary>
    /// <param name="connectionFactory">假連線工廠。The fake connection factory.</param>
    /// <param name="streamOptions">串流設定,未提供時採用預設值。The stream settings, or the defaults.</param>
    /// <param name="listenKey">假憑證。The fake credential.</param>
    /// <param name="keepAliveResponder">
    /// <c>PUT</c> 的回應覆寫,未提供時回傳與 <c>POST</c> 同形的成功回應。
    /// An override for the <c>PUT</c> response; without one it answers in the same shape as the <c>POST</c>.
    /// </param>
    /// <param name="masker">
    /// 遮罩器,取得的憑證會登記上去。未提供時走沒有遮罩器的建構式多載。
    /// The masker onto which every credential obtained is registered; without one the overload that takes no
    /// masker is used.
    /// </param>
    /// <param name="loggerFactory">
    /// 日誌工廠,轉交給連線層。
    /// The logger factory, handed to the connection layer.
    /// </param>
    /// <returns>串流、假 HTTP 處理器與底層的 <see cref="HttpClient"/>。The stream, the stub, and the client.</returns>
    public static (BinanceUserDataFeed Feed, StubHttpMessageHandler Stub, HttpClient Http) Create(
        IWebSocketConnectionFactory connectionFactory,
        BinanceUserDataStreamOptions? streamOptions = null,
        string listenKey = UserDataSamples.ListenKey,
        Func<string>? keepAliveResponder = null,
        SecretMasker? masker = null,
        ILoggerFactory? loggerFactory = null)
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();
        var stub = ListenKeyStub(listenKey, keepAliveResponder);
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        return (
            new BinanceUserDataFeed(
                pipeline,
                options,
                masker,
                streamOptions,
                loggerFactory,
                timeProvider: clock,
                connectionFactory: connectionFactory),
            stub,
            http);
    }

    /// <summary>
    /// 這一組測試共用的固定時鐘讀數。
    /// The fixed clock reading shared by these tests.
    /// </summary>
    public static DateTimeOffset FixedNow => TestClock.AtFixedInstant().GetUtcNow();

    /// <summary>
    /// 依請求方法回應憑證端點。
    /// Answers the credential endpoint by request method.
    /// </summary>
    /// <param name="listenKey">要回傳的憑證。The credential to return.</param>
    /// <param name="keepAliveResponder">
    /// <c>PUT</c> 的回應覆寫。An override for the <c>PUT</c> response.
    /// </param>
    /// <returns>假處理器。The stub handler.</returns>
    /// <remarks>
    /// <c>PUT</c> 預設回的是完整憑證,不是空物件 —— 實測就是這樣,而這個形狀正是「回應本體不可以進日誌」
    /// 那條規則的來源。測試若把它簡化成 <c>{}</c>,安全測試就會在一條實際上不存在的路徑上跑。
    /// The <c>PUT</c> returns the full credential rather than an empty object by default, because that is what
    /// was measured, and that shape is where the "the body must not reach a log" rule comes from. Simplifying
    /// it to <c>{}</c> in tests would run the security test down a path that does not exist in reality.
    /// </remarks>
    public static StubHttpMessageHandler ListenKeyStub(string listenKey, Func<string>? keepAliveResponder = null) =>
        new((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.Method == HttpMethod.Put && keepAliveResponder is not null
                    ? keepAliveResponder()
                    : UserDataSamples.ListenKeyResponse(listenKey),
                Encoding.UTF8,
                "application/json"),
        });

    /// <summary>
    /// 等到收到指定數量的元素為止。
    /// Reads until the requested number of elements has arrived.
    /// </summary>
    /// <typeparam name="T">串流的元素型別。The stream's element type.</typeparam>
    /// <param name="stream">要讀的串流。The stream to read.</param>
    /// <param name="count">要收幾則。How many to collect.</param>
    /// <returns>收到的元素。The elements collected.</returns>
    public static async Task<List<Result<T>>> TakeAsync<T>(IAsyncEnumerable<Result<T>> stream, int count)
    {
        using var cts = new CancellationTokenSource(Timeout);

        var items = new List<Result<T>>(count);

        await foreach (var item in stream.WithCancellation(cts.Token))
        {
            items.Add(item);

            if (items.Count >= count)
            {
                break;
            }
        }

        Assert.HasCount(count, items, "串流提早結束,收到的元素比預期少。");

        return items;
    }

    /// <summary>
    /// 讀到串流自己結束或逾時為止。
    /// Reads until the stream ends by itself or the timeout expires.
    /// </summary>
    /// <typeparam name="T">串流的元素型別。The stream's element type.</typeparam>
    /// <param name="stream">要讀的串流。The stream to read.</param>
    /// <param name="timeout">等待上限,未指定時用 <see cref="Timeout"/>。The wait ceiling.</param>
    /// <returns>收到的元素。The elements collected.</returns>
    public static async Task<List<Result<T>>> DrainAsync<T>(
        IAsyncEnumerable<Result<T>> stream,
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? Timeout);

        var items = new List<Result<T>>();

        await foreach (var item in stream.WithCancellation(cts.Token))
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// 輪詢等到條件成立為止,逾時就讓測試失敗。
    /// Polls until a condition holds, failing the test on timeout.
    /// </summary>
    /// <param name="condition">要等的條件。The condition to wait for.</param>
    /// <param name="because">逾時時要說的話。What to say on timeout.</param>
    /// <returns>等待的工作。The waiting task.</returns>
    /// <remarks>
    /// 背景工作(續期計時器、重建憑證)沒有可以 await 的把手,只能輪詢;但輪詢一定要有上限,
    /// 否則一個壞掉的實作會讓測試掛在那裡而不是紅燈。
    /// The background work — the renewal timer, the credential rebuild — offers no handle to await, so polling
    /// is the only option; it always carries a ceiling, or a broken implementation hangs the test instead of
    /// failing it.
    /// </remarks>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"等了 {Timeout} 條件仍未成立:{because}");
    }
}
