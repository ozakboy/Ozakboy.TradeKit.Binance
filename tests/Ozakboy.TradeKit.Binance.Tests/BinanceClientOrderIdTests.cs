namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceClientOrderIdTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 11, 6, 49, 44, 268, TimeSpan.Zero);

    [TestMethod]
    public void AGeneratedIdFitsTheBinanceFormat()
    {
        var id = BinanceClientOrderId.Generate(Instant);

        Assert.IsTrue(BinanceClientOrderId.IsValid(id), id);
        Assert.IsLessThanOrEqualTo(BinanceClientOrderId.MaxLength, id.Length);
        StringAssert.StartsWith(id, BinanceClientOrderId.DefaultPrefix, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheTimestampIsPartOfTheIdSoALogReaderCanSeeWhenTheOrderWentOut()
    {
        var id = BinanceClientOrderId.Generate(Instant);

        StringAssert.Contains(
            id,
            Instant.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void TwoIdsFromTheSameMillisecondStillDiffer()
    {
        // 同一毫秒送出兩張單並不罕見。只靠時間戳會撞號,而撞號在幣安是 -4015 拒單。
        // Two orders within one millisecond are not unusual; a timestamp alone collides, and a collision is an
        // outright -4015 rejection.
        var first = BinanceClientOrderId.Generate(Instant);
        var second = BinanceClientOrderId.Generate(Instant);

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void TheParameterlessOverloadAlsoProducesAValidId()
    {
        Assert.IsTrue(BinanceClientOrderId.IsValid(BinanceClientOrderId.Generate()));
    }

    [TestMethod]
    [DataRow("a")]
    [DataRow("ozk-1789109384268-0a1b2c3d")]
    [DataRow("UPPER.lower:0123/_-")]
    [DataRow("012345678901234567890123456789012345")]
    public void AcceptsEveryFormBinanceAccepts(string id)
    {
        Assert.IsTrue(BinanceClientOrderId.IsValid(id), id);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("has space")]
    [DataRow("has#hash")]
    [DataRow("has\"quote")]
    [DataRow("0123456789012345678901234567890123456")]
    public void RejectsEveryFormBinanceRejects(string? id)
    {
        Assert.IsFalse(BinanceClientOrderId.IsValid(id), id);
    }

    [TestMethod]
    public void RejectsAPrefixWithCharactersBinanceWouldNotAccept()
    {
        Assert.ThrowsExactly<ArgumentException>(() => BinanceClientOrderId.Generate(Instant, "bad prefix "));
    }

    [TestMethod]
    public void RejectsAPrefixThatWouldPushTheIdPastTheLengthLimit()
    {
        // 前綴長度不是隨便加的:時間戳十三碼加上亂數八碼,前綴只剩十五碼可用。
        // The prefix budget is not arbitrary: thirteen digits of timestamp plus eight random characters leave
        // fifteen for the prefix.
        Assert.ThrowsExactly<ArgumentException>(
            () => BinanceClientOrderId.Generate(Instant, "this-prefix-is-far-too-long-"));
    }

    [TestMethod]
    public void AnEmptyPrefixIsAllowed()
    {
        var id = BinanceClientOrderId.Generate(Instant, string.Empty);

        Assert.IsTrue(BinanceClientOrderId.IsValid(id), id);
    }
}
