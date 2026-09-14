using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;
using Ozakboy.TradeKit.Binance.UserData;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流的端到端行為。連線一律走假的連線工廠,憑證端點走假的 HTTP 處理器,完全不碰網路。
/// End-to-end behaviour of the user data stream. The connection always runs through a fake factory and the
/// credential endpoint through a fake HTTP handler; nothing touches the network.
/// </summary>
[TestClass]
public sealed class BinanceUserDataFeedTests
{
    /// <summary>
    /// 使用者資料撥號位址應該恰好帶的查詢參數。
    /// The query parameters the user data address should carry, and no others.
    /// </summary>
    private static readonly string[] ExpectedUserDataQueryNames = ["listenKey", "events"];

    // ── 分流 / Fan-out ────────────────────────────────────────────────

    [TestMethod]
    public async Task AnOrderUpdateReachesTheOrderStream()
    {
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderNew]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                Assert.IsTrue(items[0].IsSuccess, items[0].Error?.Message);
                Assert.AreEqual("pulsetrade-uds-1", items[0].GetValueOrThrow().ClientOrderId);
            }
        }
    }

    [TestMethod]
    public async Task AFillFeedsTheOrderStreamAndTheTradeStreamAtOnce()
    {
        // 一則 ORDER_TRADE_UPDATE 是「委託狀態變了」和「成交了一筆」兩件事。只餵其中一條,
        // 另一邊就會少掉整條資訊,而少掉的那一邊不會有任何徵兆。
        // One ORDER_TRADE_UPDATE is both a state change and a fill. Feeding only one stream leaves the other
        // missing that information entirely, with nothing on that side to show for it.
        // 劇本先留空,兩個訂閱者都登記完才把事件放上去。第一次 MoveNextAsync 不只是登記訂閱者,
        // 它還會同步跑完建立憑證、握手與啟動讀取迴圈 —— 所以「建構式就排好一則事件」時,
        // 那一則可能在它回來之前就已經分送完畢,而第二個訂閱者還沒登記。
        // The script starts empty and the event goes on only once both subscribers are registered. The first
        // MoveNextAsync does more than register: it synchronously creates the credential, completes the
        // handshake, and starts the read loop, so a frame queued in the constructor can be dispatched before it
        // returns — with the second subscriber not yet registered.
        var connection = new FakeWebSocketConnection([]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var trades = feed.SubscribeTradeUpdatesAsync().GetAsyncEnumerator();

                var orderMove = orders.MoveNextAsync();
                var tradeMove = trades.MoveNextAsync();

                // 登記是在迭代器主體的第一個 await 之前同步完成的,所以這兩行回來之後,兩個訂閱者都在名單上了。
                // Registration happens synchronously before the iterator body's first await, so once these two
                // statements have returned both subscribers are on the list.
                connection.Enqueue(UserDataSamples.OrderPartiallyFilled);

                try
                {
                    Assert.IsTrue(await orderMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(await tradeMove.AsTask().WaitAsync(UserDataFixture.Timeout));

                    Assert.AreEqual(OrderStatus.PartiallyFilled, orders.Current.GetValueOrThrow().Status);
                    Assert.AreEqual("701001", trades.Current.GetValueOrThrow().TradeId);
                }
                finally
                {
                    await orders.DisposeAsync();
                    await trades.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task AConditionalOrderEventFeedsOnlyItsOwnStream()
    {
        // ALGO_UPDATE 與 ORDER_TRADE_UPDATE 是兩個不同的事件,分流也必須是分開的。
        // 若條件單事件漏進委託串流,上層會看到一張沒有成交資訊、狀態語意也不同的假委託;
        // 若它沒進條件單串流,「停損被觸發了」這件事就整個消失。
        // ALGO_UPDATE and ORDER_TRADE_UPDATE are different events and the fan-out has to keep them apart.
        // Leaking a conditional event into the order stream shows the caller a phantom order with no fill
        // information and different status semantics; failing to publish it loses the fact that a stop
        // triggered at all.
        var connection = new FakeWebSocketConnection([]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var conditionals = feed.SubscribeConditionalOrderUpdatesAsync().GetAsyncEnumerator();
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();

                var conditionalMove = conditionals.MoveNextAsync();
                var orderMove = orders.MoveNextAsync();

                connection.Enqueue(UserDataSamples.AlgoNew);

                try
                {
                    Assert.IsTrue(await conditionalMove.AsTask().WaitAsync(UserDataFixture.Timeout));

                    var update = conditionals.Current.GetValueOrThrow();

                    Assert.AreEqual("pulsetrade-algo-1", update.ConditionalOrder.ClientConditionalOrderId);
                    Assert.AreEqual(ConditionalOrderStatus.New, update.ConditionalOrder.Status);

                    // 委託串流不該收到任何東西。再推一則真正的委託事件上去,它才會動 ——
                    // 那也證明這條串流本來就是活的,上面那個「沒收到」不是因為它根本沒在跑。
                    // The order stream should have received nothing. Pushing a real order event makes it move,
                    // which also proves the stream was alive and the silence above was not simply a dead one.
                    Assert.IsFalse(orderMove.IsCompleted, "條件單事件不該出現在委託串流上。");

                    connection.Enqueue(UserDataSamples.OrderNew);

                    Assert.IsTrue(await orderMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.AreEqual("pulsetrade-uds-1", orders.Current.GetValueOrThrow().ClientOrderId);
                }
                finally
                {
                    await conditionals.DisposeAsync();
                    await orders.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task ARejectedConditionalOrderReachesTheSubscriberWithItsReason()
    {
        // 停損被拒是「以為有保護、其實沒有」,而原因只在這則事件裡出現一次。
        // 分流若把它吞掉,上層就再也查不到為什麼。
        // A rejected stop is protection believed to be in place that is not, and the reason appears exactly
        // once in this event. If the fan-out swallows it, the caller can never find out why.
        var connection = new FakeWebSocketConnection([UserDataSamples.AlgoRejected]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeConditionalOrderUpdatesAsync(), 1);

                var update = items[0].GetValueOrThrow();

                Assert.AreEqual(ConditionalOrderStatus.Rejected, update.ConditionalOrder.Status);
                Assert.AreEqual("Reduce Only reject", update.RejectReason);
            }
        }
    }

    [TestMethod]
    public async Task TwoSubscribersToTheSameEventEachReceiveTheirOwnCompleteCopy()
    {
        // 共用一份佇列會讓兩個消費端瓜分事件,而那種錯誤的症狀是「兩邊都只看到一半,
        // 但都不覺得自己漏了」。
        // A shared queue splits the events between the two consumers, and the symptom of that is both sides
        // seeing half of them and neither noticing anything is missing.
        // 劇本先留空,兩個訂閱者都登記完才放事件上去 —— 理由同
        // AFillFeedsTheOrderStreamAndTheTradeStreamAtOnce:第一次 MoveNextAsync 會順便啟動整條串流。
        // The script starts empty and the events go on once both subscribers are registered, for the same reason
        // as in AFillFeedsTheOrderStreamAndTheTradeStreamAtOnce: the first MoveNextAsync starts the whole feed.
        var connection = new FakeWebSocketConnection([]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var first = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var second = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();

                var firstMove = first.MoveNextAsync();
                var secondMove = second.MoveNextAsync();

                connection.Enqueue(UserDataSamples.OrderNew);
                connection.Enqueue(UserDataSamples.OrderPartiallyFilled);

                try
                {
                    Assert.IsTrue(await firstMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(await secondMove.AsTask().WaitAsync(UserDataFixture.Timeout));

                    Assert.AreEqual(OrderStatus.New, first.Current.GetValueOrThrow().Status);
                    Assert.AreEqual(OrderStatus.New, second.Current.GetValueOrThrow().Status);

                    Assert.IsTrue(await first.MoveNextAsync().AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(await second.MoveNextAsync().AsTask().WaitAsync(UserDataFixture.Timeout));

                    Assert.AreEqual(OrderStatus.PartiallyFilled, first.Current.GetValueOrThrow().Status);
                    Assert.AreEqual(OrderStatus.PartiallyFilled, second.Current.GetValueOrThrow().Status);
                }
                finally
                {
                    await first.DisposeAsync();
                    await second.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task AnAccountUpdateReachesOnlyTheAccountStream()
    {
        var connection = new FakeWebSocketConnection([UserDataSamples.AccountUpdate]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeAccountUpdatesAsync(), 1);

                Assert.AreEqual(AccountUpdateReason.Order, items[0].GetValueOrThrow().Reason);
            }
        }
    }

    [TestMethod]
    public async Task AMarginCallReachesTheMarginCallStream()
    {
        var connection = new FakeWebSocketConnection([UserDataSamples.MarginCall]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeMarginCallsAsync(), 1);

                Assert.AreEqual("ETHUSDT", items[0].GetValueOrThrow().Positions[0].Symbol);
            }
        }
    }

    [TestMethod]
    public async Task AnUnreadableFrameIsReportedWithoutEndingTheStream()
    {
        // 解析失敗若就地丟掉,幣安哪天改了欄位名,症狀會是「委託狀態慢慢對不上」而不是任何錯誤。
        // Dropping a parse failure means that the day Binance renames a field, the symptom is order state
        // slowly drifting out of agreement rather than any error at all.
        var connection = new FakeWebSocketConnection([
            "}{ not json",
            UserDataSamples.OrderNew,
        ]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 2);

                Assert.IsTrue(items[0].IsFailure);
                Assert.AreEqual(BinanceErrorCodes.MalformedResponse, items[0].Error?.Code);
                Assert.IsTrue(items[1].IsSuccess, "一則壞訊息不該讓後面的帳戶事件停下來。");
            }
        }
    }

    [TestMethod]
    public async Task ABinaryFrameIsReportedWithoutEndingTheStream()
    {
        var connection = new FakeWebSocketConnection([
            FakeWebSocketFrame.Binary(UserDataSamples.OrderNew),
            UserDataSamples.OrderNew,
        ]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 2);

                Assert.IsTrue(items[0].IsFailure);
                Assert.IsTrue(items[1].IsSuccess);
            }
        }
    }

    // ── 背壓 / Backpressure ───────────────────────────────────────────

    [TestMethod]
    public async Task ASubscriberThatFallsBehindEndsWithAFailureRatherThanLosingEventsInSilence()
    {
        // 這是本設計的重點:訂單事件絕不靜默丟棄。丟掉一筆成交回報而不說,本地部位就會與交易所分岔,
        // 而帳面上的數字依然是個合理的數字。
        // This is the point of the design: order events are never dropped in silence. Losing a fill without
        // saying so leaves local positions diverging from the exchange's while the books stay plausible.
        var frames = Enumerable.Repeat<FakeWebSocketFrame>(UserDataSamples.OrderNew, 40).ToArray();
        var connection = new FakeWebSocketConnection(frames);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions { SubscriberQueueCapacity = 2 });

        using (http)
        {
            await using (feed)
            {
                using var cts = new CancellationTokenSource(UserDataFixture.Timeout);

                var items = new List<Result<Order>>();
                var slowedDown = false;

                await foreach (var item in feed.SubscribeOrderUpdatesAsync(cts.Token))
                {
                    items.Add(item);

                    if (!slowedDown)
                    {
                        // 讀一則之後停手一下,讓佇列真的被塞滿 —— 一路跟著讀的消費端永遠不會溢位。
                        // Pause after the first one so the queue really fills: a consumer that keeps up never
                        // overflows.
                        slowedDown = true;
                        await Task.Delay(500, cts.Token);
                    }

                    if (item.IsFailure)
                    {
                        break;
                    }
                }

                var last = items[^1];

                Assert.IsTrue(last.IsFailure, "佇列塞滿之後這條訂閱應該以一筆失敗結束。");
                Assert.AreEqual(TradeErrorCodes.StreamDisconnected, last.Error?.Code);
                Assert.IsFalse(
                    last.Error!.IsTransient,
                    "漏掉事件不是等一下就會好的事,分類必須讓 IsTransient 為 false。");
                Assert.AreEqual(ErrorCategory.Exhausted, last.Error!.Category);
                Assert.IsLessThan(frames.Length, items.Count, "溢位之後不該還把全部事件都收齊。");
            }
        }
    }

    // ── 憑證與重連 / Credential and reconnection ──────────────────────

    [TestMethod]
    public async Task AnExpiredCredentialRaisesAResyncSignalAndRebuildsTheStream()
    {
        // 只送訊號就停擺,等於把「帳戶事件從此消失」變成一件沒有人負責的事。
        // Raising the signal and stopping leaves "account events have vanished" as nobody's problem.
        var first = new FakeWebSocketConnection([UserDataSamples.ListenKeyExpired(UserDataSamples.ListenKey)]);
        var second = new FakeWebSocketConnection([UserDataSamples.OrderNew]);
        var factory = new FakeWebSocketConnectionFactory(first, second);

        var (feed, stub, http) = UserDataFixture.Create(factory);

        using (http)
        {
            await using (feed)
            {
                var signals = await UserDataFixture.TakeAsync(feed.SubscribeResyncSignalsAsync(), 1);

                var signal = signals[0].GetValueOrThrow();

                Assert.AreEqual(ResyncReason.StreamCredentialExpired, signal.Reason);

                // 「從哪一刻起不可信」是憑證失效的時刻,「訊號什麼時候送出」是現在。
                // 兩個欄位分開記錄,正是因為它們不同。
                // "Untrusted from" is when the credential lapsed and "raised at" is now. The two fields are
                // kept apart precisely because they differ.
                Assert.AreEqual(
                    DateTimeOffset.FromUnixTimeMilliseconds(UserDataSamples.ListenKeyExpiredEventTimeMs),
                    signal.UntrustedSince);
                Assert.AreEqual(UserDataFixture.FixedNow, signal.Timestamp);
                Assert.AreNotEqual(signal.UntrustedSince, signal.Timestamp);

                await UserDataFixture.WaitUntilAsync(
                    () => stub.Requests.Count(request => request.Method == HttpMethod.Post) >= 2,
                    "憑證失效之後應該重新建立一把新的 listenKey。");

                await UserDataFixture.WaitUntilAsync(
                    () => factory.Created.Count >= 2,
                    "憑證失效之後應該重新連線。");
            }
        }
    }

    [TestMethod]
    public async Task ADroppedConnectionRaisesAReconnectedSignalOnceItIsBack()
    {
        // 斷線期間發生的委託與成交交易所不補送。沒有這個訊號,本地部位會與交易所安靜分岔,
        // 而帳面上的數字依然合理。
        // The exchange replays nothing from a drop. Without this signal, local positions diverge from the
        // exchange's in silence while the numbers stay plausible.
        var first = new FakeWebSocketConnection([UserDataSamples.OrderNew], closeWhenScriptEnds: true);
        var second = new FakeWebSocketConnection([UserDataSamples.OrderPartiallyFilled]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(first, second));

        using (http)
        {
            await using (feed)
            {
                // 斷線本身也會以一筆暫時性失敗出現在這條串流上,訊號排在它後面 ——
                // 「剛剛斷了」和「已經回來了、請對帳」是兩件不同的事,消費端兩件都該看到。
                // The drop itself also appears on this stream as a transient failure, with the signal behind
                // it: "it just dropped" and "it is back, go reconcile" are different facts and the consumer is
                // owed both.
                var items = await UserDataFixture.TakeAsync(feed.SubscribeResyncSignalsAsync(), 2);

                Assert.IsTrue(items[0].IsFailure);
                Assert.IsTrue(items[0].Error!.IsTransient);

                var signal = items[1].GetValueOrThrow();

                Assert.AreEqual(ResyncReason.Reconnected, signal.Reason);
                Assert.AreNotEqual(default, signal.UntrustedSince);
                Assert.IsNotNull(signal.Detail);
            }
        }
    }

    [TestMethod]
    public async Task ADroppedConnectionAlsoSurfacesAsATransientFailureOnEveryStream()
    {
        var first = new FakeWebSocketConnection([UserDataSamples.OrderNew], closeWhenScriptEnds: true);
        var second = new FakeWebSocketConnection([UserDataSamples.OrderPartiallyFilled]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(first, second));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 3);

                Assert.IsTrue(items[0].IsSuccess, items[0].Error?.Message);

                Assert.IsTrue(items[1].IsFailure);
                Assert.AreEqual(TradeErrorCodes.StreamDisconnected, items[1].Error?.Code);
                Assert.IsTrue(
                    items[1].Error!.IsTransient,
                    "重連中的斷線是暫時性的,消費端應該知道這只是缺口而不是結束。");

                Assert.IsTrue(items[2].IsSuccess, items[2].Error?.Message);
            }
        }
    }

    // ── 心跳與存活偵測 / Heartbeat and liveness ───────────────────────

    [TestMethod]
    public async Task AHeartbeatReplyIsIgnoredWithoutRaisingAnyError()
    {
        // 心跳每 30 秒一則。回覆若被當成讀不懂的訊息,消費端每 30 秒就收到一筆假警報,
        // 真正的失敗會被淹掉 —— 而且那筆「失敗」的來源是一則帶著憑證的訊息。
        // A heartbeat goes out every 30 seconds. Treating its reply as unreadable hands the consumer a false
        // alarm that often and buries the real failures — and each of those "failures" would stem from a frame
        // carrying the credential.
        var connection = new FakeWebSocketConnection([
            UserDataSamples.HeartbeatReply(UserDataSamples.ListenKey, 1),
            UserDataSamples.HeartbeatRejection(UserDataSamples.ListenKey),
            UserDataSamples.HeartbeatReply(UserDataSamples.ListenKey, 2),
            UserDataSamples.OrderNew,
        ]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                // 對帳訊號這一條預期什麼都收不到,所以它的 MoveNextAsync 會一直懸著。非同步迭代器不允許在
                // MoveNextAsync 懸著的時候 DisposeAsync(會擲 NotSupportedException),因此用它自己的權杖
                // 取消,等那一次移動結束之後才釋放。
                // The resync subscription is expected to receive nothing, so its MoveNextAsync stays pending. An
                // async iterator refuses DisposeAsync while a MoveNextAsync is pending (NotSupportedException), so
                // it is cancelled through its own token and disposed only once that move has finished.
                using var signalsCts = new CancellationTokenSource();

                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var signals = feed.SubscribeResyncSignalsAsync(signalsCts.Token).GetAsyncEnumerator();

                var orderMove = orders.MoveNextAsync();
                var signalMove = signals.MoveNextAsync();

                try
                {
                    // 第一個交到手上的就是那筆委託 —— 前面三則回覆沒有在任何一條串流上留下東西。
                    // The first thing delivered is the order: the three replies ahead of it left nothing behind.
                    Assert.IsTrue(await orderMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(orders.Current.IsSuccess, orders.Current.Error?.Message);
                    Assert.AreEqual("pulsetrade-uds-1", orders.Current.GetValueOrThrow().ClientOrderId);

                    Assert.IsFalse(signalMove.IsCompleted, "心跳回覆不該在對帳訊號串流上產生任何東西。");
                }
                finally
                {
                    await signalsCts.CancelAsync();
                    _ = await signalMove.AsTask().WaitAsync(UserDataFixture.Timeout);

                    await orders.DisposeAsync();
                    await signals.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task TheHeartbeatIsAListSubscriptionsCommandWithAnIncreasingId()
    {
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderNew]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                KeepAliveInterval = TimeSpan.FromMilliseconds(50),
                IdleTimeout = TimeSpan.FromSeconds(10),
            });

        using (http)
        {
            await using (feed)
            {
                _ = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                await UserDataFixture.WaitUntilAsync(
                    () => connection.Sent.Count >= 2,
                    "心跳計時器應該在連線上送出 LIST_SUBSCRIPTIONS。");

                var sent = connection.Sent;

                Assert.AreEqual("""{"method":"LIST_SUBSCRIPTIONS","id":1}""", sent[0]);
                Assert.AreEqual("""{"method":"LIST_SUBSCRIPTIONS","id":2}""", sent[1]);
            }
        }
    }

    [TestMethod]
    public async Task AnIdleConnectionIsAbortedAndTheReconnectRaisesAResyncSignal()
    {
        // 「握手成功、狀態顯示已連線、資料流卻被吃掉」的連線,只有閒置逾時抓得到。抓到之後要走的是一般斷線
        // 的重連路徑:同一把憑證、重新撥號、回來之後送 Reconnected —— 那段期間的成交交易所不補送。
        // A connection that shook hands, reads connected, and has its data swallowed is only caught by the idle
        // timeout. Once caught it takes the ordinary reconnect path — same credential, redial, Reconnected once
        // back — because nothing from the gap is replayed.
        var clock = TestClock.AtFixedInstant();
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var stub = UserDataFixture.ListenKeyStub(UserDataSamples.ListenKey);
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        // 第一條連線送完一則之後就再也不說話,也不會自己關 —— 心跳送得出去,但永遠等不到回覆。
        // 唯一能讓它重連的,就是閒置逾時。
        // The first connection delivers one frame and then never speaks again, nor closes by itself: heartbeats
        // go out and are never answered. The idle timeout is the only thing that can make it reconnect.
        var first = new FakeWebSocketConnection([UserDataSamples.OrderNew]);
        var second = new FakeWebSocketConnection([UserDataSamples.OrderPartiallyFilled]);
        var factory = new FakeWebSocketConnectionFactory(first, second);

        var feed = new BinanceUserDataFeed(
            pipeline,
            options,
            new BinanceUserDataStreamOptions
            {
                KeepAliveInterval = TimeSpan.FromMilliseconds(50),
                IdleTimeout = TimeSpan.FromMilliseconds(200),
            },
            loggerFactory: null,
            timeProvider: clock,
            connectionFactory: factory);

        using (http)
        {
            await using (feed)
            {
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var signals = feed.SubscribeResyncSignalsAsync().GetAsyncEnumerator();

                var orderMove = orders.MoveNextAsync();
                var signalMove = signals.MoveNextAsync();

                try
                {
                    Assert.IsTrue(await orderMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.AreEqual(OrderStatus.New, orders.Current.GetValueOrThrow().Status);
                    Assert.HasCount(1, factory.Created);

                    // 連線層用注入的時鐘量「多久沒收到訊息」。讓時鐘越過閒置逾時,下一次檢查就會判定這條連線已死。
                    // The connection layer measures silence with the injected clock. Moving it past the idle
                    // timeout makes the next check declare the connection dead.
                    clock.Advance(TimeSpan.FromSeconds(1));

                    // 先是斷線(暫時性失敗),再是「回來了、請對帳」。
                    // First the drop, as a transient failure; then "it is back, go reconcile".
                    Assert.IsTrue(await signalMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(signals.Current.IsFailure, "閒置逾時的斷線應該先以一筆失敗出現。");
                    Assert.IsTrue(signals.Current.Error!.IsTransient, "閒置逾時會重連,這筆失敗必須是暫時性的。");

                    Assert.IsTrue(await signals.MoveNextAsync().AsTask().WaitAsync(UserDataFixture.Timeout));

                    var signal = signals.Current.GetValueOrThrow();

                    Assert.AreEqual(ResyncReason.Reconnected, signal.Reason);
                    Assert.AreNotEqual(default, signal.UntrustedSince);

                    // 重連之後的事件照常送達,而且用的是同一把憑證 —— 閒置逾時不是憑證過期,不該重建憑證。
                    // Events flow again after the reconnect, on the same credential: an idle timeout is not an
                    // expiry and must not rebuild the credential.
                    Assert.IsTrue(await orders.MoveNextAsync().AsTask().WaitAsync(UserDataFixture.Timeout));

                    var afterReconnect = orders.Current;

                    if (afterReconnect.IsFailure)
                    {
                        // 斷線那一筆也會出現在委託串流上,排在重連後的事件前面。
                        // The drop also appears on the order stream, ahead of the events after the reconnect.
                        Assert.IsTrue(afterReconnect.Error!.IsTransient);
                        Assert.IsTrue(await orders.MoveNextAsync().AsTask().WaitAsync(UserDataFixture.Timeout));
                        afterReconnect = orders.Current;
                    }

                    Assert.AreEqual(OrderStatus.PartiallyFilled, afterReconnect.GetValueOrThrow().Status);
                    Assert.IsGreaterThanOrEqualTo(2, factory.Created.Count);
                    Assert.AreEqual(1, stub.Requests.Count(request => request.Method == HttpMethod.Post));
                }
                finally
                {
                    await orders.DisposeAsync();
                    await signals.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task TheStreamDialsThePrivateRouteWithTheCredentialOnlyInTheQuery()
    {
        // 路由錯了不會有任何錯誤,只會握手成功、零事件 —— 0.1.0 撥的 /ws/{listenKey} 在 Testnet 上就是這樣。
        // events 又是真的過濾器、名稱不被驗證,少列或拼錯一個,那一類事件就安靜地永遠不來。
        // A wrong route raises no error, only a handshake followed by silence — which is what the 0.1.0
        // /ws/{listenKey} now does on the testnet. And events really filters without validating names, so one
        // omission or misspelling makes that kind of event silently never arrive.
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderNew]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                _ = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                var uri = connection.ConnectedUri;

                Assert.IsNotNull(uri, "串流沒有撥號。");
                Assert.AreEqual("wss", uri.Scheme);
                Assert.AreEqual("stream.binancefuture.com", uri.Host);
                Assert.AreEqual("/private/ws", uri.AbsolutePath);

                var query = ParseQuery(uri);

                CollectionAssert.AreEquivalent(ExpectedUserDataQueryNames, query.Keys.ToArray());
                Assert.AreEqual(UserDataSamples.ListenKey, query["listenKey"]);

                // 憑證在整個位址裡只能出現這一次:不在路徑、不在事件清單。
                // The credential may appear exactly once in the whole address: not in the path, not among the events.
                Assert.AreEqual(
                    1,
                    uri.OriginalString.Split(UserDataSamples.ListenKey).Length - 1,
                    "憑證出現在查詢參數 listenKey= 以外的地方。");

                var events = query["events"].Split('/');

                Assert.HasCount(5, events);
                CollectionAssert.AreEquivalent(
                    new[]
                    {
                        BinanceUserDataPaths.OrderTradeUpdateEvent,

                        // 漏掉這一個,條件單事件就永遠不會來 —— 而且連線照樣成功、其他事件照樣收得到,
                        // 沒有任何錯誤指向這裡。停損被觸發或被拒絕,上層全部看不到。
                        // Leave this one out and conditional order events never arrive, while the connection
                        // still succeeds and every other event still flows, with nothing pointing here. A stop
                        // triggering or being rejected becomes invisible to the caller.
                        BinanceUserDataPaths.AlgoUpdateEvent,
                        BinanceUserDataPaths.AccountUpdateEvent,
                        BinanceUserDataPaths.MarginCallEvent,
                        BinanceUserDataPaths.ListenKeyExpiredEvent,
                    },
                    events);
            }
        }
    }

    [TestMethod]
    public async Task TheStreamIdentifierInDiagnosticsIsAFixedLiteralRatherThanTheCredential()
    {
        var connection = new FakeWebSocketConnection(["}{ not json"]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                Assert.IsTrue(
                    items[0].Error!.TryGetData(BinanceStreamErrorDataKeys.StreamNames, out var identifier));

                Assert.AreEqual("userDataStream", identifier);
            }
        }
    }

    /// <summary>
    /// 把查詢字串拆成已解碼的名稱與值。
    /// Splits a query string into decoded names and values.
    /// </summary>
    /// <param name="uri">要拆的位址。The address to split.</param>
    /// <returns>名稱對應值。Names mapped to values.</returns>
    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static pair => Uri.UnescapeDataString(pair[0]),
                static pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty,
                StringComparer.Ordinal);

    // ── 憑證生命週期 / Credential lifecycle ────────────────────────────

    [TestMethod]
    public async Task TheCredentialIsCreatedOnlyOnTheFirstSubscription()
    {
        // 延遲啟動:只想查交易規則的宿主不該因為註冊了這個服務就多出一把私有資料憑證。
        // Lazy start: a host that only queries trading rules should not acquire a private-data credential
        // merely by registering the service.
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderNew]);
        var (feed, stub, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                Assert.AreEqual(0, stub.CallCount, "還沒有人訂閱就不該建立憑證。");

                _ = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                Assert.AreEqual(1, stub.Requests.Count(request => request.Method == HttpMethod.Post));
            }
        }
    }

    [TestMethod]
    public async Task TwoSubscriptionsShareOneCredentialAndOneConnection()
    {
        // 帳戶同時只有一把 listenKey。建出第二把不是多一條串流,而是把第一把的效期一起改掉。
        // An account holds one listenKey at a time. Creating a second is not another stream; it changes the
        // validity of the first.
        var connection = new FakeWebSocketConnection([]);
        var factory = new FakeWebSocketConnectionFactory(connection);

        var (feed, stub, http) = UserDataFixture.Create(factory);

        using (http)
        {
            await using (feed)
            {
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var trades = feed.SubscribeTradeUpdatesAsync().GetAsyncEnumerator();

                var orderMove = orders.MoveNextAsync();
                var tradeMove = trades.MoveNextAsync();

                // 兩個訂閱者都登記完才放事件上去,否則先啟動的那一次可能已經把它分送掉了。
                // The event goes on only once both are registered; otherwise the one that started the feed may
                // already have dispatched it.
                connection.Enqueue(UserDataSamples.OrderPartiallyFilled);

                try
                {
                    Assert.IsTrue(await orderMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(await tradeMove.AsTask().WaitAsync(UserDataFixture.Timeout));

                    Assert.AreEqual(1, stub.Requests.Count(request => request.Method == HttpMethod.Post));
                    Assert.AreEqual(1, factory.Created.Count);
                }
                finally
                {
                    await orders.DisposeAsync();
                    await trades.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task EndingOneSubscriptionDoesNotCloseTheSharedConnection()
    {
        var connection = new FakeWebSocketConnection([]);

        var (feed, stub, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var trades = feed.SubscribeTradeUpdatesAsync().GetAsyncEnumerator();

                var orderMove = orders.MoveNextAsync();
                var tradeMove = trades.MoveNextAsync();

                // 兩個訂閱者都登記完才放事件上去。
                // The events go on only once both subscribers are registered.
                connection.Enqueue(UserDataSamples.OrderNew);
                connection.Enqueue(UserDataSamples.OrderPartiallyFilled);

                try
                {
                    Assert.IsTrue(await orderMove.AsTask().WaitAsync(UserDataFixture.Timeout));
                    Assert.IsTrue(await tradeMove.AsTask().WaitAsync(UserDataFixture.Timeout));

                    // 成交那一條訂閱就此結束。連線是共用的,不可以跟著它一起關掉。
                    // The fill subscription ends here. The connection is shared and must not go with it.
                    await trades.DisposeAsync();

                    Assert.IsTrue(
                        await orders.MoveNextAsync().AsTask().WaitAsync(UserDataFixture.Timeout),
                        "另一條訂閱結束之後,這一條就收不到事件了 —— 連線被順手關掉了。");

                    Assert.AreEqual(OrderStatus.PartiallyFilled, orders.Current.GetValueOrThrow().Status);
                    Assert.AreEqual(1, stub.Requests.Count(request => request.Method == HttpMethod.Post));
                }
                finally
                {
                    await orders.DisposeAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task TheCredentialIsRenewedOnTheConfiguredInterval()
    {
        // 憑證只活 60 分鐘。沒有人續期的話,帳戶事件會在一小時內整個消失,而且是無聲的。
        // A credential lives 60 minutes. With nothing renewing it, account events disappear within the hour,
        // and silently.
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderNew]);

        var (feed, stub, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50),
            });

        using (http)
        {
            await using (feed)
            {
                _ = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                await UserDataFixture.WaitUntilAsync(
                    () => stub.Requests.Any(request => request.Method == HttpMethod.Put),
                    "續期計時器應該對 listenKey 端點送出 PUT。");
            }
        }
    }

    [TestMethod]
    public async Task AFailedRenewalIsReportedInsteadOfBeingSwallowed()
    {
        // 續期失敗是「再過不到一小時,帳戶事件就會全部消失」的唯一預告。
        // A failed renewal is the only advance warning that account events will stop within the hour.
        var connection = new FakeWebSocketConnection([]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50),
            },
            keepAliveResponder: static () => """{"code":-1125,"msg":"This listenKey does not exist."}""");

        using (http)
        {
            await using (feed)
            {
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                Assert.IsTrue(items[0].IsFailure, "續期失敗應該出現在訂閱者的串流上。");
            }
        }
    }

    [TestMethod]
    public async Task DisposingClosesTheCredential()
    {
        // 不刪的話那把憑證還會活滿 60 分鐘,交易所仍然認得它 —— 一把沒人用也沒人管的私有資料憑證。
        // Left alone the credential stays valid for its remaining 60 minutes and the exchange keeps honouring
        // it: a private-data credential nobody is using and nobody is watching.
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderNew]);
        var (feed, stub, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            _ = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

            await feed.DisposeAsync();

            Assert.AreEqual(1, stub.Requests.Count(request => request.Method == HttpMethod.Delete));
        }
    }

    [TestMethod]
    public async Task DisposingWithoutEverSubscribingDoesNotTouchTheCredentialEndpoint()
    {
        var (feed, stub, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory());

        using (http)
        {
            await feed.DisposeAsync();

            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task SubscribingAfterDisposalFailsImmediatelyInsteadOfHangingForEver()
    {
        // 訂閱成功卻永遠收不到事件,是這個套件最不願意產生的故障。
        // Subscribed successfully and never receiving anything is the failure mode this package exists to
        // avoid.
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory());

        using (http)
        {
            await feed.DisposeAsync();

            var items = await UserDataFixture.DrainAsync(feed.SubscribeOrderUpdatesAsync());

            Assert.HasCount(1, items);
            Assert.IsTrue(items[0].IsFailure);
        }
    }

    [TestMethod]
    public async Task ACredentialThatCannotBeCreatedEndsTheSubscriptionWithThatFailure()
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);
        var clock = TestClock.AtFixedInstant();
        var stub = UserDataFixture.ListenKeyStub(UserDataSamples.ListenKey);
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        using (http)
        {
            var feed = new BinanceUserDataFeed(
                pipeline,
                options,
                streamOptions: null,
                loggerFactory: null,
                timeProvider: clock,
                connectionFactory: new FakeWebSocketConnectionFactory());

            await using (feed)
            {
                var items = await UserDataFixture.DrainAsync(feed.SubscribeOrderUpdatesAsync());

                Assert.HasCount(1, items);
                Assert.AreEqual(BinanceErrorCodes.CredentialsMissing, items[0].Error?.Code);
                Assert.AreEqual(0, stub.CallCount, "沒有 API 金鑰的請求應該在送出之前就被擋下來。");
            }
        }
    }

    // ── 設定 / Settings ───────────────────────────────────────────────

    [TestMethod]
    public void InvalidStreamSettingsAreRejectedAtConstruction()
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, StubHttpMessageHandler.Json("{}"), clock);

        using (http)
        {
            Assert.ThrowsExactly<ArgumentException>(() => new BinanceUserDataFeed(
                pipeline,
                options,
                new BinanceUserDataStreamOptions { ListenKeyKeepAliveInterval = TimeSpan.Zero }));
        }
    }

    [TestMethod]
    public void NullOptionsAreRejected()
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, StubHttpMessageHandler.Json("{}"), clock);

        using (http)
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new BinanceUserDataFeed(pipeline, null!));
        }
    }

    [TestMethod]
    public void TheStreamReportsTheEndpointSetItTalksTo()
    {
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory());

        using (http)
        {
            Assert.AreEqual(BinanceEndpoints.Testnet, feed.Endpoints);
            Assert.AreEqual(
                BinanceUserDataStreamOptions.DefaultListenKeyKeepAliveInterval,
                feed.StreamOptions.ListenKeyKeepAliveInterval);
        }
    }
}
