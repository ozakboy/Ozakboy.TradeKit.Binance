using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceResponseReaderTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 11, 7, 5, 53, TimeSpan.Zero);

    [TestMethod]
    public void ReadsServerTimeAsUtc()
    {
        var result = BinanceResponseReader.ReadServerTime(Fixtures.ServerTime);

        Assert.IsTrue(result.TryGetValue(out var serverTime));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789110429074L), serverTime);
        Assert.AreEqual(TimeSpan.Zero, serverTime.Offset);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("{oops")]
    [DataRow("{}")]
    [DataRow("""{"serverTime":0}""")]
    [DataRow("""{"serverTime":"not a number"}""")]
    public void RejectsAServerTimeItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadServerTime(json).IsFailure);
    }

    [TestMethod]
    public void ReadsPositionsAndDropsFlatOnesByDefault()
    {
        var result = BinanceResponseReader.ReadPositions(Fixtures.PositionRisk, AsOf);

        Assert.IsTrue(result.TryGetValue(out var positions));

        // fixture 有三筆,其中 DOGEUSDT 是空手的。
        // The fixture holds three rows, one of them flat.
        Assert.HasCount(2, positions);
        Assert.IsFalse(positions.Any(position => position.Symbol == "DOGEUSDT"));
    }

    [TestMethod]
    public void ReadsALongCrossPositionCompletely()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRisk, AsOf).GetValueOrThrow();
        var btc = positions.Single(position => position.Symbol == "BTCUSDT");

        Assert.AreEqual(0.015m, btc.Quantity);
        Assert.IsTrue(btc.IsLong);
        Assert.AreEqual(62000.00m, btc.EntryPrice);
        Assert.AreEqual(62822.66666666m, btc.MarkPrice);
        Assert.AreEqual(12.33999999m, btc.UnrealizedPnl);
        Assert.AreEqual(51230.10m, btc.LiquidationPrice);
        Assert.AreEqual(10, btc.Leverage);
        Assert.AreEqual(MarginMode.Cross, btc.MarginMode);
        Assert.AreEqual(PositionSide.Both, btc.Side);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1789109000000L), btc.UpdatedAt);

        // 名目價值是由 MarkPrice 算的 —— 這正是不能用帳戶端點那份沒有 markPrice 的持倉的原因。
        // The notional derives from MarkPrice, which is why the account endpoint's markPrice-less positions
        // cannot be used.
        Assert.IsGreaterThan(0m, btc.Notional);
    }

    [TestMethod]
    public void ReadsAShortIsolatedPositionCompletely()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRisk, AsOf).GetValueOrThrow();
        var eth = positions.Single(position => position.Symbol == "ETHUSDT");

        Assert.AreEqual(-0.500m, eth.Quantity);
        Assert.IsTrue(eth.IsShort);
        Assert.AreEqual(OrderSide.Buy, eth.ClosingSide);
        Assert.AreEqual(MarginMode.Isolated, eth.MarginMode);
        Assert.AreEqual(240.00000000m, eth.Margin);
        Assert.AreEqual(5, eth.Leverage);
    }

    [TestMethod]
    public void AZeroLiquidationPriceBecomesNullRatherThanZero()
    {
        // 強平價 0 代表「沒有強平價」,不是「會在零元被強平」。留成 0 會讓風控看到一個近在眼前的強平價。
        // A zero means there is none, not that liquidation happens at zero; keeping it would show risk
        // management an imminent liquidation.
        var positions = BinanceResponseReader
            .ReadPositions(Fixtures.PositionRisk, AsOf, includeFlat: true)
            .GetValueOrThrow();

        Assert.IsNull(positions.Single(position => position.Symbol == "DOGEUSDT").LiquidationPrice);
    }

    [TestMethod]
    public void AZeroUpdateTimeFallsBackToTheSnapshotMomentNotToNineteenSeventy()
    {
        var positions = BinanceResponseReader
            .ReadPositions(Fixtures.PositionRisk, AsOf, includeFlat: true)
            .GetValueOrThrow();

        Assert.AreEqual(AsOf, positions.Single(position => position.Symbol == "DOGEUSDT").UpdatedAt);
    }

    [TestMethod]
    public void ReadsHedgeModeSides()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskHedge, AsOf).GetValueOrThrow();

        Assert.HasCount(2, positions);
        Assert.AreEqual(PositionSide.Long, positions[0].Side);
        Assert.AreEqual(PositionSide.Short, positions[1].Side);
    }

    [TestMethod]
    [DataRow("symbol")]
    [DataRow("positionAmt")]
    [DataRow("entryPrice")]
    [DataRow("markPrice")]
    [DataRow("unRealizedProfit")]
    [DataRow("leverage")]
    [DataRow("marginType")]
    public void AMissingPositionFieldFails(string field)
    {
        var json = Fixtures.PositionRisk.Replace($"\"{field}\":", $"\"{field}_removed\":", StringComparison.Ordinal);
        var result = BinanceResponseReader.ReadPositions(json, AsOf);

        Assert.IsTrue(result.IsFailure, $"{field} 被拿掉之後應該解析失敗。");
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
        Assert.IsTrue(result.Error.TryGetData(BinanceErrorDataKeys.Field, out var named));
        StringAssert.Contains(named!, field, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheCapitalRSpellingIsWhatPositionRiskActuallyUses()
    {
        // unRealizedProfit(大寫 R)是 positionRisk 的拼法。抄成小寫不會報錯,只會讓浮動盈虧永遠是零 ——
        // 所以這裡明確驗證「小寫拼法會被當成缺欄位」,而不是預設值 0。
        // Copying the account endpoint's lower-case spelling would not fail; it would silently zero the P&L.
        var json = Fixtures.PositionRisk.Replace(
            "\"unRealizedProfit\":",
            "\"unrealizedProfit\":",
            StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadPositions(json, AsOf).IsFailure);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{oops")]
    [DataRow("""{"not":"an array"}""")]
    [DataRow("""["not an object"]""")]
    public void RejectsAPositionPayloadItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadPositions(json, AsOf).IsFailure);
    }

    [TestMethod]
    public void RejectsANonPositiveLeverage()
    {
        var json = Fixtures.PositionRisk.Replace("\"leverage\": \"10\"", "\"leverage\": \"0\"", StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadPositions(json, AsOf).IsFailure);
    }

    [TestMethod]
    public void ReadsAccountBalances()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRisk, AsOf).GetValueOrThrow();
        var result = BinanceResponseReader.ReadAccountSnapshot(Fixtures.Account, positions, AsOf);

        Assert.IsTrue(result.TryGetValue(out var snapshot));
        Assert.HasCount(2, snapshot.Balances);
        Assert.AreEqual(AsOf, snapshot.TakenAt);
        Assert.IsTrue(snapshot.CanTrade);
        Assert.IsFalse(snapshot.IsHedgeMode);

        Assert.IsTrue(snapshot.GetBalance("USDT").TryGetValue(out var usdt));
        Assert.AreEqual(1000.00000000m, usdt.WalletBalance);
        Assert.AreEqual(886.94000000m, usdt.AvailableBalance);
        Assert.AreEqual(12.34000000m, usdt.UnrealizedPnl);
        Assert.AreEqual(1012.34000000m, usdt.MarginBalance);
    }

    [TestMethod]
    public void PositionsComeFromTheCallerNotFromTheAccountPayload()
    {
        // fixture 的 account.positions 是空陣列,快照裡的持倉必須來自另外傳進來的那一份。
        // The fixture's account.positions is empty, so the snapshot's positions must be the ones passed in.
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRisk, AsOf).GetValueOrThrow();
        var snapshot = BinanceResponseReader
            .ReadAccountSnapshot(Fixtures.Account, positions, AsOf)
            .GetValueOrThrow();

        Assert.HasCount(2, snapshot.Positions);
        Assert.IsTrue(snapshot.GetPosition("BTCUSDT").IsSuccess);
    }

    [TestMethod]
    public void HedgeModeIsInferredFromThePositionSides()
    {
        var positions = BinanceResponseReader.ReadPositions(Fixtures.PositionRiskHedge, AsOf).GetValueOrThrow();
        var snapshot = BinanceResponseReader
            .ReadAccountSnapshot(Fixtures.Account, positions, AsOf)
            .GetValueOrThrow();

        Assert.IsTrue(snapshot.IsHedgeMode);
    }

    [TestMethod]
    [DataRow("asset")]
    [DataRow("walletBalance")]
    [DataRow("availableBalance")]
    [DataRow("unrealizedProfit")]
    public void AMissingBalanceFieldFails(string field)
    {
        var json = Fixtures.Account.Replace($"\"{field}\":", $"\"{field}_removed\":", StringComparison.Ordinal);
        var result = BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf);

        Assert.IsTrue(result.IsFailure, $"{field} 被拿掉之後應該解析失敗。");
        Assert.AreEqual(BinanceErrorCodes.MalformedResponse, result.Error!.Code);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{oops")]
    [DataRow("[]")]
    [DataRow("""{"canTrade":true}""")]
    [DataRow("""{"assets":["not an object"]}""")]
    public void RejectsAnAccountPayloadItCannotRead(string json)
    {
        Assert.IsTrue(BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf).IsFailure);
    }

    [TestMethod]
    public void AccountSnapshotRejectsNullPositions()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BinanceResponseReader.ReadAccountSnapshot(Fixtures.Account, null!, AsOf));
    }

    [TestMethod]
    public void MissingCanTradeIsTreatedAsTradableRatherThanBlocking()
    {
        // canTrade 缺漏時預設可交易。反過來預設「不可交易」會讓一個解析瑕疵直接停掉整個系統,
        // 而這個欄位不是風控的最後一道防線。
        // A missing canTrade defaults to tradable; defaulting the other way would let one parsing quirk halt
        // the whole system, and this flag is not the last line of risk control.
        var json = Fixtures.Account.Replace("\"canTrade\":", "\"canTrade_removed\":", StringComparison.Ordinal);

        Assert.IsTrue(BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf).GetValueOrThrow().CanTrade);
    }

    [TestMethod]
    public void ADisabledAccountIsReportedAsSuch()
    {
        var json = Fixtures.Account.Replace("\"canTrade\": true", "\"canTrade\": false", StringComparison.Ordinal);

        Assert.IsFalse(BinanceResponseReader.ReadAccountSnapshot(json, [], AsOf).GetValueOrThrow().CanTrade);
    }
}
