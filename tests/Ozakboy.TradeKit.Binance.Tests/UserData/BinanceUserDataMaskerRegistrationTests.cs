using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Ozakboy.Http;
using Ozakboy.Security.Masking;
using Ozakboy.TradeKit.Binance.Tests.TestSupport;
using Ozakboy.WebSockets;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 串流憑證一取得就被登記成遮罩器的已知祕密,之後它出現在哪裡都會被換成遮罩字串。
/// The stream credential is registered as a known secret on the masker the moment it is obtained, so wherever it
/// turns up afterwards it comes out as the mask segment.
/// </summary>
/// <remarks>
/// <para>
/// 這一組與 <see cref="BinanceUserDataCredentialLeakTests"/> 驗的<b>不是</b>同一件事,兩組都要有。
/// 那一組驗的是「本套件自己產生的文字裡沒有憑證」—— 結構上的保護;這一組驗的是
/// 「憑證即使從本套件管不到的路徑流出去,也會被字面替換攔下來」。兩者的差別在
/// <b>欄位名規則看不見沒有欄位名的位置</b>:位址的路徑段、其他套件已經格式化好的訊息、例外文字。
/// These tests check something <b>different</b> from <see cref="BinanceUserDataCredentialLeakTests"/> and both
/// are needed. That class checks that no text this package produces carries the credential — structural
/// protection. This one checks that a credential leaving by a route this package does not control is still
/// caught, by literal replacement. The difference is that <b>a field-name rule cannot see a position that has
/// no field name</b>: a path segment of an address, a message another package has already formatted, exception
/// text.
/// </para>
/// <para>
/// <b>這一組被故意弄壞驗證過。</b> 把 <c>BinanceListenKeyClient.CreateAsync</c> 裡呼叫
/// <c>RegisterKnownCredential</c> 的那一行拿掉,743 條測試裡有 4 條變紅,而且紅的正是該紅的四條:
/// <see cref="TheCredentialIsRegisteredWithTheMaskerAsSoonAsItIsObtained"/>(<c>KnownSecretCount</c> 仍是 0)、
/// <see cref="AnAddressWrittenToALogInFullComesOutMasked"/>、
/// <see cref="TheRegistrationLandsOnTheMaskerTheContainerGaveTheHttpClient"/>,以及
/// <see cref="ARenewedCredentialIsRegisteredAndTheEarlierOneStaysMasked"/> 的<b>舊</b>憑證那一半
/// —— 續期那一半仍然綠,因為續期的登記是另一個呼叫點,兩處各自獨立。改回來就全綠。
/// 一條從來沒紅過的安全測試,和沒有測試沒有分別。
/// <b>These tests have been verified by deliberately breaking them.</b> With the
/// <c>RegisterKnownCredential</c> call removed from <c>BinanceListenKeyClient.CreateAsync</c>, four of the 743
/// tests went red and they were the right four:
/// <see cref="TheCredentialIsRegisteredWithTheMaskerAsSoonAsItIsObtained"/> (the count stayed at zero),
/// <see cref="AnAddressWrittenToALogInFullComesOutMasked"/>,
/// <see cref="TheRegistrationLandsOnTheMaskerTheContainerGaveTheHttpClient"/>, and the <b>earlier</b>-credential
/// half of <see cref="ARenewedCredentialIsRegisteredAndTheEarlierOneStaysMasked"/> — its renewal half stayed
/// green, because the renewal registers at a separate call site and the two are independent. Restoring the line
/// restored green. A security test that has never failed is indistinguishable from no test.
/// </para>
/// </remarks>
[TestClass]
public sealed class BinanceUserDataMaskerRegistrationTests
{
    /// <summary>
    /// 第一把憑證。假值,但長度遠超過 <see cref="SecretMasker.MinimumKnownSecretLength"/>,登記得進去。
    /// The first credential: fake, and far longer than <see cref="SecretMasker.MinimumKnownSecretLength"/>, so it
    /// registers.
    /// </summary>
    private const string Canary = UserDataSamples.ListenKey;

    /// <summary>
    /// 續期時交易所換發的第二把。與第一把明顯不同,「換發之後兩把都還遮得到嗎」才問得清楚。
    /// The second credential, handed out by the exchange on renewal. Visibly different from the first, so that
    /// "are both still masked after a rotation" is a clean question.
    /// </summary>
    private const string RenewedCanary = "LISTENKEY-CANARY-RENEWED-NOT-A-REAL-CREDENTIAL-4a5b6c7d";

    private const string ApiKey = "FAKE-API-KEY-NOT-A-REAL-CREDENTIAL";
    private const string SecretKey = "FAKE-SECRET-NOT-A-REAL-CREDENTIAL";

    /// <summary>
    /// 「連線層把完整位址寫進日誌」的那一行。用 <see cref="LoggerMessage"/> 是因為那正是一個正經的連線層會用的寫法
    /// —— 位址以結構化參數傳進去,格式化之後就只是訊息字串的一部分,欄位名從那一刻起不存在。
    /// The line standing in for a connection layer logging the whole address. It goes through
    /// <see cref="LoggerMessage"/> because that is how a serious connection layer writes one: the address arrives
    /// as a structured argument and, once formatted, is merely part of the message string with no field name left.
    /// </summary>
    private static readonly Action<ILogger, Uri, Exception?> LogConnecting =
        LoggerMessage.Define<Uri>(
            LogLevel.Information,
            new EventId(1, "Connecting"),
            "connecting to {Address}");

    [TestMethod]
    public async Task TheCredentialIsRegisteredWithTheMaskerAsSoonAsItIsObtained()
    {
        var masker = new SecretMasker();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection([]));
        var (feed, _, http) = UserDataFixture.Create(factory, masker: masker);

        using (http)
        {
            // 起點必須是乾淨的,否則「遮到了」可能只是因為別人先登記過。
            // The starting point has to be clean, or "it was masked" might only mean somebody else registered it
            // first.
            Assert.AreEqual(0, masker.KnownSecretCount, "遮罩器不是乾淨的,這條測試證明不了是誰登記的。");

            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                var dialled = factory.Created[0].ConnectedUri?.OriginalString ?? string.Empty;

                // 先確認 canary 真的就是撥號用的那把憑證,否則後面遮到什麼都不代表什麼。
                // First confirm the canary really is the credential dialled with; otherwise whatever gets masked
                // afterwards means nothing.
                Assert.Contains(
                    Canary,
                    dialled,
                    StringComparison.Ordinal,
                    "canary 不是撥號時用的憑證,這條測試驗不到任何東西。");

                Assert.AreEqual(1, masker.KnownSecretCount, "憑證拿到手了,卻沒有登記到遮罩器上。");

                var masked = masker.MaskText(dialled) ?? string.Empty;

                Assert.DoesNotContain(
                    Canary,
                    masked,
                    StringComparison.OrdinalIgnoreCase,
                    $"撥號位址經過遮罩仍帶著憑證:{masked}");

                // 遮掉的只有祕密,不是整段位址 —— 否則診斷時連「連的是哪一台」都看不出來。
                // Only the secret is replaced, not the whole address, or diagnostics lose even which host was
                // dialled.
                Assert.Contains(
                    factory.Created[0].ConnectedUri!.Authority,
                    masked,
                    StringComparison.Ordinal,
                    "遮罩把整段位址都吃掉了,該遮的只有祕密。");
            }
        }
    }

    [TestMethod]
    public async Task AnAddressWrittenToALogInFullComesOutMasked()
    {
        var masker = new SecretMasker();
        var loggers = new CollectingLoggerFactory();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection([]));
        var (feed, _, http) = UserDataFixture.Create(factory, masker: masker, loggerFactory: loggers);

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                var dialled = factory.Created[0].ConnectedUri;

                Assert.IsNotNull(dialled, "連線沒有記下撥號位址,這條測試沒有東西可以驗。");

                // 模擬本套件管不到的那一層:把完整位址當成一個參數寫進日誌。Ozakboy.WebSockets 0.2.1 只寫
                // authority,但「連線層寫了什麼」不是這個套件能保證的事 —— 換一個版本、換一個實作就變了,
                // 而字面替換這一道不必知道對方寫了什麼。
                // Standing in for a layer this package does not control: the whole address written to a log as one
                // argument. Ozakboy.WebSockets 0.2.1 logs only the authority, but what a connection layer writes is
                // not something this package can guarantee — another version or another implementation changes it —
                // while literal replacement never needs to know what the other side wrote.
                var logger = loggers.CreateLogger("Ozakboy.WebSockets.WebSocketClient");

                LogConnecting(logger, dialled, null);

                // 路徑段的形式。欄位名規則對它<b>完全</b>無能為力:那一段沒有名字。
                // 幣安使用者資料串流 0.1.0 撥的就是這個形狀。
                // The path-segment form, which a field-name rule <b>cannot</b> touch at all, because that segment
                // has no name. It is the shape this stream dialled in 0.1.0.
                LogConnecting(logger, new Uri($"wss://{dialled.Authority}/ws/{Canary}"), null);

                var raw = string.Join('\n', loggers.Messages);

                // 日誌裡真的有憑證,才談得上「遮掉了」。
                // The credential really is in the log; only then does "it was masked" mean anything.
                Assert.Contains(
                    Canary,
                    raw,
                    StringComparison.Ordinal,
                    "日誌裡根本沒有出現憑證,這條測試驗不到遮罩。");

                var masked = masker.MaskText(raw) ?? string.Empty;

                Assert.DoesNotContain(
                    Canary,
                    masked,
                    StringComparison.OrdinalIgnoreCase,
                    $"日誌經過遮罩仍帶著憑證:\n{masked}");

                Assert.Contains(
                    dialled.Authority,
                    masked,
                    StringComparison.Ordinal,
                    "遮罩把整段日誌都吃掉了,該遮的只有祕密。");
            }
        }
    }

    [TestMethod]
    public async Task ARenewedCredentialIsRegisteredAndTheEarlierOneStaysMasked()
    {
        var masker = new SecretMasker();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection([]));
        var (feed, stub, http) = UserDataFixture.Create(
            factory,
            new BinanceUserDataStreamOptions { ListenKeyKeepAliveInterval = TimeSpan.FromMilliseconds(50) },
            masker: masker,
            keepAliveResponder: static () => UserDataSamples.ListenKeyResponse(RenewedCanary));

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                await UserDataFixture.WaitUntilAsync(
                    () => stub.Requests.Any(request => request.Method == HttpMethod.Put),
                    "續期從來沒有跑到,換發的憑證有沒有被登記就無從驗起。");

                // 續期的回應本體帶回了另一把,它同樣是能連上帳戶私有資料的憑證。
                // The renewal came back with a different credential, and it too opens the account's private data.
                await UserDataFixture.WaitUntilAsync(
                    () => (masker.MaskText(RenewedCanary) ?? RenewedCanary) != RenewedCanary,
                    "續期換發的憑證沒有被登記到遮罩器上。");

                // 舊的那一把不移除。已經失效的憑證被多遮一次沒有壞處,少遮一次就是外流。
                // The earlier key is not removed: masking a lapsed credential once more costs nothing, masking it
                // once less is a leak.
                foreach (var credential in new[] { Canary, RenewedCanary })
                {
                    var masked = masker.MaskText($"wss://host/private/ws?listenKey={credential}") ?? string.Empty;

                    Assert.DoesNotContain(
                        credential,
                        masked,
                        StringComparison.OrdinalIgnoreCase,
                        $"這一把憑證沒有被遮掉:{masked}");
                }
            }
        }
    }

    [TestMethod]
    public async Task TheRegistrationLandsOnTheMaskerTheContainerGaveTheHttpClient()
    {
        // 這一條是整串接線的驗收:遮罩器不是測試自己 new 的,而是 AddBinanceFutures 為這個具名用戶端建立、
        // 日誌處理器與重試處理器實際在用的那一個。登記到別的遮罩器上一樣會成功,但什麼也沒保護到,
        // 而且不會有任何跡象。
        // This is the acceptance check on the whole wiring: the masker is not one the test built but the one
        // AddBinanceFutures created for this named client and that its logging and retry handlers actually use.
        // Registering on a different masker succeeds just as well and protects nothing, with no sign of it.
        var stub = UserDataFixture.ListenKeyStub(Canary);
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection([]));
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(TestClock.AtFixedInstant());
        services.AddSingleton<IWebSocketConnectionFactory>(factory);

        services.AddBinanceFutures(options =>
        {
            options.Environment = BinanceEnvironment.Testnet;
            options.ApiKey = ApiKey;
            options.SecretKey = SecretKey;
        });

        services.AddBinanceUserData();

        // 只換掉最內層的傳輸,其餘全部是正式註冊的那一份。
        // Only the innermost transport is replaced; everything else is what the real registration built.
        services.AddHttpClient(BinanceConstants.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        await using var provider = services.BuildServiceProvider();

        var masker = provider.GetOzakboyHttpMasker(BinanceConstants.HttpClientName);
        var feed = provider.GetRequiredService<BinanceUserDataFeed>();
        var started = await feed.StartAsync();

        Assert.IsTrue(started.IsSuccess, started.Error?.Message);

        var dialled = factory.Created[0].ConnectedUri?.OriginalString ?? string.Empty;

        Assert.Contains(
            Canary,
            dialled,
            StringComparison.Ordinal,
            "canary 不是撥號時用的憑證,這條測試驗不到任何東西。");

        Assert.DoesNotContain(
            Canary,
            masker.MaskText(dialled) ?? string.Empty,
            StringComparison.OrdinalIgnoreCase,
            "憑證沒有登記到這個具名用戶端的遮罩器上,相依注入那條線接錯了。");
    }

    [TestMethod]
    public async Task WithoutAMaskerTheCredentialIsNotRegisteredAnywhere()
    {
        // 舊多載仍然可用,而它的後果要講清楚:憑證只剩結構上的保護,字面替換那一道完全沒有。
        // 這一條同時是前面幾條的對照組 —— 沒有它,「遮到了」可能只是遮罩器本來就會遮任何東西。
        // The older overload still works and its consequence has to be stated: the credential keeps only its
        // structural protection and gets no literal replacement at all. This also serves as the control for the
        // tests above; without it, "it was masked" could just mean the masker masks anything.
        var masker = new SecretMasker();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection([]));
        var (feed, _, http) = UserDataFixture.Create(factory);

        using (http)
        {
            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);

                var dialled = factory.Created[0].ConnectedUri?.OriginalString ?? string.Empty;

                Assert.AreEqual(0, masker.KnownSecretCount);
                Assert.Contains(Canary, masker.MaskText(dialled) ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [TestMethod]
    public async Task ACredentialResponseWithoutACredentialRegistersNothingAndStillFails()
    {
        // 讀不出憑證時不可以登記任何東西 —— 尤其不可以把整段本體或某個湊合的欄位當成祕密登記進去。
        // A body with no credential must register nothing, and above all must not register the whole body or some
        // improvised field as a secret.
        var masker = new SecretMasker();
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();
        var stub = StubHttpMessageHandler.Json($$"""{"notTheExpectedField":"{{Canary}}"}""");
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        using (http)
        {
            var feed = new BinanceUserDataFeed(
                pipeline,
                options,
                masker,
                streamOptions: null,
                loggerFactory: null,
                timeProvider: clock,
                connectionFactory: new FakeWebSocketConnectionFactory());

            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsFailure, "回應裡沒有憑證,啟動不應該成功。");
                Assert.AreEqual(0, masker.KnownSecretCount, "讀不出憑證卻登記了東西。");
            }
        }
    }

    [TestMethod]
    public async Task ACredentialTooShortToRegisterIsSkippedRatherThanThrowing()
    {
        // 遮罩器拒絕過短的值是它的設計(全域字面替換會誤遮大量正常文字)。這裡驗的是本套件的反應:
        // 略過,而不是讓 ArgumentException 打到啟動路徑上 —— 那會把一個畸形的回應變成啟動失敗。
        // The masker refuses values that short by design, since a global literal replacement would mask swathes
        // of ordinary text. What is checked here is this package's reaction: skip it, rather than let the
        // ArgumentException reach the start-up path and turn a malformed response into a failure to start.
        var masker = new SecretMasker();
        var options = TestPipeline.CreateOptions(BinanceEnvironment.Testnet);
        var clock = TestClock.AtFixedInstant();
        var shortKey = new string('k', SecretMasker.MinimumKnownSecretLength - 1);
        var stub = UserDataFixture.ListenKeyStub(shortKey);
        var (pipeline, http) = TestPipeline.Create(options, stub, clock);

        using (http)
        {
            var feed = new BinanceUserDataFeed(
                pipeline,
                options,
                masker,
                streamOptions: null,
                loggerFactory: null,
                timeProvider: clock,
                connectionFactory: new FakeWebSocketConnectionFactory());

            await using (feed)
            {
                var started = await feed.StartAsync();

                Assert.IsTrue(started.IsSuccess, started.Error?.Message);
                Assert.AreEqual(0, masker.KnownSecretCount, "過短的值不應該被登記。");
            }
        }
    }
}
