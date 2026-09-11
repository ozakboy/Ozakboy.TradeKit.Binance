using System.Net;

using Ozakboy.TradeKit.Binance.MarketData;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 行情來源的端到端行為。串流一律走假的連線工廠,完全不碰網路。
/// End-to-end behaviour of the market data feed. Streams always run through a fake connection factory and
/// never touch the network.
/// </summary>
[TestClass]
public sealed class BinanceMarketDataFeedTests
{
    private static readonly string[] Btc = ["BTCUSDT"];

    private static readonly string[] Eth = ["ETHUSDT"];

    private const string SubscribeBtcKline1m =
        """{"method":"SUBSCRIBE","params":["btcusdt@kline_1m"],"id":1}""";

    // ── 歷史 K 線(REST) ──────────────────────────────────────────────
    // ── Historical klines over REST ───────────────────────────────────

    [TestMethod]
    public async Task HistoricalKlinesAreParsedIntoTheAbstraction()
    {
        var (feed, _, http) = CreateRest(StubHttpMessageHandler.Json(MarketDataSamples.RestKlines));

        using (http)
        {
            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
            });

            Assert.IsTrue(result.TryGetValue(out var candles), result.Error?.Message);
            Assert.HasCount(2, candles);
            Assert.AreEqual(77322.40m, candles[0].Open);
        }
    }

    [TestMethod]
    public async Task TheStillTickingLastCandleIsNotReportedAsClosed()
    {
        // REST 沒有收盤旗標,時鐘設在實際呼叫的那一刻,最後一根就該是未收盤的。
        // The REST response has no closed flag; with the clock at the moment of the real call, the last candle
        // must come back open.
        var (feed, _, http) = CreateRest(StubHttpMessageHandler.Json(MarketDataSamples.RestKlines));

        using (http)
        {
            var candles = (await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
            })).GetValueOrThrow();

            Assert.IsTrue(candles[0].IsClosed);
            Assert.IsFalse(candles[1].IsClosed);
        }
    }

    [TestMethod]
    public async Task TheEndTimeIsMovedBackOneMillisecondToKeepTheRangeHalfOpen()
    {
        // 幣安的 endTime 含端點,KlineQuery 的區間是左閉右開。不減這一毫秒,分頁抓歷史時
        // 每一頁的最後一根都會和下一頁的第一根重複,而重複的 K 線在指標裡是一次不存在的價格變動。
        // The Binance endTime is inclusive while a KlineQuery range is half-open. Without the millisecond,
        // paging repeats the last candle of each page as the first of the next, and a duplicate reads to an
        // indicator as a price move that never happened.
        var (feed, stub, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(1789110000000L);
            var end = DateTimeOffset.FromUnixTimeMilliseconds(1789110300000L);

            _ = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.FifteenMinutes,
                StartTime = start,
                EndTime = end,
                Limit = 10,
            });

            var request = stub.LastRequest;

            Assert.AreEqual("BTCUSDT", request.Parameter("symbol"));
            Assert.AreEqual("15m", request.Parameter("interval"));
            Assert.AreEqual("1789110000000", request.Parameter("startTime"));
            Assert.AreEqual("1789110299999", request.Parameter("endTime"));
            Assert.AreEqual("10", request.Parameter("limit"));
        }
    }

    [TestMethod]
    public async Task TheRequestDeclaresTheWeightBandOfTheRequestedCount()
    {
        var (feed, stub, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            _ = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
                Limit = 1000,
            });

            Assert.AreEqual(BinanceRequestWeights.Klines(1000), stub.LastRequest.Weight);
        }
    }

    [TestMethod]
    public async Task AQueryWithoutALimitDeclaresTheWeightOfTheExchangeDefault()
    {
        // 沒宣告權重的請求會用預設的 1 計價,而幣安實際上照 500 根收 5 點。差額不會在本地被擋下來,
        // 只會在交易所端變成 429,再不停手就是 418 封鎖 IP。
        // An undeclared request is metered locally at 1 while Binance charges 5 for the 500 candles it
        // actually returns. The gap is not caught locally; it turns into a 429 and then a 418 IP ban.
        var (feed, stub, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            _ = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
            });

            Assert.AreEqual(BinanceRequestWeights.Klines(500), stub.LastRequest.Weight);
            Assert.IsNull(stub.LastRequest.Parameter("limit"));
        }
    }

    [TestMethod]
    public async Task AnInvalidQueryIsRejectedBeforeAnythingIsSent()
    {
        var (feed, stub, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "   ",
                Interval = KlineInterval.OneMinute,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error?.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task ACountAboveTheExchangeCeilingIsRejectedRatherThanClamped()
    {
        // 悄悄截成 1500 會讓指標的暖機長度短一大截,而算出來的數字看起來完全正常。
        // Quietly clamping to 1500 leaves an indicator far short of its warm-up, and the numbers it produces
        // look entirely reasonable.
        var (feed, stub, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
                Limit = 5000,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error?.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task AnExchangeErrorIsMappedToTheNeutralCode()
    {
        var stub = StubHttpMessageHandler.Json(
            """{"code":-1121,"msg":"Invalid symbol."}""",
            HttpStatusCode.BadRequest);

        var (feed, _, http) = CreateRest(stub);

        using (http)
        {
            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "NOPEUSDT",
                Interval = KlineInterval.OneMinute,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.SymbolNotFound, result.Error?.Code);
        }
    }

    [TestMethod]
    public async Task AMalformedBodyIsAFailureRatherThanAnEmptyList()
    {
        var (feed, _, http) = CreateRest(StubHttpMessageHandler.Json("""{"unexpected":true}"""));

        using (http)
        {
            var result = await feed.GetKlinesAsync(new KlineQuery
            {
                Symbol = "BTCUSDT",
                Interval = KlineInterval.OneMinute,
            });

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error?.Code);
        }
    }

    [TestMethod]
    public async Task ANullQueryIsRejected()
    {
        var (feed, _, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => feed.GetKlinesAsync(null!));
        }
    }

    // ── K 線訂閱 ──────────────────────────────────────────────────────
    // ── Kline subscription ────────────────────────────────────────────

    [TestMethod]
    public async Task SubscribingSendsTheSubscribeCommandAndYieldsParsedCandles()
    {
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.KlineInProgressFirst,
            MarketDataSamples.KlineClosed,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            var items = await TakeAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute), 2);

            Assert.IsTrue(items[0].IsSuccess, items[0].Error?.Message);
            Assert.IsTrue(items[1].IsSuccess, items[1].Error?.Message);

            // 同一根 K 線,先未收盤、後收盤。訂閱路徑上的旗標對映在這裡被鎖住。
            // The same candle, open then closed. This pins the flag mapping on the subscription path itself.
            Assert.IsFalse(items[0].GetValueOrThrow().IsClosed);
            Assert.IsTrue(items[1].GetValueOrThrow().IsClosed);

            Assert.AreEqual(SubscribeBtcKline1m, connection.Sent[0]);
        }
    }

    [TestMethod]
    public async Task TheSubscribeCommandGoesOutBeforeAnyDataIsRead()
    {
        // 連線公開給呼叫端之前訂閱就要送出去,否則會有一段「連上了但什麼都沒訂」的空窗,
        // 而那段空窗裡漏掉的 K 線不會有任何人通知。
        // The subscription goes out before the connection is published, or there is a window in which the
        // socket is up and nothing is subscribed, and the candles missed in it are never reported by anyone.
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.KlineClosed,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            // 收到第一筆 K 線就代表連線已建立;此時連線上送出過的唯一一則訊息必須是那個 SUBSCRIBE。
            // Receiving the first candle proves the connection is up, and by then the only thing ever sent on
            // it must be that SUBSCRIBE.
            var items = await TakeAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute), 1);

            Assert.IsTrue(items[0].IsSuccess);
            Assert.HasCount(1, connection.Sent);
            Assert.AreEqual(SubscribeBtcKline1m, connection.Sent[0]);
        }
    }

    [TestMethod]
    public async Task MarkPriceUpdatesAreParsed()
    {
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.MarkPrice,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            var items = await TakeAsync(feed.SubscribeMarkPricesAsync(Eth), 1);

            Assert.IsTrue(items[0].IsSuccess, items[0].Error?.Message);
            Assert.AreEqual("ETHUSDT", items[0].GetValueOrThrow().Symbol);
            Assert.AreEqual(2475.24000000m, items[0].GetValueOrThrow().MarkPrice);
            Assert.AreEqual(
                """{"method":"SUBSCRIBE","params":["ethusdt@markPrice@1s"],"id":1}""",
                connection.Sent[0]);
        }
    }

    [TestMethod]
    public async Task TheSlowerMarkPriceStreamCanBeSelected()
    {
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.MarkPrice,
        ]);

        var (feed, http) = CreateStream(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceMarketStreamOptions { UseFastMarkPriceUpdates = false });

        using (http)
        {
            _ = await TakeAsync(feed.SubscribeMarkPricesAsync(Eth), 1);

            Assert.AreEqual(
                """{"method":"SUBSCRIBE","params":["ethusdt@markPrice"],"id":1}""",
                connection.Sent[0]);
        }
    }

    [TestMethod]
    public async Task ADroppedConnectionSurfacesAsATransientGapAndTheSubscriptionIsReplayed()
    {
        // 重連成功卻沒有重新訂閱,是這一塊最惡劣的失敗:連線是活的、狀態顯示已連線、沒有錯誤,
        // 資料卻永遠不會再進來。這條測試驗的就是第二條連線上有沒有那則 SUBSCRIBE。
        // A reconnect that succeeds without re-subscribing is the nastiest failure here: the connection is
        // alive, the status reads connected, nothing errors, and data never arrives again. This checks that
        // the SUBSCRIBE really went out on the second connection.
        var first = new FakeWebSocketConnection(
            [MarketDataSamples.SubscribeAck, MarketDataSamples.KlineClosed],
            closeWhenScriptEnds: true);

        var second = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.KlineInProgressFirst,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(first, second));

        using (http)
        {
            var items = await TakeAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute), 3);

            Assert.IsTrue(items[0].IsSuccess);

            Assert.IsTrue(items[1].IsFailure);
            Assert.AreEqual(TradeErrorCodes.StreamDisconnected, items[1].Error?.Code);
            Assert.IsTrue(
                items[1].Error!.IsTransient,
                "重連中的斷線是暫時性的,消費端應該知道這只是缺口而不是結束。");

            Assert.IsTrue(items[2].IsSuccess, items[2].Error?.Message);

            Assert.AreEqual(SubscribeBtcKline1m, second.Sent[0]);
        }
    }

    [TestMethod]
    public async Task AnExhaustedStreamEndsWithANonTransientFailure()
    {
        // 「還在重連」與「結束了」必須分得出來:前者記錄後繼續等,後者要重建訂閱或讓策略停手。
        // "Still reconnecting" and "finished" have to be distinguishable: the first means keep waiting, the
        // second means rebuild the subscription or stand the strategy down.
        var connection = new FakeWebSocketConnection(
            [MarketDataSamples.SubscribeAck, MarketDataSamples.KlineClosed],
            closeWhenScriptEnds: true);

        var (feed, http) = CreateStream(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceMarketStreamOptions { MaxReconnectAttempts = 0 });

        using (http)
        {
            var items = await DrainAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute));

            Assert.IsTrue(items[0].IsSuccess);

            var last = items[^1];

            Assert.IsTrue(last.IsFailure);
            Assert.AreEqual(TradeErrorCodes.StreamDisconnected, last.Error?.Code);
            Assert.IsFalse(last.Error!.IsTransient, "重連已放棄,這是終局失敗。");
            Assert.AreEqual(ErrorCategory.Exhausted, last.Error!.Category);
        }
    }

    [TestMethod]
    public async Task TheOriginalConnectionCodeIsKeptForDiagnostics()
    {
        var connection = new FakeWebSocketConnection([MarketDataSamples.SubscribeAck], closeWhenScriptEnds: true);

        var (feed, http) = CreateStream(
            new FakeWebSocketConnectionFactory(connection),
            new BinanceMarketStreamOptions { MaxReconnectAttempts = 0 });

        using (http)
        {
            var items = await DrainAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute));

            Assert.IsTrue(items[^1].Error!.TryGetData(WebSocketErrorDataKeys.InnerCode, out var inner));
            Assert.AreEqual(WebSocketErrorCodes.ReconnectExhausted, inner);
        }
    }

    [TestMethod]
    public async Task AnUnreadableFrameIsReportedWithoutEndingTheStream()
    {
        // 解析失敗若就地丟掉,幣安哪天改了欄位名,症狀會是「資料量慢慢變少」而不是任何錯誤。
        // Dropping a parse failure means that the day Binance renames a field, the symptom is "slightly less
        // data" rather than any error at all.
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            "}{ not json",
            MarketDataSamples.KlineClosed,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            var items = await TakeAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute), 2);

            Assert.IsTrue(items[0].IsFailure);
            Assert.AreEqual(BinanceErrorCodes.MalformedResponse, items[0].Error?.Code);
            Assert.IsTrue(items[1].IsSuccess, "一則壞訊息不該讓後面的行情停下來。");
        }
    }

    [TestMethod]
    public async Task DuplicateSymbolsAreCollapsedIntoOneStream()
    {
        // 幣安會照單全收重複的串流名稱,同一筆行情就會出現兩次;逐筆累加成交量的策略會憑空多算一份。
        // Binance accepts a duplicated stream name and the same update then arrives twice; a strategy
        // accumulating volume tick by tick would silently count it twice.
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.KlineClosed,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            _ = await TakeAsync(
                feed.SubscribeKlinesAsync(["BTCUSDT", "btcusdt", "BtcUsdt"], KlineInterval.OneMinute),
                1);

            Assert.AreEqual(SubscribeBtcKline1m, connection.Sent[0]);
        }
    }

    [TestMethod]
    public async Task AnEmptySymbolListFailsWithoutDialling()
    {
        var factory = new FakeWebSocketConnectionFactory();
        var (feed, http) = CreateStream(factory);

        using (http)
        {
            var items = await DrainAsync(feed.SubscribeKlinesAsync([], KlineInterval.OneMinute));

            Assert.HasCount(1, items);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, items[0].Error?.Code);
            Assert.AreEqual(0, factory.Created.Count);
        }
    }

    [TestMethod]
    public async Task AnInvalidSymbolFailsWithoutDialling()
    {
        var factory = new FakeWebSocketConnectionFactory();
        var (feed, http) = CreateStream(factory);

        using (http)
        {
            var items = await DrainAsync(feed.SubscribeMarkPricesAsync(["BTC/USDT"]));

            Assert.HasCount(1, items);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, items[0].Error?.Code);
            Assert.AreEqual(0, factory.Created.Count);
        }
    }

    [TestMethod]
    public async Task MoreStreamsThanOneConnectionCarriesIsRejected()
    {
        // 超過上限的訂閱會被截斷,而截斷的症狀是「有些標的就是沒有資料」,看起來像那幾檔沒成交。
        // A subscription beyond the ceiling gets truncated, and truncation looks like "some symbols have no
        // data", which reads as a quiet market.
        var symbols = Enumerable
            .Range(0, BinanceStreamNames.MaxStreamsPerConnection + 1)
            .Select(index => $"SYM{index}USDT")
            .ToArray();

        var factory = new FakeWebSocketConnectionFactory();
        var (feed, http) = CreateStream(factory);

        using (http)
        {
            var items = await DrainAsync(feed.SubscribeKlinesAsync(symbols, KlineInterval.OneMinute));

            Assert.HasCount(1, items);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, items[0].Error?.Code);
            Assert.AreEqual(0, factory.Created.Count);
        }
    }

    [TestMethod]
    public async Task AnUnsupportedIntervalFailsWithoutDialling()
    {
        var factory = new FakeWebSocketConnectionFactory();
        var (feed, http) = CreateStream(factory);

        using (http)
        {
            var items = await DrainAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.Unspecified));

            Assert.HasCount(1, items);
            Assert.AreEqual(TradeErrorCodes.UnsupportedInterval, items[0].Error?.Code);
            Assert.AreEqual(0, factory.Created.Count);
        }
    }

    [TestMethod]
    public async Task EachSubscriptionDialsItsOwnConnection()
    {
        // 兩種訂閱共用一條連線,取消其中一種就有可能把另一種一起停掉,而那個錯誤的症狀是
        // 「某一種行情靜悄悄地不再更新」,連線狀態卻一切正常。
        // Sharing one connection between the two subscriptions makes it possible for cancelling one to stop
        // the other, a mistake whose only symptom is one kind of data quietly ceasing while the connection
        // status stays healthy.
        var klineConnection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.KlineClosed,
        ]);

        var markPriceConnection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            MarketDataSamples.MarkPrice,
        ]);

        var factory = new FakeWebSocketConnectionFactory(klineConnection, markPriceConnection);
        var (feed, http) = CreateStream(factory);

        using (http)
        {
            _ = await TakeAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute), 1);
            _ = await TakeAsync(feed.SubscribeMarkPricesAsync(Eth), 1);
        }

        Assert.HasCount(2, factory.Created);
        Assert.AreEqual(SubscribeBtcKline1m, klineConnection.Sent[0]);
        Assert.IsTrue(
            markPriceConnection.Sent[0].Contains("ethusdt@markPrice@1s", StringComparison.Ordinal),
            markPriceConnection.Sent[0]);
    }

    [TestMethod]
    public async Task ABinaryFrameIsReportedRatherThanSilentlyIgnored()
    {
        // 幣安的行情推送一律是文字。收到二進位就代表協定變了或連錯了東西,安靜跳過會讓那件事
        // 以「資料變少」的形式出現,而不是以錯誤的形式。
        // Binance market pushes are always text. A binary frame means the protocol changed or the client is
        // talking to something else, and skipping it quietly turns that into "less data" rather than an error.
        var connection = new FakeWebSocketConnection([
            MarketDataSamples.SubscribeAck,
            FakeWebSocketFrame.Binary(MarketDataSamples.KlineClosed),
            MarketDataSamples.KlineClosed,
        ]);

        var (feed, http) = CreateStream(new FakeWebSocketConnectionFactory(connection));

        using (http)
        {
            var items = await TakeAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute), 2);

            Assert.IsTrue(items[0].IsFailure);
            Assert.AreEqual(BinanceErrorCodes.MalformedResponse, items[0].Error?.Code);
            Assert.IsTrue(items[1].IsSuccess, "一則二進位訊息不該讓後面的行情停下來。");
        }
    }

    [TestMethod]
    public async Task AnEndpointOverrideThatIsNotAWebSocketAddressFailsTheSubscription()
    {
        // 端點覆寫是後門,填錯配置(http 而不是 ws)不會有人擋。這裡要的是一筆說得出原因的失敗,
        // 而不是一條永遠連不上、卻也不說話的訂閱。
        // The endpoint override is a back door and nothing stops a wrong scheme going in. What is required is
        // a failure that says why, rather than a subscription that never connects and never says so.
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);

        options.EndpointOverride = BinanceEndpoints
            .CreateOverride(
                "設定錯誤的覆寫 / misconfigured override",
                new Uri("https://testnet.binancefuture.com", UriKind.Absolute),
                new Uri("https://not-a-websocket.example", UriKind.Absolute),
                isTestnet: true)
            .GetValueOrThrow();

        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, StubHttpMessageHandler.Json("[]"), clock);
        var factory = new FakeWebSocketConnectionFactory();

        using (http)
        {
            var feed = new BinanceMarketDataFeed(
                pipeline,
                options,
                streamOptions: null,
                loggerFactory: null,
                timeProvider: clock,
                connectionFactory: factory);

            var items = await DrainAsync(feed.SubscribeKlinesAsync(Btc, KlineInterval.OneMinute));

            Assert.HasCount(1, items);
            Assert.AreEqual(TradeErrorCodes.SubscriptionFailed, items[0].Error?.Code);
            Assert.AreEqual(0, factory.Created.Count);
        }
    }

    // ── 建構 ──────────────────────────────────────────────────────────
    // ── Construction ──────────────────────────────────────────────────

    [TestMethod]
    public void InvalidStreamSettingsAreRejectedAtConstruction()
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, StubHttpMessageHandler.Json("[]"), clock);

        using (http)
        {
            Assert.ThrowsExactly<ArgumentException>(() => new BinanceMarketDataFeed(
                pipeline,
                options,
                new BinanceMarketStreamOptions { IdleTimeout = TimeSpan.Zero }));
        }
    }

    [TestMethod]
    public void TheFeedReportsTheEndpointSetItTalksTo()
    {
        var (feed, _, http) = CreateRest(StubHttpMessageHandler.Json("[]"));

        using (http)
        {
            Assert.AreEqual(BinanceEndpoints.Testnet, feed.Endpoints);
            Assert.IsTrue(feed.StreamOptions.UseFastMarkPriceUpdates);
        }
    }

    [TestMethod]
    public void NullOptionsAreRejected()
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, StubHttpMessageHandler.Json("[]"), clock);

        using (http)
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new BinanceMarketDataFeed(pipeline, null!));
        }
    }

    // ── 測試輔助 ──────────────────────────────────────────────────────
    // ── Helpers ───────────────────────────────────────────────────────

    private static (BinanceMarketDataFeed Feed, StubHttpMessageHandler Stub, HttpClient Http) CreateRest(
        StubHttpMessageHandler stub)
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);

        // 時鐘定在錄下那份 klines 回應的那一刻,「最後一根還沒收盤」才是可重現的事實而不是碰巧。
        // The clock sits at the moment the klines response was recorded, which makes "the last candle is
        // still open" a reproducible fact rather than a coincidence.
        var clock = new TestClock(
            DateTimeOffset.FromUnixTimeMilliseconds(MarketDataSamples.RestKlinesServerTimeMs));

        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        return (new BinanceMarketDataFeed(pipeline, options, timeProvider: clock), stub, http);
    }

    private static (BinanceMarketDataFeed Feed, HttpClient Http) CreateStream(
        IWebSocketConnectionFactory connectionFactory,
        BinanceMarketStreamOptions? streamOptions = null)
    {
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet, withCredentials: false);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, StubHttpMessageHandler.Json("[]"), clock);

        return (
            new BinanceMarketDataFeed(
                pipeline,
                options,
                streamOptions,
                loggerFactory: null,
                timeProvider: clock,
                connectionFactory: connectionFactory),
            http);
    }

    private static async Task<List<Result<T>>> TakeAsync<T>(IAsyncEnumerable<Result<T>> stream, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

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

    private static async Task<List<Result<T>>> DrainAsync<T>(
        IAsyncEnumerable<Result<T>> stream,
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));

        var items = new List<Result<T>>();

        await foreach (var item in stream.WithCancellation(cts.Token))
        {
            items.Add(item);
        }

        return items;
    }
}
