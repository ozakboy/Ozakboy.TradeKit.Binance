# Changelog

本檔記錄本套件所有值得注意的變更。
All notable changes to this package are documented here.

格式依循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/),版號依循
[語意化版本](https://semver.org/lang/zh-TW/)。
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the versioning follows
[Semantic Versioning](https://semver.org/).

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
