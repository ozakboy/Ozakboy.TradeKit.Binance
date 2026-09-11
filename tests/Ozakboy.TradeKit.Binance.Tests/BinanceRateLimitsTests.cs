using System.Text.Json;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceRateLimitsTests
{
    /// <summary>
    /// 從錄製的 exchangeInfo 回應裡讀出交易所自己宣告的 REQUEST_WEIGHT 上限。
    /// Reads the REQUEST_WEIGHT ceiling the exchange itself declares, out of the recorded response.
    /// </summary>
    private static int DeclaredCeiling(string exchangeInfoJson)
    {
        using var document = JsonDocument.Parse(exchangeInfoJson);

        foreach (var limit in document.RootElement.GetProperty("rateLimits").EnumerateArray())
        {
            if (limit.GetProperty("rateLimitType").GetString() == "REQUEST_WEIGHT"
                && limit.GetProperty("interval").GetString() == "MINUTE"
                && limit.GetProperty("intervalNum").GetInt32() == 1)
            {
                return limit.GetProperty("limit").GetInt32();
            }
        }

        throw new InvalidOperationException("錄製的回應裡沒有 REQUEST_WEIGHT 限制。");
    }

    [TestMethod]
    public void TheConstantsMatchWhatEachEnvironmentActuallyDeclares()
    {
        // 不是把常數跟常數比,而是跟 2026-09-11 那天交易所自己回的 rateLimits 比。
        // 幣安調整額度時,這條測試會紅,而不是靜靜地留著一個過時的數字。
        // Rather than comparing a constant to itself, this compares against the rateLimits the exchange
        // returned on 2026-09-11, so a change of allowance turns the test red instead of leaving a stale
        // number in place.
        Assert.AreEqual(
            BinanceRateLimits.MainnetWeightPerMinute,
            DeclaredCeiling(Fixtures.ExchangeInfoMainnet));

        Assert.AreEqual(
            BinanceRateLimits.TestnetAdvertisedWeightPerMinute,
            DeclaredCeiling(Fixtures.ExchangeInfoTestnet));
    }

    [TestMethod]
    public void BothEnvironmentsDefaultToTheSameCeiling()
    {
        // Testnet 宣告的額度較寬,但採用它會讓「測試環境一切正常、上線立刻被限流」——
        // 而那時系統已經在下真錢的單了。
        // Adopting the looser Testnet allowance means everything passes in testing and the 429s begin only
        // once real orders are going out.
        var mainnet = BinanceRateLimits.DefaultWeightPerMinute(BinanceEnvironment.Mainnet);
        var testnet = BinanceRateLimits.DefaultWeightPerMinute(BinanceEnvironment.Testnet);

        Assert.AreEqual(mainnet, testnet);
        Assert.AreEqual(DeclaredCeiling(Fixtures.ExchangeInfoMainnet), testnet);
        Assert.AreNotEqual(DeclaredCeiling(Fixtures.ExchangeInfoTestnet), testnet);
    }

    [TestMethod]
    public void RejectsAnUndefinedEnvironment()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BinanceRateLimits.DefaultWeightPerMinute((BinanceEnvironment)77));
    }

    [TestMethod]
    public void BuildsASingleRequestWeightBucketOverAOneMinuteWindow()
    {
        var options = BinanceRateLimits.CreateOptions(2400, TimeSpan.FromSeconds(30));

        // 只有一個桶。加上下單速率桶會讓查詢請求也扣掉下單額度,結果是行情查一查就下不了單。
        // Only one bucket: an order-rate bucket would let ordinary queries eat the order allowance.
        Assert.HasCount(1, options.Buckets);
        Assert.AreEqual(BinanceRateLimits.RequestWeightBucketName, options.Buckets[0].Name);
        Assert.AreEqual(2400, options.Buckets[0].PermitLimit);
        Assert.AreEqual(TimeSpan.FromMinutes(1), options.Buckets[0].Window);
        Assert.AreEqual(1, options.DefaultWeight);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.AcquisitionTimeout);
        Assert.IsTrue(options.Validate().IsSuccess);
    }

    [TestMethod]
    public void RejectsNonPositiveArguments()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BinanceRateLimits.CreateOptions(0, TimeSpan.FromSeconds(1)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BinanceRateLimits.CreateOptions(2400, TimeSpan.Zero));
    }
}
