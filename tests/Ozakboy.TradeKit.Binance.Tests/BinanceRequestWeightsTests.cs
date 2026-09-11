namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceRequestWeightsTests
{
    /// <summary>
    /// 端點名稱到宣告權重的對照,供逐項比對用。
    /// The declared weight of each endpoint, for item-by-item comparison.
    /// </summary>
    private static Dictionary<string, int> DeclaredWeights() => new(StringComparer.Ordinal)
    {
        ["exchangeInfo"] = BinanceRequestWeights.ExchangeInfo,
        ["time"] = BinanceRequestWeights.ServerTime,
        ["ping"] = BinanceRequestWeights.Ping,
        ["trades"] = BinanceRequestWeights.RecentTrades,
        ["account"] = BinanceRequestWeights.Account,
        ["balance"] = BinanceRequestWeights.Balance,
        ["positionRisk"] = BinanceRequestWeights.PositionRisk,
        ["allOrders"] = BinanceRequestWeights.AllOrders,
        ["userTrades"] = BinanceRequestWeights.UserTrades,
        ["leverageBracket"] = BinanceRequestWeights.LeverageBracket,
        ["placeOrder"] = BinanceRequestWeights.PlaceOrder,
        ["queryOrder"] = BinanceRequestWeights.QueryOrder,
        ["cancelOrder"] = BinanceRequestWeights.CancelOrder,
        ["cancelAllOpenOrders"] = BinanceRequestWeights.CancelAllOpenOrders,
        ["accountSetting"] = BinanceRequestWeights.AccountSetting,
        ["default"] = BinanceRequestWeights.Default,
    };

    [TestMethod]
    public void PublicEndpointWeightsMatchWhatTheExchangeReported()
    {
        // 2026-09-11 實測:單次呼叫之後 x-mbx-used-weight-1m 標頭為 1。
        // Measured on 2026-09-11: the x-mbx-used-weight-1m header read 1 after a single call.
        var weights = DeclaredWeights();

        Assert.AreEqual(1, weights["exchangeInfo"]);
        Assert.AreEqual(1, weights["time"]);
        Assert.AreEqual(1, weights["ping"]);
    }

    [TestMethod]
    public void SignedAccountEndpointsCostFive()
    {
        var weights = DeclaredWeights();

        Assert.AreEqual(5, weights["account"]);
        Assert.AreEqual(5, weights["balance"]);
        Assert.AreEqual(5, weights["positionRisk"]);
    }

    [TestMethod]
    public void EveryDeclaredWeightIsUsableByTheLimiter()
    {
        // WithWeight 只接受正整數,而且權重不得超過最小配額桶的上限,否則等多久都排不到額度。
        // WithWeight only accepts positive integers, and a weight above the smallest bucket can never be met.
        var ceiling = BinanceRateLimits.MainnetWeightPerMinute;

        var weights = DeclaredWeights();
        weights["openOrders(all)"] = BinanceRequestWeights.OpenOrders(hasSymbol: false);
        weights["markPrice(all)"] = BinanceRequestWeights.MarkPrice(hasSymbol: false);
        weights["klines(1500)"] = BinanceRequestWeights.Klines(1500);
        weights["depth(1000)"] = BinanceRequestWeights.Depth(1000);

        foreach (var (name, weight) in weights)
        {
            Assert.IsGreaterThanOrEqualTo(1, weight, $"{name} 的權重必須是正整數。");
            Assert.IsLessThanOrEqualTo(ceiling, weight, $"{name} 的權重超過配額桶上限,等多久都排不到。");
        }
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(99, 1)]
    [DataRow(100, 2)]
    [DataRow(499, 2)]
    [DataRow(500, 5)]
    [DataRow(1000, 5)]
    [DataRow(1001, 10)]
    [DataRow(1500, 10)]
    public void KlineWeightFollowsTheOfficialTable(int limit, int expected)
    {
        Assert.AreEqual(expected, BinanceRequestWeights.Klines(limit));
    }

    [TestMethod]
    [DataRow(5, 2)]
    [DataRow(50, 2)]
    [DataRow(100, 5)]
    [DataRow(500, 10)]
    [DataRow(1000, 20)]
    public void DepthWeightFollowsTheOfficialTable(int limit, int expected)
    {
        Assert.AreEqual(expected, BinanceRequestWeights.Depth(limit));
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 40)]
    public void OmittingASymbolIsFortyTimesAsExpensiveOnOpenOrders(bool hasSymbol, int expected)
    {
        Assert.AreEqual(expected, BinanceRequestWeights.OpenOrders(hasSymbol));
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 10)]
    public void OmittingASymbolIsTenTimesAsExpensiveOnMarkPrice(bool hasSymbol, int expected)
    {
        Assert.AreEqual(expected, BinanceRequestWeights.MarkPrice(hasSymbol));
    }

    [TestMethod]
    public void WeightHelpersRejectNonPositiveLimits()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceRequestWeights.Klines(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinanceRequestWeights.Depth(0));
    }

    [TestMethod]
    public void TheTableCanBeRenderedForStartupLogging()
    {
        var described = BinanceRequestWeights.Describe();

        StringAssert.Contains(described, "exchangeInfo=1", StringComparison.Ordinal);
        StringAssert.Contains(described, "positionRisk=5", StringComparison.Ordinal);
    }
}
