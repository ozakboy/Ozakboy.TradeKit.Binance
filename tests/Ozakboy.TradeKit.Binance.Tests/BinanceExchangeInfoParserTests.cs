using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 以 2026-09-11 錄製的真實 <c>exchangeInfo</c> 回應驗證交易規則對映。
/// Verifies the trading-rule mapping against a real <c>exchangeInfo</c> response recorded on 2026-09-11.
/// </summary>
/// <remarks>
/// 這裡的數值全部是交易所當天實際回的值,不是為了讓測試過而挑的。對映錯一位小數,
/// 下單量就會差十倍,所以這幾條斷言寫死實際值而不是寫「大於零」。
/// Every figure here is what the exchange actually returned that day rather than something chosen to make the
/// test pass. One misplaced decimal is a tenfold error in order size, so these assertions pin the real values
/// instead of asserting "greater than zero".
/// </remarks>
[TestClass]
public sealed class BinanceExchangeInfoParserTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 9, 11, 7, 5, 53, TimeSpan.Zero);

    private static BinanceExchangeInfoSnapshot ParseMainnet() =>
        BinanceExchangeInfoParser
            .Parse(Fixtures.ExchangeInfoMainnet, BinanceEndpoints.Mainnet, FetchedAt)
            .GetValueOrThrow();

    [TestMethod]
    public void ParsesBtcUsdtExactlyAsTheExchangeReportedIt()
    {
        Assert.IsTrue(ParseMainnet().TryGetSymbol("BTCUSDT", out var btc));

        var info = btc!.Info;

        Assert.AreEqual("BTC", info.BaseAsset);
        Assert.AreEqual("USDT", info.QuoteAsset);
        Assert.AreEqual(0.10m, info.TickSize);
        Assert.AreEqual(0.001m, info.StepSize);
        Assert.AreEqual(0.001m, info.MinQuantity);
        Assert.AreEqual(1000m, info.MaxQuantity);
        Assert.AreEqual(556.80m, info.MinPrice);
        Assert.AreEqual(4529764m, info.MaxPrice);
        Assert.AreEqual(50m, info.MinNotional);
        Assert.IsTrue(info.IsTradingEnabled);

        Assert.AreEqual("TRADING", btc.Status);
        Assert.AreEqual("PERPETUAL", btc.ContractType);
        Assert.IsTrue(btc.IsPerpetual);
        Assert.AreEqual("USDT", btc.MarginAsset);
        Assert.AreEqual(2, btc.PricePrecision);
        Assert.AreEqual(3, btc.QuantityPrecision);
        Assert.AreEqual(200, btc.MaxOpenOrders);
    }

    [TestMethod]
    public void DerivedScalesComeFromTheStepSizesNotTheDeclaredPrecision()
    {
        // BTCUSDT 宣告 pricePrecision = 2,但 tickSize 是 0.10,也就是 1 位。
        // 校正若採用宣告的精度就會送出 0.05 這種未對齊的價格,換來 -4014。
        // BTCUSDT declares pricePrecision 2 while its tickSize of 0.10 implies 1. Normalising against the
        // declared precision would send prices like 0.05 and earn a -4014.
        Assert.IsTrue(ParseMainnet().TryGetSymbol("BTCUSDT", out var btc));

        Assert.AreEqual(1, btc!.Info.PriceScale);
        Assert.AreEqual(2, btc.PricePrecision);
        Assert.AreNotEqual(btc.PricePrecision, btc.Info.PriceScale);
    }

    [TestMethod]
    public void SmallCapSymbolsCarryTheFiveUsdtMinimumNotional()
    {
        var snapshot = ParseMainnet();

        Assert.IsTrue(snapshot.TryGetSymbol("DOGEUSDT", out var doge));
        Assert.IsTrue(snapshot.TryGetSymbol("1000PEPEUSDT", out var pepe));
        Assert.IsTrue(snapshot.TryGetSymbol("XRPUSDT", out var xrp));
        Assert.IsTrue(snapshot.TryGetSymbol("ETHUSDT", out var eth));
        Assert.IsTrue(snapshot.TryGetSymbol("BTCUSDT", out var btc));

        Assert.AreEqual(5m, doge!.Info.MinNotional);
        Assert.AreEqual(5m, pepe!.Info.MinNotional);
        Assert.AreEqual(5m, xrp!.Info.MinNotional);
        Assert.AreEqual(20m, eth!.Info.MinNotional);
        Assert.AreEqual(50m, btc!.Info.MinNotional);
    }

    [TestMethod]
    public void ParsesWholeNumberStepSizesAndVerySmallTickSizes()
    {
        var snapshot = ParseMainnet();

        Assert.IsTrue(snapshot.TryGetSymbol("DOGEUSDT", out var doge));
        Assert.IsTrue(snapshot.TryGetSymbol("1000PEPEUSDT", out var pepe));

        // DOGEUSDT 的 stepSize 是 "1":整數步進,數量小數位數為 0。
        Assert.AreEqual(1m, doge!.Info.StepSize);
        Assert.AreEqual(0, doge.Info.QuantityScale);

        // tickSize "0.000010" 帶了尾隨零,有效位數應為 5 而不是字面上的 6。
        // The trailing zero in "0.000010" means five significant decimals, not the six it literally shows.
        Assert.AreEqual(0.00001m, doge.Info.TickSize);
        Assert.AreEqual(5, doge.Info.PriceScale);

        Assert.AreEqual(0.0000001m, pepe!.Info.TickSize);
        Assert.AreEqual(7, pepe.Info.PriceScale);
    }

    [TestMethod]
    public void MarketLotSizeIsKeptSeparateFromTheLimitOrderCeiling()
    {
        // 2026-09-11 的主網快照裡,897 個商品的 LOT_SIZE.maxQty 與 MARKET_LOT_SIZE.maxQty 全部不同。
        // 中立模型只有一個 MaxQuantity,填錯一邊就是「市價單被拒」或「合法限價單被本地擋下」。
        // In that snapshot all 897 symbols had different limit and market ceilings, and the neutral model has
        // only one MaxQuantity.
        Assert.IsTrue(ParseMainnet().TryGetSymbol("BTCUSDT", out var btc));

        Assert.AreEqual(1000m, btc!.Info.MaxQuantity);
        Assert.AreEqual(120m, btc.MarketMaxQuantity);
        Assert.AreEqual(0.001m, btc.MarketMinQuantity);
        Assert.AreEqual(0.001m, btc.MarketStepSize);
        Assert.AreNotEqual(btc.Info.MaxQuantity, btc.MarketMaxQuantity);
    }

    [TestMethod]
    public void NonTradingSymbolsAreReportedAsNotTradable()
    {
        Assert.IsTrue(ParseMainnet().TryGetSymbol("OMGUSDT", out var omg));

        Assert.AreEqual("SETTLING", omg!.Status);
        Assert.IsFalse(omg.Info.IsTradingEnabled);
    }

    [TestMethod]
    public void LeverageCeilingIsLeftAtTheAbstractionDefaultBecauseExchangeInfoDoesNotCarryIt()
    {
        // exchangeInfo 根本沒有最大槓桿。由 requiredMarginPercent 反推得到的是預設分層的槓桿
        // (BTCUSDT 反推 20,實際上限 125),填進去等於用錯的值冒充事實。
        // exchangeInfo has no leverage ceiling at all, and deriving one would pass a wrong number off as fact.
        Assert.IsTrue(ParseMainnet().TryGetSymbol("BTCUSDT", out var btc));

        Assert.AreEqual(1, btc!.Info.MaxLeverage);
    }

    [TestMethod]
    public void EverySymbolPassesItsOwnRuleValidation()
    {
        foreach (var symbol in ParseMainnet().Symbols)
        {
            Assert.IsTrue(
                symbol.Info.Validate().IsSuccess,
                $"{symbol.Name} 的交易規則沒有通過自我檢查。");
        }
    }

    [TestMethod]
    public void SnapshotRemembersItsSourceEnvironmentAndServerTime()
    {
        var snapshot = ParseMainnet();

        Assert.AreEqual(BinanceEndpoints.Mainnet, snapshot.Endpoints);
        Assert.AreEqual(FetchedAt, snapshot.FetchedAt);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(1789109384268L),
            snapshot.ServerTime);
        Assert.HasCount(6, snapshot.Symbols);
        Assert.HasCount(6, snapshot.SymbolInfos);
    }

    [TestMethod]
    public void TestnetAndMainnetDisagreeOnTheSameSymbol()
    {
        // 這是本套件最重要的一條測試。兩份 fixture 都是同一天從各自的環境真實抓下來的。
        // The single most important test here; both fixtures were recorded the same day from their own
        // environments.
        var mainnet = ParseMainnet();
        var testnet = BinanceExchangeInfoParser
            .Parse(Fixtures.ExchangeInfoTestnet, BinanceEndpoints.Testnet, FetchedAt)
            .GetValueOrThrow();

        Assert.IsTrue(mainnet.TryGetSymbol("BTCUSDT", out var live));
        Assert.IsTrue(testnet.TryGetSymbol("BTCUSDT", out var sandbox));
        Assert.IsNotNull(live);
        Assert.IsNotNull(sandbox);

        Assert.AreEqual(0.001m, live!.Info.StepSize);
        Assert.AreEqual(0.0001m, sandbox!.Info.StepSize);
        Assert.AreEqual(0.001m, live.Info.MinQuantity);
        Assert.AreEqual(0.0001m, sandbox.Info.MinQuantity);

        // 在 Testnet 完全合法的數量,在主網連對齊都過不了。
        // A quantity that is perfectly legal on Testnet is not even aligned on production.
        Assert.IsTrue(sandbox.Info.IsQuantityAligned(0.0015m));
        Assert.IsFalse(live.Info.IsQuantityAligned(0.0015m));

        Assert.AreNotEqual(mainnet.Endpoints, testnet.Endpoints);
    }

    [TestMethod]
    public void LookupIsCaseInsensitiveAndRejectsBlanks()
    {
        var snapshot = ParseMainnet();

        Assert.IsTrue(snapshot.TryGetSymbol("btcusdt", out _));
        Assert.IsFalse(snapshot.TryGetSymbol("  ", out var blank));
        Assert.IsNull(blank);
        Assert.IsFalse(snapshot.TryGetSymbol("NOSUCHUSDT", out _));
    }

    [TestMethod]
    public void ExpiryIsMeasuredFromTheLocalFetchTime()
    {
        var snapshot = ParseMainnet();

        Assert.IsFalse(snapshot.IsExpired(FetchedAt.AddHours(23), TimeSpan.FromHours(24)));
        Assert.IsTrue(snapshot.IsExpired(FetchedAt.AddHours(24), TimeSpan.FromHours(24)));
    }

    [TestMethod]
    public void RejectsAnEmptyOrMalformedBody()
    {
        Assert.AreEqual(
            BinanceErrorCodes.MalformedResponse,
            BinanceExchangeInfoParser.Parse("   ", BinanceEndpoints.Mainnet, FetchedAt).Error!.Code);

        Assert.AreEqual(
            BinanceErrorCodes.MalformedResponse,
            BinanceExchangeInfoParser.Parse("{not json", BinanceEndpoints.Mainnet, FetchedAt).Error!.Code);

        Assert.AreEqual(
            BinanceErrorCodes.MalformedResponse,
            BinanceExchangeInfoParser.Parse("[]", BinanceEndpoints.Mainnet, FetchedAt).Error!.Code);
    }

    [TestMethod]
    public void RejectsAResponseWithoutServerTimeOrSymbols()
    {
        Assert.IsTrue(BinanceExchangeInfoParser
            .Parse("""{"symbols":[]}""", BinanceEndpoints.Mainnet, FetchedAt).IsFailure);

        Assert.IsTrue(BinanceExchangeInfoParser
            .Parse("""{"serverTime":1789109384268}""", BinanceEndpoints.Mainnet, FetchedAt).IsFailure);
    }

    [TestMethod]
    public void ThrowsWhenNoEnvironmentIsSupplied()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceExchangeInfoParser.Parse("{}", null!, FetchedAt));
    }

    [TestMethod]
    [DataRow("PRICE_FILTER", "tickSize")]
    [DataRow("PRICE_FILTER", "minPrice")]
    [DataRow("PRICE_FILTER", "maxPrice")]
    [DataRow("LOT_SIZE", "stepSize")]
    [DataRow("LOT_SIZE", "minQty")]
    [DataRow("LOT_SIZE", "maxQty")]
    [DataRow("MARKET_LOT_SIZE", "stepSize")]
    [DataRow("MARKET_LOT_SIZE", "minQty")]
    [DataRow("MARKET_LOT_SIZE", "maxQty")]
    [DataRow("MIN_NOTIONAL", "notional")]
    public void AMissingFilterFieldFailsAndNamesItself(string filterType, string field)
    {
        // §12.3:讀取失敗禁止以預設值猜測,直接拒絕。這裡逐項拔掉必要欄位,確認每一項都會被擋下來,
        // 而且錯誤訊息說得出是哪個商品的哪個欄位 —— 少了這一點,九百多個商品裡查不出是哪一個壞掉。
        // Reading failures must be rejected rather than defaulted. Each required field is removed in turn to
        // confirm every one of them fails, naming the symbol and the field.
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            $"\"{field}\": \"",
            $"\"{field}_removed\": \"",
            StringComparison.Ordinal);

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        // 這個欄位每個商品都有,拔掉等於全部商品都解析不了 —— 那是格式問題而不是個別商品的資料問題,
        // 所以整份失敗,而不是回傳一份空的交易規則。
        // Every symbol carries this field, so removing it fails them all — a format problem rather than one
        // symbol's bad data, which fails the whole parse instead of yielding an empty rule set.
        Assert.IsTrue(result.IsFailure, $"{filterType}.{field} 被拿掉之後應該解析失敗。");
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
        Assert.IsTrue(result.Error.TryGetData(BinanceErrorDataKeys.Field, out var named));
        Assert.IsNotNull(named);
        StringAssert.Contains(named!, field, StringComparison.Ordinal);
        Assert.IsTrue(result.Error.TryGetData(BinanceErrorDataKeys.Symbol, out var symbol));
        Assert.IsNotNull(symbol);
    }

    [TestMethod]
    [DataRow("symbol")]
    [DataRow("baseAsset")]
    [DataRow("quoteAsset")]
    [DataRow("status")]
    public void AMissingSymbolFieldFailsEverySymbolAndThereforeTheWholeParse(string field)
    {
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            $"\"{field}\": \"",
            $"\"{field}_removed\": \"",
            StringComparison.Ordinal);

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
    }

    [TestMethod]
    public void AMissingFiltersArrayFails()
    {
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            "\"filters\":",
            "\"filters_removed\":",
            StringComparison.Ordinal);

        Assert.IsTrue(BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt).IsFailure);
    }

    [TestMethod]
    public void RulesThatFailSelfValidationExcludeOnlyThatSymbol()
    {
        // tickSize 為 0 會通過「欄位存在」的檢查,卻是不可用的規則。這一條守的是「解析成功 ≠ 規則可用」,
        // 同時守住「一顆壞蘋果不該倒掉整籃」:其餘五個商品必須照常可用。
        // A zero tickSize passes the presence check yet is unusable. This pins both "parsed is not usable" and
        // "one bad apple does not empty the basket": the other five symbols stay available.
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            "\"tickSize\": \"0.10\"",
            "\"tickSize\": \"0\"",
            StringComparison.Ordinal);

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        Assert.IsTrue(result.TryGetValue(out var snapshot));
        Assert.HasCount(5, snapshot.Symbols);
        Assert.HasCount(1, snapshot.RejectedSymbols);
        Assert.AreEqual("BTCUSDT", snapshot.RejectedSymbols[0].Name);
        Assert.AreEqual(TradeErrorCodes.InvalidSymbolRules, snapshot.RejectedSymbols[0].Reason.Code);
        Assert.IsFalse(snapshot.TryGetSymbol("BTCUSDT", out _));
        Assert.IsTrue(snapshot.TryGetSymbol("ETHUSDT", out _));
    }

    [TestMethod]
    public void ScientificNotationIsRejectedRatherThanSilentlyAccepted()
    {
        // Precision.TryParsePlain 刻意拒絕科學記號。寧可當場失敗,也不要拿一個位數已失真的值去算下單量。
        // TryParsePlain rejects exponent notation on purpose: a loud failure beats a silently rescaled value.
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            "\"tickSize\": \"0.10\"",
            "\"tickSize\": \"1E-1\"",
            StringComparison.Ordinal);

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        Assert.IsTrue(result.TryGetValue(out var snapshot));
        Assert.HasCount(1, snapshot.RejectedSymbols);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, snapshot.RejectedSymbols[0].Reason.Code);
    }

    [TestMethod]
    public void APendingSymbolWithPlaceholderRulesIsExcludedNotFatal()
    {
        // 真實資料:2026-09-11 的 Testnet 快照裡,ELSAUSDT 的狀態是 PENDING_TRADING 而 tickSize 是 "0"。
        // 幣安會先把尚未上架的商品放進 exchangeInfo 再補規則。讓這一筆把整份交易規則判定為失敗,
        // 系統就會因為一個根本不能交易的標的而完全無法運作。
        // Real data: in the Testnet snapshot of 2026-09-11, ELSAUSDT was PENDING_TRADING with a tickSize of
        // "0". Binance lists symbols before their rules are filled in, and failing the whole rule set over one
        // would stop the system for an instrument nobody can trade.
        var snapshot = BinanceExchangeInfoParser
            .Parse(Fixtures.ExchangeInfoTestnet, BinanceEndpoints.Testnet, FetchedAt)
            .GetValueOrThrow();

        Assert.IsTrue(snapshot.TryGetSymbol("BTCUSDT", out _));
        Assert.IsFalse(snapshot.TryGetSymbol("ELSAUSDT", out _));

        Assert.IsTrue(snapshot.TryGetRejection("ELSAUSDT", out var rejection));
        Assert.IsNotNull(rejection);
        Assert.AreEqual(TradeErrorCodes.InvalidSymbolRules, rejection!.Reason.Code);
        StringAssert.Contains(rejection.Reason.Message, "ELSAUSDT", StringComparison.Ordinal);

        Assert.IsFalse(snapshot.TryGetRejection("BTCUSDT", out _));
        Assert.IsFalse(snapshot.TryGetRejection("   ", out var blank));
        Assert.IsNull(blank);
    }

    [TestMethod]
    public void WhenEverySymbolFailsTheWholeParseFails()
    {
        // 全部商品都解析不了不是資料瑕疵,是回應格式變了或對映寫錯。這時回傳一份空的交易規則,
        // 會讓系統看起來運作正常卻一張單都下不出去。
        // Every symbol failing is not a data quirk but a changed format or a broken mapping, and an empty rule
        // set would leave the system looking healthy while unable to place a single order.
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            "\"stepSize\":",
            "\"stepSize_removed\":",
            StringComparison.Ordinal);

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
        StringAssert.Contains(result.Error.Message, "格式改變", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ASymbolWithoutEvenANameIsRecordedByItsPosition()
    {
        // 連代碼都讀不出來時仍要留下紀錄,否則那一筆會無聲消失,而「少了一個商品」沒有任何症狀。
        // A record is kept even when the code itself is unreadable; otherwise the entry vanishes silently, and
        // "one symbol fewer" has no symptom at all.
        // 單引號寫成 JSON 再換回雙引號,省掉一層跳脫,讀起來才看得出資料長什麼樣。
        // Single quotes swapped for double ones afterwards: one fewer escaping layer, and the shape of the
        // data stays readable.
        var json = ("{'serverTime':1789109384268,'symbols':["
            + "{'baseAsset':'X'},"
            + "{'symbol':'BTCUSDT','baseAsset':'BTC','quoteAsset':'USDT','status':'TRADING','filters':["
            + "{'filterType':'PRICE_FILTER','tickSize':'0.1','minPrice':'1','maxPrice':'2'},"
            + "{'filterType':'LOT_SIZE','stepSize':'0.001','minQty':'0.001','maxQty':'1000'},"
            + "{'filterType':'MARKET_LOT_SIZE','stepSize':'0.001','minQty':'0.001','maxQty':'120'},"
            + "{'filterType':'MIN_NOTIONAL','notional':'50'}]}]}").Replace('\'', '"');

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        Assert.IsTrue(result.TryGetValue(out var snapshot));
        Assert.HasCount(1, snapshot.Symbols);
        Assert.HasCount(1, snapshot.RejectedSymbols);
        Assert.AreEqual("#0", snapshot.RejectedSymbols[0].Name);
    }

    [TestMethod]
    public void UnknownFilterTypesAreIgnoredRatherThanRejected()
    {
        // 幣安新增篩選器類型時,不該讓整份交易規則變成解析失敗。
        // A newly introduced filter type must not fail the whole rule set.
        var json = Fixtures.ExchangeInfoMainnet.Replace(
            "\"filterType\": \"MAX_NUM_ORDERS\"",
            "\"filterType\": \"SOME_FUTURE_FILTER\"",
            StringComparison.Ordinal);

        var result = BinanceExchangeInfoParser.Parse(json, BinanceEndpoints.Mainnet, FetchedAt);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.GetValueOrThrow().TryGetSymbol("BTCUSDT", out var btc));
        Assert.IsNull(btc!.MaxOpenOrders);
    }
}
