using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 實際連上幣安 Testnet 使用者資料串流的整合測試。需要真實憑證,會實際送單與撤單。
/// Integration tests that really connect to the Binance testnet user data stream. They need real credentials
/// and really place and cancel orders.
/// </summary>
/// <remarks>
/// <para>
/// 這幾條會連線,因此不在一般測試回合中執行:以
/// <c>dotnet test --filter "TestCategory=Testnet"</c> 明確指定才會跑。
/// 憑證一律從環境變數 <c>BINANCE_TESTNET_API_KEY</c> 與 <c>BINANCE_TESTNET_API_SECRET</c> 讀取,
/// 絕不寫進任何檔案;缺憑證時判定為 Inconclusive 而不是通過 ——
/// 一個「沒跑但綠燈」的測試會讓「帳戶串流驗過了」這件事在完全沒有驗過的情況下被記成已完成。
/// These reach the network and so do not run in an ordinary pass; select them with
/// <c>dotnet test --filter "TestCategory=Testnet"</c>. Credentials always come from the
/// <c>BINANCE_TESTNET_API_KEY</c> and <c>BINANCE_TESTNET_API_SECRET</c> environment variables and never from a
/// file. Without them the tests report Inconclusive rather than passing: a green test that never ran would
/// record "the account stream was verified" when nothing of the sort happened.
/// </para>
/// <para>
/// <b>單元測試驗不到的,正是這裡要驗的。</b> 單元測試用的樣本是依官方文件組出來的,它只能證明
/// 「對映照文件寫對了」;這裡證明的是「文件說的就是交易所真的送出來的」。少了這一半,
/// 一個文件與現實不符的欄位會一路綠燈到實盤。
/// <b>What the unit tests cannot check is exactly what these are for.</b> The unit test samples are built from
/// the documentation and can only prove that the mapping follows it; these prove that what the documentation
/// describes is what the exchange actually sends. Without this half, a field where the documentation and
/// reality disagree stays green all the way to live trading.
/// </para>
/// <para>
/// <b>測試單的紀律。</b> 每一張測試單都掛在遠離標記價的位置(買單在標記價下方約 4%)並使用 GTC,
/// 因此不會成交;每一張都在 <c>finally</c> 裡撤掉,即使斷言失敗也一樣;串流憑證同樣在 <c>finally</c> 裡
/// 關掉。所有等待都有上限,沒有任何一段會無限等 —— 交易所安靜的時候,掛住的測試比紅燈難查得多。
/// <b>Test-order discipline.</b> Every test order rests about 4% away from the mark with GTC so that it cannot
/// fill; every one is cancelled in a <c>finally</c> block even when an assertion fails, and the stream
/// credential is closed in a <c>finally</c> too. Every wait has a ceiling and nothing waits indefinitely: when
/// the exchange is quiet, a hung test is harder to diagnose than a red one.
/// </para>
/// <para>
/// 容器一律以 <c>await using</c> 收尾。<see cref="BinanceUserDataFeed"/> 只實作
/// <see cref="IAsyncDisposable"/>(關閉憑證要打一次 <c>DELETE</c>,那是非同步的),
/// <c>ServiceProvider.Dispose()</c> 遇到這種服務會直接擲例外。
/// The container is always disposed with <c>await using</c>: <see cref="BinanceUserDataFeed"/> implements only
/// <see cref="IAsyncDisposable"/> — closing the credential is an asynchronous <c>DELETE</c> — and
/// <c>ServiceProvider.Dispose()</c> throws outright on such a service.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class BinanceUserDataTestnetTests
{
    private const string ApiKeyVariable = "BINANCE_TESTNET_API_KEY";
    private const string SecretVariable = "BINANCE_TESTNET_API_SECRET";

    /// <summary>
    /// 測試單的用戶端訂單編號前綴。留下孤兒單時,這是唯一能認出來源的線索。
    /// The client order id prefix for test orders: the only clue to their origin should one be left behind.
    /// </summary>
    private const string TestOrderPrefix = "pulsetrade-test-";

    /// <summary>測試用的交易對。The symbol under test.</summary>
    private const string Symbol = "BTCUSDT";

    /// <summary>
    /// 掛單價相對標記價的比例。Testnet 上 BTCUSDT 的 <c>PERCENT_PRICE</c> 篩選器 <c>multiplierDown</c>
    /// 是 0.95,所以 0.96 是「掛得上、又絕不會成交」的位置。
    /// The resting price as a fraction of the mark. The <c>PERCENT_PRICE</c> filter on testnet BTCUSDT has a
    /// <c>multiplierDown</c> of 0.95, so 0.96 both passes the filter and cannot possibly fill.
    /// </summary>
    private const decimal RestingPriceRatio = 0.96m;

    /// <summary>
    /// 等待一則事件的上限。帳戶事件是交易所推的,推不推得出來不是這裡能控制的,所以一定要有上限。
    /// The ceiling on waiting for one event. Account events are pushed by the exchange and whether one arrives
    /// is not under this test's control, so the wait is always bounded.
    /// </summary>
    /// <remarks>
    /// 兩分鐘看起來很長,實測有必要。Testnet 在忙的時候、以及一把 listenKey 才剛被 <c>DELETE</c> 又立刻
    /// 重新 <c>POST</c> 出來的那段時間裡,推送會明顯延遲。這個上限不是效能指標,只是「別讓測試無限等」。
    /// Two minutes looks generous and measurement says it is needed: pushes are noticeably delayed when the
    /// testnet is busy, and in the window after a listenKey has just been <c>DELETE</c>d and immediately
    /// <c>POST</c>ed again. The ceiling is not a performance target, only a guarantee that nothing waits for
    /// ever.
    /// </remarks>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(120);

    private static bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SecretVariable));

    /// <summary>
    /// 一條串流走完整個生命週期:建立憑證、連上、收到委託成立與撤單的回報、關閉憑證。
    /// One stream through its whole lifecycle: create the credential, connect, receive the acceptance and the
    /// cancellation of an order, and close the credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>整個帳戶只有一把 listenKey,所以這裡只開一條串流。</b> 拆成兩條測試各自建立與關閉憑證,
    /// 第二條就會在第一條剛 <c>DELETE</c> 過的時間點重新 <c>POST</c> ——
    /// 實測那樣會拿到一條「連得上、訂閱受理、一筆事件都沒有」的串流,然後在逾時之後紅燈,
    /// 而紅燈的原因看起來會像套件壞了。憑證的建立與關閉在這一條裡各發生一次,就沒有那個時間窗。
    /// <b>The account holds one listenKey, so this opens one stream.</b> Splitting it into two tests that each
    /// create and close a credential makes the second <c>POST</c> land right after the first <c>DELETE</c> —
    /// measured, that yields a stream that connects, is accepted, and delivers nothing, going red at the
    /// timeout for a reason that reads as a broken package. Creating and closing once inside this one test
    /// removes that window.
    /// </para>
    /// <para>
    /// 對帳訊號同一條串流一起訂,收尾時檢查:健康的串流不該要求對帳,出現就代表中間斷過。
    /// The reconciliation signals are subscribed on the same stream and checked at the end: a healthy stream
    /// demands no reconciliation, and one that does was interrupted somewhere in the middle.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("Testnet")]
    public async Task RunsTheWholeCredentialAndOrderLifecycleOnOneStream()
    {
        await using var provider = BuildOrSkip();

        var client = provider!.GetRequiredService<BinanceFuturesClient>();
        var feed = provider!.GetRequiredService<BinanceUserDataFeed>();

        await using (feed)
        {
            // 這個權杖同時是列舉器的取消來源與等待事件的看門狗:等不到事件時把它取消,
            // 列舉就會乾淨地結束,測試立刻以一句說得出原因的失敗收尾。
            // 若改成用 WaitAsync 之類的方式逾時,推進會留在進行中,而對一個推進還沒完成的非同步列舉器
            // 呼叫 DisposeAsync 會擲 NotSupportedException —— 那個例外會把真正的失敗原因整個蓋掉。
            // This token is both the enumerator's cancellation source and the watchdog for waiting on events:
            // cancelling it when nothing arrives ends the enumeration cleanly and fails the test at once with a
            // reason. Timing out with something like WaitAsync instead would leave the advance in flight, and
            // disposing an async enumerator mid-advance throws NotSupportedException, which buries the real
            // cause entirely.
            using var cts = new CancellationTokenSource();

            var resyncs = new List<Result<ResyncRequired>>();
            var watching = WatchResyncSignalsAsync(feed, resyncs, cts.Token);

            var orders = feed.SubscribeOrderUpdatesAsync(cts.Token).GetAsyncEnumerator(cts.Token);

            // 三個動作的順序都是必要的,少一個就會等一則永遠不會來的事件。
            //
            // 一、MoveNextAsync 觸發列舉開始,訂閱者在它的同步階段就登記完成 —— 訂閱之前發生的事件
            //     不補送,所以登記必須早於任何會產生事件的動作。
            // 二、StartAsync 等連線<b>真的</b>就緒。只登記還不夠:連線還在握手的那幾百毫秒裡,
            //     交易所送出的事件沒有任何一條連線收得到,而它不補送。這一步曾經漏掉,症狀是
            //     「委託以 REST 查得到、確實掛在簿上,串流卻兩分鐘沒有一則事件」,看起來完全像
            //     交易所停擺 —— 實測用同一支探針只改這一點就能開關這個現象。
            // 三、連線就緒之後才下單。
            //
            // Each of the three steps is load-bearing. MoveNextAsync begins the enumeration and registers the
            // subscriber in its synchronous part, which must happen before anything that produces events since
            // nothing earlier is replayed. StartAsync then waits for the connection to actually be live:
            // registration alone is not enough, because during the handshake no connection receives what the
            // exchange sends, and it does not replay. Omitting that step presents as "REST confirms the order
            // resting on the book while the stream stays silent for two minutes" — indistinguishable from an
            // exchange outage, and reproducible on demand by toggling this one wait.
            var firstEvent = orders.MoveNextAsync();

            var ready = await feed.StartAsync(cts.Token);

            Assert.IsTrue(ready.IsSuccess, ready.Error?.Message);

            var (price, quantity) = await BuildRestingOrderAsync(client);
            var clientOrderId = NewClientOrderId("uds");

            var placed = await client.PlaceOrderAsync(
                new OrderRequest
                {
                    Symbol = Symbol,
                    Side = OrderSide.Buy,
                    OrderType = OrderType.Limit,
                    Quantity = quantity,
                    Price = price,
                    TimeInForce = TimeInForce.GoodTilCanceled,
                    ClientOrderId = clientOrderId,
                },
                cts.Token);

            try
            {
                Assert.IsTrue(placed.IsSuccess, placed.Error?.Message);

                var accepted = await AwaitOrderAsync(orders, firstEvent, clientOrderId, OrderStatus.New, client, cts);

                Assert.AreEqual(Symbol, accepted.Symbol);
                Assert.AreEqual(OrderSide.Buy, accepted.Side);
                Assert.AreEqual(OrderType.Limit, accepted.OrderType);
                Assert.AreEqual(quantity, accepted.Quantity);
                Assert.AreEqual(price, accepted.Price);
                Assert.AreEqual(0m, accepted.FilledQuantity, "測試單成交了,掛單價離標記價不夠遠。");
                Assert.IsNotNull(accepted.ExchangeOrderId);

                var cancelled = await client.CancelOrderAsync(
                    Symbol,
                    OrderIdentifier.FromClientId(clientOrderId),
                    cts.Token);

                Assert.IsTrue(cancelled.IsSuccess, cancelled.Error?.Message);

                var cancelEvent = await AwaitOrderAsync(orders, null, clientOrderId, OrderStatus.Canceled, client, cts);

                Assert.AreEqual(OrderStatus.Canceled, cancelEvent.Status);
                Assert.AreEqual(accepted.ExchangeOrderId, cancelEvent.ExchangeOrderId);
            }
            finally
            {
                // 斷言失敗也要撤。留下孤兒單會污染後續測試,而且在 Testnet 上會一直掛著。
                // Cancelled even when an assertion fails: a stray order pollutes every later test and rests on
                // the testnet indefinitely.
                _ = await client.CancelOrderAsync(Symbol, OrderIdentifier.FromClientId(clientOrderId));

                await orders.DisposeAsync();
                await cts.CancelAsync();
                await watching;
            }

            foreach (var item in resyncs)
            {
                Assert.IsTrue(
                    item.IsFailure,
                    "串流從頭到尾都沒斷過,卻送出了對帳訊號。");

                Assert.IsTrue(item.Error!.IsTransient, item.Error!.Message);
            }
        }

        // 離開這個區塊時 DisposeAsync 會對 listenKey 端點送出 DELETE。
        // Leaving this block makes DisposeAsync send the DELETE to the listenKey endpoint.
    }

    /// <summary>
    /// 在背景把對帳訊號收起來。
    /// Collects the reconciliation signals in the background.
    /// </summary>
    /// <param name="feed">串流。The stream.</param>
    /// <param name="into">收集到哪裡。Where to collect them.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>收集的工作。The collecting task.</returns>
    private static async Task WatchResyncSignalsAsync(
        BinanceUserDataFeed feed,
        List<Result<ResyncRequired>> into,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        await foreach (var item in feed.SubscribeResyncSignalsAsync(cancellationToken))
        {
            lock (into)
            {
                into.Add(item);
            }
        }
    }

    /// <summary>
    /// 等到指定的委託出現在串流上、且狀態符合為止。
    /// Waits for the named order to appear on the stream with the expected status.
    /// </summary>
    /// <param name="orders">委託串流的列舉器。The order stream's enumerator.</param>
    /// <param name="pending">
    /// 已經發動但還沒等到的第一次推進;沒有時傳 <see langword="null"/>。
    /// A first advance already issued but not yet awaited, or <see langword="null"/>.
    /// </param>
    /// <param name="clientOrderId">要等的委託編號。The client order id to wait for.</param>
    /// <param name="expected">要等的狀態。The status to wait for.</param>
    /// <param name="client">交易用戶端,失敗時用來查這張單到底在不在。The trading client, used on failure to see whether the order exists at all.</param>
    /// <param name="watchdog">列舉器的取消來源,兼作等待的看門狗。The enumerator's cancellation source, doubling as the watchdog.</param>
    /// <returns>符合條件的委託。The matching order.</returns>
    /// <remarks>
    /// <para>
    /// 同一條串流上會有別人的委託(手動下的、其他測試留下的),所以一定要比對編號;
    /// 只看「下一則」會在帳戶有其他活動時隨機失敗,而那種失敗看起來像套件壞了。
    /// Other orders appear on the same stream — placed by hand, or left by another test — so the id has to be
    /// matched. Taking "the next one" fails at random whenever the account is otherwise busy, and that failure
    /// looks like a broken package.
    /// </para>
    /// <para>
    /// <b>每一次 <c>MoveNextAsync</c> 都等到它完成才離開這個方法。</b> 非同步列舉器在推進還沒完成的時候
    /// 被 <c>DisposeAsync</c>,擲的是 <c>NotSupportedException</c> —— 而那個例外會蓋掉真正的失敗原因,
    /// 讓「等不到事件」看起來像一個莫名其妙的不支援錯誤。等待的上限由列舉器自己的取消權杖負責。
    /// <b>Every <c>MoveNextAsync</c> is awaited to completion before this method returns.</b> Disposing an
    /// async enumerator while an advance is still in flight throws <c>NotSupportedException</c>, and that
    /// exception hides the real failure, making "no event arrived" look like an inexplicable unsupported
    /// operation. The enumerator's own cancellation token bounds the wait.
    /// </para>
    /// </remarks>
    private static async Task<Order> AwaitOrderAsync(
        IAsyncEnumerator<Result<Order>> orders,
        ValueTask<bool>? pending,
        string clientOrderId,
        OrderStatus expected,
        BinanceFuturesClient client,
        CancellationTokenSource watchdog)
    {
        var advance = pending;

        watchdog.CancelAfter(EventTimeout);

        while (true)
        {
            var moved = advance ?? orders.MoveNextAsync();

            advance = null;

            if (!await moved)
            {
                break;
            }

            var item = orders.Current;

            if (!item.TryGetValue(out var order))
            {
                // 暫時性的失敗(斷線、缺口)不該讓測試紅,它本來就會重連;
                // 非暫時性的才是「這條串流結束了」。
                // A transient failure — a drop, a gap — must not fail the test, since it reconnects by itself;
                // a non-transient one means the stream has ended.
                Assert.IsTrue(item.Error!.IsTransient, item.Error!.Message);

                continue;
            }

            if (string.Equals(order.ClientOrderId, clientOrderId, StringComparison.Ordinal)
                && order.Status == expected)
            {
                // 等到了就把看門狗收回去,下一段等待再重新上緊。
                // The watchdog is stood down once the event arrives and rearmed for the next wait.
                watchdog.CancelAfter(Timeout.InfiniteTimeSpan);

                return order;
            }
        }

        // 失敗訊息要說得出「單到底送出去了沒」。少了這一句,「串流沒推」和「單根本沒進交易所」
        // 看起來一模一樣,而那是兩個完全不同的問題。
        // The failure has to say whether the order actually reached the exchange. Without it, "the stream did
        // not push" and "the order never got there" look identical, and they are entirely different problems.
        var open = await client.GetOpenOrdersAsync(Symbol);
        var resting = open.TryGetValue(out var orderList)
            && orderList.Any(candidate => string.Equals(candidate.ClientOrderId, clientOrderId, StringComparison.Ordinal));

        Assert.Fail(
            $"等了 {EventTimeout} 仍未在串流上看到委託「{clientOrderId}」進入 {expected};"
            + $"以 REST 查詢,這張單目前{(resting ? "還掛在簿上" : "不在掛單清單裡")},"
            + "所以問題出在串流推送這一側而不是下單那一側。");

        throw new InvalidOperationException("unreachable");
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddBinanceFutures(options =>
        {
            options.Environment = BinanceEnvironment.Testnet;
            options.ApiKey = Environment.GetEnvironmentVariable(ApiKeyVariable)!;
            options.SecretKey = Environment.GetEnvironmentVariable(SecretVariable)!;
        });

        services.AddBinanceUserData();

        return services.BuildServiceProvider();
    }

    private static ServiceProvider? BuildOrSkip()
    {
        if (!HasCredentials)
        {
            Assert.Inconclusive(
                $"未設定 {ApiKeyVariable} 與 {SecretVariable},略過需要真實憑證的整合測試。Skipped: {ApiKeyVariable} and {SecretVariable} are not set.");

            return null;
        }

        return Build();
    }

    /// <summary>
    /// 取得 Testnet 的標記價。這是公開端點,不需要簽章。
    /// Reads the testnet mark price from the public, unsigned endpoint.
    /// </summary>
    /// <returns>標記價。The mark price.</returns>
    private static async Task<decimal> GetMarkPriceAsync()
    {
        using var http = new HttpClient
        {
            BaseAddress = BinanceEndpoints.Testnet.RestBaseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };

        var body = await http.GetStringAsync($"fapi/v1/premiumIndex?symbol={Symbol}");

        using var document = JsonDocument.Parse(body);

        return decimal.Parse(
            document.RootElement.GetProperty("markPrice").GetString()!,
            NumberStyles.Number,
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 算出一張「掛得上、但不會成交」的買單。
    /// Builds a buy order that rests without filling.
    /// </summary>
    /// <param name="client">交易用戶端。The trading client.</param>
    /// <returns>掛單價與數量。The price and quantity.</returns>
    private static async Task<(decimal Price, decimal Quantity)> BuildRestingOrderAsync(BinanceFuturesClient client)
    {
        var symbol = (await client.GetSymbolAsync(Symbol)).GetValueOrThrow();
        var mark = await GetMarkPriceAsync();

        Assert.IsGreaterThan(0m, mark, "取不到標記價,無法算出安全的掛單價。");

        var price = symbol.NormalizePrice(mark * RestingPriceRatio, PriceRounding.Down).GetValueOrThrow();
        var quantity = symbol.GetMinimumQuantity(price).GetValueOrThrow();

        return (price, quantity);
    }

    /// <summary>
    /// 產生一個測試單的用戶端訂單編號。
    /// Builds a client order id for a test order.
    /// </summary>
    /// <param name="suffix">後綴。The suffix.</param>
    /// <returns>編號。The id.</returns>
    private static string NewClientOrderId(string suffix)
    {
        var id = TestOrderPrefix
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            + "-"
            + suffix;

        Assert.IsTrue(
            BinanceClientOrderId.IsValid(id),
            $"測試單的編號「{id}」共 {id.Length} 個字元,超過幣安的 {BinanceClientOrderId.MaxLength} 字上限,請縮短後綴。");

        return id;
    }
}
