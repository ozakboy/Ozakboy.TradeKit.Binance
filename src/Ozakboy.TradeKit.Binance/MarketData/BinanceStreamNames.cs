namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 幣安 USDⓈ-M 合約行情串流的名稱與路徑組裝。
/// Builds the stream names and URLs of the Binance USDⓈ-M futures market streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>合約 WebSocket 依資料類別拆成三條路由,位址必須帶路由前綴。</b> 依據是幣安官方公告
/// (USDⓈ-M Futures「Important WebSocket Change Notice」):
/// Binance futures WebSockets are split into three routes by kind of data, and every address must carry its
/// route prefix. The source is Binance's official notice for USDⓈ-M futures, the "Important WebSocket Change
/// Notice":
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>/public</c>(<see cref="PublicRoute"/>):<c>bookTicker</c>、<c>depth</c> 等盤口資料。本套件目前沒有這類串流;
/// 日後加入 bookTicker 或 depth 時必須走這一條,而不是 <see cref="MarketRoute"/>。
/// <c>/public</c> (<see cref="PublicRoute"/>): order-book data such as <c>bookTicker</c> and <c>depth</c>. This
/// package has no such stream yet; adding bookTicker or depth later means dialling this route, not
/// <see cref="MarketRoute"/>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>/market</c>(<see cref="MarketRoute"/>):kline、continuousKline、markPrice、aggTrade、ticker、miniTicker、
/// 強平等。本套件現有的 K 線與標記價都屬這一類,所以 <see cref="CombinedStreamUri"/> 走這一條。
/// <c>/market</c> (<see cref="MarketRoute"/>): kline, continuousKline, markPrice, aggTrade, ticker, miniTicker,
/// liquidations, and so on. Both of this package's streams, klines and mark prices, belong here, which is why
/// <see cref="CombinedStreamUri"/> dials this route.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>/private</c>(<see cref="PrivateRoute"/>):listenKey 使用者資料串流,見 <see cref="UserDataStreamUri"/>。
/// <c>/private</c> (<see cref="PrivateRoute"/>): the listenKey user data stream; see
/// <see cref="UserDataStreamUri"/>.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>不帶路由前綴的舊位址只收得到 public 類資料,而且失敗方式最壞。</b> 舊位址 2026-04-23 起停用。
/// 2026-09-12 主網實測:<c>/stream?streams=btcusdt@kline_1m/btcusdt@markPrice@1s</c>、<c>/ws/btcusdt@kline_1m</c>、
/// <c>/ws/btcusdt@markPrice@1s</c> 全部「握手成功、一個 frame 都沒有」;同樣的串流改走 <c>/market/…</c> 立刻有資料。
/// 沒有錯誤、沒有斷線,只有永遠不動的價格 —— 0.1.0 就是這樣在主網上零資料,當時還被誤判成本機網路問題。
/// Testnet 目前對行情仍相容舊位址,但 <c>/market</c> 在兩個環境都通,所以一律走 <c>/market</c>。
/// 0.1.0 的註解曾記載 Testnet 的 <c>/public/…</c> 收不到 K 線,這與路由拆分的規則一致:K 線屬 market 類。
/// <b>An address without a route prefix delivers public-class data only, and it fails in the worst way.</b> The
/// unprefixed addresses were retired on 2026-04-23. Measured on production on 2026-09-12:
/// <c>/stream?streams=btcusdt@kline_1m/btcusdt@markPrice@1s</c>, <c>/ws/btcusdt@kline_1m</c>, and
/// <c>/ws/btcusdt@markPrice@1s</c> all completed the handshake and then delivered not one frame, while the same
/// streams on <c>/market/…</c> delivered at once. No error, no disconnect, just a price that never moves — which
/// is how 0.1.0 received nothing on production, at the time misread as a local network problem. The testnet still
/// honours the old addresses for market data, but <c>/market</c> works on both, so <c>/market</c> is used
/// everywhere. The 0.1.0 note that the testnet's <c>/public/…</c> delivered no klines agrees with the split:
/// klines are market-class data.
/// </para>
/// <para>
/// 路由之後的形式(同樣是 2026-09-11 在 Testnet 實測):
/// The shape after the route, measured on the testnet on 2026-09-11:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>…/ws/&lt;串流名&gt;</c> 的訊息<b>沒有</b>外層包裝,直接就是事件物件。
/// Frames on <c>…/ws/&lt;stream&gt;</c> carry <b>no</b> envelope: the event object arrives bare.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>…/stream?streams=a/b</c> 的訊息包在 <c>{"stream":…,"data":…}</c> 裡;分隔符號必須是斜線,
/// 換成逗號連握手都不會成功。
/// <c>…/stream?streams=a/b</c> wraps every frame in <c>{"stream":…,"data":…}</c>. The separator must be a
/// slash; a comma fails the handshake outright.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>…/ws</c> 與 <c>…/stream</c> 可以不帶任何串流直接連上,之後用 <c>SUBSCRIBE</c> 控制訊息訂閱。
/// <b>外層包裝由路徑決定,與訂閱幾檔無關</b>:走 <c>/stream</c> 訂一檔也有包裝,走 <c>/ws</c> 訂十檔也沒有。
/// Both <c>…/ws</c> and <c>…/stream</c> accept a connection with no streams at all and take <c>SUBSCRIBE</c>
/// control messages afterwards. <b>The envelope follows the path, not the number of streams</b>: one stream on
/// <c>/stream</c> is still wrapped, and ten streams on <c>/ws</c> are still bare.
/// </description>
/// </item>
/// </list>
/// <para>
/// 本類別把路由與路徑寫死,不接受由設定拼裝 —— 拼錯的位址不會產生任何症狀,只會安靜地沒有資料。
/// 端點覆寫(<see cref="BinanceEndpoints.CreateOverride"/>)因此應該給<b>主機根位址</b>(或代理的前綴),
/// 路由由這裡接上。
/// Routes and paths are hard-coded here rather than assembled from configuration, because a wrong address raises
/// no symptom at all, only silence. An endpoint override (<see cref="BinanceEndpoints.CreateOverride"/>) should
/// therefore supply the <b>host root</b>, or a proxy prefix, and leave the route to this type.
/// </para>
/// <para>
/// 串流名稱的<b>交易對一律小寫</b>,週期則保持原本的大小寫。這兩件事只能分開做:整串轉小寫會把
/// <c>kline_1M</c>(一個月)變成 <c>kline_1m</c>(一分鐘),訂閱照樣成功、資料照樣進來,
/// 只是週期完全不是要的那個,而且要等到對帳時才看得出來。
/// The <b>symbol is always lower-cased</b> while the interval keeps its own casing, and the two must be handled
/// separately: lower-casing the whole name turns <c>kline_1M</c> (one month) into <c>kline_1m</c> (one minute).
/// The subscription still succeeds and data still flows — on an interval nobody asked for, discovered only when
/// something is reconciled much later.
/// </para>
/// </remarks>
public static class BinanceStreamNames
{
    /// <summary>
    /// 盤口類資料(<c>bookTicker</c>、<c>depth</c>)的路由區段。本套件目前沒有走這條的串流。
    /// The route segment for order-book data such as <c>bookTicker</c> and <c>depth</c>. No stream in this
    /// package uses it yet.
    /// </summary>
    /// <remarks>
    /// 先把常數立起來,是為了讓日後加 bookTicker 或 depth 的人看到「這類要走 public」,
    /// 而不是順手沿用 <see cref="CombinedStreamUri"/> 的 market —— 走錯路由的症狀是握手成功、零資料。
    /// The constant exists ahead of use so that whoever adds bookTicker or depth sees that they belong on
    /// public rather than reusing the market route of <see cref="CombinedStreamUri"/>; the wrong route shows up
    /// as a successful handshake followed by no data at all.
    /// </remarks>
    public const string PublicRoute = "public";

    /// <summary>
    /// 行情類資料(kline、markPrice、aggTrade、ticker 等)的路由區段。本套件的 K 線與標記價走這一條。
    /// The route segment for market data such as kline, markPrice, aggTrade, and ticker. This package's klines
    /// and mark prices use it.
    /// </summary>
    public const string MarketRoute = "market";

    /// <summary>
    /// 使用者資料(listenKey)串流的路由區段。
    /// The route segment for the listenKey user data stream.
    /// </summary>
    public const string PrivateRoute = "private";

    /// <summary>
    /// 原始串流的路徑區段:訊息沒有外層包裝。
    /// The raw stream path segment, whose frames carry no envelope.
    /// </summary>
    public const string RawStreamPath = "ws";

    /// <summary>
    /// 組合串流的路徑區段:訊息包在 <c>{"stream":…,"data":…}</c> 裡。
    /// The combined stream path segment, whose frames are wrapped in <c>{"stream":…,"data":…}</c>.
    /// </summary>
    public const string CombinedStreamPath = "stream";

    /// <summary>
    /// <c>streams=</c> 查詢字串裡的串流分隔符號。必須是斜線,逗號會讓握手失敗。
    /// The separator inside the <c>streams=</c> query. It must be a slash; a comma fails the handshake.
    /// </summary>
    public const char StreamNameSeparator = '/';

    /// <summary>
    /// K 線推送的事件型別值(<c>e</c> 欄位)。
    /// The event type value of a kline push, carried in the <c>e</c> field.
    /// </summary>
    public const string KlineEventType = "kline";

    /// <summary>
    /// 標記價推送的事件型別值(<c>e</c> 欄位)。注意是 <c>markPriceUpdate</c>,不是串流名稱裡的 <c>markPrice</c>。
    /// The event type value of a mark price push. Note that it is <c>markPriceUpdate</c> rather than the
    /// <c>markPrice</c> that appears in the stream name.
    /// </summary>
    public const string MarkPriceEventType = "markPriceUpdate";

    /// <summary>
    /// 組合串流外層的串流名欄位。
    /// The property naming the stream in the combined-stream envelope.
    /// </summary>
    public const string EnvelopeStreamProperty = "stream";

    /// <summary>
    /// 組合串流外層的資料欄位。
    /// The property carrying the payload in the combined-stream envelope.
    /// </summary>
    public const string EnvelopeDataProperty = "data";

    /// <summary>
    /// 單一連線可訂閱的串流數上限。
    /// The maximum number of streams one connection may carry.
    /// </summary>
    /// <remarks>
    /// 超過上限的訂閱會被交易所拒絕或截斷,而截斷的症狀是「有些標的就是沒有資料」——
    /// 看起來像那幾檔沒成交,不像設定超標。呼叫端在送出之前就該被擋下來。
    /// A subscription beyond the ceiling is rejected or truncated by the exchange, and truncation shows up as
    /// "some symbols simply have no data", which reads like a quiet market rather than an over-sized request.
    /// The caller is stopped before anything goes out.
    /// </remarks>
    public const int MaxStreamsPerConnection = 200;

    private const string KlineSuffix = "@kline_";

    private const string MarkPriceSuffix = "@markPrice";

    private const string FastUpdateSuffix = "@1s";

    private const string ListenKeyQueryParameter = "listenKey";

    private const string EventsQueryParameter = "events";

    /// <summary>
    /// <c>events=</c> 查詢參數裡的事件名分隔符號,官方文件的格式是 <c>events=A/B</c>。
    /// The separator between event names in the <c>events=</c> query, which the documentation gives as
    /// <c>events=A/B</c>.
    /// </summary>
    private const char EventNameSeparator = '/';

    /// <summary>
    /// 組出 K 線串流名稱,例如 <c>btcusdt@kline_15m</c>。
    /// Builds a kline stream name such as <c>btcusdt@kline_15m</c>.
    /// </summary>
    /// <param name="symbol">交易對代碼,大小寫不拘,會轉成小寫。The symbol; any casing, lower-cased here.</param>
    /// <param name="interval">K 線週期。The interval.</param>
    /// <returns>
    /// 串流名稱,或說明哪一項不合法的失敗。
    /// The stream name, or a failure saying which argument is invalid.
    /// </returns>
    public static Result<string> Kline(string symbol, KlineInterval interval)
    {
        var normalized = NormalizeSymbol(symbol);

        if (!normalized.TryGetValue(out var lowerSymbol))
        {
            return normalized;
        }

        if (interval == KlineInterval.Unspecified || !Enum.IsDefined(interval))
        {
            return TradeErrors.UnsupportedInterval(interval.ToString());
        }

        // 週期字串刻意不跟著轉小寫,見類別註解:1M 與 1m 只差在大小寫,意義差了四萬倍。
        // The interval string deliberately keeps its casing; see the type remarks. 1M and 1m differ by case
        // alone and by a factor of forty thousand in meaning.
        return string.Concat(lowerSymbol, KlineSuffix, interval.ToExchangeString());
    }

    /// <summary>
    /// 組出標記價串流名稱,例如 <c>btcusdt@markPrice@1s</c>。
    /// Builds a mark price stream name such as <c>btcusdt@markPrice@1s</c>.
    /// </summary>
    /// <param name="symbol">交易對代碼,大小寫不拘,會轉成小寫。The symbol; any casing, lower-cased here.</param>
    /// <param name="fastUpdates">
    /// 是否使用每秒更新的版本。<see langword="false"/> 為每三秒更新一次。
    /// Whether to use the one-second variant; <see langword="false"/> updates every three seconds.
    /// </param>
    /// <returns>
    /// 串流名稱,或說明交易對不合法的失敗。
    /// The stream name, or a failure saying the symbol is invalid.
    /// </returns>
    public static Result<string> MarkPrice(string symbol, bool fastUpdates = true)
    {
        var normalized = NormalizeSymbol(symbol);

        return normalized.TryGetValue(out var lowerSymbol)
            ? string.Concat(lowerSymbol, MarkPriceSuffix, fastUpdates ? FastUpdateSuffix : string.Empty)
            : normalized;
    }

    /// <summary>
    /// 組出組合串流的連線位址,例如 <c>wss://stream.binancefuture.com/market/stream</c>。
    /// Builds the combined-stream address, such as <c>wss://stream.binancefuture.com/market/stream</c>.
    /// </summary>
    /// <param name="webSocketBaseUri">
    /// WebSocket 基底位址,取自 <see cref="BinanceEndpoints.WebSocketBaseUri"/>。應為主機根位址(或代理前綴),
    /// 不含路由。
    /// The WebSocket base address from <see cref="BinanceEndpoints.WebSocketBaseUri"/>: the host root, or a proxy
    /// prefix, without any route.
    /// </param>
    /// <returns>連線位址。The address to dial.</returns>
    /// <remarks>
    /// <para>
    /// <b>走 <see cref="MarketRoute"/>。</b> 本套件透過這條連線訂的只有 K 線與標記價,兩者都屬 market 類。
    /// 不帶路由的 <c>/stream</c> 在主網上握手成功卻零資料(2026-09-12 實測,舊位址 2026-04-23 起停用),
    /// 這正是 0.1.0 主網收不到行情的原因。日後若要在這裡訂 bookTicker 或 depth,那兩類屬 public,
    /// 必須另開一條走 <see cref="PublicRoute"/> 的連線,不能混進這一條。
    /// <b>It dials <see cref="MarketRoute"/>.</b> The only streams this package subscribes through it are klines
    /// and mark prices, both market-class data. The unprefixed <c>/stream</c> completes the handshake on
    /// production and then delivers nothing — measured on 2026-09-12, the old addresses having been retired on
    /// 2026-04-23 — which is exactly why 0.1.0 received no market data on production. Should bookTicker or depth
    /// ever be wanted, those are public-class data and need a separate connection on <see cref="PublicRoute"/>
    /// rather than being mixed into this one.
    /// </para>
    /// <para>
    /// 這裡不訂任何串流,連上之後才用 <c>SUBSCRIBE</c> 控制訊息訂閱。這樣做有兩個好處:
    /// 重連時由 <c>Ozakboy.WebSockets</c> 自動重放同一則 <c>SUBSCRIBE</c>,不必為了換訂閱而重新撥號;
    /// 而且 <c>streams=</c> 查詢字串有長度上限,標的一多就會踩到。
    /// No streams are named here; they are subscribed with a <c>SUBSCRIBE</c> control message after the
    /// connection is up. That buys two things: <c>Ozakboy.WebSockets</c> replays the very same <c>SUBSCRIBE</c>
    /// on reconnect, so changing a subscription never needs a redial, and the <c>streams=</c> query has a length
    /// ceiling that a long symbol list runs into.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="webSocketBaseUri"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="webSocketBaseUri"/> is <see langword="null"/>.
    /// </exception>
    public static Uri CombinedStreamUri(Uri webSocketBaseUri) =>
        AppendSegment(AppendSegment(webSocketBaseUri, MarketRoute), CombinedStreamPath);

    /// <summary>
    /// 組出使用者資料串流的連線位址,例如
    /// <c>wss://stream.binancefuture.com/private/ws?listenKey=&lt;key&gt;&amp;events=ORDER_TRADE_UPDATE/ACCOUNT_UPDATE</c>。
    /// Builds the user data stream address, such as
    /// <c>wss://stream.binancefuture.com/private/ws?listenKey=&lt;key&gt;&amp;events=ORDER_TRADE_UPDATE/ACCOUNT_UPDATE</c>.
    /// </summary>
    /// <param name="webSocketBaseUri">
    /// WebSocket 基底位址(主機根位址或代理前綴,不含路由)。
    /// The WebSocket base address: the host root or a proxy prefix, without any route.
    /// </param>
    /// <param name="listenKey">串流憑證。The stream credential.</param>
    /// <param name="events">
    /// 要訂閱的事件型別名稱,至少一個。The event type names to subscribe to; at least one.
    /// </param>
    /// <returns>連線位址。<b>它含有憑證,本身就是祕密。</b>The address to dial. <b>It carries the credential and is itself a secret.</b></returns>
    /// <remarks>
    /// <para>
    /// <b>走 <see cref="PrivateRoute"/>,並採用官方文件的查詢字串形式。</b> 2026-09-12 Testnet 實測:同一把
    /// listenKey 同時連兩條,舊的 <c>/ws/&lt;listenKey&gt;</c> 收到 0 則事件,<c>/private/…</c> 收到委託事件 ——
    /// 0.1.0 的撥號位址在 Testnet 上已經收不到任何事件。路徑形式 <c>/private/ws/&lt;listenKey&gt;</c> 雖然也通,
    /// 但文件沒有寫,這裡不用。
    /// <b>It dials <see cref="PrivateRoute"/> in the documented query-string form.</b> Measured on the testnet on
    /// 2026-09-12 with one listenKey on two simultaneous connections: the old <c>/ws/&lt;listenKey&gt;</c>
    /// received no events while <c>/private/…</c> received the order events, so the 0.1.0 address no longer
    /// delivers anything on the testnet. The path form <c>/private/ws/&lt;listenKey&gt;</c> also works but is
    /// undocumented, so it is not used.
    /// </para>
    /// <para>
    /// <b><c>events</c> 是真的過濾器,而且名稱不會被驗證。</b> 只帶 <c>events=ACCOUNT_UPDATE</c> 的連線收不到
    /// <c>ORDER_TRADE_UPDATE</c>;夾一個不存在的名稱照樣連得上、照樣收到其他事件 —— 拼錯只會安靜地收不到。
    /// 不帶 <c>events</c> 的連線兩次實測結果不一致(一次收到、一次 0 則),不可依賴,所以這個方法拒絕空清單。
    /// <b><c>events</c> really filters, and the names are not validated.</b> A connection with only
    /// <c>events=ACCOUNT_UPDATE</c> never receives <c>ORDER_TRADE_UPDATE</c>, and a made-up name still connects and
    /// still receives the rest — a misspelling fails in silence. Omitting <c>events</c> gave inconsistent results
    /// across two measurements, once receiving and once nothing, so it cannot be relied upon and an empty list is
    /// refused here.
    /// </para>
    /// <para>
    /// 憑證與每一個事件名都以 <see cref="Uri.EscapeDataString(string)"/> 編碼,事件名之間用文件格式的斜線相接。
    /// 憑證只出現在 <c>listenKey=</c> 查詢參數,不進路徑;路由與路徑沿用 <see cref="AppendSegment"/> 逐段接上,
    /// 端點覆寫的代理前綴會被保留。
    /// The credential and each event name are escaped with <see cref="Uri.EscapeDataString(string)"/>, and the
    /// names are joined with the documented slash. The credential appears only in the <c>listenKey=</c> query
    /// parameter, never in the path, and the route and path are appended segment by segment through
    /// <see cref="AppendSegment"/> so that a proxy prefix from an endpoint override survives.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="webSocketBaseUri"/> 或 <paramref name="events"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="webSocketBaseUri"/> or <paramref name="events"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="listenKey"/> 為空白、<paramref name="events"/> 為空集合或含空白名稱時擲出。
    /// Thrown when <paramref name="listenKey"/> is blank, or <paramref name="events"/> is empty or holds a blank
    /// name.
    /// </exception>
    public static Uri UserDataStreamUri(Uri webSocketBaseUri, string listenKey, IReadOnlyCollection<string> events)
    {
        ArgumentNullException.ThrowIfNull(webSocketBaseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenKey);
        ArgumentNullException.ThrowIfNull(events);

        // 例外訊息刻意不含 listenKey 或位址:這個方法的輸入本身就是祕密。
        // The exception messages deliberately carry neither the listenKey nor the address: this method's
        // input is itself a secret.
        if (events.Count == 0)
        {
            throw new ArgumentException(
                "事件清單不可為空。省略 events 的連線是否收得到事件實測不一致,不可依賴。The event list must not be empty; whether a connection without events receives anything proved inconsistent in measurement.",
                nameof(events));
        }

        if (events.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "事件清單不可含空白名稱。The event list must not contain a blank name.",
                nameof(events));
        }

        var builder = new UriBuilder(AppendSegment(AppendSegment(webSocketBaseUri, PrivateRoute), RawStreamPath))
        {
            Query = string.Concat(
                ListenKeyQueryParameter,
                "=",
                Uri.EscapeDataString(listenKey),
                "&",
                EventsQueryParameter,
                "=",
                string.Join(EventNameSeparator, events.Select(Uri.EscapeDataString))),
        };

        return builder.Uri;
    }

    /// <summary>
    /// 組出單一原始串流的連線位址,例如 <c>wss://stream.binancefuture.com/ws/btcusdt@bookTicker</c>。
    /// Builds a raw single-stream address, such as <c>wss://stream.binancefuture.com/ws/btcusdt@bookTicker</c>.
    /// </summary>
    /// <param name="webSocketBaseUri">WebSocket 基底位址。The WebSocket base address.</param>
    /// <param name="streamName">串流名稱。The stream name.</param>
    /// <returns>連線位址。The address to dial.</returns>
    /// <remarks>
    /// <para>
    /// <b>這個位址不帶路由,只收得到 public 類資料。</b> 自路由拆分(舊位址 2026-04-23 起停用)之後,
    /// 不帶路由的 <c>/ws/</c> 在主網上對 K 線與標記價都是「握手成功、零資料」(2026-09-12 實測)。
    /// 本套件的行情走 <see cref="CombinedStreamUri"/>、使用者資料走 <see cref="UserDataStreamUri"/>,
    /// <b>兩者都不再用這個方法</b>;0.1.0 的使用者資料串流曾以它撥 <c>/ws/{listenKey}</c>,
    /// 那個位址在 Testnet 上已經收不到任何事件。保留它只為了相容既有的公開 API。
    /// <b>This address carries no route and receives public-class data only.</b> Since the route split, the
    /// unprefixed addresses having been retired on 2026-04-23, an unprefixed <c>/ws/</c> on production completes
    /// the handshake and then delivers nothing for klines or mark prices (measured on 2026-09-12). Market data here
    /// goes through <see cref="CombinedStreamUri"/> and user data through <see cref="UserDataStreamUri"/>;
    /// <b>neither uses this method any more</b>. The 0.1.0 user data stream dialled <c>/ws/{listenKey}</c> through
    /// it, and that address no longer delivers any event on the testnet. It is kept only for compatibility with the
    /// existing public API.
    /// </para>
    /// <para>
    /// 若拿憑證當串流名稱,產生的 <see cref="Uri"/> 本身就是祕密,不可以寫進錯誤訊息、診斷資料或日誌。
    /// Should a credential ever be passed as the stream name, the resulting <see cref="Uri"/> is itself a secret
    /// and must not reach an error message, diagnostic data, or a log.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="webSocketBaseUri"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="webSocketBaseUri"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="streamName"/> 為空白時擲出。Thrown when <paramref name="streamName"/> is blank.
    /// </exception>
    public static Uri RawStreamUri(Uri webSocketBaseUri, string streamName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        return AppendSegment(AppendSegment(webSocketBaseUri, RawStreamPath), streamName);
    }

    /// <summary>
    /// 檢查並小寫化交易對代碼。
    /// Validates a symbol and lower-cases it.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <returns>小寫後的代碼,或驗證失敗。The lower-cased symbol, or a validation failure.</returns>
    /// <remarks>
    /// 只接受 ASCII 英數與底線。底線是必要的 —— 交割合約的代碼長成 <c>BTCUSDT_250926</c>;
    /// 而 <c>@</c> 與 <c>/</c> 必須擋下,它們是串流名稱與串流清單的分隔符號,
    /// 混進代碼裡會把一個訂閱悄悄變成兩個(或一個不存在的)訂閱。
    /// Only ASCII alphanumerics and the underscore are accepted. The underscore is required because delivery
    /// contracts look like <c>BTCUSDT_250926</c>, while <c>@</c> and <c>/</c> must be rejected: they separate
    /// stream names and stream lists, and letting one through quietly turns one subscription into two — or into
    /// one that does not exist.
    /// </remarks>
    public static Result<string> NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        foreach (var character in symbol)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return TradeErrors.InvalidQuery(
                    $"交易對代碼「{symbol}」含有不合法的字元「{character}」,串流名稱只接受英數與底線。The symbol \"{symbol}\" contains the invalid character '{character}'; stream names accept only alphanumerics and underscores.")
                    .WithData(BinanceErrorDataKeys.Symbol, symbol);
            }
        }

        return symbol.ToLowerInvariant();
    }

    private static Uri AppendSegment(Uri baseUri, string segment)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        // 用 UriBuilder 逐段接,而不是 new Uri(baseUri, "stream")。相對位址的解析會把基底既有的路徑
        // 當成檔名丟掉,指向代理或重播伺服器(例如 wss://proxy/binance)時就會靜默打到 wss://proxy/stream。
        // The segment is appended with UriBuilder rather than resolved as a relative URI, because relative
        // resolution discards the base path: pointing at a proxy such as wss://proxy/binance would quietly dial
        // wss://proxy/stream instead.
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');

        builder.Path = string.Concat(path, "/", segment);

        return builder.Uri;
    }
}
