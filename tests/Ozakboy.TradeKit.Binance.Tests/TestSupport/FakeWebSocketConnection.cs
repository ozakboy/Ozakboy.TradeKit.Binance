using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 照劇本送出訊息的假 WebSocket 連線。
/// A fake WebSocket connection that plays a scripted list of frames.
/// </summary>
/// <remarks>
/// <c>Ozakboy.WebSockets</c> 把建立連線抽成 <see cref="IWebSocketConnectionFactory"/>,
/// 這個接縫讓整條訂閱路徑(登記訂閱 → 連線 → 重放 SUBSCRIBE → 收訊息 → 解析 → 交給消費端)
/// 可以在完全不碰網路的情況下被驗證。單元測試連網會變成「交易所維護時 CI 就紅燈」,
/// 而那種紅燈久了就沒人看了。
/// <c>Ozakboy.WebSockets</c> factors connection creation behind
/// <see cref="IWebSocketConnectionFactory"/>, and that seam lets the whole subscription path — register,
/// connect, replay the SUBSCRIBE, receive, parse, hand to the consumer — be verified without touching the
/// network. A unit test that dials out turns exchange maintenance into a red CI, and a CI that goes red for
/// reasons nobody caused is a CI nobody reads.
/// </remarks>
internal sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<FakeWebSocketFrame> _inbound = Channel.CreateUnbounded<FakeWebSocketFrame>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _sent = [];
    private readonly Lock _sentGate = new();
    private readonly bool _closeWhenScriptEnds;

    public FakeWebSocketConnection(IReadOnlyList<FakeWebSocketFrame> frames, bool closeWhenScriptEnds = false)
    {
        ArgumentNullException.ThrowIfNull(frames);

        _closeWhenScriptEnds = closeWhenScriptEnds;

        foreach (var frame in frames)
        {
            _inbound.Writer.TryWrite(frame);
        }
    }

    /// <summary>
    /// 把一則訊息接到劇本後面,連線建立之後也可以。
    /// Appends one frame to the script, including after the connection is live.
    /// </summary>
    /// <param name="frame">要送出的訊息。The frame to deliver.</param>
    /// <remarks>
    /// <b>要測「兩個訂閱者都收到同一則事件」就非用它不可。</b> 建構式排好的訊息在連線一建立就送得出去,
    /// 而串流是由<b>第一次</b> <c>MoveNextAsync</c> 啟動的 —— 那一次同步跑完建立憑證、握手與啟動讀取迴圈,
    /// 所以它回來的時候,第一則事件可能已經分送完畢,第二個訂閱者卻還沒登記。
    /// 劇本先留空、兩個訂閱者都登記完再 <see cref="Enqueue"/>,那個競態就不存在。
    /// <b>Testing that two subscribers both receive one event needs this.</b> Frames queued in the constructor
    /// can go out the moment the connection is live, and the feed is started by the <b>first</b>
    /// <c>MoveNextAsync</c> — which synchronously creates the credential, completes the handshake, and starts the
    /// read loop, so by the time it returns the first event may already have been dispatched while the second
    /// subscriber has yet to register. Leaving the script empty and enqueueing once both are registered removes
    /// the race entirely.
    /// </remarks>
    public void Enqueue(FakeWebSocketFrame frame) => _inbound.Writer.TryWrite(frame);

    /// <summary>
    /// 這條連線收到過哪些送出的內容,依序排列。
    /// What was sent on this connection, in order.
    /// </summary>
    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_sentGate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>
    /// 握手是否被呼叫過。
    /// Whether the handshake was attempted.
    /// </summary>
    public bool Connected { get; private set; }

    /// <summary>
    /// 握手時撥的是哪個位址;還沒撥過時為 <see langword="null"/>。
    /// The address the handshake dialled, or <see langword="null"/> before any attempt.
    /// </summary>
    /// <remarks>
    /// 路由錯了不會有任何錯誤,只會握手成功、零資料 —— 所以撥號位址本身就是要斷言的東西。
    /// A wrong route raises no error, only a successful handshake followed by silence, so the dialled address is
    /// itself something to assert on.
    /// </remarks>
    public Uri? ConnectedUri { get; private set; }

    /// <inheritdoc />
    public WebSocketState State { get; private set; } = WebSocketState.None;

    /// <inheritdoc />
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectedUri = uri;
        Connected = true;
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        lock (_sentGate)
        {
            _sent.Add(Encoding.UTF8.GetString(buffer.Span));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_inbound.Reader.TryRead(out var frame))
            {
                var bytes = Encoding.UTF8.GetBytes(frame.Payload);

                bytes.CopyTo(buffer.Span);

                return new ValueWebSocketReceiveResult(bytes.Length, frame.Type, endOfMessage: true);
            }

            if (_closeWhenScriptEnds)
            {
                State = WebSocketState.Closed;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }

            // 劇本演完了就靜靜等著,直到測試取消或關閉連線。真實連線在沒有成交時也是這個樣子,
            // 這裡若自作主張回傳關閉,測到的就會是「每收完一批就重連」的假行為。
            // Once the script runs out the connection simply waits until the test cancels or closes it, which
            // is what a real connection does between trades. Returning a close here instead would have the
            // tests exercising a reconnect after every batch, which is not what happens.
            var waitToRead = _inbound.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(waitToRead, _closed.Task).ConfigureAwait(false);

            if (completed == _closed.Task || !await waitToRead.ConfigureAwait(false))
            {
                State = WebSocketState.Closed;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }
        }
    }

    /// <inheritdoc />
    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        State = WebSocketState.CloseSent;
        _closed.TrySetResult();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Abort()
    {
        State = WebSocketState.Aborted;
        _closed.TrySetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        State = WebSocketState.Closed;
        _closed.TrySetResult();
        _inbound.Writer.TryComplete();
    }
}
