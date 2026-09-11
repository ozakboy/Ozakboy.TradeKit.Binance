using System.Globalization;

namespace Ozakboy.TradeKit.Binance.Tests.UserData;

/// <summary>
/// 使用者資料串流的事件樣本。
/// Sample frames of the user data stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>來源與 <c>MarketDataSamples</c> 不同,這一點必須講清楚。</b> 行情那一份是從 Testnet 實際收到、
/// 一字未改的訊息;這一份是<b>依官方 USDⓈ-M Futures User Data Streams 文件組出來的</b>——
/// 帳戶事件只有在真的下單、真的成交、真的被追繳保證金的時候才會出現,錄不到就是錄不到。
/// 因此這些樣本驗的是「欄位對映有沒有照文件寫對」,而「文件有沒有說對」由
/// <c>BinanceUserDataTestnetTests</c> 那一組連線測試負責。兩者缺一不可,把它們混為一談會讓人以為
/// 單元測試綠燈就代表對映驗過了。
/// <b>The provenance differs from <c>MarketDataSamples</c> and that has to be said plainly.</b> The market
/// samples are frames received verbatim from the testnet; these are <b>constructed from the official USDⓈ-M
/// Futures User Data Streams documentation</b>, because account events only occur when an order is really
/// placed, really filled, or really margin-called, and what cannot be recorded cannot be recorded. These
/// samples therefore check that the field mapping follows the documentation, while whether the documentation
/// is right is the job of the live tests in <c>BinanceUserDataTestnetTests</c>. Both are needed, and conflating
/// them would let a green unit test pass for a verified mapping.
/// </para>
/// <para>
/// <see cref="OrderNew"/> 與 <see cref="OrderPartiallyFilled"/> 是<b>同一張單</b>
/// (<c>i</c> 都是 <c>8886774</c>):前者只是掛上去,後者成交了一半。這正是「一則
/// <c>ORDER_TRADE_UPDATE</c> 何時該多產生一筆成交」的分界。
/// <see cref="OrderNew"/> and <see cref="OrderPartiallyFilled"/> are the <b>same order</b> — both carry
/// <c>i</c> of <c>8886774</c> — first resting and then half filled. That is exactly the line between an
/// <c>ORDER_TRADE_UPDATE</c> that should also produce a fill and one that should not.
/// </para>
/// </remarks>
internal static class UserDataSamples
{
    /// <summary>
    /// 測試用的假串流憑證。刻意寫成一眼看得出是假的,而且夠獨特,grep 得到。
    /// The fake stream credential used in tests: obviously fake at a glance, and distinctive enough to grep for.
    /// </summary>
    public const string ListenKey = "LISTENKEY-CANARY-NOT-A-REAL-CREDENTIAL-0f1e2d3c";

    /// <summary>掛單成立,沒有成交。An order resting on the book, with no fill.</summary>
    public const string OrderNew =
        """{"e":"ORDER_TRADE_UPDATE","T":1789117380100,"E":1789117380188,"o":{"s":"BTCUSDT","c":"pulsetrade-uds-1","S":"BUY","o":"LIMIT","f":"GTC","q":"0.002","p":"74000.00","ap":"0","sp":"0","x":"NEW","X":"NEW","i":8886774,"l":"0","z":"0","L":"0","n":"0","N":"USDT","T":1789117380100,"t":0,"b":"148.00","a":"0","m":false,"R":false,"wt":"CONTRACT_PRICE","ot":"LIMIT","ps":"BOTH","cp":false,"rp":"0","pP":false,"si":0,"ss":0}}""";

    /// <summary>同一張單成交了一半。The same order, half filled.</summary>
    public const string OrderPartiallyFilled =
        """{"e":"ORDER_TRADE_UPDATE","T":1789117381000,"E":1789117381005,"o":{"s":"BTCUSDT","c":"pulsetrade-uds-1","S":"BUY","o":"LIMIT","f":"GTC","q":"0.002","p":"74000.00","ap":"73999.50","sp":"0","x":"TRADE","X":"PARTIALLY_FILLED","i":8886774,"l":"0.001","z":"0.001","L":"73999.50","n":"0.02959980","N":"USDT","T":1789117381000,"t":701001,"b":"74.00","a":"0","m":true,"R":false,"wt":"CONTRACT_PRICE","ot":"LIMIT","ps":"BOTH","cp":false,"rp":"1.25000000","pP":false,"si":0,"ss":0}}""";

    /// <summary>
    /// 條件單觸發之後的委託更新:<c>o</c> 已經變成實際掛出去的 <c>MARKET</c>,<c>ot</c> 仍是當初的 <c>STOP_MARKET</c>。
    /// An order update after a conditional trigger: <c>o</c> has become the <c>MARKET</c> actually placed while
    /// <c>ot</c> is still the <c>STOP_MARKET</c> that was submitted.
    /// </summary>
    public const string OrderTriggeredStop =
        """{"e":"ORDER_TRADE_UPDATE","T":1789117382500,"E":1789117382505,"o":{"s":"BTCUSDT","c":"pulsetrade-uds-2","S":"SELL","o":"MARKET","f":"GTC","q":"0.001","p":"0","ap":"0","sp":"73000.00","x":"NEW","X":"NEW","i":8886900,"l":"0","z":"0","L":"0","n":"0","N":"USDT","T":1789117382500,"t":0,"b":"0","a":"73.00","m":false,"R":true,"wt":"MARK_PRICE","ot":"STOP_MARKET","ps":"BOTH","cp":false,"rp":"0","pP":false,"si":0,"ss":0}}""";

    /// <summary>狀態對不上任何已知值的委託更新。An order update whose status maps to nothing known.</summary>
    public const string OrderUnknownStatus =
        """{"e":"ORDER_TRADE_UPDATE","T":1789117382600,"E":1789117382605,"o":{"s":"BTCUSDT","c":"pulsetrade-uds-3","S":"BUY","o":"LIMIT","f":"GTC","q":"0.001","p":"74000.00","ap":"0","sp":"0","x":"NEW","X":"NOT_A_REAL_STATUS","i":8886901,"l":"0","z":"0","L":"0","n":"0","N":"USDT","T":1789117382600,"t":0,"b":"74.00","a":"0","m":false,"R":false,"wt":"CONTRACT_PRICE","ot":"LIMIT","ps":"BOTH","cp":false,"rp":"0","pP":false,"si":0,"ss":0}}""";

    /// <summary>成交造成的帳戶增量。The account delta produced by a fill.</summary>
    public const string AccountUpdate =
        """{"e":"ACCOUNT_UPDATE","T":1789117382000,"E":1789117382003,"a":{"m":"ORDER","B":[{"a":"USDT","wb":"15000.12345678","cw":"15000.12345678","bc":"0"}],"P":[{"s":"BTCUSDT","pa":"0.001","ep":"73999.50000","bep":"73999.5","cr":"0","up":"-0.00045000","mt":"cross","iw":"0","ps":"BOTH"}]}}""";

    /// <summary>資金費結算造成的帳戶增量,只動到餘額。A funding settlement, which moves only the balance.</summary>
    public const string AccountUpdateFunding =
        """{"e":"ACCOUNT_UPDATE","T":1789117390000,"E":1789117390002,"a":{"m":"FUNDING_FEE","B":[{"a":"USDT","wb":"14999.98345678","cw":"14999.98345678","bc":"-0.14000000"}],"P":[]}}""";

    /// <summary>原因代碼是本套件沒收錄的值。An update whose reason code this package does not cover.</summary>
    public const string AccountUpdateUnknownReason =
        """{"e":"ACCOUNT_UPDATE","T":1789117391000,"E":1789117391002,"a":{"m":"A_REASON_ADDED_LATER","B":[{"a":"USDT","wb":"14999.00000000","cw":"14999.00000000","bc":"0"}],"P":[]}}""";

    /// <summary>保證金追繳警告。A margin call.</summary>
    public const string MarginCall =
        """{"e":"MARGIN_CALL","E":1789117383000,"cw":"3.16812045","p":[{"s":"ETHUSDT","ps":"LONG","pa":"1.327","mt":"CROSSED","iw":"0","mp":"7.10","up":"-1.166074","mm":"1.614445"}]}""";

    /// <summary>
    /// 本套件不處理的事件型別。交易所會持續新增這類事件,它們不是錯誤。
    /// An event type this package does not model. The exchange keeps adding them and they are not errors.
    /// </summary>
    public const string AccountConfigUpdate =
        """{"e":"ACCOUNT_CONFIG_UPDATE","T":1789117385000,"E":1789117385002,"ac":{"s":"BTCUSDT","l":20}}""";

    /// <summary>事件時間對應 <see cref="OrderNew"/> 的 <c>E</c>。The event time of <see cref="OrderNew"/>.</summary>
    public const long OrderNewEventTimeMs = 1789117380188L;

    /// <summary>憑證失效事件的 <c>E</c>。The <c>E</c> of the credential-expired event.</summary>
    public const long ListenKeyExpiredEventTimeMs = 1789117384000L;

    /// <summary>
    /// 憑證失效事件。<b>訊息本體帶著憑證</b>,這正是本套件絕不轉述串流訊息原文的理由。
    /// The credential-expired event. <b>The frame carries the credential</b>, which is precisely why this
    /// package never relays raw stream text.
    /// </summary>
    /// <param name="listenKey">要放進訊息裡的憑證。The credential to embed.</param>
    /// <returns>事件訊息。The frame.</returns>
    public static string ListenKeyExpired(string listenKey) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"e":"listenKeyExpired","E":{{ListenKeyExpiredEventTimeMs}},"listenKey":"{{listenKey}}"}""");

    /// <summary>
    /// 少了 <c>E</c> 的憑證失效事件,用來逼出一筆「缺少欄位」的失敗。
    /// A credential-expired event with no <c>E</c>, used to force a missing-field failure.
    /// </summary>
    /// <param name="listenKey">要放進訊息裡的憑證。The credential to embed.</param>
    /// <returns>事件訊息。The frame.</returns>
    /// <remarks>
    /// 這個樣本存在的唯一目的,是讓「解析失敗時會不會把訊息原文寫進錯誤」這件事有東西可驗 ——
    /// 而它的原文裡就有憑證。
    /// This sample exists only so that "does a parse failure write the raw frame into the error" has something
    /// to test against — and its raw text contains the credential.
    /// </remarks>
    public static string ListenKeyExpiredWithoutEventTime(string listenKey) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"e":"listenKeyExpired","listenKey":"{{listenKey}}"}""");

    /// <summary>
    /// 建立憑證端點的回應。<c>POST</c> 與 <c>PUT</c> 都是這個形狀 ——
    /// <c>PUT</c> 回的<b>不是</b>官方文件說的空物件,而是帶著完整憑證的物件。
    /// The credential endpoint response. Both <c>POST</c> and <c>PUT</c> have this shape: the <c>PUT</c> does
    /// <b>not</b> return the empty object the documentation describes but one carrying the whole credential.
    /// </summary>
    /// <param name="listenKey">憑證。The credential.</param>
    /// <returns>回應本體。The response body.</returns>
    public static string ListenKeyResponse(string listenKey) =>
        $$"""{"listenKey":"{{listenKey}}"}""";
}
