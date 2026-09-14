# Changelog

本檔記錄本套件所有值得注意的變更。
All notable changes to this package are documented here.

格式依循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/),版號依循
[語意化版本](https://semver.org/lang/zh-TW/)。
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the versioning follows
[Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-09-14

**條件單改走 Algo Order;`PlaceOrderAsync` 送條件單型別會被交易所拒絕。**
**Conditional orders now go through the Algo Order endpoints; `PlaceOrderAsync` sends a conditional type only
to have the exchange reject it.**

幣安自 2025-12-09 起把 `STOP_MARKET`、`TAKE_PROFIT_MARKET`、`STOP`、`TAKE_PROFIT`、
`TRAILING_STOP_MARKET` 移到 Algo Service,舊的 `POST /fapi/v1/order` 對這幾個型別一律回
`-4120 STOP_ORDER_SWITCH_ALGO`。0.1.x 送得出去,但拿回來的是一句看不出原因的拒單 ——
而拒單的那張單是停損,上層卻以為部位有保護。本版把那條路徑補齊。
Binance moved those five types to the Algo Service on 2025-12-09, and the old `POST /fapi/v1/order` answers
`-4120 STOP_ORDER_SWITCH_ALGO` for every one of them. 0.1.x could send them and got back an uninformative
rejection — for an order that was a stop, while the caller believed the position was protected. This release
supplies the missing path.

### 新增功能 / Added

- **`BinanceFuturesClient` 的五個條件單方法 / five conditional order methods**:
  `PlaceConditionalOrderAsync`(`POST /fapi/v1/algoOrder`)、
  `CancelConditionalOrderAsync`(`DELETE /fapi/v1/algoOrder`)、
  `GetConditionalOrderAsync`(`GET /fapi/v1/algoOrder`)、
  `GetOpenConditionalOrdersAsync`(`GET /fapi/v1/openAlgoOrders`)、
  `CancelAllConditionalOrdersAsync`(`DELETE /fapi/v1/algoOpenOrders`)。
  **送單與一般下單同樣絕不重試**:標記 `AsNonIdempotent()` 並釘上 `RetryPolicy.NoRetry`。
  逾時之後請用同一個 `clientAlgoId` 查單 —— 重送的後果比重複下單更糟,兩張停損之中的一張會在部位
  被另一張平掉之後反手開倉。
  **Placement is never retried**, exactly as for an ordinary order. After a timeout, look it up by the same
  `clientAlgoId`: re-sending is worse here than for a plain order, because once one of two stops closes the
  position the other opens an inverted one.
- **`BinanceUserDataFeed.SubscribeConditionalOrderUpdatesAsync`**:條件單的狀態變化來自新的
  `ALGO_UPDATE` 事件,**不會**出現在 `SubscribeOrderUpdatesAsync`。事件名已加進撥號時的 `events`
  過濾器 —— 那份清單漏一個名稱,對應的事件就永遠不來,而連線與其他事件完全正常,沒有任何錯誤指向它。
  Conditional order state changes arrive on the new `ALGO_UPDATE` event and **never** on
  `SubscribeOrderUpdatesAsync`. The event name has been added to the `events` filter used when dialling: a name
  missing from that list means the event never arrives, while the connection and every other event behave
  normally and nothing points at the cause.
- **`BinanceRequestWeights`** 新增五個條件單端點的權重,含 `OpenAlgoOrders(bool hasSymbol)` ——
  不帶商品代碼是 **40**,帶了是 1。對帳輪詢務必帶上商品代碼。
  Five conditional endpoint weights, including `OpenAlgoOrders(bool hasSymbol)`: **40** without a symbol
  against 1 with one. Reconciliation polling must pass the symbol.

### 破壞性變更 / Breaking

- 相依的 `Ozakboy.TradeKit.Abstractions` 由 0.3.0 升至 **0.4.0**,後者在 `IExchangeClient` 與
  `IUserDataFeed` 上新增了條件單成員。自訂實作這兩個介面的呼叫端需要一併補上。
  The dependency moves to **0.4.0**, which adds conditional members to `IExchangeClient` and `IUserDataFeed`.
  Anyone implementing those interfaces has to supply them too.
- `PlaceOrderAsync` 的合約收窄為只收非條件單。這不是本套件新加的限制,是交易所已經在做的事。
  `PlaceOrderAsync` now takes non-conditional orders only — not a new restriction here but the one the
  exchange already enforces.

### 技術改進 / Technical

- **幣安的 Algo 端點沒有自己的一組錯誤碼**:官方 error-code 頁面上唯一與 algo 有關的只有 `-4120`。
  條件單查不到回的是一般的 `-2013`、編號重複回 `-4116`、掛太多回 `-2025`,與一般委託完全同碼。
  因此區分在呼叫端做:條件單路徑上的失敗會依原始代碼改標成
  `trade.conditional_order_not_found`、`trade.duplicate_client_conditional_order_id` 與
  `trade.conditional_order_limit_exceeded`。共用代碼的代價是上層分不出「停損不見了」與
  「進場單不見了」,而前者代表部位正在裸奔。
  **The algo endpoints have no error codes of their own**; the only algo-related entry on the official page is
  `-4120`. Failures on the conditional path are therefore re-labelled at the call site from the raw code,
  because sharing them leaves callers unable to tell "the stop is gone" from "the entry is gone".
- **`-4120` 改對映到 `trade.conditional_order_path_required`**,不再是籠統的 `trade.not_supported`,
  訊息也直接指名 `PlaceConditionalOrderAsync`。收到這一碼的人幾乎都是「程式碼寫在條件單搬家以前」,
  需要的是下一步怎麼做,不是再一句「不被接受」。
  請求仍然照送、不在本地攔截:舊端點拒絕條件單這件事由一條 Testnet 測試釘著,
  哪天它又被接受了那條測試要紅 —— 本地攔掉就等於把那個哨兵拆了。
  **`-4120` now maps to `trade.conditional_order_path_required`** rather than a generic
  `trade.not_supported`, and the message names `PlaceConditionalOrderAsync`. Whoever sees this code almost
  always has code written before the migration and needs the next step, not another way of saying "not
  accepted". The request is still sent rather than short-circuited locally: a Testnet test pins the refusal
  down so that it goes red the day Binance accepts these again, and catching it locally would remove that
  sentinel.
- **`-4116 DUPLICATED_CLIENT_ORDER_ID` 先前完全沒有對映**,一律落到
  `trade.unknown_exchange_error`。這一碼在冪等送單下**不是壞消息**:它代表那張單已經進去了,
  正確反應是用同一個編號查單,不是換一個編號重送。現在對映到
  `trade.duplicate_client_order_id`,一般委託與條件單兩條路徑都受益。
  **`-4116 DUPLICATED_CLIENT_ORDER_ID` had no mapping at all** and fell through to
  `trade.unknown_exchange_error`. Under idempotent submission it is **not** bad news — it means the order got
  through, and the answer is to look it up rather than retry under a fresh id. Both paths benefit.
- `-2025 MAX_OPEN_ORDER_EXCEEDED` 的說明補上條件單的差異:上限是**全帳戶合計 200 張**,
  不是每個商品各 200 張(幣安於 2025-12-29 移除 `exchangeInfo` 的 `MAX_NUM_ALGO_ORDERS` per-symbol 篩選器)。
  The note on `-2025` now records that a conditional order's ceiling is **200 across the whole account** rather
  than per symbol, after Binance removed the per-symbol `MAX_NUM_ALGO_ORDERS` filter on 2025-12-29.
- **撤銷單張條件單會打兩次網路**:`DELETE` 的回應只有 `{algoId, clientAlgoId, code, msg}`,
  沒有方向、沒有類型、沒有觸發價,湊不出一個誠實的 `ConditionalOrder`;因此撤完再查一次。
  多一次權重 1 的查詢,換的是不必在回傳值裡填 `Unspecified`。
  **Cancelling one conditional order makes two calls**: the `DELETE` response carries no side, type, or trigger
  price, so the order is read back afterwards. One extra request of weight 1 buys not returning `Unspecified`.
- 幾個必須逐字照抄、抄錯不會報錯只會靜默出事的地方,都有測試釘住:
  參數名是 `triggerPrice` 而不是 `stopPrice`、是 `activatePrice`(動詞)而不是 `activationPrice`;
  `algoStatus` 的拼字是 `CANCELED`(一個 L),而且沒有 `WORKING` 與 `FILLED`;
  `FINISHED` 的官方定義是「filled or canceled」,對映到 `ConditionalOrderStatus.Finished` 而不是
  `Filled` —— 讀成成交會讓一張觸發後被撤掉的停損在帳上變成一次不存在的平倉。
  Every literal that has to be copied exactly — and whose mistyping fails silently rather than loudly — is
  pinned by a test.
- 條件單有**全帳戶合計 200 張**的上限(不是每個商品各 200 張),已寫進
  `PlaceConditionalOrderAsync` 的說明。**未觸發的條件單不支援改單**,調整觸發價只能撤掉重下。
  The limit is **200 conditional orders across the whole account**, not per symbol, and an untriggered
  conditional order **cannot be modified**; changing a trigger price means cancel and replace.

規格出處:幣安官方 USDⓈ-M Futures 文件的 Trade REST API、User Data Streams、Error Code 與 Change Log
四頁,擷取日期 2026-09-14。
Specification sources: the Trade REST API, User Data Streams, Error Code, and Change Log pages of the official
Binance USDⓈ-M Futures documentation, retrieved 2026-09-14.

## [0.1.4] - 2026-09-14

串流憑證的生命週期從這一版起看得見。續期成功、續期失敗、憑證失效各有一行日誌,次數與時刻另以
`BinanceUserDataFeed.ListenKeyStatus` 公開成一份唯讀快照;續期失敗不再空等下一個三十分鐘,而是以短退避重試。
公開 API 只有新增(具體型別多一個屬性、設定多一個選項),`IUserDataFeed` 沒有變更,升級不需要改呼叫端程式碼。
The stream credential's lifecycle becomes visible in this release: a line for a successful renewal, one for a
failed renewal, and one for an expiry, with the counts and instants also exposed as a read-only snapshot on
`BinanceUserDataFeed.ListenKeyStatus`; a failed renewal no longer waits out the next thirty minutes but retries
after a short backoff. The public API only gains members — one property on the concrete type and one setting —
`IUserDataFeed` is unchanged, and upgrading needs no caller changes.

### 新增功能 / Added

- **續期有日誌了 / Renewals are logged**:這一版之前,`PUT /fapi/v1/listenKey` **完全沒有**日誌、也沒有任何計數。
  24 小時長跑時串流在 21 小時 24 分後收到 `listenKeyExpired`、套件自動重建 —— 但「30 分鐘一次的續期為什麼撐不到
  60 分鐘的有效期」在現場答不出來:是幣安對單一憑證另有壽命上限,還是某幾次 `PUT` 失敗了而沒有人知道?
  兩者在日誌與畫面上長得一模一樣。現在三行日誌各自回答一件事:
  Until this release the renewal endpoint wrote **no** log line and kept no count. On a 24-hour run the stream
  received a `listenKeyExpired` after 21 hours 24 minutes and rebuilt itself, and nothing on hand could say why a
  renewal every 30 minutes failed to keep a 60-minute credential alive — a ceiling of Binance's own, or some
  `PUT`s failing unseen? The two looked identical. Three lines now separate them:

  - 成功(Information):累計第幾次、耗時幾毫秒、距上次建立或續期幾分鐘。
    Success: the running count, the round trip in milliseconds, and the minutes since the credential was created
    or last renewed.
  - 失敗(Warning):連續第幾次失敗、**中立錯誤碼**(只有代碼,不含交易所回應原文 —— 這個端點的回應本體正常
    情況下就是憑證)、耗時,以及接下來會不會再試、多久之後。
    Failure: which consecutive failure this is, the **neutral error code** alone — never the exchange's own text,
    because the body of this endpoint is the credential in the normal case — the elapsed time, and whether and
    when another attempt follows.
  - 收到 `listenKeyExpired`(Warning):憑證建立於何時、活了幾分鐘、最後一次成功續期是什麼時候、距今多久、
    累計成功續期幾次、目前連續失敗幾次。**這一行就是用來回答上面那個問題的** —— 最後一次成功續期才過幾分鐘、
    連續失敗是 0,那就是憑證本身有壽命上限;連續失敗不是 0,就是那幾次 `PUT` 沒送成功。
    The expiry line carries the whole history that explains it, and a recent successful renewal with no
    consecutive failures points at a ceiling of the credential's own while a non-zero count points at the `PUT`s.

  **沒有一行帶得到 listenKey**:傳進去的只有時刻、次數、毫秒數與中立錯誤碼,型別上就沒有一個參數放得下憑證。
  本套件自己寫出去的日誌**不**經過 `Ozakboy.Http` 的遮罩器(那一道遮的是它自己的請求日誌與錯誤),
  所以這一層唯一的保護就是「根本不把憑證傳進來」。既有的憑證外洩測試已經把這幾行一併收進斷言。
  **No line can carry the listenKey**: every parameter is an instant, a count, a duration, or a neutral code.
  What this package logs itself does **not** pass through the `Ozakboy.Http` masker, which covers its own request
  logs and errors, so never handing the credential over is the only protection at this layer — and the existing
  credential-leak test now collects these lines along with everything else.

- **`BinanceUserDataFeed.ListenKeyStatus` 唯讀快照 / A read-only `ListenKeyStatus` snapshot**:
  新型別 `BinanceListenKeyStatus`,五個成員 —— `CreatedAt`(目前這一把憑證的建立時刻)、
  `LastRenewedAt`(最後一次**成功**續期)、`RenewalCount`(成功續期的累計次數)、
  `ConsecutiveRenewalFailures`(目前連續失敗幾次,成功即歸零)、`RebuildCount`(因憑證失效而重建的次數)。
  每次讀都在鎖內取出一份不會再變的複本,所以幾個欄位彼此一致,不會出現「次數加了、時刻還沒跟上」的組合。
  重建憑證會把 `CreatedAt` 換成新那一把的時刻、把 `LastRenewedAt` 清空、連續失敗次數歸零,
  因為那些讀數屬於**某一把**憑證;`RenewalCount` 與 `RebuildCount` 則是整個串流生命週期的累計值。
  A new `BinanceListenKeyStatus` carries the creation instant of the credential in use, the last **successful**
  renewal, the running renewal count, the current consecutive failure count, and the number of rebuilds. Each
  read takes an immutable copy under the lock so the members agree with one another. A rebuild restarts the
  per-credential readings while the two counts run for the feed's whole life.

  **刻意只加在具體型別上,`IUserDataFeed` 沒有變更。** 那是跨交易所的契約,而 listenKey 是幣安這一家的機制,
  別家的串流憑證未必有續期這回事;推上介面等於要求每一家都長出一個它沒有的概念。
  `AddBinanceUserData` 本來就以具體型別與介面登記同一個單例,宿主要呈現健康度時取具體型別即可。
  **Deliberately on the concrete type alone.** `IUserDataFeed` is a cross-exchange contract and the listenKey is
  Binance's own mechanism; another exchange's credential may have no renewal at all. `AddBinanceUserData` already
  registers one singleton behind both the type and the interface, so a host reads it from the type.

### 問題修正 / Fixed

- **續期失敗之後會重試,不再空等下一個三十分鐘 / A failed renewal is retried instead of waiting out the next
  thirty minutes**:續期週期是有效期的一半,原本的理由是「容許連續失敗一次」—— 但失敗之後什麼都不做、
  等下一個排程,那一把憑證就**只剩最後一次機會**;那一次再失敗,憑證過期、串流斷掉,缺口期間的委託與成交
  交易所不補送。續期失敗的原因多半是網路抖動或限流,一分鐘後再打一次多半就成功了。
  現在失敗之後依 `BinanceUserDataStreamOptions.ListenKeyRenewalRetryBackoffs`(預設 1、2、4 分鐘)重試,
  清單用完就停,等下一個排程續期 —— 一直重打一個持續回絕的端點只會撞上限流,而限流正是它一開始失敗的原因之一。
  The interval is half the lifetime to leave room for one failure, but doing nothing after that failure left the
  credential exactly one more chance, and losing it means an expired credential, a dropped stream, and a gap the
  exchange never replays. Retries now follow `ListenKeyRenewalRetryBackoffs` — one, two, then four minutes by
  default — and stop once the list runs out, because hammering an endpoint that keeps refusing only runs into the
  rate limiter that may have caused the failure.

  **重試絕不推遲下一個排程續期。** 每一輪開始前先算好下一個排程時刻,退避會跨過它就不再重試,直接把機會留給
  排程的那一次;否則「續期愈失敗、下一次排程愈晚」,而愈失敗正是愈該早一點再試的時候,方向剛好相反。
  每一次嘗試各在串流上報一筆失敗,不是整輪只報一次:上層要看到的是失敗了幾次,而重試把那個數字藏起來
  正是這裡最不該做的事。
  **A retry never pushes the scheduled renewal back.** The next scheduled instant is fixed before the wait and a
  backoff that would cross it is skipped, since the more renewals fail the sooner the next attempt should be, not
  the later. Every attempt still reports its own failure on the stream rather than one report per round, because
  how many failed is exactly what the layer above needs to see.

### 技術改進 / Changed

- **一條時好時壞的測試,根因是測試對啟動時機的假設錯了 / A flaky test rested on a wrong assumption about when
  the feed starts**:`BinanceUserDataFeedTests.AFillFeedsTheOrderStreamAndTheTradeStreamAtOnce` 全套平行跑時
  三次會逾時一次,單獨跑六次全綠。原本的註解寫著「訂閱的登記是同步發生的,所以兩次 `MoveNextAsync` 一起發動
  就夠了」—— 登記確實是同步的,但**第一次 `MoveNextAsync` 不只是登記**:它在迭代器主體的第一個 await
  之前同步跑完建立憑證、握手與啟動背景讀取迴圈。假連線的劇本在建構式就排好了那一則事件,
  所以它可能在第一次 `MoveNextAsync` 回來之前就已經分送完畢,而第二個訂閱者還沒登記 ——
  然後第二條串流等到 15 秒逾時。32 個平行 worker 讓那個縫被撞到的機率剛好高得看得見。
  改法是劇本先留空,兩個訂閱者都登記完之後才用新增的 `FakeWebSocketConnection.Enqueue` 把事件放上去,
  競態就不存在;**不加重試、不加長逾時**。同一個形狀的三條測試
  (`TwoSubscribersToTheSameEventEachReceiveTheirOwnCompleteCopy`、
  `TwoSubscriptionsShareOneCredentialAndOneConnection`、
  `EndingOneSubscriptionDoesNotCloseTheSharedConnection`)一併改掉,它們原本帶著同一顆未爆彈。
  被測程式沒有問題:「訂閱之前發生的事件不補送」正是 `IUserDataFeed` 明訂的語意。
  The test timed out roughly once in three full parallel runs and never on its own. Registration is synchronous,
  but the first `MoveNextAsync` also creates the credential, completes the handshake, and starts the read loop
  before its first await — so a frame queued in the fake connection's constructor could be dispatched before that
  call returned, with the second subscriber not yet registered. The scripts now start empty and the frames go on
  through a new `FakeWebSocketConnection.Enqueue` once both subscribers are registered; no retry and no longer
  timeout was added, and three sibling tests carrying the same latent race were fixed the same way. The code under
  test is correct: not replaying what happened before a subscription is `IUserDataFeed`'s documented behaviour.

- **新測試 / New tests**:`BinanceUserDataListenKeyStatusTests` 涵蓋快照的初始狀態、續期計數與時刻、
  失敗計數與成功後歸零、重建計數與新憑證的生命週期重置、三行日誌各自的內容(而且每一條都斷言日誌裡沒有憑證)、
  一個續期週期內的退避重試,以及「退避不得跨過下一個排程時刻」。
  `BinanceUserDataStreamOptionsTests` 另加三條驗證退避清單的規則。
  **這一組被故意弄壞驗證過**:把 `RecordRenewalSucceeded` 裡的 `_renewalCount++` 拿掉,
  110 條使用者資料測試裡恰好 3 條變紅,而且紅的正是依賴那個計數的三條
  (`TheRenewalCountAndTimestampsAreVisibleOnTheStatusSnapshot`、
  `ASuccessfulRenewalIsLoggedWithoutTheCredential`、
  `AFailedRenewalIsCountedAndSuccessClearsTheConsecutiveCount`);改回來全綠。
  A new test class covers the snapshot, the counters, the three log lines with a no-credential assertion on each,
  the backoff retries within one renewal period, and the rule that a retry never crosses the next scheduled
  instant; the options tests gain three rules for the backoff list. **Verified by deliberately breaking it**:
  removing `_renewalCount++` turned exactly three of the 110 user data tests red — the three that depend on that
  count — and restoring it turned them green.

  離線測試 755 個全綠,Release 建置 0 警告;2026-09-14 帶 Testnet 憑證依 CI 篩選
  (`TestCategory!=MainnetPublic`)跑 775 個全綠、無略過,其中 `TestCategory=Testnet` 的真實連線測試 20 個。
  全套平行連跑五次沒有任何一次紅燈。
  755 offline tests pass with no warnings in the Release build; on 2026-09-14 the CI filter with testnet
  credentials ran 775 tests green with none skipped, 20 of them against a live connection, and five consecutive
  full parallel runs produced no failure.

- **相依 / Dependencies**:沒有變更。`dotnet list package --include-transitive` 仍只有 Microsoft.\*、System.\*
  與 Ozakboy.\*。
  Unchanged; the transitive graph still contains only Microsoft.\*, System.\*, and Ozakboy.\* packages.

## [0.1.3] - 2026-09-13

使用者資料串流的 listenKey 在**取得的當下**就登記成遮罩器的已知祕密。它原本只有「結構上不進任何錯誤與日誌」
這一道保護;這一版補上字面替換那一道,不管它之後從哪條路徑流出去都會被換成遮罩字串。公開 API 只多一個建構式多載,
既有的多載沒有變更,走相依注入的宿主不必改任何程式碼。
The user data stream's listenKey is registered as a known secret **the moment it is obtained**. Until now it was
protected only structurally — it entered no error and no log — and this release adds literal replacement, so it
comes out masked whichever route it later leaves by. The public API gains one constructor overload, the existing
one is unchanged, and a host using dependency injection needs no changes at all.

### 安全 / Security

- **listenKey 一取得就登記成字面祕密 / The listenKey is registered as a literal secret on acquisition**:
  建立(`POST /fapi/v1/listenKey`)與續期(`PUT`)一讀出憑證,`BinanceListenKeyClient` 立刻以
  `Ozakboy.Security` 的 `SecretMasker.RegisterKnownSecret` 把它登記到**這個具名用戶端的**遮罩器上,
  登記發生在值回到呼叫端之前 —— 晚一步就有一段「憑證在手、遮罩器還不認得它」的空窗,而撥號位址正是在那段空窗裡組出來的。
  原本的保護只有 `BinanceConstants.SensitiveParameterNames` 的欄位名規則,而欄位名規則**看不見沒有欄位名的位置**:
  位址的路徑段(幣安現貨的使用者資料串流位址就是 `wss://host/ws/<listenKey>`)、其他套件已經格式化好的訊息、例外文字。
  As soon as a create or a renewal yields the credential, `BinanceListenKeyClient` registers it on **this named
  client's** masker with `SecretMasker.RegisterKnownSecret`, before the value returns to the caller — a later
  registration would leave a window in which the credential is in hand and the masker does not know it, and the
  dialled address is built inside that window. The previous protection was the field-name rule alone, and a
  field-name rule **cannot see a position that has no name**: a path segment of an address (the Binance spot user
  data stream dials `wss://host/ws/<listenKey>`), a message another package has already formatted, exception text.

- **續期換發的憑證也登記,舊的那把不移除 / A renewed credential is registered too, and the earlier one is kept**:
  實測 `PUT` 回的不是官方文件說的空物件而是一把完整的憑證,交易所換發時那會是**另一把**。續期的回應本體因此會被讀出憑證登記,
  讀不到就略過(文件說的空物件就是這個形狀),而且無論如何都不影響這次續期的成敗判定。
  舊的那一把刻意留著不移除:`SecretMasker` 只有清空全部的 `ClearKnownSecrets`、沒有移除單一項的 API,就算有也不會用 ——
  一把已經失效的憑證被多遮一次沒有任何壞處,少遮一次就是外流;帳戶一小時才換一次憑證,清單長不到值得擔心。
  Measured, the `PUT` returns a full credential rather than the empty object the documentation describes, and on a
  rotation that is a **different** key, so the renewal body is read for one and registered. A body without one is
  skipped — the documented empty object has exactly that shape — and either way this never affects whether the
  renewal is judged to have succeeded. The earlier key is deliberately left registered: `SecretMasker` offers only
  the clear-everything `ClearKnownSecrets` and no per-value removal, and even with one it would go unused, because
  masking a lapsed credential once more costs nothing while masking it once less is a leak.

### 新增功能 / Added

- **`BinanceUserDataFeed` 接受 `SecretMasker` 的建構式多載 / A `BinanceUserDataFeed` constructor overload taking a
  `SecretMasker`**:遮罩器以 `provider.GetOzakboyHttpMasker(BinanceConstants.HttpClientName)` 取得,
  必須是**同一個**實例 —— 另建一個新的遮罩器登記得成功,但真正在遮日誌與錯誤的是用戶端的那一個,兩者不同等於什麼都沒做,
  而且不會有任何跡象。`AddBinanceUserData` 已經自動帶上,走相依注入的宿主不必處理;`AddBinanceFutures` 沒有先跑的話,
  解析時會以 `InvalidOperationException` 當場失敗,而不是安靜地少掉一道保護。
  舊多載**維持不變**,但它不登記任何東西,XML 註解已寫明後果。
  The masker comes from `provider.GetOzakboyHttpMasker(BinanceConstants.HttpClientName)` and has to be the **same**
  instance: registering on a freshly built one succeeds while the masker actually covering this client's logs and
  errors is the client's, so a mismatch achieves nothing and shows no sign of it. `AddBinanceUserData` supplies it
  automatically; without a prior `AddBinanceFutures`, resolution fails outright with an `InvalidOperationException`
  rather than quietly going one guard short. The older overload is **unchanged** and registers nothing, which its
  XML documentation now states.

### 技術改進 / Changed

- **相依 / Dependencies**:新增 `Ozakboy.Security` 0.1.1 的明確引用。`SecretMasker` 進了公開 API,
  靠 `Ozakboy.Http` 遞移帶進來的話,上游哪天不再帶它,斷的是本套件的編譯。版號與 `Ozakboy.Http` 0.3.0 帶的那一份相同,
  `dotnet list package --include-transitive` 仍只有 Microsoft.\*、System.\* 與 Ozakboy.\*。
  `Ozakboy.Security` 0.1.1 is now referenced directly, because a public-API type arriving transitively breaks this
  package's build the day upstream stops carrying it. The transitive graph still contains only Microsoft.\*,
  System.\*, and Ozakboy.\* packages.

- **新測試 / New tests**:`BinanceUserDataMaskerRegistrationTests` 驗的是與既有
  `BinanceUserDataCredentialLeakTests` **不同**的一件事 —— 後者驗「本套件自己產生的文字裡沒有憑證」(結構上的保護),
  前者驗「憑證即使從本套件管不到的路徑流出去,也會被字面替換攔下來」。涵蓋:取得當下就登記、
  完整位址寫進日誌之後遮得掉(含欄位名規則完全攔不到的路徑段形式)、續期換發後新舊兩把都遮得到、
  登記確實落在容器交給這個具名用戶端的那一個遮罩器上,以及兩條對照組(沒有遮罩器時不登記、過短的值略過而不擲例外)。
  **這一組被故意弄壞驗證過**:拿掉 `CreateAsync` 裡登記的那一行,743 條測試裡恰好 4 條變紅
  (續期那一半仍綠,因為它是另一個獨立的登記點);改回來全綠。
  `BinanceUserDataMaskerRegistrationTests` checks something **different** from the existing
  `BinanceUserDataCredentialLeakTests`: that class checks no text this package produces carries the credential,
  this one checks that a credential leaving by a route this package does not control is still caught. **Verified by
  deliberately breaking it**: removing the registration from `CreateAsync` turned exactly four of the 743 tests red
  and restoring it turned them green.

## [0.1.2] - 2026-09-12

改用 `Ozakboy.Http` 0.3.0。重試的每一次嘗試都各自排隊等限流許可、各自付權重,並在拿到許可之後以當下時間重新簽章;
REST 用戶端回傳的每一個錯誤都以已登記的 API 金鑰與密鑰做字面遮罩。公開 API 沒有變更,升級不需要改呼叫端程式碼。
Moves to `Ozakboy.Http` 0.3.0. Every retry attempt now queues for its own rate-limit permit, pays its own weight, and
is re-signed with the current time once the permit is held; every error the REST client returns is masked against
the registered API key and secret. No public API changes, so upgrading needs no caller changes.

### 問題修正 / Fixed

- **重試沿用第一次的時間戳 / Retries reused the first attempt's timestamp**:0.1.1 相依的 `Ozakboy.Http` 0.2.0
  實際的管線順序是「簽章 → 限流 → 重試」,重試在簽章之內,每次重試送出的都是第一次嘗試的簽章與 `timestamp`。
  退避加上排隊一久超過 `recvWindow`(預設 5 秒),重試本身就被幣安以 `-1021` 拒絕,而 `-1021` 對映成非暫時性的
  `TimestampOutOfSync`,上層只會看到一次莫名其妙的時鐘偏移。0.3.0 改為「重試 → 限流 → 簽章 → 日誌」;
  本版再把 `SigningOptions.TimestampParameterName` 設為 `timestamp`(在 `BinanceOptions.CreateSigningOptions`
  設定,`AddBinanceFutures` 註冊時一併複製),簽章處理器每一次嘗試都把它在原位換成當下時間再簽,待簽字串的參數順序不變。
  With `Ozakboy.Http` 0.2.0 retry sat inside signing, so a retry re-sent the first attempt's signature and timestamp
  and, after a long enough backoff, was rejected with the non-transient `-1021`. The 0.3.0 order plus
  `TimestampParameterName = "timestamp"` restamps it in place on every attempt.

- **重試不付權重 / Retries paid no rate-limit weight**:同一個順序問題,限流在重試之外只被穿過一次,重試 N 次本地配額只扣一份,
  錯誤率高時低估實際用量。現在每次嘗試各付一份權重。
  Under the 0.2.0 order N retries paid for one; every attempt now pays its own weight.

- **錯誤裡的金鑰沒有被遮罩 / Credentials in errors were not masked**:0.1.1 手動建立 `HttpPipelineClient`,
  而 `Ozakboy.Http` 0.2.0 的門面對錯誤不做已登記祕密的字面替換。交易所把金鑰 echo 回錯誤本文、或傳輸層例外訊息帶到金鑰時,
  它會原樣出現在 `Error.Message` 與 `Error.Data`。門面改由 `provider.CreateOzakboyHttpPipelineClient(BinanceConstants.HttpClientName)`
  建立,自動帶入這個具名用戶端的遮罩器(API 金鑰與密鑰由 `AddOzakboyHttpPipeline` 登記)與管線同一份逾時設定。
  The facade is now built with `CreateOzakboyHttpPipelineClient`, which brings in the client's masker, so an echoed or
  exception-borne key no longer reaches `Error.Message` or `Error.Data`.

### 技術改進 / Changed

- **相依 / Dependencies**:`Ozakboy.Http` 0.2.0 → 0.3.0。`dotnet list package --include-transitive` 仍只有
  Microsoft.\*、System.\* 與 Ozakboy.\*。
  The transitive graph still contains only Microsoft.\*, System.\*, and Ozakboy.\* packages.

- **繼承自 `Ozakboy.Http` 0.3.0 的行為變更 / Behaviour inherited from `Ozakboy.Http` 0.3.0**:
  本地限流逾時(`RateLimitAcquisitionTimeout` 內拿不到許可)預設不重試,對映後仍是 `TradeErrorCodes.RateLimited`;
  單次嘗試逾時不再計入排隊等許可的時間,整體逾時仍涵蓋;`Error.Exception` 一律是 `SanitizedException`
  (原型別名在 `OriginalExceptionType`),請改以 `Error.Code` / `Error.Category` 分支;
  具名用戶端不再有 `IHttpClientFactory` 的預設日誌,`HttpClient.Timeout` 為無限,整趟逾時由 `Timeouts.OverallTimeout` 負責。
  A local rate-limit timeout is not retried; the attempt timeout excludes queueing; `Error.Exception` is a
  `SanitizedException`; the named client has no default factory logging and an infinite `HttpClient.Timeout`.

- **測試管線跟上新順序 / The test pipeline follows the new order**:測試用的 `TestPipeline` 原本照 0.2.0 的實際順序
  組成「簽章 → 限流 → 重試」,改為「重試 → 限流 → 簽章」,簽章處理器改用注入的假時鐘。
  `TestPipeline` now assembles retry, rate limiting, signing, with the signing handler on the injected clock.

- **新測試 / New tests**:`BinanceHttpPipelineRegistrationTests` 以 `AddBinanceFutures` 的正式註冊為對象,只換掉最內層傳輸:
  重試時第二次嘗試的 `timestamp` 是當下時間、參數順序不變、簽章可用同一把密鑰重算驗證;交易所 echo 回金鑰與傳輸例外帶到金鑰兩種情境,
  `Error.Message`、`Error.Data` 與例外文字都不含金鑰。兩條都以故意失敗驗證過:拿掉複製 `TimestampParameterName` 那一行,
  時間戳測試變紅(第二次嘗試帶的是 7 秒前的時間戳);門面改回手動建立,兩條遮罩測試都變紅(金鑰原樣出現在 `Error.Message`)。
  單元測試 736 個全綠,Release 建置 0 警告;2026-09-12 帶 Testnet 憑證依 CI 篩選(`TestCategory!=MainnetPublic`)
  跑 756 個全綠、無略過,主網公開行情測試(`TestCategory=MainnetPublic`,不帶憑證)1 個通過。
  Tests against the real registration cover the restamped retry and key masking; both were verified by breaking
  them on purpose. 736 unit tests pass and the Release build has no warnings; with Testnet credentials the CI filter
  ran 756 tests, all green with none skipped, and the production public market test passed.

## [0.1.1] - 2026-09-12

修正幣安合約 WebSocket 路由拆分造成的不相容:主網行情收不到任何資料,Testnet 的使用者資料串流收不到事件。
公開 API 只有新增、沒有變更,升級不需要改呼叫端程式碼。
Fixes the incompatibility with Binance's futures WebSocket route split: no market data on production, and no user
data events on the testnet. The public API only gains members, so upgrading needs no caller changes.

### 問題修正 / Fixed

- **主網行情零資料的真正原因是路由拆分 / The real cause of no production market data is the route split**:
  幣安把合約 WebSocket 拆成三條路由 —— `/public`(bookTicker、depth)、`/market`(kline、continuousKline、
  markPrice、aggTrade、ticker、miniTicker、強平等)、`/private`(listenKey 使用者資料),沒帶路由前綴的舊位址
  2026-04-23 起停用,只收得到 public 類資料
  ([官方公告](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Important-WebSocket-Change-Notice))。
  0.1.0 走不帶路由的 `/stream`,所以在主網上握手成功卻零資料。**0.1.0「已知限制」把這判為本機網路環境問題,
  那個判斷是錯的。** 2026-09-12 主網實測:`/stream?streams=btcusdt@kline_1m/btcusdt@markPrice@1s`、
  `/ws/btcusdt@kline_1m`、`/ws/btcusdt@markPrice@1s` 全部連得上、零 frame,`/market/stream…` 與 `/market/ws/…`
  立刻有資料。`BinanceStreamNames.CombinedStreamUri` 改為產生 `{base}/market/stream`(簽章不變);
  Testnet 對行情仍相容舊位址,但 `/market` 在兩個環境都通。另新增 `PublicRoute`、`MarketRoute`、`PrivateRoute`
  常數 —— 日後加 bookTicker 或 depth 要走 `/public`,不是 `/market`。
  The unprefixed addresses were retired on 2026-04-23 and deliver public-class data only; 0.1.0's "local network
  problem" diagnosis was wrong. Market streams now dial `/market/stream`.

- **Testnet 使用者資料串流在舊位址已收不到事件 / The testnet user data stream no longer delivers on the old
  address**:2026-09-12 以同一把 listenKey 同時連兩條,0.1.0 撥的 `/ws/<listenKey>` 收到 0 則,`/private/…`
  收到委託事件 —— 0.1.0 的使用者資料串流在 Testnet 上已經收不到事件(幾小時前還收得到)。
  改撥官方文件的查詢字串形式 `{base}/private/ws?listenKey=<key>&events=ORDER_TRADE_UPDATE/ACCOUNT_UPDATE/MARGIN_CALL/listenKeyExpired`;
  路徑形式 `/private/ws/<key>` 在 Testnet 也通,但文件沒寫,不採用。新增
  `BinanceStreamNames.UserDataStreamUri(webSocketBaseUri, listenKey, events)`:憑證與每個事件名都以
  `Uri.EscapeDataString` 編碼、憑證只出現在 `listenKey=` 查詢參數,端點覆寫的代理前綴照舊保留。
  `RawStreamUri` 保留以相容既有 API,但本套件已不再使用它。
  The 0.1.0 `/ws/<listenKey>` receives nothing on the testnet any more; the stream now dials the documented
  `/private/ws?listenKey=…&events=…` form.

- **`events` 的語意 / What `events` means**:它是**真的過濾器** —— 只帶 `events=ACCOUNT_UPDATE` 的連線收不到
  `ORDER_TRADE_UPDATE`。事件名**不會被驗證**,夾一個不存在的名稱照樣連得上、照樣收到其他事件,拼錯只會安靜地收不到。
  **省略 `events` 不可依賴**:兩次實測結果不一致(一次收到、一次 0 則)。因此事件清單一律明列,
  而且直接引用解析器分派用的同一組常數(內部的 `BinanceUserDataPaths.StreamEvents`),不另寫一份字串;
  `UserDataStreamUri` 拒絕空清單與空白名稱。
  `events` really filters, names are not validated, and omitting it is unreliable — so the list is always explicit
  and shares its constants with the reader.

### 技術改進 / Changed

- **心跳在新路由上重新實測 / Heartbeats re-measured on the new routes**:2026-09-12 以探針實測,
  `LIST_SUBSCRIPTIONS` 在 Testnet 的 `/private/ws?listenKey=…&events=…` 上 46–120 ms 回覆,在 `/market/stream`
  上 Testnet 65–77 ms、主網 58–78 ms 回覆,兩條串流的閒置偵測維持預設開啟。`/private` 上的回覆形狀變成
  `{"result":["<listenKey>@ACCOUNT_UPDATE","<listenKey>@MARGIN_CALL",…],"id":N}`,每個元素仍帶著憑證;
  解析器照舊一個字都不讀。事件名以斜線原樣相接或整段編碼成 `%2F`,伺服器都拆得出四個事件。
  Both heartbeats still answer on the new routes, so idle detection stays on by default. The private reply now
  lists `<listenKey>@<event>` entries, each still carrying the credential, and is still ignored unread.

- **主網公開行情整合測試 / A production public market data test**:新增 `BinanceMarketDataMainnetPublicTests`,
  標記 `[TestCategory("MainnetPublic")]`。以 `BinanceEnvironment.Mainnet`、不設任何憑證,經
  `SubscribeKlinesAsync` 在 60 秒內收到至少一根 BTCUSDT 1m K 線。Testnet 對行情仍相容舊位址,
  所以這是修正有效的唯一證據。以故意失敗驗證過:把 `CombinedStreamUri` 暫時改回 `/stream`,
  這條測試在 60 秒後紅燈(零資料);改回 `/market/stream` 即綠。
  The only evidence of the fix, since the testnet still honours the old address; verified by reverting to
  `/stream` once and watching it go red.

- **撥號位址有單元測試 / The dialled address is unit-tested**:假連線記下撥號位址,單元測試斷言使用者資料走
  `/private/ws`、listenKey 只出現在 `listenKey=` 查詢參數一次、`events` 恰好是四個事件名(以常數比對)。
  憑證外洩測試照舊全綠,並先確認 canary 確實就是撥號時用的那把憑證 —— 否則「沒出現」什麼也證明不了。
  A test asserts the private route, a single occurrence of the credential in the query, and exactly the four event
  names; the leak test still passes and now confirms its canary is the credential actually dialled.

### 已知限制 / Known limitations

- **`MARGIN_CALL` 與 `listenKeyExpired` 的實際推送未經實測**。這兩種事件在 Testnet 觸發不了(前者要逼近強平,
  後者要讓憑證真的過期),名稱與官方文件一致、也已列進 `events`,但「交易所在新路由上真的會推送」這件事沒有驗到。
  名稱不會被驗證,這一條要到真的發生時才會知道。
  Neither event can be triggered on the testnet; the names match the documentation and are subscribed, but actual
  delivery on the new route is unverified.

- **主網的使用者資料串流未實測**。本專案禁止使用主網憑證,主網的 `/private` 路由只依官方文件與 Testnet 的行為。
  The production user data stream has not been dialled; this project uses no production credentials.

- 0.1.0 的其餘已知限制仍然適用;其中「主網的行情串流尚未實際連線驗證」一條已由本版的主網公開行情測試解除。
  The other 0.1.0 limitations still apply, except the unverified production market stream, which this release
  verifies.

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

[0.1.4]: https://github.com/ozakboy/Ozakboy.TradeKit.Binance/releases/tag/v0.1.4
[0.1.3]: https://github.com/ozakboy/Ozakboy.TradeKit.Binance/releases/tag/v0.1.3
[0.1.1]: https://github.com/ozakboy/Ozakboy.TradeKit.Binance/releases/tag/v0.1.1
[0.1.0]: https://github.com/ozakboy/Ozakboy.TradeKit.Binance/releases/tag/v0.1.0
