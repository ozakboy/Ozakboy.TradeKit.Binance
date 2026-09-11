using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 串流憑證不得外流:跑完整條生命週期,把過程中產生的每一段文字串起來,斷言那把憑證一次都沒出現。
/// The stream credential must not escape: the whole lifecycle is exercised, every piece of text it produced is
/// concatenated, and the credential is asserted never to appear in it.
/// </summary>
/// <remarks>
/// <para>
/// listenKey 能連上這個帳戶的私有資料。它進了日誌,就等於把帳戶的私有資料閘門寫在一個會被複製、
/// 會被轉寄、會被留在磁碟上好幾個月的地方。所以這一條不是「順便檢查一下」,而是這一層的驗收條件。
/// A listenKey reaches this account's private data. Once it is in a log, the gate to that data has been written
/// somewhere that gets copied, forwarded, and left on disk for months. This is therefore not a nice-to-have
/// check but the acceptance condition for this layer.
/// </para>
/// <para>
/// <b>這條測試被故意弄壞驗證過。</b> 把 <c>BinanceUserDataReader</c> 判「缺少事件時間」的那一行改成
/// 在錯誤訊息裡附上訊息原文(而原文正是 <see cref="UserDataSamples.ListenKeyExpiredWithoutEventTime"/>,
/// 裡面就有憑證),這條測試會紅;改回來就綠。一條從來沒紅過的安全測試,和沒有測試沒有分別。
/// <b>This test has been verified by deliberately breaking it.</b> Changing the missing-event-time branch of
/// <c>BinanceUserDataReader</c> to attach the raw frame to its message — and that frame,
/// <see cref="UserDataSamples.ListenKeyExpiredWithoutEventTime"/>, carries the credential — turns this test
/// red; reverting turns it green. A security test that has never failed is indistinguishable from no test.
/// </para>
/// <para>
/// 加入心跳回覆之後又弄壞驗證了一次:把 <c>BinanceUserDataReader</c> 認出指令回覆的那一支,改成判失敗並把
/// <c>root.GetRawText()</c> 接進錯誤訊息。這條測試立刻變紅,收集到的文字裡 <c>id</c> 為 1、7、42 的三則心跳回覆
/// 與 <c>id</c> 為 9 的被拒回覆,每一則都把 canary 帶進了 <see cref="Error.Message"/>;改回來之後恢復綠燈。
/// It was broken on purpose once more after heartbeat replies were added: the branch of
/// <c>BinanceUserDataReader</c> that recognises command replies was changed to fail with <c>root.GetRawText()</c>
/// in the message. This test went red at once, with the canary carried into <see cref="Error.Message"/> by every
/// heartbeat reply — ids 1, 7, and 42 — and by the rejection with id 9; reverting restored green.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceUserDataCredentialLeakTests
{
    /// <summary>
    /// 這條測試用的憑證。夠獨特,所以「有沒有出現」是個乾淨的問題。
    /// The credential this test uses: distinctive enough that "did it appear" is a clean question.
    /// </summary>
    private const string Canary = UserDataSamples.ListenKey;

    [TestMethod]
    public async Task TheCredentialNeverAppearsInAnythingTheStreamProduces()
    {
        // 一條會產生各種失敗的劇本:讀不懂的訊息、非文字訊息、狀態對不上的委託、缺欄位的憑證失效事件
        // (它的原文裡就有憑證),最後斷線重連。
        // A script that produces every kind of failure: an unreadable frame, a binary frame, an order with an
        // unmappable status, a credential-expired frame missing a field — whose raw text carries the
        // credential — and finally a drop and a reconnect.
        //
        // 心跳回覆也在劇本裡:Testnet 實測,LIST_SUBSCRIPTIONS 的回覆就是 {"result":["<listenKey>"],"id":N},
        // 預設設定下每 30 秒一則。它是這條串流上出現頻率最高的「帶憑證的訊息」,比 listenKeyExpired 高得多。
        // Heartbeat replies are in the script too. Measured on the testnet, the answer to LIST_SUBSCRIPTIONS is
        // {"result":["<listenKey>"],"id":N}, one every 30 seconds by default — by far the most frequent
        // credential-bearing frame on this stream, much more common than listenKeyExpired.
        var first = new FakeWebSocketConnection(
            [
                UserDataSamples.HeartbeatReply(Canary, 1),
                "}{ not json",
                FakeWebSocketFrame.Binary(UserDataSamples.OrderNew),
                UserDataSamples.HeartbeatReply(Canary, 42),
                UserDataSamples.OrderUnknownStatus,
                UserDataSamples.HeartbeatRejection(Canary),
                UserDataSamples.ListenKeyExpiredWithoutEventTime(Canary),
                UserDataSamples.OrderNew,
            ],
            closeWhenScriptEnds: true);

        // 重連之後才送出真正的憑證失效事件,逼出重建憑證那一段。
        // The real credential-expired event comes after the reconnect, which forces the rebuild path.
        var second = new FakeWebSocketConnection([
            UserDataSamples.HeartbeatReply(Canary, 7),
            UserDataSamples.AccountUpdate,
            UserDataSamples.ListenKeyExpired(Canary),
        ]);

        var third = new FakeWebSocketConnection([UserDataSamples.MarginCall]);
        var factory = new FakeWebSocketConnectionFactory(first, second, third);

        var (feed, stub, http) = UserDataFixture.Create(
            factory,
            new BinanceUserDataStreamOptions
            {
                // 續期也要跑到,而且讓它失敗 —— 失敗的回應會經過錯誤對映,那是另一條可能外流的路。
                // The renewal has to run and to fail: a failed response goes through the error mapping, which
                // is another route by which the credential could escape.
                ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50),

                // 心跳也要真的送出去 —— 送出的內容一樣算進「這條串流產生的文字」。
                // Heartbeats have to go out for real as well: what is sent counts as text this stream produced.
                KeepAliveInterval = TimeSpan.FromMilliseconds(50),
            },
            Canary,
            keepAliveResponder: static () =>
                $$"""{"code":-1125,"msg":"This listenKey does not exist.","listenKey":"{{Canary}}"}""");

        var texts = new ConcurrentBag<string>();

        using (http)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var readers = new[]
            {
                CollectAsync(feed.SubscribeOrderUpdatesAsync(cts.Token), texts, static order => order.ClientOrderId),
                CollectAsync(feed.SubscribeTradeUpdatesAsync(cts.Token), texts, static trade => trade.TradeId),
                CollectAsync(feed.SubscribeAccountUpdatesAsync(cts.Token), texts, static update => update.RawReason),
                CollectAsync(feed.SubscribeMarginCallsAsync(cts.Token), texts, static call => call.Positions.Count.ToString(CultureInfo.InvariantCulture)),
                CollectAsync(feed.SubscribeResyncSignalsAsync(cts.Token), texts, static signal => signal.Detail),
            };

            // 讓整條劇本跑完:斷線、重連、憑證失效、重建憑證、續期失敗。
            // Let the whole script play out: the drop, the reconnect, the expiry, the rebuild, the failed
            // renewal.
            await UserDataFixture.WaitUntilAsync(
                () => stub.Requests.Count(request => request.Method == HttpMethod.Post) >= 2
                    && stub.Requests.Any(request => request.Method == HttpMethod.Put)
                    && factory.Created.Any(connection => connection.Sent.Count > 0),
                "劇本沒有跑到重建憑證、續期與心跳,這條測試就沒有驗到那幾段路。");

            await feed.DisposeAsync();
            await cts.CancelAsync();

            foreach (var reader in readers)
            {
                await reader;
            }

            // 送出去的請求本身也算 —— 憑證若出現在查詢字串裡,同樣是寫進每一份請求紀錄。
            // The requests themselves count too: a credential in a query string is written into every request
            // log just the same.
            foreach (var request in stub.Requests)
            {
                texts.Add(request.RequestUri?.ToString() ?? string.Empty);
            }

            // WebSocket 上送出去的也算:心跳訊息若把回覆裡的東西帶回去,那同樣是一條外流的路。
            // What goes out over the WebSocket counts too: a heartbeat echoing anything from a reply would be
            // another route out.
            foreach (var connection in factory.Created)
            {
                foreach (var sent in connection.Sent)
                {
                    texts.Add(sent);
                }
            }
        }

        // 0.1.1 起憑證放在撥號位址的 listenKey= 查詢參數裡 —— 撥號位址是它<b>唯一</b>該出現的地方,
        // 所以這裡刻意不收集它。先確認 canary 真的就是撥號用的那把憑證,否則「沒出現」什麼也證明不了。
        // Since 0.1.1 the credential sits in the listenKey= query of the dialled address, the one place it
        // belongs, so that address is deliberately not collected. First confirm the canary really is the
        // credential being dialled with; otherwise "it never appeared" proves nothing.
        Assert.Contains(
            Canary,
            factory.Created[0].ConnectedUri?.OriginalString ?? string.Empty,
            StringComparison.Ordinal,
            "canary 不是撥號時用的憑證,這條測試驗不到任何東西。");

        var combined = string.Join('\n', texts);

        Assert.DoesNotContain(
            Canary,
            combined,
            StringComparison.OrdinalIgnoreCase,
            $"串流憑證外流了。以下是這次收集到的全部文字:\n{combined}");
    }

    [TestMethod]
    public async Task AnUnreadableCredentialResponseIsReportedWithoutQuotingTheBody()
    {
        // 這個端點的回應本體在正常情況下就是憑證。把讀不懂的本體截一段進錯誤,
        // 在別的端點上是好習慣,在這裡是把憑證寫進日誌。
        // The body of this endpoint is the credential in the normal case. Truncating an unreadable body into
        // the error is good practice elsewhere and is writing the credential into a log here.
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();

        var stub = StubHttpMessageHandler.Json($$"""{"notTheExpectedField":"{{Canary}}"}""");
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
                Assert.IsTrue(items[0].IsFailure);
                Assert.DoesNotContain(Canary, Describe(items[0].Error!), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static async Task CollectAsync<T>(
        IAsyncEnumerable<Result<T>> stream,
        ConcurrentBag<string> texts,
        Func<T, string?> describeValue)
        where T : class
    {
        try
        {
            await foreach (var item in stream)
            {
                texts.Add(item.TryGetValue(out var value)
                    ? describeValue(value) ?? string.Empty
                    : Describe(item.Error!));
            }
        }
        catch (OperationCanceledException)
        {
            // 收尾時的取消是預期內的。
            // A cancellation during shutdown is expected.
        }
    }

    /// <summary>
    /// 把一筆錯誤的每一個會變成字串的部分都攤平。
    /// Flattens every part of an error that can become a string.
    /// </summary>
    /// <param name="error">要攤平的錯誤。The error to flatten.</param>
    /// <returns>攤平後的文字。The flattened text.</returns>
    /// <remarks>
    /// 代碼、訊息、診斷資料、例外訊息都要看。只檢查 <see cref="Error.Message"/> 的話,
    /// 一個放進 <see cref="Error.Data"/> 的憑證就會安然通過 —— 而那正是最容易寫出來的那種外流。
    /// The code, the message, the diagnostic data, and the exception text all count. Checking only
    /// <see cref="Error.Message"/> would let a credential placed in <see cref="Error.Data"/> straight through,
    /// and that is the easiest kind of leak to write by accident.
    /// </remarks>
    private static string Describe(Error error)
    {
        var builder = new StringBuilder()
            .Append(error.Code)
            .Append('|')
            .Append(error.Message)
            .Append('|')
            .Append(error.Exception?.ToString() ?? string.Empty);

        if (error.Data is not null)
        {
            foreach (var (key, value) in error.Data)
            {
                builder.Append('|').Append(key).Append('=').Append(value);
            }
        }

        return builder.ToString();
    }
}
