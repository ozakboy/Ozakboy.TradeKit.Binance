using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Ozakboy.Http;
using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.UserData;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約的帳戶私有資料來源:委託狀態、成交、帳戶增量、保證金追繳與對帳訊號。
/// The Binance USDⓈ-M private account data source: order state, fills, account deltas, margin calls, and
/// reconciliation signals.
/// </summary>
/// <remarks>
/// <para>
/// <b>一條連線,內部分流。</b> 五個 <c>Subscribe</c> 方法共用同一條 WebSocket 與同一把串流憑證,
/// 背景讀取迴圈解析事件之後分送給登記中的訂閱者。每一次訂閱各開一條連線在這裡是行不通的:
/// 帳戶同時只有一把 listenKey,第二條連線只是把同一份事件再收一次,徒然多一份限流與一份憑證管理。
/// <b>One connection, fanned out inside.</b> The five <c>Subscribe</c> methods share a single WebSocket and a
/// single stream credential; a background read loop parses each event and hands it to the registered
/// subscribers. Dialling once per subscription is not an option here: an account holds one listenKey at a time,
/// so a second connection merely receives the same events again at the cost of more quota and another
/// credential to keep alive.
/// </para>
/// <para>
/// <b>每個訂閱者一份佇列,不是每種事件一份。</b> 同一種事件被訂閱兩次,兩邊都會收到完整的一份。
/// 共用一份佇列會讓兩個消費端瓜分事件,而那種錯誤的症狀是「兩邊都只看到一半,但都不覺得自己漏了」。
/// <b>One queue per subscriber, not one per event type.</b> Subscribing twice to the same kind of event gives
/// each subscription the complete set. A shared queue would split the events between the two consumers, and the
/// symptom of that is both sides seeing half of them and neither noticing anything is missing.
/// </para>
/// <para>
/// <b>訂閱之前發生的事件不補送。</b> 這是 <see cref="IUserDataFeed"/> 明訂的語意,這裡不加重播緩衝。
/// 要補回那一段只有一個辦法:全量對帳 —— 而那本來就是啟動時必做的事。
/// <b>Nothing from before a subscription is replayed.</b> That is the documented semantics of
/// <see cref="IUserDataFeed"/> and no replay buffer is added here. Recovering that interval has exactly one
/// route, a full reconciliation, which start-up owes anyway.
/// </para>
/// <para>
/// <b>訂單事件永不靜默丟棄。</b> 訂閱者的佇列是有界的,但塞滿時不是丟棄:那一條訂閱會以一筆
/// <see cref="Error.IsTransient"/> 為 <see langword="false"/> 的失敗結束,訊息說明已漏事件、
/// 請重新訂閱並全量對帳。靜默丟掉一筆成交回報,本地部位就會與交易所安靜分岔,而帳面數字依然合理。
/// <b>Order events are never dropped in silence.</b> A subscriber's queue is bounded, but filling it does not
/// discard anything: that subscription ends with a failure whose <see cref="Error.IsTransient"/> is
/// <see langword="false"/>, saying that events were lost and that the consumer must resubscribe and reconcile
/// in full. Quietly dropping a fill leaves local positions diverging from the exchange's with the books still
/// looking plausible.
/// </para>
/// <para>
/// <b>延遲啟動、集中收尾。</b> 第一次訂閱時才建立憑證並連線;單一訂閱結束<b>不</b>關連線,因為可能還有
/// 其他訂閱者,而且重建憑證要一次往返。整條串流由 <see cref="DisposeAsync"/> 收尾:關閉憑證(<c>DELETE</c>)
/// 並關閉連線。
/// <b>Started lazily, closed centrally.</b> The credential is created and the socket dialled on the first
/// subscription. Ending one subscription does <b>not</b> close the connection: others may still be reading, and
/// rebuilding the credential costs a round trip. <see cref="DisposeAsync"/> closes the whole thing, deleting
/// the credential and shutting the socket down.
/// </para>
/// <para>
/// <b>串流憑證是祕密。</b> listenKey 能連上這個帳戶的私有資料,因此它不會出現在任何錯誤訊息、
/// <see cref="Error.Data"/>、例外訊息或串流識別字裡。診斷資料用的是固定字面值
/// <c>userDataStream</c>,不是位址;連 <c>listenKeyExpired</c> 事件的原文都不會被轉述,
/// 因為那則訊息本體就帶著憑證。
/// <b>The stream credential is a secret.</b> A listenKey reaches this account's private data, so it appears in
/// no error message, no <see cref="Error.Data"/>, no exception text, and no stream identifier. Diagnostics use
/// the fixed literal <c>userDataStream</c> rather than the address, and not even the text of a
/// <c>listenKeyExpired</c> frame is relayed, because that frame carries the credential itself.
/// </para>
/// </remarks>
public sealed class BinanceUserDataFeed : IUserDataFeed, IAsyncDisposable
{
    private readonly BinanceApiClient _api;
    private readonly BinanceEndpoints _endpoints;
    private readonly BinanceListenKeyClient _listenKeys;
    private readonly BinanceUserDataStreamOptions _streamOptions;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IWebSocketConnectionFactory? _connectionFactory;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Lock _subscriberGate = new();
    private readonly List<IUserDataSubscriber> _subscribers = [];
    private readonly CancellationTokenSource _lifetime = new();

    private WebSocketClient? _session;
    private Task? _pump;
    private Task? _keepAlive;
    private bool _started;
    private bool _credentialCreated;
    private int _disposed;
    private Error? _stopReason;

    /// <summary>
    /// 建立使用者資料來源。
    /// Creates the user data source.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="streamOptions">
    /// 串流設定,未提供時採用預設值。
    /// The stream settings; the defaults are used when none is supplied.
    /// </param>
    /// <param name="loggerFactory">
    /// 日誌工廠,轉交給連線層。
    /// The logger factory, handed to the connection layer.
    /// </param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <param name="connectionFactory">
    /// WebSocket 連線工廠。未提供時由 <c>Ozakboy.WebSockets</c> 建立真正的連線;
    /// 單元測試傳入假工廠,整條訂閱流程就能在完全不碰網路的情況下被驗證。
    /// The WebSocket connection factory. When none is supplied <c>Ozakboy.WebSockets</c> builds a real
    /// connection; a unit test passes a fake one and exercises the whole path without touching the network.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="http"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="http"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> 或 <paramref name="streamOptions"/> 不合法時擲出。
    /// Thrown when <paramref name="options"/> or <paramref name="streamOptions"/> is invalid.
    /// </exception>
    public BinanceUserDataFeed(
        HttpPipelineClient http,
        BinanceOptions options,
        BinanceUserDataStreamOptions? streamOptions = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        IWebSocketConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _streamOptions = streamOptions ?? new BinanceUserDataStreamOptions();

        var streamValidation = _streamOptions.Validate();

        if (streamValidation.IsFailure)
        {
            throw new ArgumentException(streamValidation.Error!.Message, nameof(streamOptions));
        }

        _api = new BinanceApiClient(http, options, timeProvider);
        _endpoints = _api.Endpoints;
        _listenKeys = new BinanceListenKeyClient(_api);
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// 一則事件會被送到哪一條串流。
    /// Which stream an event goes to.
    /// </summary>
    private enum UserDataChannel
    {
        Orders,
        Trades,
        AccountUpdates,
        MarginCalls,
        ResyncSignals,
    }

    /// <summary>
    /// 一次讀取工作階段是怎麼結束的。
    /// How one read session ended.
    /// </summary>
    private enum SessionOutcome
    {
        /// <summary>
        /// 憑證失效,必須重建憑證與連線。
        /// The credential lapsed and both it and the connection must be rebuilt.
        /// </summary>
        CredentialExpired,

        /// <summary>
        /// 串流結束,不會再有事件。
        /// The stream ended and will deliver nothing further.
        /// </summary>
        Ended,
    }

    /// <summary>
    /// 這個串流連的是哪一組端點。
    /// Which endpoint set this stream talks to.
    /// </summary>
    public BinanceEndpoints Endpoints => _endpoints;

    /// <summary>
    /// 這個串流採用的設定。
    /// The settings in force.
    /// </summary>
    public BinanceUserDataStreamOptions StreamOptions => _streamOptions;

    /// <inheritdoc />
    public IAsyncEnumerable<Result<Order>> SubscribeOrderUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync<Order>(UserDataChannel.Orders, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// 一則 <c>ORDER_TRADE_UPDATE</c> 若含成交,會同時餵委託串流與這一條:委託那邊拿到的是狀態,
    /// 這邊拿到的是那一筆成交的量價與手續費。兩者是同一件事的兩個面向,不是重複。
    /// An <c>ORDER_TRADE_UPDATE</c> that carries a fill feeds both the order stream and this one: the order
    /// stream receives the state and this one receives that fill's size, price, and fee. They are two views of
    /// one event rather than a duplicate.
    /// </remarks>
    public IAsyncEnumerable<Result<Trade>> SubscribeTradeUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync<Trade>(UserDataChannel.Trades, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Result<AccountUpdate>> SubscribeAccountUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync<AccountUpdate>(UserDataChannel.AccountUpdates, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Result<MarginCall>> SubscribeMarginCallsAsync(
        CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync<MarginCall>(UserDataChannel.MarginCalls, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// 這條串流上會出現兩種訊號。<see cref="ResyncReason.StreamCredentialExpired"/> 代表串流憑證過期,
    /// 憑證與連線都已經重建;<see cref="ResyncReason.Reconnected"/> 代表斷線之後重新連上。
    /// 兩者的後果一模一樣 —— 中斷期間的委託與成交交易所不補送 —— 所以兩者都要做全量對帳。
    /// Two signals appear here. <see cref="ResyncReason.StreamCredentialExpired"/> means the credential lapsed
    /// and both it and the connection have been rebuilt; <see cref="ResyncReason.Reconnected"/> means the
    /// stream came back after a drop. The consequence is identical — the exchange replays nothing from the
    /// interruption — so both call for a full reconciliation.
    /// </remarks>
    public IAsyncEnumerable<Result<ResyncRequired>> SubscribeResyncSignalsAsync(
        CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync<ResyncRequired>(UserDataChannel.ResyncSignals, cancellationToken);

    /// <summary>
    /// 關閉串流憑證與連線,並結束所有還開著的訂閱。
    /// Closes the stream credential and the connection, and ends every subscription still open.
    /// </summary>
    /// <returns>完成關閉的工作。The task that completes once everything is shut down.</returns>
    /// <remarks>
    /// 憑證一定要 <c>DELETE</c>。不刪的話它還會活滿 60 分鐘,期間交易所仍然認得那把鑰匙 ——
    /// 一把沒人用、也沒人管的私有資料憑證,存在本身就是風險。
    /// The credential is always deleted. Left alone it stays valid for its remaining 60 minutes and the exchange
    /// keeps honouring it: a private-data credential that nobody is using and nobody is watching is a risk by
    /// its own existence.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // 這道閘不能用 _startGate 來守。容器把同一個單例登記在兩個服務描述之下(具體型別與
        // IUserDataFeed),收尾時就會呼叫兩次 DisposeAsync —— 第二次拿到的是一個已經被釋放的號誌,
        // 換來的是一個 ObjectDisposedException,而那是在應用程式關機的路徑上。
        // This guard cannot use _startGate. A container registers the same singleton under two service
        // descriptors — the concrete type and IUserDataFeed — and calls DisposeAsync twice on shutdown; the
        // second call would reach an already-disposed semaphore and throw ObjectDisposedException, on the
        // application's shutdown path of all places.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        await AwaitQuietlyAsync(_pump).ConfigureAwait(false);
        await AwaitQuietlyAsync(_keepAlive).ConfigureAwait(false);

        var session = Volatile.Read(ref _session);

        Volatile.Write(ref _session, null);

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (_credentialCreated)
        {
            // 刻意用 CancellationToken.None:這個請求正是「收尾」本身,拿一個已經取消的權杖去送它,
            // 等於永遠不刪。
            // Deliberately CancellationToken.None: this request is the cleanup, and sending it with an
            // already-cancelled token means never deleting anything.
            _ = await _listenKeys.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Stop(BinanceUserDataErrors.Disposed(_endpoints));
        CompleteAll();

        _lifetime.Dispose();
        _startGate.Dispose();
    }

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收尾時的取消是預期內的,不是錯誤。
            // A cancellation during shutdown is expected rather than an error.
        }
    }

    private async IAsyncEnumerable<Result<T>> SubscribeCoreAsync<T>(
        UserDataChannel channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        var subscriber = new Subscriber<T>(channel, _streamOptions.SubscriberQueueCapacity, _endpoints);

        // 先登記再啟動。順序是重點:啟動期間就可能有事件進來,晚一步登記那幾則就只會被丟進一條
        // 還沒有人在聽的分流裡,而「訂閱成功之後的第一則事件不見了」在事後完全查不出來。
        // Registration comes before start-up, and the order matters: events can arrive while the connection is
        // coming up, and registering afterwards drops them into a channel nobody is listening on yet — "the
        // first event after subscribing went missing" being something nobody can reconstruct later.
        Register(subscriber);

        try
        {
            var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            if (started.IsFailure)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    yield return Result.Failure<T>(started.Error!);
                }

                yield break;
            }

            await foreach (var item in subscriber.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            Deregister(subscriber);
        }
    }

    /// <summary>
    /// 建立憑證並連上串流,<b>等連線真的就緒才回傳</b>。已經啟動時直接回成功。
    /// Creates the credential and connects the stream, <b>returning only once the connection is live</b>; a
    /// call on an already-started feed succeeds immediately.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>啟動結果。The outcome of starting.</returns>
    /// <remarks>
    /// <para>
    /// 訂閱本身會自己啟動,所以平常不必呼叫這個方法。<b>但只要接下來要做的事情自己會產生事件 ——
    /// 下單、撤單、改槓桿 —— 就必須先等這個方法回來。</b>訂閱之前發生的事件不補送,而
    /// 「訂閱的列舉已經開始、連線卻還在握手」正是最容易漏掉第一則事件的那個縫。
    /// Subscribing starts the feed on its own, so this is not needed in the ordinary case. <b>It becomes
    /// necessary the moment the next thing you do produces events of its own</b> — placing an order,
    /// cancelling one, changing leverage. Nothing from before a subscription is replayed, and "enumeration has
    /// begun but the socket is still shaking hands" is exactly the gap where the first event goes missing.
    /// </para>
    /// <para>
    /// 正確的順序是<b>先訂閱、再等這個方法、最後才動作</b>:訂閱者在列舉開始的同一瞬間就登記完成,
    /// 所以連線就緒之後的每一則事件都有人接。反過來先啟動再訂閱,中間那一小段沒有人在聽。
    /// The order is <b>subscribe, await this, then act</b>: a subscriber registers in the same synchronous step
    /// that begins the enumeration, so every event after the connection comes up has somewhere to land.
    /// Starting first and subscribing afterwards leaves a short stretch with nobody listening.
    /// </para>
    /// <code>
    /// var orders = feed.SubscribeOrderUpdatesAsync(token).GetAsyncEnumerator(token);
    /// var first = orders.MoveNextAsync();          // 登記訂閱者(同步完成)
    /// var ready = await feed.StartAsync(token);    // 等連線就緒
    /// // 從這裡開始下單,事件不會漏
    /// </code>
    /// </remarks>
    public Task<Result> StartAsync(CancellationToken cancellationToken = default) =>
        EnsureStartedAsync(cancellationToken);

    private async Task<Result> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopReason) is { } alreadyStopped)
        {
            return Result.Failure(alreadyStopped);
        }

        if (Volatile.Read(ref _started))
        {
            return Result.Success();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

        try
        {
            await _startGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(BinanceUserDataErrors.StartupCancelled(_endpoints));
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return Result.Failure(BinanceUserDataErrors.Disposed(_endpoints));
            }

            if (Volatile.Read(ref _stopReason) is { } stopped)
            {
                return Result.Failure(stopped);
            }

            if (_started)
            {
                return Result.Success();
            }

            var session = await OpenSessionAsync(linked.Token).ConfigureAwait(false);

            if (!session.TryGetValue(out var opened))
            {
                return Result.Failure(session.Error!);
            }

            Volatile.Write(ref _session, opened);
            Volatile.Write(ref _started, true);

            _pump = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
            _keepAlive = Task.Run(() => KeepAliveAsync(_lifetime.Token), CancellationToken.None);

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(BinanceUserDataErrors.StartupCancelled(_endpoints));
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<Result<WebSocketClient>> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var created = await _listenKeys.CreateAsync(cancellationToken).ConfigureAwait(false);

        if (!created.TryGetValue(out var listenKey))
        {
            return created.ToFailure<WebSocketClient>();
        }

        _credentialCreated = true;

        // 位址含憑證,所以這個 Uri 本身也是祕密。Ozakboy.WebSockets 0.2.1 只把 authority 寫進日誌,
        // 而這一層產生的每一筆錯誤都由 BinanceUserDataErrors 負責,不會帶上位址。
        // The address embeds the credential, so the Uri is a secret too. Ozakboy.WebSockets 0.2.1 logs only the
        // authority, and every error this layer produces goes through BinanceUserDataErrors, which never
        // carries the address.
        var uri = BinanceStreamNames.RawStreamUri(_endpoints.WebSocketBaseUri, listenKey);
        var options = _streamOptions.CreateWebSocketOptions(uri);
        var validation = options.Validate();

        if (validation.IsFailure)
        {
            // 設定不合法就連不起來,那把剛建立的憑證留著也沒有用途 —— 一把沒人管的私有資料憑證
            // 會在交易所那邊活滿 60 分鐘。
            // Invalid options mean no connection, and the credential just created has no purpose — an
            // unattended private-data credential would stay valid at the exchange for its full 60 minutes.
            await DeleteCredentialQuietlyAsync().ConfigureAwait(false);

            return BinanceUserDataErrors.FromWebSocketError(validation.Error!, _endpoints);
        }

        // 啟動失敗時一定要把連線關掉。留著不關,那個 WebSocketClient 自己的重連迴圈還會在背景跑,
        // 而沒有人在讀它送出來的訊息 —— 一條沒人聽的連線會一直重連下去,直到程序結束。
        // A failed start must close the connection. Left alone, that WebSocketClient keeps its own reconnect
        // loop running in the background with nobody reading what it delivers, and an unheard connection
        // reconnects for as long as the process lives.
        var client = CreateClient(options);

        // ConnectAsync 而不是 Start:前者等握手完成才回來,後者只是把連線工作丟到背景。
        // 差別看起來只是幾百毫秒,實際後果是「訂閱回來了但連線還沒好」—— 這段期間交易所送出的
        // 事件沒有任何一條連線收得到,而它<b>不補送</b>。實測(Testnet,同一支探針只差這一點):
        // 等連線就緒再下單,ORDER_TRADE_UPDATE 立刻到;不等就下單,三十秒內一則都沒有,
        // 而委託以 REST 查得到、確實掛在簿上。那種漏失事後完全查不出來。
        // ConnectAsync rather than Start: the former returns once the handshake is done, the latter merely
        // hands the connection off to the background. The difference looks like a few hundred milliseconds and
        // actually means "subscribed but not yet connected" — a window in which no connection receives what the
        // exchange sends, and it <b>does not replay</b>. Measured on the testnet with one probe differing in
        // this alone: waiting for the socket before placing an order delivers ORDER_TRADE_UPDATE at once, and
        // not waiting delivers nothing in thirty seconds while REST confirms the order resting on the book.
        var started = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (started.IsFailure)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await DeleteCredentialQuietlyAsync().ConfigureAwait(false);

            return BinanceUserDataErrors.FromWebSocketError(started.Error!, _endpoints);
        }

        return client;
    }

    /// <summary>
    /// 建立一次讀取工作階段的連線。
    /// Creates the connection of one read session.
    /// </summary>
    /// <param name="options">連線層的設定。The connection-layer settings.</param>
    /// <returns>尚未啟動的連線。The connection, not yet started.</returns>
    /// <remarks>
    /// 沒有注入工廠時就用 <c>Ozakboy.WebSockets</c> 自己的預設工廠,而不是走另一個建構式多載。
    /// 兩條路長得很像,但只有其中一條會被單元測試走到,另一條就成了永遠沒人驗過的程式碼。
    /// With no injected factory, the package's own default is used rather than a second constructor overload.
    /// The two paths look alike, but only one of them is ever exercised by a unit test and the other becomes
    /// code nobody has run.
    /// </remarks>
    private WebSocketClient CreateClient(WebSocketClientOptions options) =>
        new(
            options,
            _connectionFactory ?? new ClientWebSocketConnectionFactory(options),
            _loggerFactory?.CreateLogger<WebSocketClient>(),
            _timeProvider);

    private async Task DeleteCredentialQuietlyAsync()
    {
        _ = await _listenKeys.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        _credentialCreated = false;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = Volatile.Read(ref _session);

                if (client is null)
                {
                    break;
                }

                var outcome = await ReadSessionAsync(client, cancellationToken).ConfigureAwait(false);

                // 讀取迴圈結束時關一次,DisposeAsync 也會關一次 —— 迴圈若是被取消打斷的,它那一次就不會發生。
                // WebSocketClient.DisposeAsync 重複呼叫是安全的,兩邊都關比去推敲「這次該由誰關」可靠。
                // The read loop closes it when it finishes and DisposeAsync closes it too, because a loop torn
                // out by a cancellation never reaches its own close. WebSocketClient.DisposeAsync is safe to
                // call twice, and closing from both is more reliable than reasoning about whose turn it was.
                await client.DisposeAsync().ConfigureAwait(false);

                if (outcome == SessionOutcome.Ended || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // 憑證過期只送訊號就停擺,等於把「帳戶事件從此消失」變成一件沒有人負責的事。
                // 重建憑證、重新連線,讓串流自己接回來。
                // Raising the signal and stopping would leave "account events have vanished" as nobody's
                // problem. The credential is rebuilt and the socket redialled so the stream restores itself.
                var rebuilt = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);

                if (!rebuilt.TryGetValue(out var next))
                {
                    Broadcast(rebuilt.Error!);
                    Stop(rebuilt.Error!);
                    break;
                }

                Volatile.Write(ref _session, next);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消來自 DisposeAsync,是正常收尾。
            // The cancellation comes from DisposeAsync and is a normal shutdown.
        }
        finally
        {
            Stop(BinanceUserDataErrors.StreamEnded(_endpoints));
            CompleteAll();
        }
    }

    private async Task<SessionOutcome> ReadSessionAsync(
        WebSocketClient client,
        CancellationToken cancellationToken)
    {
        var reportedDrops = 0L;
        DateTimeOffset? untrustedSince = null;

        await foreach (var message in client.Messages(cancellationToken).ConfigureAwait(false))
        {
            var dropped = client.Statistics.MessagesDropped;

            if (dropped > reportedDrops)
            {
                Broadcast(BinanceUserDataErrors.MessagesDropped(dropped - reportedDrops, dropped, _endpoints));
                reportedDrops = dropped;
            }

            if (!message.TryGetValue(out var frame))
            {
                // Error 在失敗結果上必定有值,可空性標註表達不了這個前提。
                // A failed result always carries an Error; the nullability annotation cannot say so.
                var error = BinanceUserDataErrors.FromWebSocketError(message.Error!, _endpoints);

                Broadcast(error);

                if (!error.IsTransient)
                {
                    Stop(error);
                    return SessionOutcome.Ended;
                }

                // 第一次斷線的時刻才是「本地狀態從哪一刻起不可信」。重連期間可能連續出現好幾筆失敗,
                // 每一筆都覆寫的話,對帳的起點會被推到最後一次嘗試,中間那一段就漏掉了。
                // The moment of the first drop is when local state stopped being trustworthy. Several failures
                // can appear while reconnecting, and overwriting on each one would push the reconciliation
                // start to the last attempt and skip everything before it.
                untrustedSince ??= _timeProvider.GetUtcNow();

                continue;
            }

            if (untrustedSince is { } since)
            {
                // 收到訊息就代表連線回來了。斷線期間發生的委託與成交交易所不補送,所以這裡要送訊號。
                // A message arriving means the connection is back. The exchange replays nothing from the
                // interval, which is exactly what this signal is for.
                PublishResync(ResyncReason.Reconnected, since, ReconnectedDetail(since));
                untrustedSince = null;
            }

            if (frame.Kind != WebSocketMessageKind.Text || frame.Text is null)
            {
                Broadcast(BinanceUserDataErrors.MalformedEvent(
                    $"{BinanceUserDataPaths.Context} 收到非文字訊息,幣安的帳戶事件一律是文字。A non-text frame arrived on the {BinanceUserDataPaths.Context}; Binance account events are always text.",
                    _endpoints));

                continue;
            }

            var read = BinanceUserDataReader.Read(frame.Text);

            if (!read.TryGetValue(out var evt))
            {
                // 解析失敗一定要浮上串流。吞掉的話,幣安哪天改了欄位名,症狀會是「委託狀態慢慢對不上」
                // 而不是任何錯誤,而且從哪一天開始、漏了多少,事後查不出來。
                // A parse failure must surface. Swallowing it means that the day Binance renames a field the
                // symptom is order state slowly drifting out of agreement rather than any error at all, with
                // no way to tell afterwards since when, or how much.
                Broadcast(BinanceUserDataErrors.Decorate(read.Error!, _endpoints));

                continue;
            }

            if (Dispatch(evt))
            {
                return SessionOutcome.CredentialExpired;
            }
        }

        return SessionOutcome.Ended;
    }

    /// <summary>
    /// 把一則已解析的事件分送給訂閱者。
    /// Hands one parsed event to the subscribers.
    /// </summary>
    /// <param name="evt">已解析的事件。The parsed event.</param>
    /// <returns>
    /// 這則事件代表憑證已失效、必須重建時為 <see langword="true"/>。
    /// <see langword="true"/> when the event means the credential lapsed and everything must be rebuilt.
    /// </returns>
    private bool Dispatch(BinanceUserDataEvent evt)
    {
        switch (evt.Kind)
        {
            case BinanceUserDataEventKind.OrderTradeUpdate:
                if (evt.OrderUpdate is { } order)
                {
                    Publish(UserDataChannel.Orders, order);
                }

                // 含成交的事件同時餵兩條串流:委託那邊是狀態,成交那邊是那一筆的量價與手續費。
                // An event carrying a fill feeds both streams: the state on one, that fill's size, price, and
                // fee on the other.
                if (evt.Fill is { } fill)
                {
                    Publish(UserDataChannel.Trades, fill);
                }

                return false;

            case BinanceUserDataEventKind.AccountUpdate:
                if (evt.Account is { } account)
                {
                    Publish(UserDataChannel.AccountUpdates, account);
                }

                return false;

            case BinanceUserDataEventKind.MarginCall:
                if (evt.MarginWarning is { } marginCall)
                {
                    Publish(UserDataChannel.MarginCalls, marginCall);
                }

                return false;

            case BinanceUserDataEventKind.ListenKeyExpired:
                // UntrustedSince 用事件時間而不是「現在」:憑證是在那一刻失效的,而這則訊號可能晚幾毫秒
                // 才送出去。兩個時刻在型別上是分開的欄位,正是因為它們不同。
                // UntrustedSince takes the event time rather than "now": the credential lapsed at that moment
                // and the signal goes out a few milliseconds later. The type keeps the two apart precisely
                // because they differ.
                PublishResync(
                    ResyncReason.StreamCredentialExpired,
                    evt.EventTime,
                    "串流憑證已失效,已自動重建憑證與連線;失效期間的委託與成交不會補送。The stream credential expired; it and the connection have been rebuilt automatically. Nothing from the gap is replayed.");

                return true;

            case BinanceUserDataEventKind.Ignored:
            default:
                return false;
        }
    }

    private async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task
                    .Delay(_streamOptions.ListenKeyKeepAliveInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);

                var kept = await _listenKeys.KeepAliveAsync(cancellationToken).ConfigureAwait(false);

                if (kept.IsFailure)
                {
                    // 續期失敗不會立刻讓串流停掉 —— 憑證還有效期可以撐,而下一次續期多半會成功。
                    // 但它一定要被看見:連續失敗到憑證過期,帳戶事件就會整個消失,
                    // 而在那之前這是唯一的預告。
                    // A failed renewal does not stop the stream at once: the credential still has time left and
                    // the next attempt usually succeeds. It must still be seen — enough consecutive failures
                    // and account events disappear altogether, and until then this is the only warning.
                    Broadcast(BinanceUserDataErrors.Decorate(kept.Error!, _endpoints));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消來自 DisposeAsync,是正常收尾。
            // The cancellation comes from DisposeAsync and is a normal shutdown.
        }
    }

    private string ReconnectedDetail(DateTimeOffset untrustedSince)
    {
        var gap = _timeProvider.GetUtcNow() - untrustedSince;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"串流中斷約 {gap} 之後重新連上;中斷期間的委託與成交不會補送。The stream reconnected after roughly {gap}; nothing that happened while it was down is replayed.");
    }

    private void PublishResync(ResyncReason reason, DateTimeOffset untrustedSince, string detail) =>
        Publish(
            UserDataChannel.ResyncSignals,
            new ResyncRequired
            {
                Reason = reason,
                Detail = detail,
                UntrustedSince = untrustedSince,
                Timestamp = _timeProvider.GetUtcNow(),
            });

    private void Publish(UserDataChannel channel, object payload)
    {
        foreach (var subscriber in SnapshotSubscribers())
        {
            if (subscriber.Channel == channel)
            {
                subscriber.Publish(payload);
            }
        }
    }

    private void Broadcast(Error error)
    {
        foreach (var subscriber in SnapshotSubscribers())
        {
            subscriber.Fail(error);
        }
    }

    private void CompleteAll()
    {
        foreach (var subscriber in SnapshotSubscribers())
        {
            subscriber.Complete();
        }
    }

    private void Stop(Error reason)
    {
        lock (_subscriberGate)
        {
            if (_stopReason is null)
            {
                Volatile.Write(ref _stopReason, reason);
            }
        }
    }

    private void Register(IUserDataSubscriber subscriber)
    {
        lock (_subscriberGate)
        {
            _subscribers.Add(subscriber);
        }
    }

    private void Deregister(IUserDataSubscriber subscriber)
    {
        lock (_subscriberGate)
        {
            _ = _subscribers.Remove(subscriber);
        }
    }

    /// <summary>
    /// 取一份訂閱者快照再分送。
    /// Takes a snapshot of the subscribers before handing anything out.
    /// </summary>
    /// <returns>目前登記中的訂閱者。The subscribers currently registered.</returns>
    /// <remarks>
    /// 分送時不持有鎖。持有鎖去呼叫訂閱者,等於讓一個消費端的行為影響到登記與除名 ——
    /// 而除名發生在消費端自己的 <c>finally</c> 裡,那是一條會互相等待的路。
    /// The lock is not held while dispatching. Calling into a subscriber under the lock would let one
    /// consumer's behaviour block registration and removal — and removal happens in that consumer's own
    /// <c>finally</c>, which is a path where the two can wait on each other.
    /// </remarks>
    private IUserDataSubscriber[] SnapshotSubscribers()
    {
        lock (_subscriberGate)
        {
            return [.. _subscribers];
        }
    }

    /// <summary>
    /// 一個登記中的訂閱者。
    /// One registered subscriber.
    /// </summary>
    private interface IUserDataSubscriber
    {
        /// <summary>
        /// 這個訂閱者聽的是哪一條串流。
        /// Which stream this subscriber listens to.
        /// </summary>
        UserDataChannel Channel { get; }

        /// <summary>
        /// 送出一筆事件;型別不符時略過。
        /// Publishes one event, skipping anything of the wrong type.
        /// </summary>
        /// <param name="payload">事件酬載。The event payload.</param>
        void Publish(object payload);

        /// <summary>
        /// 送出一筆失敗。
        /// Publishes one failure.
        /// </summary>
        /// <param name="error">失敗原因。The failure.</param>
        void Fail(Error error);

        /// <summary>
        /// 結束這條訂閱。
        /// Ends this subscription.
        /// </summary>
        void Complete();
    }

    /// <summary>
    /// 一個訂閱者的有界佇列。
    /// One subscriber's bounded queue.
    /// </summary>
    /// <typeparam name="T">這條串流的事件型別。The event type of this stream.</typeparam>
    private sealed class Subscriber<T> : IUserDataSubscriber
        where T : class
    {
        private readonly Channel<Result<T>> _channel;
        private readonly Lock _gate = new();
        private readonly int _capacity;
        private readonly BinanceEndpoints _endpoints;

        private Error? _overflow;

        public Subscriber(UserDataChannel channel, int capacity, BinanceEndpoints endpoints)
        {
            Channel = channel;
            _capacity = capacity;
            _endpoints = endpoints;

            // FullMode 必須是 Wait:只有它會讓 TryWrite 在佇列滿時回傳 false。
            // 另外三種 Drop 模式的 TryWrite 一律回傳 true 並<b>靜默丟掉</b>一則 ——
            // 那正是這個型別存在要避免的事,而且從回傳值完全看不出來。
            // The full mode must be Wait: it is the only one whose TryWrite returns false on a full queue. The
            // three dropping modes all return true and <b>silently discard</b> a message, which is the exact
            // behaviour this type exists to prevent and which the return value gives no hint of.
            _channel = System.Threading.Channels.Channel.CreateBounded<Result<T>>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
        }

        public UserDataChannel Channel { get; }

        public void Publish(object payload)
        {
            if (payload is T typed)
            {
                Offer(Result.Success(typed));
            }
        }

        public void Fail(Error error) => Offer(Result.Failure<T>(error));

        public void Complete() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<Result<T>> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                var next = await TryReadAsync(cancellationToken).ConfigureAwait(false);

                if (next is not { } item)
                {
                    break;
                }

                yield return item;
            }

            // 已經排進佇列的事件先全部交出去,才輪到那筆「你漏掉了」的失敗。
            // 反過來先報失敗,消費端會以為連手上這些都不可信。
            // Everything already queued is handed over before the "you have lost events" failure. Raising it
            // first would tell the consumer that even the events in hand cannot be trusted.
            Error? overflow;

            lock (_gate)
            {
                overflow = _overflow;
            }

            if (overflow is not null)
            {
                yield return Result.Failure<T>(overflow);
            }
        }

        private async ValueTask<Result<T>?> TryReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_channel.Reader.TryRead(out var item))
                    {
                        return item;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消只結束這一次列舉,不是錯誤 —— 這是 IUserDataFeed 說的「取消即代表結束訂閱」。
                // Cancelling ends this enumeration and is not an error: it is what IUserDataFeed means by
                // "cancelling ends the subscription".
            }

            return null;
        }

        private void Offer(Result<T> item)
        {
            lock (_gate)
            {
                if (_overflow is not null)
                {
                    return;
                }

                if (_channel.Writer.TryWrite(item))
                {
                    return;
                }

                // 佇列滿了。不丟棄、不阻塞讀取迴圈(阻塞會拖垮其他訂閱者),
                // 而是讓這一條訂閱以一筆說得出原因的失敗結束。
                // The queue is full. Nothing is discarded and the read loop is not blocked — blocking would
                // drag the other subscribers down with it — so this one subscription ends with a failure that
                // says why.
                _overflow = BinanceUserDataErrors.SubscriberFellBehind(_capacity, _endpoints);
                _ = _channel.Writer.TryComplete();
            }
        }
    }
}
