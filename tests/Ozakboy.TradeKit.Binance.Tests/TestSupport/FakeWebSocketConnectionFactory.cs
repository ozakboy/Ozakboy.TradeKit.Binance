using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 依序交出預先排好的假連線,讓重連也能被測。
/// Hands out pre-arranged fake connections in order, so reconnection can be tested too.
/// </summary>
/// <remarks>
/// 每一次連線都有自己的劇本,重連拿到的是<b>下一份</b>劇本。這樣才驗得出「重連之後訂閱有沒有被重放」
/// —— 少了重放,連線會是活的、狀態會顯示已連線、沒有任何錯誤,資料卻永遠不會再進來。
/// Each connection gets its own script and a reconnect gets the <b>next</b> one, which is what makes it
/// possible to check that the subscription was replayed after reconnecting. Without the replay the connection
/// is alive, the status reads connected, nothing errors, and data never arrives again.
/// </remarks>
internal sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    private readonly Queue<FakeWebSocketConnection> _pending;
    private readonly List<FakeWebSocketConnection> _created = [];
    private readonly Lock _gate = new();

    public FakeWebSocketConnectionFactory(params FakeWebSocketConnection[] connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _pending = new Queue<FakeWebSocketConnection>(connections);
    }

    /// <summary>
    /// 已經交出去的連線,依序排列。
    /// The connections handed out so far, in order.
    /// </summary>
    public IReadOnlyList<FakeWebSocketConnection> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    /// <inheritdoc />
    public IWebSocketConnection Create()
    {
        lock (_gate)
        {
            // 劇本用完之後一律給一條「連得上但永遠不說話」的連線。回傳 null 或擲例外都會讓重連迴圈
            // 走到別的分支,測到的就不是原本要測的那條路。
            // Once the scripts run out, every further attempt gets a connection that dials fine and never says
            // anything. Returning null or throwing would send the reconnect loop down a different branch and
            // the test would stop exercising the path it was written for.
            var connection = _pending.Count > 0 ? _pending.Dequeue() : new FakeWebSocketConnection([]);

            _created.Add(connection);

            return connection;
        }
    }
}
