using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 憑證生命週期的可觀測性:計數與時刻的快照、三條日誌行的內容,以及續期失敗的退避重試。
/// The observability of the credential lifecycle: the snapshot of counts and instants, what the three log lines
/// say, and the backoff retries of a failed renewal.
/// </summary>
/// <remarks>
/// <para>
/// <b>這一組要回答的是一個實際發生過、日誌答不出來的問題。</b> 24 小時長跑時串流在 21 小時 24 分後收到
/// <c>listenKeyExpired</c>、套件自動重建 —— 但 30 分鐘一次的續期為什麼撐不到 60 分鐘的有效期?
/// 是幣安對單一憑證另有壽命上限,還是某幾次 <c>PUT</c> 失敗了而沒有人知道?當時的日誌與畫面上,
/// 這兩件事長得一模一樣,因為續期<b>完全沒有</b>日誌、也沒有任何計數。
/// <b>These tests answer a question that actually came up and that the logs could not settle.</b> On a 24-hour
/// run the stream received a <c>listenKeyExpired</c> after 21 hours 24 minutes and rebuilt itself — but why did a
/// renewal every 30 minutes fail to keep a 60-minute credential alive? Does Binance cap a single credential's
/// life, or did some of those <c>PUT</c>s fail unnoticed? In the logs and on screen the two looked identical,
/// because the renewal wrote no log line and kept no count at all.
/// </para>
/// <para>
/// <b>這一組被故意弄壞驗證過。</b> 把 <c>BinanceUserDataFeed.RecordRenewalSucceeded</c> 裡的
/// <c>_renewalCount++</c> 拿掉,<see cref="TheRenewalCountAndTimestampsAreVisibleOnTheStatusSnapshot"/> 變紅
/// (次數停在 0,等到逾時);改回來就綠。一條從來沒紅過的計數測試,和沒有測試沒有分別。
/// <b>Verified by deliberately breaking it.</b> Removing <c>_renewalCount++</c> from
/// <c>BinanceUserDataFeed.RecordRenewalSucceeded</c> turns
/// <see cref="TheRenewalCountAndTimestampsAreVisibleOnTheStatusSnapshot"/> red — the count stays at zero until
/// the wait times out — and restoring it turns it green. A counter test that has never failed is
/// indistinguishable from no test.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceUserDataListenKeyStatusTests
{
    /// <summary>
    /// 這一組用的假憑證。日誌裡不該出現它,一次都不該。
    /// The fake credential these tests use. It must not appear in a log line, not once.
    /// </summary>
    private const string Canary = UserDataSamples.ListenKey;

    /// <summary>
    /// 續期失敗時交易所的回應:<c>-1125</c>,而且本體裡就帶著憑證(實測的形狀)。
    /// The exchange's answer to a failed renewal: <c>-1125</c>, with the credential in the body, as measured.
    /// </summary>
    /// <returns>回應本體。The response body.</returns>
    private static string RenewalRejection() =>
        $$"""{"code":-1125,"msg":"This listenKey does not exist.","listenKey":"{{Canary}}"}""";

    // ── 快照 / Snapshot ───────────────────────────────────────────────

    [TestMethod]
    public async Task TheStatusStartsEmptyBeforeAnythingIsSubscribed()
    {
        // 延遲啟動:還沒有人訂閱就還沒有憑證,快照必須照實說,而不是填一個看起來像真的的時刻。
        // Lazy start: with nobody subscribed there is no credential yet, and the snapshot has to say so rather
        // than offer an instant that looks real.
        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory());

        using (http)
        {
            await using (feed)
            {
                var status = feed.ListenKeyStatus;

                Assert.IsNull(status.CreatedAt);
                Assert.IsNull(status.LastRenewedAt);
                Assert.AreEqual(0, status.RenewalCount);
                Assert.AreEqual(0, status.ConsecutiveRenewalFailures);
                Assert.AreEqual(0, status.RebuildCount);
            }
        }
    }

    [TestMethod]
    public async Task TheRenewalCountAndTimestampsAreVisibleOnTheStatusSnapshot()
    {
        // 「這 24 小時續期了幾次」在 0.1.3 之前完全無從得知 —— 連一個計數都沒有。
        // Before 0.1.4 there was no way to tell how many renewals a day had seen: there was no counter at all.
        var connection = new FakeWebSocketConnection([]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions { ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50) });

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);
                Assert.IsNotNull(feed.ListenKeyStatus.CreatedAt, "憑證建立了,快照卻沒有建立時刻。");
                Assert.IsNull(feed.ListenKeyStatus.LastRenewedAt, "還沒續期過,卻已經有續期時刻。");

                await UserDataFixture.WaitUntilAsync(
                    () => feed.ListenKeyStatus.RenewalCount >= 2,
                    "續期成功了卻沒有被計數。");

                var status = feed.ListenKeyStatus;

                Assert.IsNotNull(status.LastRenewedAt, "續期成功了,快照卻沒有續期時刻。");
                Assert.AreEqual(0, status.ConsecutiveRenewalFailures, "全部成功,連續失敗次數應該是 0。");
                Assert.AreEqual(0, status.RebuildCount, "沒有收到 listenKeyExpired,不該有重建。");
            }
        }
    }

    [TestMethod]
    public async Task AFailedRenewalIsCountedAndSuccessClearsTheConsecutiveCount()
    {
        // 連續失敗次數是「還剩幾次機會」的唯一讀數。成功之後不歸零的話,它會一路累積成一個無意義的數字。
        // The consecutive count is the only reading of how many chances are left. Not clearing it on success
        // would let it accumulate into a number that means nothing.
        var failing = true;
        var connection = new FakeWebSocketConnection([]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50),
                ListenKeyRenewalRetryBackoffs = [],
            },
            keepAliveResponder: () => failing ? RenewalRejection() : UserDataSamples.ListenKeyResponse(Canary));

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                await UserDataFixture.WaitUntilAsync(
                    () => feed.ListenKeyStatus.ConsecutiveRenewalFailures >= 2,
                    "續期一直失敗,連續失敗次數卻沒有累加。");

                Assert.AreEqual(0, feed.ListenKeyStatus.RenewalCount, "失敗不該被算成成功。");

                failing = false;

                await UserDataFixture.WaitUntilAsync(
                    () => feed.ListenKeyStatus.RenewalCount >= 1,
                    "端點恢復之後續期應該成功。");

                Assert.AreEqual(
                    0,
                    feed.ListenKeyStatus.ConsecutiveRenewalFailures,
                    "成功一次之後,連續失敗次數應該歸零。");
            }
        }
    }

    [TestMethod]
    public async Task AnExpiredCredentialIsCountedAsARebuildAndStartsAFreshLifecycle()
    {
        // 重建之後那把憑證是新的,續期時刻與連續失敗次數都屬於上一把 —— 留著會讓「這把撐了多久」變成湊出來的數字。
        // The credential after a rebuild is a new one, and the renewal instant and failure count belong to the
        // previous key; keeping them makes "how long did this one last" a number nobody can trust.
        var first = new FakeWebSocketConnection([UserDataSamples.ListenKeyExpired(Canary)]);
        var second = new FakeWebSocketConnection([UserDataSamples.OrderNew]);

        var (feed, _, http) = UserDataFixture.Create(new FakeWebSocketConnectionFactory(first, second));

        using (http)
        {
            await using (feed)
            {
                var signals = await UserDataFixture.TakeAsync(feed.SubscribeResyncSignalsAsync(), 1);

                Assert.AreEqual(ResyncReason.StreamCredentialExpired, signals[0].GetValueOrThrow().Reason);

                await UserDataFixture.WaitUntilAsync(
                    () => feed.ListenKeyStatus.RebuildCount >= 1,
                    "憑證失效並重建了,快照卻沒有記下這次重建。");

                var status = feed.ListenKeyStatus;

                Assert.IsNotNull(status.CreatedAt, "重建之後應該有新那一把的建立時刻。");
                Assert.IsNull(status.LastRenewedAt, "新的憑證還沒有續期過,續期時刻應該是空的。");
                Assert.AreEqual(0, status.ConsecutiveRenewalFailures);
            }
        }
    }

    // ── 日誌 / Logging ────────────────────────────────────────────────

    [TestMethod]
    public async Task ASuccessfulRenewalIsLoggedWithoutTheCredential()
    {
        var loggers = new CollectingLoggerFactory();
        var connection = new FakeWebSocketConnection([]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions { ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50) },
            loggerFactory: loggers);

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                await UserDataFixture.WaitUntilAsync(
                    () => loggers.Messages.Any(message => message.Contains("續期成功", StringComparison.Ordinal)),
                    "續期成功了,日誌裡卻沒有這一行 —— 那正是 24 小時長跑時答不出問題的原因。");

                var renewals = loggers.Messages
                    .Where(message => message.Contains("續期成功", StringComparison.Ordinal))
                    .ToArray();

                // 第幾次、耗時、距上次多久,三個值都要在。少了「距上次多久」,續期的節奏就看不出來。
                // The count, the elapsed time, and the gap all have to be there. Without the gap the cadence of
                // the renewals cannot be read at all.
                Assert.Contains("累計第 1 次", renewals[0], StringComparison.Ordinal);
                Assert.Contains("ms", renewals[0], StringComparison.Ordinal);
                Assert.Contains("距上次建立或續期", renewals[0], StringComparison.Ordinal);

                AssertNoCredential(loggers);
            }
        }
    }

    [TestMethod]
    public async Task AFailedRenewalIsLoggedWithTheNeutralCodeAndTheConsecutiveFailureCount()
    {
        var loggers = new CollectingLoggerFactory();
        var connection = new FakeWebSocketConnection([]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50),
                ListenKeyRenewalRetryBackoffs = [],
            },
            keepAliveResponder: RenewalRejection,
            loggerFactory: loggers);

        using (http)
        {
            await using (feed)
            {
                // 續期失敗照舊出現在串流上 —— 日誌是補上去的,不是拿來取代它的。
                // The failure still reaches the stream: the log line is an addition, not a replacement.
                var items = await UserDataFixture.TakeAsync(feed.SubscribeOrderUpdatesAsync(), 1);

                Assert.IsTrue(items[0].IsFailure);

                var code = items[0].Error!.Code;

                await UserDataFixture.WaitUntilAsync(
                    () => loggers.Messages.Any(message => message.Contains("續期失敗", StringComparison.Ordinal)),
                    "續期失敗了,日誌裡卻沒有這一行。");

                var failure = loggers.Messages.First(message => message.Contains("續期失敗", StringComparison.Ordinal));

                Assert.Contains("連續第 1 次", failure, StringComparison.Ordinal);

                // 中立錯誤碼要在,交易所的回應原文不可以在 —— 這個端點的回應本體正常情況下就是憑證。
                // The neutral code belongs there and the exchange's own text does not: the body of this endpoint
                // is the credential in the normal case.
                Assert.Contains(code, failure, StringComparison.Ordinal);
                Assert.DoesNotContain("This listenKey does not exist", failure, StringComparison.Ordinal);

                AssertNoCredential(loggers);
            }
        }
    }

    [TestMethod]
    public async Task AnExpiredCredentialIsLoggedWithTheHistoryThatExplainsIt()
    {
        // 這一行就是 24 小時長跑缺的那一行:建立於何時、活了多久、最後一次成功續期是什麼時候、失敗過幾次。
        // This is the line the 24-hour run was missing: created when, alive how long, last renewed when, and how
        // many failures there were.
        var loggers = new CollectingLoggerFactory();
        var first = new FakeWebSocketConnection([UserDataSamples.ListenKeyExpired(Canary)]);
        var second = new FakeWebSocketConnection([UserDataSamples.OrderNew]);

        var (feed, _, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(first, second),
            loggerFactory: loggers);

        using (http)
        {
            await using (feed)
            {
                _ = await UserDataFixture.TakeAsync(feed.SubscribeResyncSignalsAsync(), 1);

                await UserDataFixture.WaitUntilAsync(
                    () => loggers.Messages.Any(message =>
                        message.Contains("listenKeyExpired", StringComparison.Ordinal)),
                    "收到 listenKeyExpired,日誌裡卻沒有那一行憑證履歷。");

                var expiry = loggers.Messages.First(message =>
                    message.Contains("listenKeyExpired", StringComparison.Ordinal));

                Assert.Contains("憑證建立於", expiry, StringComparison.Ordinal);
                Assert.Contains("已存活", expiry, StringComparison.Ordinal);
                Assert.Contains("累計成功續期 0 次", expiry, StringComparison.Ordinal);
                Assert.Contains("連續失敗 0 次", expiry, StringComparison.Ordinal);

                // 一次都沒續期成功過,就要寫明「沒發生過」,不能填一個看起來像真的的時刻。
                // Never renewed has to say so rather than show an instant that looks real.
                Assert.Contains("尚未發生", expiry, StringComparison.Ordinal);

                AssertNoCredential(loggers);
            }
        }
    }

    // ── 續期失敗的重試 / Retrying a failed renewal ─────────────────────

    [TestMethod]
    public async Task AFailedRenewalIsRetriedWithinTheSameRenewalPeriod()
    {
        // 沒有重試的話,30 分鐘一次的續期失敗之後,60 分鐘的有效期就只剩最後一次機會。
        // Without retries, a failed 30-minute renewal leaves a 60-minute credential exactly one more chance.
        var connection = new FakeWebSocketConnection([]);
        var backoff = TimeSpan.FromMilliseconds(30);

        var (feed, stub, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(300),
                ListenKeyRenewalRetryBackoffs = [backoff, backoff, backoff],
            },
            keepAliveResponder: RenewalRejection);

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                // 一個續期週期(300 ms)之內:排程那一次加上三次退避重試,共四次 PUT。
                // Within one renewal period of 300 ms: the scheduled attempt plus three backoff retries, four PUTs.
                await UserDataFixture.WaitUntilAsync(
                    () => stub.Requests.Count(request => request.Method == HttpMethod.Put) >= 4,
                    "續期失敗之後沒有重試,60 分鐘的有效期就只剩一次機會。");

                Assert.IsGreaterThanOrEqualTo(
                    4,
                    feed.ListenKeyStatus.ConsecutiveRenewalFailures,
                    "每一次嘗試都要計數,不是整輪只算一次。");
            }
        }
    }

    [TestMethod]
    public async Task ARetryNeverPushesTheNextScheduledRenewalBack()
    {
        // 退避若可以跨過下一個排程時刻,「續期愈失敗、下一次愈晚」—— 方向剛好相反。
        // A backoff allowed to cross the next scheduled instant would make the next renewal later the more
        // renewals failed, which is exactly backwards.
        var connection = new FakeWebSocketConnection([]);

        var (feed, stub, http) = UserDataFixture.Create(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceUserDataStreamOptions
            {
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(100),

                // 退避遠長於續期週期,所以它一次都不該被採用。
                // A backoff far longer than the renewal period, so it must never be taken.
                ListenKeyRenewalRetryBackoffs = [TimeSpan.FromSeconds(10)],
            },
            keepAliveResponder: RenewalRejection);

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                // 十秒的退避若被採用,這裡最多只看得到一兩次 PUT。
                // Were the ten-second backoff taken, only one or two PUTs would be visible here.
                await UserDataFixture.WaitUntilAsync(
                    () => stub.Requests.Count(request => request.Method == HttpMethod.Put) >= 5,
                    "排程續期被退避推遲了 —— 退避不可以跨過下一個排程時刻。");
            }
        }
    }

    /// <summary>
    /// 斷言收集到的日誌裡一個憑證字元都沒有。
    /// Asserts that not one character of the credential reached a collected log line.
    /// </summary>
    /// <param name="loggers">收集日誌的工廠。The collecting factory.</param>
    private static void AssertNoCredential(CollectingLoggerFactory loggers)
    {
        var combined = string.Join('\n', loggers.Messages);

        Assert.DoesNotContain(
            Canary,
            combined,
            StringComparison.OrdinalIgnoreCase,
            $"串流憑證出現在日誌裡了。以下是這次收集到的全部日誌:\n{combined}");
    }
}
