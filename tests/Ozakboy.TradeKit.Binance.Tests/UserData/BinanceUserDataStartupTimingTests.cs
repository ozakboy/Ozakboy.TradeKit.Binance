using System.Net.WebSockets;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// <see cref="BinanceUserDataFeed.StartAsync"/> 必須等連線<b>真的</b>就緒才回傳。
/// <see cref="BinanceUserDataFeed.StartAsync"/> must not return until the connection is genuinely live.
/// </summary>
/// <remarks>
/// <para>
/// 這條測試釘住的是一個實際發生過、而且症狀完全指向別處的缺陷:啟動若只是把連線工作丟到背景
/// (<c>WebSocketClient.Start</c>)而不等握手完成(<c>ConnectAsync</c>),呼叫端會在「訂閱回來了但連線
/// 還沒好」的那幾百毫秒裡開始下單,而交易所在那段期間送出的事件<b>不補送</b>。
/// </para>
/// <para>
/// 它看起來像交易所故障:委託以 REST 查得到、確實掛在簿上,串流卻兩分鐘一則事件都沒有。當時的第一個
/// 結論是「幣安 Testnet 推送停擺」,直到用同一支探針只改「等不等連線就緒」這一點,才證明推送一直
/// 都是好的 —— 等就緒再下單,事件立刻到;不等,三十秒零訊息。
/// This pins a defect that really happened and whose symptoms pointed everywhere but here: if starting merely
/// hands the connection to the background instead of awaiting the handshake, the caller begins placing orders
/// during the few hundred milliseconds of "subscribed but not connected", and the exchange does not replay what
/// it sent then. It presents as an exchange outage — REST confirms the order resting on the book while the
/// stream stays silent — and was only settled by a probe that differed in this one wait.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceUserDataStartupTimingTests
{
    [TestMethod]
    public async Task StartAsyncDoesNotReturnUntilTheHandshakeCompletes()
    {
        using var handshake = new SemaphoreSlim(0, 1);
        var factory = new GatedConnectionFactory(handshake);

        var (feed, _, http) = UserDataFixture.Create(factory);

        // http 在外、feed 在內:處置是反序的,feed 收尾時要送 DELETE 把憑證註銷,
        // 那一步需要 HttpClient 還活著。順序寫反會以 ObjectDisposedException 收場。
        using (http)
        {
            await using (feed)
            {
                var starting = feed.StartAsync(CancellationToken.None);

                // 握手還沒放行,啟動就不可以宣告完成。給它一段遠超過排程抖動的時間 ——
                // 若實作改回「背景連線」,這裡會在毫秒內就完成,這個等待只是讓失敗確定而不是碰運氣。
                var settledEarly = await Task.WhenAny(starting, Task.Delay(TimeSpan.FromSeconds(2)));

                Assert.AreNotSame(
                    starting,
                    settledEarly,
                    "StartAsync 在握手完成前就回傳了 —— 呼叫端會在連線還沒好的時候開始動作,而那段期間的事件交易所不補送。");

                handshake.Release();

                var started = await starting.WaitAsync(UserDataFixture.Timeout);

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);
                Assert.IsNotEmpty(factory.Created, "啟動成功卻沒有建立過任何連線。");
            }
        }
    }

    /// <summary>握手會停在閘門上的連線工廠。The factory whose connections block on a gate during the handshake.</summary>
    private sealed class GatedConnectionFactory(SemaphoreSlim gate) : IWebSocketConnectionFactory
    {
        private readonly List<GatedConnection> _created = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<GatedConnection> Created
        {
            get
            {
                lock (_lock)
                {
                    return [.. _created];
                }
            }
        }

        public IWebSocketConnection Create()
        {
            GatedConnection connection = new(gate);

            lock (_lock)
            {
                _created.Add(connection);
            }

            return connection;
        }
    }

    /// <summary>
    /// 只在握手處加一道閘門,其餘行為委派給既有的假連線 —— 不重造一套替身。
    /// A gate on the handshake only; everything else delegates to the existing fake, so no second test double
    /// is introduced.
    /// </summary>
    private sealed class GatedConnection(SemaphoreSlim gate) : IWebSocketConnection
    {
        private readonly FakeWebSocketConnection _inner = new([]);

        public WebSocketState State => _inner.State;

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _inner.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken) =>
            _inner.ReceiveAsync(buffer, cancellationToken);

        public Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public void Abort() => _inner.Abort();

        public void Dispose() => _inner.Dispose();
    }
}
