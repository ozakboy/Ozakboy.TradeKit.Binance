using System.Net;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceFuturesClientTests
{
    private static (BinanceFuturesClient Client, StubHttpMessageHandler Stub, HttpClient Http) Create(
        StubHttpMessageHandler stub,
        bool withCredentials = true)
    {
        var options = TestPipeline.CreateOptions(withCredentials: withCredentials);
        var clock = TestClock.AtFixedInstant();
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        return (new BinanceFuturesClient(pipeline, options, clock), stub, http);
    }

    private static StubHttpMessageHandler AccountAndPositions() => StubHttpMessageHandler.ByPath(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/fapi/v2/account"] = Fixtures.Account,
            ["/fapi/v2/positionRisk"] = Fixtures.PositionRisk,
            ["/fapi/v1/exchangeInfo"] = Fixtures.ExchangeInfoMainnet,
            ["/fapi/v1/time"] = Fixtures.ServerTime,
        });

    [TestMethod]
    public async Task AccountSnapshotCombinesBalancesWithFullyPopulatedPositions()
    {
        var (client, stub, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            var result = await client.GetAccountSnapshotAsync();

            Assert.IsTrue(result.TryGetValue(out var snapshot));
            Assert.HasCount(2, snapshot.Balances);
            Assert.HasCount(2, snapshot.Positions);

            // 兩個端點各打一次(帳戶 5 + 持倉 5,合計權重 10)。多花的那 5 點換到 markPrice 與
            // liquidationPrice —— 帳戶端點的持倉沒有它們,名目價值會算成零。
            // Two calls, weight 10 in total. The extra five buys markPrice and liquidationPrice, without which
            // the notional comes out zero.
            Assert.AreEqual(2, stub.CallCount);
            Assert.IsGreaterThan(0m, snapshot.GetPosition("BTCUSDT").GetValueOrThrow().Notional);
        }
    }

    [TestMethod]
    public async Task SignedRequestsCarryTheKeyHeaderTimestampAndRecvWindow()
    {
        var (client, stub, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            _ = await client.GetPositionsAsync();

            var request = stub.LastRequest;
            var query = request.RequestUri!.Query;

            Assert.IsTrue(request.Headers.ContainsKey("X-MBX-APIKEY"));
            StringAssert.Contains(query, "recvWindow=5000", StringComparison.Ordinal);
            StringAssert.Contains(query, "timestamp=", StringComparison.Ordinal);
            StringAssert.Contains(query, "signature=", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task TheSignatureIsAppendedLastAndIsLowerCaseHexadecimal()
    {
        // 簽章不是待簽字串的一部分,必須接在最後;順序放錯對方就算出不同的簽章。
        // The signature is not an input to itself and must come last, or the peer computes a different one.
        var (client, stub, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            _ = await client.GetPositionAsync("BTCUSDT");

            var query = stub.LastRequest.RequestUri!.Query.TrimStart('?');
            var parts = query.Split('&');

            StringAssert.StartsWith(parts[^1], "signature=", StringComparison.Ordinal);

            var signature = parts[^1]["signature=".Length..];
            Assert.HasCount(64, signature);
            Assert.IsTrue(
                signature.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')),
                $"簽章不是小寫十六進位:{signature}");
        }
    }

    [TestMethod]
    public async Task ParameterOrderIsPreservedExactlyAsItWasBuilt()
    {
        // 幣安簽的就是實際送出的查詢字串,順序換掉簽章就不同。用字典承載參數的下場是
        // 「隨機出現 -1022、重跑又好」,所以這裡把順序釘死。
        // Binance signs the query string it receives, so a different order is a different signature. The order
        // is pinned here because a dictionary would produce intermittent -1022 failures.
        var (client, stub, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            _ = await client.GetPositionAsync("BTCUSDT");

            var parts = stub.LastRequest.RequestUri!.Query.TrimStart('?').Split('&');

            StringAssert.StartsWith(parts[0], "symbol=BTCUSDT", StringComparison.Ordinal);
            StringAssert.StartsWith(parts[1], "recvWindow=", StringComparison.Ordinal);
            StringAssert.StartsWith(parts[2], "timestamp=", StringComparison.Ordinal);
            StringAssert.StartsWith(parts[3], "signature=", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task OpenPositionsExcludeTheFlatRows()
    {
        var (client, _, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            var positions = (await client.GetPositionsAsync()).GetValueOrThrow();

            Assert.HasCount(2, positions);
            Assert.IsFalse(positions.Any(position => position.IsFlat));
        }
    }

    [TestMethod]
    public async Task AFlatSymbolComesBackAsAZeroPositionRatherThanAFailure()
    {
        // IExchangeClient 的契約:空手回傳數量為零的持倉,不是失敗。
        // The interface contract: being flat is a zero position, not an error.
        var stub = StubHttpMessageHandler.Json("""
            [{"symbol":"ADAUSDT","positionAmt":"0","entryPrice":"0.0","markPrice":"0.45",
              "unRealizedProfit":"0","liquidationPrice":"0","leverage":"20","marginType":"cross",
              "isolatedMargin":"0","positionSide":"BOTH","updateTime":0}]
            """);

        var (client, _, http) = Create(stub);

        using (client)
        using (http)
        {
            var result = await client.GetPositionAsync("ADAUSDT");

            Assert.IsTrue(result.TryGetValue(out var position));
            Assert.IsTrue(position.IsFlat);
            Assert.AreEqual("ADAUSDT", position.Symbol);
        }
    }

    [TestMethod]
    public async Task ASingleSymbolQueryReturnsThatPosition()
    {
        var (client, _, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            var result = await client.GetPositionAsync("BTCUSDT");

            Assert.IsTrue(result.TryGetValue(out var position));
            Assert.AreEqual("BTCUSDT", position.Symbol);
            Assert.AreEqual(0.015m, position.Quantity);
        }
    }

    [TestMethod]
    public async Task OtherSymbolsInTheResponseAreIgnoredRatherThanMiscounted()
    {
        // 這份回應同時帶了 BTCUSDT 與 ETHUSDT 的未平部位。若不先依代碼篩過,
        // 「有兩個未平部位」會被當成雙向模式而整筆拒絕 —— 查一個商品卻因為另一個商品而失敗。
        // The response carries open positions on two symbols. Without filtering by code first, "two open
        // positions" reads as hedge mode and the query fails because of a symbol nobody asked about.
        var (client, _, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            var eth = await client.GetPositionAsync("ETHUSDT");

            Assert.IsTrue(eth.TryGetValue(out var position));
            Assert.AreEqual("ETHUSDT", position.Symbol);
            Assert.AreEqual(-0.500m, position.Quantity);
        }
    }

    [TestMethod]
    public async Task ASymbolAbsentFromTheResponseIsFlatRatherThanAnError()
    {
        var (client, _, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            var result = await client.GetPositionAsync("SOLUSDT");

            Assert.IsTrue(result.TryGetValue(out var position));
            Assert.IsTrue(position.IsFlat);
            Assert.AreEqual("SOLUSDT", position.Symbol);
        }
    }

    [TestMethod]
    public async Task HedgeModeWithBothSidesOpenIsRefusedRatherThanHalfAnswered()
    {
        // 挑一邊回傳會讓平倉指令只平掉一半,另一半留在市場上 —— 比查不到部位危險得多。
        // Picking a side would flatten half the exposure and leave the rest live.
        var (client, _, http) = Create(StubHttpMessageHandler.Json(Fixtures.PositionRiskHedge));

        using (client)
        using (http)
        {
            var result = await client.GetPositionAsync("BTCUSDT");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.NotSupported, result.Error!.Code);
            StringAssert.Contains(result.Error.Message, "GetPositionsAsync", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SignedCallsAreRefusedBeforeTheyLeaveWhenNoCredentialsAreConfigured()
    {
        // 沒有憑證還是把請求送出去,拿到的會是 -2014 或 -2015,那兩句訊息會把人帶去查 IP 白名單,
        // 真正的原因卻只是設定沒讀進來。
        // Sending anyway earns a -2014 or -2015 whose wording sends the reader to inspect IP allowlists.
        var (client, stub, http) = Create(AccountAndPositions(), withCredentials: false);

        using (client)
        using (http)
        {
            foreach (var result in new[]
            {
                (await client.GetPositionsAsync()).ToResult(),
                (await client.GetAccountSnapshotAsync()).ToResult(),
                (await client.GetPositionAsync("BTCUSDT")).ToResult(),
            })
            {
                Assert.IsTrue(result.IsFailure);
                Assert.AreEqual(BinanceErrorCodes.CredentialsMissing, result.Error!.Code);
                Assert.IsFalse(result.Error.IsTransient);
            }

            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task ABlankSymbolIsRejectedWithoutCallingTheExchange()
    {
        var (client, stub, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            var result = await client.GetPositionAsync("  ");

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidQuery, result.Error!.Code);
            Assert.AreEqual(0, stub.CallCount);
        }
    }

    [TestMethod]
    public async Task ExchangeRejectionsArriveAlreadyTranslated()
    {
        var stub = StubHttpMessageHandler.Json(
            """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}""",
            HttpStatusCode.Unauthorized);

        var (client, _, http) = Create(stub);

        using (client)
        using (http)
        {
            var result = await client.GetPositionsAsync();

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.InvalidCredentials, result.Error!.Code);
            Assert.IsFalse(result.Error.IsTransient);
            StringAssert.Contains(result.Error.Message, "白名單", StringComparison.Ordinal);
            Assert.IsTrue(result.Error.TryGetData(BinanceErrorDataKeys.Endpoint, out var endpoint));
            Assert.AreEqual("fapi/v2/positionRisk", endpoint);
        }
    }

    [TestMethod]
    public async Task AFailedPositionQueryAbortsTheAccountSnapshot()
    {
        // 帳戶快照的持倉是另外一支請求拿的。那支失敗時不應該回傳一份「餘額正確、持倉是空的」的快照 ——
        // 空持倉在風控眼中是「目前沒有部位」,那是一個會讓人加碼的謊言。
        // The positions come from a second request. If it fails, returning a snapshot with correct balances and
        // no positions would tell risk management there is no exposure — a lie that invites more.
        var stub = StubHttpMessageHandler.ByPath(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/fapi/v2/account"] = Fixtures.Account,
        });

        var (client, _, http) = Create(stub);

        using (client)
        using (http)
        {
            var result = await client.GetAccountSnapshotAsync();

            Assert.IsTrue(result.IsFailure);
            Assert.AreEqual(TradeErrorCodes.SymbolNotFound, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task TradingRuleCallsAreDelegatedToTheProvider()
    {
        var (client, _, http) = Create(AccountAndPositions());

        using (client)
        using (http)
        {
            Assert.IsTrue((await client.GetSymbolsAsync()).IsSuccess);
            Assert.IsTrue((await client.GetSymbolAsync("BTCUSDT")).IsSuccess);
            Assert.IsTrue((await client.GetServerTimeAsync()).IsSuccess);
            Assert.AreEqual(BinanceEndpoints.Mainnet, client.Endpoints);
        }
    }

    [TestMethod]
    public async Task UsingADisposedClientThrows()
    {
        var (client, _, http) = Create(AccountAndPositions());

        using (http)
        {
            client.Dispose();
            client.Dispose();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetPositionsAsync());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetAccountSnapshotAsync());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetPositionAsync("BTCUSDT"));
        }
    }

    [TestMethod]
    public void RejectsMissingArguments()
    {
        var options = TestPipeline.CreateOptions();

        using var stub = StubHttpMessageHandler.Json("{}");
        using var http = new HttpClient(stub) { BaseAddress = BinanceEndpoints.Mainnet.RestBaseUri };
        var pipeline = new Ozakboy.Http.HttpPipelineClient(http);

        Assert.ThrowsExactly<ArgumentNullException>(() => new BinanceFuturesClient(pipeline, null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BinanceFuturesClient(pipeline, options, (BinanceExchangeInfoProvider)null!));
    }
}
