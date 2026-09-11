using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流的端到端行為。連線一律走假的連線工廠,憑證端點走假的 HTTP 處理器,完全不碰網路。
/// End-to-end behaviour of the user data stream. The connection always runs through a fake factory and the
/// credential endpoint through a fake HTTP handler; nothing touches the network.
/// </summary>
[TestClass]
public sealed class BinanceUserDataFeedTests
{
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
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderPartiallyFilled]);
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var trades = feed.SubscribeTradeUpdatesAsync().GetAsyncEnumerator();

                // 兩個訂閱者都要在第一則事件送達之前登記完。訂閱的登記是同步發生的,
                // 所以兩次 MoveNextAsync 一起發動就夠了。
                // Both subscribers must be registered before the first event arrives. Registration happens
                // synchronously, so issuing both MoveNextAsync calls together is enough.
                var orderMove = orders.MoveNextAsync();
                var tradeMove = trades.MoveNextAsync();

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
    public async Task TwoSubscribersToTheSameEventEachReceiveTheirOwnCompleteCopy()
    {
        // 共用一份佇列會讓兩個消費端瓜分事件,而那種錯誤的症狀是「兩邊都只看到一半,
        // 但都不覺得自己漏了」。
        // A shared queue splits the events between the two consumers, and the symptom of that is both sides
        // seeing half of them and neither noticing anything is missing.
        var connection = new FakeWebSocketConnection([
            UserDataSamples.OrderNew,
            UserDataSamples.OrderPartiallyFilled,
        ]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var first = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var second = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();

                var firstMove = first.MoveNextAsync();
                var secondMove = second.MoveNextAsync();

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
        var connection = new FakeWebSocketConnection([UserDataSamples.OrderPartiallyFilled]);
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
        var connection = new FakeWebSocketConnection([
            UserDataSamples.OrderNew,
            UserDataSamples.OrderPartiallyFilled,
        ]);

        var (feed, stub, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            await using (feed)
            {
                var orders = feed.SubscribeOrderUpdatesAsync().GetAsyncEnumerator();
                var trades = feed.SubscribeTradeUpdatesAsync().GetAsyncEnumerator();

                var orderMove = orders.MoveNextAsync();
                var tradeMove = trades.MoveNextAsync();

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
