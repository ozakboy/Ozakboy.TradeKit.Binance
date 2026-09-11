using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance.Tests;

[TestClass]
public sealed class BinanceOptionsTests
{
    [TestMethod]
    public void DefaultsToProductionWithNoCredentials()
    {
        var options = new BinanceOptions();

        Assert.AreEqual(BinanceEnvironment.Mainnet, options.Environment);
        Assert.IsFalse(options.HasCredentials);
        Assert.AreEqual(BinanceEndpoints.Mainnet, options.ResolveEndpoints());
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.RecvWindow);
        Assert.AreEqual(TimeSpan.FromHours(24), options.ExchangeInfoCacheTtl);
        Assert.IsTrue(options.Validate().IsSuccess);
    }

    [TestMethod]
    public void ShipsNoDefaultCredentials()
    {
        // 套件內不得存在任何預設金鑰。空字串是唯一可接受的預設。
        // The package must ship no default credentials; empty is the only acceptable default.
        var options = new BinanceOptions();

        Assert.AreEqual(string.Empty, options.ApiKey);
        Assert.AreEqual(string.Empty, options.SecretKey);
    }

    [TestMethod]
    public void HasCredentialsRequiresBothHalves()
    {
        var options = new BinanceOptions { ApiKey = "FAKE-KEY" };
        Assert.IsFalse(options.HasCredentials);

        options.SecretKey = "   ";
        Assert.IsFalse(options.HasCredentials);

        options.SecretKey = "FAKE-SECRET";
        Assert.IsTrue(options.HasCredentials);
    }

    [TestMethod]
    public void ValidateStillSucceedsWithoutCredentials()
    {
        // 公開端點不需要憑證,把憑證列為必填會擋住「只想查交易規則」的用途。
        // Public endpoints need none, and requiring them would block the rules-only use case.
        Assert.IsTrue(new BinanceOptions().Validate().IsSuccess);
    }

    [TestMethod]
    public void ValidateRejectsAnUndefinedEnvironment()
    {
        var result = new BinanceOptions { Environment = (BinanceEnvironment)42 }.Validate();

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(BinanceErrorCodes.InvalidOptions, result.Error!.Code);
    }

    [TestMethod]
    public void ValidateRejectsARecvWindowOutsideTheAllowedRange()
    {
        Assert.IsTrue(new BinanceOptions { RecvWindow = TimeSpan.Zero }.Validate().IsFailure);
        Assert.IsTrue(new BinanceOptions { RecvWindow = TimeSpan.FromSeconds(-1) }.Validate().IsFailure);
        Assert.IsTrue(new BinanceOptions { RecvWindow = TimeSpan.FromSeconds(61) }.Validate().IsFailure);
        Assert.IsTrue(new BinanceOptions { RecvWindow = TimeSpan.FromSeconds(60) }.Validate().IsSuccess);
    }

    [TestMethod]
    public void ValidateRejectsNonPositiveCacheLifetimeAndTimeouts()
    {
        Assert.IsTrue(new BinanceOptions { ExchangeInfoCacheTtl = TimeSpan.Zero }.Validate().IsFailure);
        Assert.IsTrue(new BinanceOptions { RequestWeightPerMinute = 0 }.Validate().IsFailure);
        Assert.IsTrue(new BinanceOptions { RateLimitAcquisitionTimeout = TimeSpan.Zero }.Validate().IsFailure);
    }

    [TestMethod]
    public void ValidatePropagatesPipelineOptionFailures()
    {
        var options = new BinanceOptions();
        options.Timeouts.OverallTimeout = TimeSpan.FromSeconds(1);
        options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(10);

        Assert.IsTrue(options.Validate().IsFailure);
    }

    [TestMethod]
    public void EndpointOverrideWinsOverEnvironment()
    {
        var custom = BinanceEndpoints.CreateOverride(
                "代理 / proxy",
                new Uri("https://proxy.internal"),
                new Uri("wss://proxy.internal"),
                isTestnet: false)
            .GetValueOrThrow();

        var options = new BinanceOptions { Environment = BinanceEnvironment.Testnet, EndpointOverride = custom };

        Assert.AreSame(custom, options.ResolveEndpoints());
        Assert.IsTrue(options.Validate().IsSuccess);
    }

    [TestMethod]
    public void WeightCeilingDefaultsToProductionOnBothEnvironments()
    {
        // Testnet 宣告 6000,但預設仍採主網的 2400:在 Testnet 跑得過的節奏必須在主網也跑得過。
        // Testnet advertises 6000, yet the default stays at production's 2400.
        Assert.AreEqual(
            BinanceRateLimits.MainnetWeightPerMinute,
            new BinanceOptions { Environment = BinanceEnvironment.Mainnet }.ResolveWeightPerMinute());

        Assert.AreEqual(
            BinanceRateLimits.MainnetWeightPerMinute,
            new BinanceOptions { Environment = BinanceEnvironment.Testnet }.ResolveWeightPerMinute());

        Assert.AreNotEqual(
            BinanceRateLimits.TestnetAdvertisedWeightPerMinute,
            new BinanceOptions { Environment = BinanceEnvironment.Testnet }.ResolveWeightPerMinute());
    }

    [TestMethod]
    public void WeightCeilingCanBeOverridden()
    {
        var options = new BinanceOptions { RequestWeightPerMinute = 600 };

        Assert.AreEqual(600, options.ResolveWeightPerMinute());
        Assert.AreEqual(600, options.CreateRateLimitOptions().Buckets[0].PermitLimit);
    }

    [TestMethod]
    public void SigningOptionsUseBinanceHeaderAndQueryPlacement()
    {
        var options = new BinanceOptions { ApiKey = "FAKE-KEY", SecretKey = "FAKE-SECRET" };
        var signing = options.CreateSigningOptions();

        Assert.AreEqual("X-MBX-APIKEY", signing.ApiKeyHeaderName);
        Assert.AreEqual("signature", signing.SignatureParameterName);
        Assert.IsTrue(signing.SendApiKeyHeader);

        // 全部參數放查詢字串,POST 也一樣 —— 混合模式的官方範例算不出刊登的簽章值。
        // Everything goes in the query string, POST included.
        Assert.AreEqual(SignedPayloadPlacement.QueryString, signing.Placement);
        Assert.IsTrue(signing.Validate().IsSuccess);
    }

    [TestMethod]
    public void RateLimitOptionsCarryOneRequestWeightBucket()
    {
        var options = new BinanceOptions().CreateRateLimitOptions();

        Assert.HasCount(1, options.Buckets);
        Assert.AreEqual(BinanceRateLimits.RequestWeightBucketName, options.Buckets[0].Name);
        Assert.AreEqual(TimeSpan.FromMinutes(1), options.Buckets[0].Window);
        Assert.AreEqual(BinanceRateLimits.MainnetWeightPerMinute, options.Buckets[0].PermitLimit);
        Assert.IsTrue(options.Validate().IsSuccess);
    }

    [TestMethod]
    public void LoggingOptionsMaskSignatureAndApiKey()
    {
        var options = new BinanceOptions();
        var logging = options.CreateLoggingOptions();

        Assert.Contains("signature", logging.AdditionalSensitiveParameterNames);
        Assert.Contains("X-MBX-APIKEY", logging.AdditionalSensitiveParameterNames);

        // 串流憑證是縱深防禦:目前沒有路徑會把它放進查詢字串,但萬一有,日誌裡也是遮蔽的。
        // The stream credential is defence in depth: nothing puts it into a query string today, and should
        // anything ever do so, the log still shows it masked.
        Assert.Contains("listenKey", logging.AdditionalSensitiveParameterNames);

        // 重複呼叫不應該把名字疊上去。
        // Calling twice must not duplicate the names.
        var second = options.CreateLoggingOptions();
        Assert.HasCount(BinanceConstants.SensitiveParameterNames.Count, second.AdditionalSensitiveParameterNames);
    }

    [TestMethod]
    public void RetryKeepsAnErrorBodySnippetSoBinanceCodesSurvive()
    {
        // 幣安的錯誤碼在回應本文裡。片段長度是 0 的話,重試耗盡後的錯誤只剩「HTTP 400」。
        // Binance error codes live in the body; a zero snippet length reduces an exhausted retry to "HTTP 400".
        Assert.IsGreaterThan(0, new BinanceOptions().Retry.ErrorBodySnippetLength);
    }
}
