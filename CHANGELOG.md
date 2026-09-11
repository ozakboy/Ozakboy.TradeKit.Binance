# Changelog

本檔記錄本套件所有值得注意的變更。
All notable changes to this package are documented here.

格式依循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/),版號依循
[語意化版本](https://semver.org/lang/zh-TW/)。
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the versioning follows
[Semantic Versioning](https://semver.org/).

## 未發布 / Unreleased

### 新增功能 / Added

- **交易端點 / Trading endpoints**:`BinanceFuturesClient` 改為實作完整的 `IExchangeClient`,
  補齊 `PlaceOrderAsync`、`CancelOrderAsync`、`CancelAllOrdersAsync`、`GetOrderAsync`、
  `GetOpenOrdersAsync`、`SetLeverageAsync`、`SetMarginModeAsync`。
  `AddBinanceFutures` 另外以 `IExchangeClient` 介面註冊,讓策略層只相依介面。
  The client now implements the whole of `IExchangeClient`, and the registration also binds the interface.

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

### 技術改進 / Changed

- **測試 fixture 全面換成真實錄製 / Fixtures are now genuine recordings**:
  上一版的 `account.json` 與 `positionRisk*.json` 是依官方文件手寫的,欄位名稱未經真實回應驗證。
  本版以 2026-09-11 對 Testnet 的實際簽章請求重新錄製,並新增下單、查單、撤單、撤銷全部掛單、
  改槓桿與三種錯誤回應的實錄。**驗證結果:手寫版的欄位名稱全部正確**,包含
  `positionRisk` 的 `unRealizedProfit`(大寫 R)與 `account` 的 `unrealizedProfit`(小寫 r)。
  實錄另外證實 `account` 的 `positions[]` 確實沒有 `markPrice` 與 `liquidationPrice`,
  也就是帳戶快照多打一次 `positionRisk` 的理由。
  The previously hand-written fixtures were replaced with live Testnet recordings, which confirmed that every
  hand-written field name was correct.

- **整合測試真的會下單 / The integration tests really place orders**:
  上一版因為沒有 Testnet 金鑰而全部 Inconclusive,本版實跑。測試單一律掛在標記價下方約 4% 且用 GTC
  (不會成交)、一律在 `finally` 裡撤掉、一律帶 `pulsetrade-test-` 前綴,
  並以一條測試加一段 `ClassCleanup` 查詢全部商品確認沒有殘留掛單。

### 已知限制 / Known limitations

- **條件單目前不被 `/fapi/v1/order` 受理**。2026-09-11 在 Testnet 實測,`STOP_MARKET` 與
  `TRAILING_STOP_MARKET` 都回 `-4120`,幣安要求改用 Algo Order 專用端點。
  參數對映已完成也有測試覆蓋,但端到端只驗到「被這個端點拒絕」;
  真正要下條件單需要另外接 Algo Order 端點,不在本階段範圍。
  Conditional order types are refused by this endpoint with `-4120`; the mapping is implemented and tested but
  end-to-end placement would need the Algo Order endpoints.

- **`POST /fapi/v1/order` 的回應沒有 `avgPrice` 也沒有 `cumQuote`**(實錄確認,只有 `cumQty`),
  因此立即成交的委託在下單回應裡讀到的 `AverageFillPrice` 與 `FilledNotional` 都是 0。
  要知道成交均價請改以 `GetOrderAsync` 查單,那份回應兩個欄位都有。
  The place-order reply carries neither field, so the average fill price of an immediately filled order has to
  come from a follow-up lookup.

- **非零的未實現損益尚未經真實回應驗證**。錄製當時 Testnet 帳戶是空手的,欄位名稱已由實錄證實
  (抄錯會讓解析直接失敗),但「非零的值有被讀進模型」是以替換過數值的 fixture 驗的。
  要真正驗證需要在 Testnet 開一個會成交的部位,而本階段的紀律是測試單一律不得成交。

- **各交易端點的權重未以回應標頭實測覆核**。`x-mbx-used-weight-1m` 是一分鐘滾動窗的累計值,
  單次呼叫前後相減得不到穩定的差值,因此權重仍沿用官方文件。

## [0.1.0] - 2026-09-11

首個版本。幣安 USDⓈ-M 永續合約的交易規則、伺服器時間與唯讀帳戶查詢,建構在 `Ozakboy.Http` 的
簽章、限流、重試與脫敏日誌管線之上。下單、撤單與 WebSocket 行情屬於下一階段,本版不含。
The first release: Binance USDⓈ-M trading rules, server time, and read-only account queries, built on the
signing, rate-limiting, retry, and log-masking pipeline of `Ozakboy.Http`. Order placement, cancellation, and
WebSocket market data belong to the next stage and are not included.

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

### 已知限制 / Known limitations

- `SymbolInfo.MaxLeverage` 維持抽象層的預設 `1`。`exchangeInfo` 不含最大槓桿,它在需要簽章的
  `/fapi/v1/leverageBracket`;由 `requiredMarginPercent` 反推得到的是預設分層的槓桿而非上限,
  填進去等於用錯的值冒充事實。下一階段接上 `leverageBracket` 後補齊。
  `exchangeInfo` does not carry a leverage ceiling, and deriving one would pass a wrong number off as fact.

- 帳戶與持倉的合約測試使用依官方文件手寫的回應,而非錄製的真實回應 —— 本階段沒有可用的 Testnet 金鑰。
  欄位名稱因此未經真實回應驗證,取得憑證後應先跑 `[TestCategory("Testnet")]` 的整合測試並以實際回應取代。
  Those fixtures are hand-written from the documentation, so their field names are unverified against a live
  response.

- WebSocket 主機取自官方文件,本版未實際連線驗證(本階段不實作串流)。
  The WebSocket hosts come from the documentation and have not been dialled in this release.

[0.1.0]: https://github.com/ozakboy/Ozakboy.TradeKit.Binance/releases/tag/v0.1.0
