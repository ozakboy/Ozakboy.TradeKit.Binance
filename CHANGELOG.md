# Changelog

本檔記錄本套件所有值得注意的變更。
All notable changes to this package are documented here.

格式依循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/),版號依循
[語意化版本](https://semver.org/lang/zh-TW/)。
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the versioning follows
[Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-09-12

首個發佈版本。幣安 USDⓈ-M 永續合約的完整用戶端:交易規則與帳戶查詢、下單撤單查單與槓桿／保證金模式、
WebSocket 的 K 線與標記價,以及使用者資料串流(委託、成交、帳戶增量、保證金追繳、對帳訊號)。
建構在 `Ozakboy.Http` 的簽章、限流、重試與脫敏日誌管線,以及 `Ozakboy.WebSockets` 的連線管理之上,
不使用任何第三方社群套件。
The first published release: a complete Binance USDⓈ-M futures client — trading rules and account queries,
order placement, cancellation and lookup, leverage and margin mode, WebSocket klines and mark prices, and the user
data stream — built on `Ozakboy.Http` and `Ozakboy.WebSockets` with no third-party dependencies.

### 新增功能 / Added

- **環境與端點 / Environment and endpoints**:`BinanceEnvironment`、`BinanceEndpoints`。
  REST 與 WebSocket 位址成套提供,沒有逐一設定的入口,「下單打 Testnet、行情接主網」在型別層面就組不出來。
  REST and WebSocket bases always come as a matched set, so a mismatched pair cannot be expressed.

- **設定 / Options**:`BinanceOptions`,含 `recvWindow`、交易規則快取有效期、每分鐘權重上限覆寫,
  以及逾時、重試、日誌設定。不含任何預設憑證。
  No default credentials ship with the package.

- **交易規則 / Trading rules**:`BinanceExchangeInfoProvider` 實作 `IExchangeInfoProvider`,
  內建每日更新的記憶體快取。快取綁在實例上、以環境為鍵,而且沒有任何公開 API 能把別的環境的快照交給它。
  The daily in-memory cache is bound to the instance and keyed by environment, with no public API through which
  another environment's snapshot could be supplied.

- **規則對映 / Rule mapping**:`BinanceExchangeInfoParser` 把 `PRICE_FILTER`、`LOT_SIZE`、`MIN_NOTIONAL`、
  `MARKET_LOT_SIZE` 對映成 `SymbolInfo`,數值一律以 `Precision.TryParsePlain` 解析(拒絕科學記號)。
  缺欄位一律拒絕該商品並記進 `RejectedSymbols`,絕不以預設值猜測;全部商品都失敗時整份解析失敗。
  A missing field rejects that symbol and is recorded rather than defaulted; every symbol failing fails the
  whole parse.

- **幣安專屬欄位 / Binance-specific fields**:`BinanceSymbolDetail` 保留 `MARKET_LOT_SIZE` 的數量上下限、
  商品狀態、合約類型與宣告精度 —— 中立模型的 `MaxQuantity` 只有一個欄位,而市價與限價的上限幾乎必定不同。
  The neutral model has one `MaxQuantity` while the market and limit ceilings almost always differ.

- **帳戶查詢 / Account queries**:`BinanceFuturesClient` 提供 `GetAccountSnapshotAsync`、
  `GetPositionsAsync`、`GetPositionAsync`。帳戶快照同時取用 `/fapi/v2/account` 與 `/fapi/v2/positionRisk`,
  因為帳戶端點的持倉沒有 `markPrice`,少了它名目價值會是零。
  The snapshot uses both endpoints because the account endpoint's positions carry no `markPrice`, without which
  the notional comes out zero.

- **錯誤對映 / Error mapping**:`BinanceErrorMapper`、`BinanceApiErrorCodes`、`BinanceErrorCodes`、
  `BinanceErrorDataKeys`、`BinanceErrors`。涵蓋 10xx / 11xx / 20xx / 40xx 四個區段,
  原始代碼與訊息一併留在 `Error.Data`。暫時性由 `ErrorCategory` 推得而非逐碼設定旗標。
  Transience follows from the category rather than from a per-code flag.

- **限流與權重 / Rate limiting and weights**:`BinanceRateLimits`、`BinanceRequestWeights`。
  只設一個 `REQUEST_WEIGHT` 桶,兩個環境都採主網的每分鐘 2400。
  A single `REQUEST_WEIGHT` bucket, at production's ceiling on both environments.

- **相依性注入 / Dependency injection**:`AddBinanceFutures`,一次註冊具名 `HttpClient`、
  `HttpPipelineClient`、交易規則來源與用戶端。
  Registers the named client, the pipeline, the rule provider, and the futures client in one call.

- **交易端點 / Trading endpoints**:`BinanceFuturesClient` 實作完整的 `IExchangeClient`,
  補齊 `PlaceOrderAsync`、`CancelOrderAsync`、`CancelAllOrdersAsync`、`GetOrderAsync`、
  `GetOpenOrdersAsync`、`SetLeverageAsync`、`SetMarginModeAsync`。
  `AddBinanceFutures` 另外以 `IExchangeClient` 介面註冊,讓策略層只相依介面。
  The client implements the whole of `IExchangeClient`, and the registration also binds the interface.

- **下單絕不重試 / Orders are never retried**:`PlaceOrderAsync` 送出的請求同時以 `AsNonIdempotent()`
  標記並釘上 `RetryPolicy.NoRetry`。逾時不代表交易所沒收到,盲目重送開出來的是兩倍的部位。
  撤單、查單、改槓桿與改保證金模式都明確宣告為冪等,可以安全重試;
  `BinanceApiClient.SendSignedAsync` 拒絕 `RequestIdempotency.Inferred`,強迫每個呼叫端自己表態。
  The placement request is both marked non-idempotent and pinned to `RetryPolicy.NoRetry`, and the signed
  sender refuses an inferred idempotency so that every call site has to decide.

- **冪等識別碼 / Idempotency key**:新增 `BinanceClientOrderId`,產生與檢查幣安的 `newClientOrderId`
  (格式 `^[\.A-Z\:/a-z0-9_-]{1,36}$`)。每張單都帶編號;呼叫端沒指定時自動產生,
  而且<b>失敗時也帶得回來</b> —— 放在 `Error.Data` 的 `BinanceErrorDataKeys.ClientOrderId`。
  少了這一項,自動產生的編號會隨著失敗一起消失,那張單就成了既查不到也撤不掉的部位。
  A generated id survives a failed submission, without which the order could be neither found nor cancelled.

- **訂單類型對映 / Order type mapping**:新增 `BinanceOrderMapper`,把七種抽象層類型對映成幣安的
  `type` 與各自的必填參數組合,並逐型別「按需加入」而非「全部加入再清掉」——
  幣安對多送一個不相干的參數同樣回 `-1106`。反向對映(狀態、方向、有效期限、持倉方向)一併提供。
  Parameters are added per type on demand, because Binance rejects an extra parameter as firmly as a missing one.

- **本地校正 / Local normalisation**:`PlaceOrderAsync` 送出前會取該商品的交易規則,
  數量向下對齊步進、價格對齊跳動點;校正後低於 `minQty` 或 `minNotional` 時直接失敗,不送出去換一次拒單。
  Normalised before sending, and failing locally rather than spending a round trip on a certain rejection.

- **幣安專屬的嚴格規則 / Binance-only validation**:抽象層通過但幣安會拒的幾條,一律在本地擋下 ——
  `clientOrderId` 的格式與長度、`closePosition` 只能用於 `STOP_MARKET` 與 `TAKE_PROFIT_MARKET`、
  雙向模式不可帶 `reduceOnly`、移動停損的回撤比例上限是 10 而非抽象層允許的 100,
  以及未定義的列舉值一律回傳失敗而不是讓對映表擲出例外。

- **錯誤碼 / Error codes**:新增 `-4120`(`OrderTypeNotSupportedOnEndpoint`),
  對映成 `TradeErrorCodes.NotSupported` 並附上說明。

- **WebSocket 行情串流 / WebSocket market streams**:新增 `BinanceMarketDataFeed`,實作 `IMarketDataFeed`
  的三個方法 —— 歷史 K 線走 REST(`GET /fapi/v1/klines`),即時 K 線與標記價走 WebSocket。
  另有 `AddBinanceMarketData()` 註冊擴充,與 `AddBinanceFutures` 分開:行情串流會開長命連線,
  只想查交易規則的宿主不該被迫帶上一個 WebSocket 客戶端。
  連線管理(重連、重連後重放訂閱、閒置逾時存活偵測、有界佇列背壓)一律取自 `Ozakboy.WebSockets` 0.2.0,
  本套件只負責幣安的協定細節。
  A new feed implements `IMarketDataFeed`, with REST klines and WebSocket kline and mark price subscriptions;
  all connection management comes from `Ozakboy.WebSockets` rather than being reimplemented.

- **`Kline.IsClosed` 的正確對映 / The closed flag is mapped, never defaulted**:串流的 `k.x` 直接對映到
  `Kline.IsClosed`,欄位缺席一律判為解析失敗。REST 的 `klines` 回應**沒有**這個旗標,而且最後一根
  通常還在跳動,因此以收盤時間與注入的時間來源比對逐根判定。
  這一點錯了不會有任何徵兆:同一根 K 線的各筆推送除了旗標之外完全相同,價格、成交量、時間照樣正確,
  而回測用的是收盤資料,連回測都一路綠燈,要到真錢在同一根 K 線內反覆進出場才會被發現。
  已由單元測試(同一根 K 線的三筆實錄推送)與真實連線的整合測試各鎖一次。
  The flag is mapped from `k.x` and a missing field fails the parse; the REST response carries no flag at all,
  so it is derived from the close time. Getting it wrong leaves every number correct and every backtest green.

- **串流路徑以實測定案 / The stream path was settled by measurement**:採用
  `wss://<host>/stream` + `SUBSCRIBE` 控制訊息。文件刊載的 `/public/ws/…` 與 `/public/stream…`
  在 Testnet 上握手成功、`SUBSCRIBE` 也回了 `{"result":null,"id":1}`,卻一筆行情都不送 ——
  沒有錯誤、沒有斷線,只有永遠不動的價格。外層包裝由**路徑**決定而非訂閱數量;
  `streams=` 的分隔符號必須是斜線,逗號連握手都不會成功。詳見 `BinanceStreamNames` 的註解。
  The verified paths are hard-coded; the documented `/public/…` forms connect, acknowledge, and deliver nothing.

- **心跳式存活偵測 / Heartbeat liveness**:固定送 `LIST_SUBSCRIPTIONS` 當應用層心跳(預設 30 秒,
  閒置逾時 90 秒)。K 線只在有成交時才推送,冷門標的可以安靜好幾分鐘,少了心跳就會把健康的連線判死
  並無止境重連。設定在 `BinanceMarketStreamOptions`,且驗證會擋下「心跳比閒置逾時還慢」的組合。

- **丟棄會回到串流上 / Drops are republished as gaps**:背壓丟掉的訊息在 `Ozakboy.WebSockets` 只出現在
  事件與統計裡,`await foreach` 看不到。被丟掉的可能正是一根已收盤的 K 線,所以這裡把它補成串流上的
  一筆暫時性失敗,寫明丟了幾則。

- **左閉右開的時間區間 / The half-open range is honoured**:幣安的 `endTime` 含端點,
  `KlineQuery` 的區間是左閉右開,因此送出前把結束時間減一毫秒(實測:同一組區間相差一根)。
  不減的話,連續分頁抓歷史時每一頁的最後一根都會和下一頁的第一根重複,
  而重複的 K 線在指標裡是一次不存在的價格變動。

- **串流訊息的失敗一律浮上來 / Unreadable frames surface**:解析失敗、非文字訊息、幣安的控制訊息
  拒絕回覆(`{"error":{"code":…,"msg":…}}`)都會變成串流上的失敗元素,不會被靜默丟棄。
  控制訊息的拒絕先走既有的 `BinanceErrorMapper`,認得出來的代碼保留更精確的對映
  (例如 `-1121` 仍對映成 `trade.symbol_not_found`),認不出來的才標成 `trade.subscription_failed`。

- **斷線在串流中現身且分得出終局 / Disconnects are visible and terminal ones are distinguishable**:
  連線層的失敗對映成 `trade.stream_disconnected` 或 `trade.subscription_failed`,
  **分類原樣保留**,所以消費端用 `Error.IsTransient` 就能分辨「有缺口、還在重連」與
  「這條串流結束了」。原始的 `ws.*` 代碼留在 `Error.Data` 的 `innerCode` 裡。

- **使用者資料串流 / User data stream**:新增 `BinanceUserDataFeed`,實作 `IUserDataFeed` 的五個訂閱方法 ——
  `SubscribeOrderUpdatesAsync`、`SubscribeTradeUpdatesAsync`、`SubscribeAccountUpdatesAsync`、
  `SubscribeMarginCallsAsync`、`SubscribeResyncSignalsAsync`。另有 `AddBinanceUserData()` 註冊擴充(單例),
  與 `AddBinanceFutures` 分開:這條串流需要 API 憑證、會開長命連線、會在背景續期憑證,
  只查公開資料的宿主不該被迫帶上這些。
  A new feed implements all five `IUserDataFeed` subscriptions, registered by its own `AddBinanceUserData()`.

- **一條連線、內部分流 / One connection, fanned out inside**:五個方法共用同一條 WebSocket 與同一把 listenKey,
  背景讀取迴圈解析後分送。每個訂閱者一份有界佇列,同一種事件訂閱兩次,兩邊各拿到完整的一份;
  含成交的 `ORDER_TRADE_UPDATE` 同時餵委託與成交兩條串流。帳戶同時只有一把 listenKey,
  每個訂閱各開一條連線只會重複收同一份事件。
  One socket and one listenKey serve every subscription; each subscriber has its own queue and sees every event.

- **listenKey 生命週期 / listenKey lifecycle**:第一次訂閱才建立(`POST`),每 30 分鐘續期(`PUT`,
  取 60 分鐘有效期的一半,容許連續失敗一次;驗證擋下達到有效期的週期),收到 `listenKeyExpired`
  時自動重建憑證與連線並送出 `ResyncRequired { Reason = StreamCredentialExpired }`,
  `DisposeAsync` 一律 `DELETE`。續期失敗會浮上串流 —— 那是憑證過期前唯一的預告。
  Created lazily, renewed every 30 minutes, rebuilt on expiry with a resync signal, and always deleted on disposal.

- **`StartAsync` 與正確的使用順序 / `StartAsync` and the order of operations**:新增公開的
  `StartAsync`,**等握手完成才回傳**(內部用 `ConnectAsync` 而非背景的 `Start`)。
  正確順序是**先訂閱 → `await StartAsync()` → 才下單**:訂閱之前與連線就緒之前的事件,交易所一律不補送。
  Testnet 實測(同一支探針只差這一點):等連線就緒再下單,`ORDER_TRADE_UPDATE` 立刻到;
  不等就下單,三十秒內一則都沒有,而 REST 查得到那張單確實掛在簿上。
  Subscribe, await `StartAsync`, then act; placing an order before the socket is live loses its events for good.

- **跟不上的訂閱者以失敗結束 / A subscriber that falls behind ends with a failure**:訂閱者佇列塞滿時
  **不靜默丟棄**,而是讓那一條訂閱以 `trade.stream_disconnected`(`ErrorCategory.Exhausted`,
  `IsTransient` 為 `false`)結束,訊息說明已漏事件、請重新訂閱並全量對帳。
  `Channel` 的三種 Drop 模式的 `TryWrite` 一律回傳 `true`,所以佇列固定用 `Wait` 模式、以回傳值判斷溢位。
  連線層佇列預設 `BackpressureStrategy.Wait`,與行情串流相反 —— 帳戶事件一則都丟不起。
  Order events are never dropped in silence: an overflowing subscriber ends with a non-transient failure.

- **斷線與對帳訊號 / Disconnects raise a resync signal**:斷線以暫時性失敗出現在每一條串流上,
  連線回來之後送出 `ResyncRequired { Reason = Reconnected }`,`UntrustedSince` 取**第一次**斷線的時刻,
  不會被重連期間的後續失敗覆寫。
  A drop surfaces as a transient failure, and the return raises a resync signal dated from the first drop.

- **閒置偵測預設開啟 / Idle detection is on by default**:比照行情串流,`BinanceUserDataStreamOptions`
  新增 `KeepAliveInterval`(預設 30 秒),`IdleTimeout` 預設 90 秒,驗證規則相同(閒置逾時必須為正、
  心跳必須短於閒置逾時)。心跳送 `{"method":"LIST_SUBSCRIPTIONS","id":N}`,`id` 遞增;
  Testnet 實測在 `/ws/{listenKey}` 上 56–111 ms 內回覆。**回覆是 `{"result":["<listenKey>"],"id":N}`,
  每一則都帶著憑證**,所以解析器看到「有 `id`、沒有 `e`」的物件就直接判為忽略,`result` 不讀也不轉述;
  沒有 `id` 也沒有 `e` 的物件仍判失敗。閒置逾時觸發的斷線走一般的重連路徑(同一把憑證),
  回來之後照樣送 `Reconnected`。
  A `LIST_SUBSCRIPTIONS` heartbeat keeps a quiet account from being mistaken for a dead socket; its reply carries the
  listenKey, so replies are recognised and dropped unread.

- **帳戶增量改用 0.3.0 的專用型別 / Account deltas use the 0.3.0 delta types**:
  `AccountUpdate.Positions` 的元素改為 `PositionChange`、`Balances` 改為 `BalanceChange`,
  `MarginCall.Positions` 改為 `MarginCallPosition`。舊版用完整的 `Position` / `Balance`,
  事件不帶的欄位只能填 0,而 `Position.Notional` 在帳戶變動裡恆為 0,風控讀到就是「沒有曝險」。
  事件可能不帶的欄位(`cw`、`bc`、`cr`、`iw`、`mm`)缺席時為 `null` 而不是 0;全倉部位的
  `IsolatedMargin` 為 `null`;`MARGIN_CALL` 缺標記價 `mp`、`ACCOUNT_UPDATE` 缺開倉均價 `ep` 一律判失敗。
  The deltas now carry only what the events deliver, with absent optional fields as null rather than zero.

- **串流憑證不外流 / The stream credential never escapes**:listenKey 不進任何錯誤訊息、`Error.Data`、
  例外、串流識別字(診斷資料固定寫 `userDataStream`)或日誌;串流訊息原文一律不轉述
  (`listenKeyExpired` 與心跳回覆本體都帶著憑證)。憑證端點的回應本體同樣不進錯誤 ——
  實測 `PUT /fapi/v1/listenKey` 回的是**完整憑證**,不是文件說的空物件。`listenKey` 另外加進
  `BinanceConstants.SensitiveParameterNames` 作為縱深防禦。以一條 canary 測試跑完整生命週期
  (含心跳回覆、續期失敗、重建憑證)鎖住,並以故意失敗驗證過該測試確實會紅。
  The listenKey reaches no error, log, or identifier, and a canary test covering the whole lifecycle guards it.

- **多工串流上的未知事件忽略 / Unknown events on the multiplexed stream are ignored**:
  `ACCOUNT_CONFIG_UPDATE` 等本套件不處理的事件型別不判失敗;但 `o.X` 對不上已知狀態、成交欄位讀不出來時
  整則判失敗。委託類型取 `o.ot`(當初送出的類型),`FilledNotional` 以 `o.z × o.ap` 算出。
  Unmodelled event types are ignored rather than failed, while an unmappable order status still fails the frame.

### 技術改進 / Changed

- **測試 fixture 是真實錄製 / Fixtures are genuine recordings**:REST 回應的合約測試用的是 2026-09-11
  對 Testnet 實際簽章請求的錄製,涵蓋帳戶、持倉、下單、查單、撤單、撤銷全部掛單、改槓桿與三種錯誤回應。
  實錄證實 `positionRisk` 的 `unRealizedProfit`(大寫 R)與 `account` 的 `unrealizedProfit`(小寫 r)
  是兩種拼法,也證實 `account` 的 `positions[]` 沒有 `markPrice` 與 `liquidationPrice` ——
  帳戶快照要多打一次 `positionRisk` 的理由。
  REST contract tests run against live testnet recordings, which is how the two spellings of unrealised profit
  were confirmed.

- **整合測試真的會下單 / The integration tests really place orders**:測試單一律掛在標記價下方約 4% 且用 GTC
  (不會成交)、一律在 `finally` 裡撤掉、一律帶 `pulsetrade-test-` 前綴,並以一條測試加一段 `ClassCleanup`
  查詢全部商品確認沒有殘留掛單。
  Test orders rest far from the mark price, are always cancelled, and a class cleanup confirms none remain.

- **相依 / Dependencies**:`Ozakboy.TradeKit.Abstractions` 0.3.0、`Ozakboy.Http` 0.2.0、
  `Ozakboy.WebSockets` 0.2.1(連線日誌只寫 scheme 與主機 —— 使用者資料串流的路徑段就是 listenKey)、
  `Ozakboy.Core.Abstractions` 0.3.0。遞移相依只有 `Microsoft.*`、`System.*` 與 `Ozakboy.*`。
  The transitive graph contains only Microsoft, System, and Ozakboy packages.

### 已知限制 / Known limitations

- **主網的行情串流尚未實際連線驗證**。M-1 探測與本版的驗證都在 Testnet
  (`wss://stream.binancefuture.com`)上進行;主網 `wss://fstream.binance.com` 在開發機上「連得上、
  收不到任何 frame」,以 Node 複驗結果相同,判定為本機網路環境問題而非程式問題,尚未排查完成。
  路徑格式在兩個環境上是同一套,但「同一套」這件事目前只有 Testnet 這一半是實測過的。
  The market streams were verified on the testnet only; the mainnet host connects but delivers no frames on the
  development machine, a local network condition that is still unresolved.

- **佇列丟棄的回報路徑沒有單元測試覆蓋**。丟棄要靠「消費端跟不上」才會發生,在單元測試裡無法穩定重現,
  硬要製造就會寫出時好時壞的測試。失敗物件本身(訊息、分類、診斷資料)有直接測試,
  串流裡那段「發現丟棄並推出失敗」的接線則是靠閱讀確認的。
  The drop-reporting path is not covered by a unit test, because a drop needs a consumer that falls behind and
  that cannot be reproduced deterministically; the failure object itself is tested directly.

- **條件單目前不被 `/fapi/v1/order` 受理**。2026-09-11 在 Testnet 實測,`STOP_MARKET` 與
  `TRAILING_STOP_MARKET` 都回 `-4120`,幣安要求改用 Algo Order 專用端點。
  參數對映已完成也有測試覆蓋,但端到端只驗到「被這個端點拒絕」;
  真正要下條件單需要另外接 Algo Order 端點,不在本版範圍。
  Conditional order types are refused by this endpoint with `-4120`; the mapping is implemented and tested but
  end-to-end placement would need the Algo Order endpoints.

- **`POST /fapi/v1/order` 的回應沒有 `avgPrice` 也沒有 `cumQuote`**(實錄確認,只有 `cumQty`),
  因此立即成交的委託在下單回應裡讀到的 `AverageFillPrice` 與 `FilledNotional` 都是 0。
  要知道成交均價請改以 `GetOrderAsync` 查單,那份回應兩個欄位都有。
  The place-order reply carries neither field, so the average fill price of an immediately filled order has to
  come from a follow-up lookup.

- **非零的未實現損益尚未經真實回應驗證**。錄製當時 Testnet 帳戶是空手的,欄位名稱已由實錄證實
  (抄錯會讓解析直接失敗),但「非零的值有被讀進模型」是以替換過數值的 fixture 驗的。
  要真正驗證需要在 Testnet 開一個會成交的部位,而本版的紀律是測試單一律不得成交。

- **各交易端點的權重未以回應標頭實測覆核**。`x-mbx-used-weight-1m` 是一分鐘滾動窗的累計值,
  單次呼叫前後相減得不到穩定的差值,因此權重仍沿用官方文件。

- **帳戶事件的單元測試樣本依官方文件組成,不是實錄**。`ACCOUNT_UPDATE` 與 `MARGIN_CALL` 只有在真的成交、
  真的被追繳時才會出現,而本版的紀律是測試單不得成交;保證金追繳更無法在不逼近強平的情況下觸發。
  欄位對映照文件寫對這件事由單元測試驗證,「文件本身說得對不對」目前只有委託事件
  (`BinanceUserDataTestnetTests` 的真實連線與真實掛單)實際驗過。
  The account-event samples are built from the documentation; only order events have been verified live.

- **心跳被拒不會浮上串流**。指令回覆(含 `{"error":…,"id":N}`)一律判為忽略而不轉述,
  因為無法保證它的內容不含連線片段。心跳被拒的後果只是那一次沒有刷新閒置計時,
  持續被拒的話會由閒置逾時接手、以一般斷線重連的形式出現。
  A rejected heartbeat is ignored rather than surfaced; if rejections persist the idle timeout takes over.

- `SymbolInfo.MaxLeverage` 維持抽象層的預設 `1`。`exchangeInfo` 不含最大槓桿,它在需要簽章的
  `/fapi/v1/leverageBracket`;由 `requiredMarginPercent` 反推得到的是預設分層的槓桿而非上限,
  填進去等於用錯的值冒充事實。下一階段接上 `leverageBracket` 後補齊。
  `exchangeInfo` does not carry a leverage ceiling, and deriving one would pass a wrong number off as fact.

[0.1.0]: https://github.com/ozakboy/Ozakboy.TradeKit.Binance/releases/tag/v0.1.0
