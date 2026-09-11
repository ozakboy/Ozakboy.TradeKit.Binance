using Microsoft.Extensions.DependencyInjection;

namespace Ozakboy.TradeKit.Binance.Tests;

/// <summary>
/// 需要真實 Testnet 憑證的整合測試。沒有憑證時判定為 Inconclusive,而不是通過。
/// Integration tests that need real Testnet credentials. Without them they report Inconclusive rather than
/// passing.
/// </summary>
/// <remarks>
/// <para>
/// 憑證一律從環境變數 <c>BINANCE_TESTNET_API_KEY</c> 與 <c>BINANCE_TESTNET_API_SECRET</c> 讀取,
/// 絕不寫進任何檔案。這幾條測試會實際連線,因此不在一般測試回合中執行:
/// 以 <c>dotnet test --filter "TestCategory=Testnet"</c> 明確指定才會跑。
/// Credentials always come from the <c>BINANCE_TESTNET_API_KEY</c> and
/// <c>BINANCE_TESTNET_API_SECRET</c> environment variables and never from a file. These tests reach the
/// network, so they do not run in an ordinary pass; select them with
/// <c>dotnet test --filter "TestCategory=Testnet"</c>.
/// </para>
/// <para>
/// 缺憑證時刻意<b>不</b>回報通過。一個「沒跑但綠燈」的測試比沒有測試更危險 ——
/// 它會讓「帳戶查詢驗過了」這件事在完全沒有驗過的情況下被記成已完成。
/// A missing credential deliberately does <b>not</b> pass. A green test that never ran is worse than no test:
/// it records "the account query was verified" when nothing of the sort happened.
/// </para>
/// <para>
/// 只呼叫唯讀端點。下單、撤單、改槓桿等會改變帳戶狀態的操作不在這裡,也不屬於這一階段。
/// Read-only endpoints only. Nothing here places, cancels, or modifies anything.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceTestnetIntegrationTests
{
    private const string ApiKeyVariable = "BINANCE_TESTNET_API_KEY";
    private const string SecretVariable = "BINANCE_TESTNET_API_SECRET";

    private static ServiceProvider? BuildOrSkip()
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        var secret = Environment.GetEnvironmentVariable(SecretVariable);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secret))
        {
            Assert.Inconclusive(
                $"未設定 {ApiKeyVariable} 與 {SecretVariable},略過需要真實憑證的整合測試。Skipped: {ApiKeyVariable} and {SecretVariable} are not set.");
            return null;
        }

        var services = new ServiceCollection();

        services.AddBinanceFutures(options =>
        {
            options.Environment = BinanceEnvironment.Testnet;
            options.ApiKey = apiKey;
            options.SecretKey = secret;
        });

        return services.BuildServiceProvider();
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReadsTheTradingRulesFromTheTestnet()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var symbol = await client.GetSymbolAsync("BTCUSDT");

        Assert.IsTrue(symbol.IsSuccess, symbol.Error?.Message);
        Assert.IsTrue(symbol.GetValueOrThrow().StepSize > 0m);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReadsTheAccountSnapshot()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var snapshot = await client.GetAccountSnapshotAsync();

        Assert.IsTrue(snapshot.IsSuccess, snapshot.Error?.Message);
        Assert.IsTrue(snapshot.GetValueOrThrow().Balances.Count > 0);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task ReadsPositions()
    {
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();

        var positions = await client.GetPositionsAsync();

        Assert.IsTrue(positions.IsSuccess, positions.Error?.Message);
    }

    [TestMethod]
    [TestCategory("Testnet")]
    public async Task LocalClockAgreesWithTheExchangeWithinTheRecvWindow()
    {
        // 時鐘偏移的症狀是每一個簽章請求都失敗,而錯誤訊息看起來像簽章問題。
        // 這條測試把它變成一個說得出原因的失敗。
        // Clock drift shows up as every signed request failing with what looks like a signature problem; this
        // turns it into a failure that says why.
        using var provider = BuildOrSkip();
        var client = provider!.GetRequiredService<BinanceFuturesClient>();
        var options = provider!.GetRequiredService<BinanceOptions>();

        var serverTime = await client.GetServerTimeAsync();

        Assert.IsTrue(serverTime.IsSuccess, serverTime.Error?.Message);

        var drift = (DateTimeOffset.UtcNow - serverTime.GetValueOrThrow()).Duration();

        Assert.IsLessThanOrEqualTo(
            options.RecvWindow,
            drift,
            $"本機時鐘與交易所相差 {drift},超過 recvWindow {options.RecvWindow},簽章請求會全部失敗。");
    }
}
