namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 讀取錄製下來的回應。來源與編輯狀況見 <c>Fixtures\PROVENANCE.md</c>。
/// Loads the recorded responses. Their provenance is documented in <c>Fixtures\PROVENANCE.md</c>.
/// </summary>
internal static class Fixtures
{
    public static string ExchangeInfoMainnet => Read("exchangeInfo-mainnet.json");

    public static string ExchangeInfoTestnet => Read("exchangeInfo-testnet.json");

    public static string ServerTime => Read("serverTime.json");

    public static string Account => Read("account.json");

    public static string PositionRisk => Read("positionRisk.json");

    public static string PositionRiskHedge => Read("positionRisk-hedge.json");

    private static string Read(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"找不到測試 fixture。Test fixture not found: {path}", path);
    }
}
