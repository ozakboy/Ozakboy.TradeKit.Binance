# 測試 fixture 來源說明 / Fixture provenance

合約測試一律以這些錄製的回應重播,**不連網**。每一份都要能回答「這份資料是哪裡來的、什麼時候取的、
有沒有被改過」——否則它就只是一份沒人敢動的魔法字串。

Contract tests replay these recorded responses and never touch the network. Each file has to answer where it
came from, when it was taken, and whether it was edited; otherwise it is just a magic string nobody dares to
change.

---

## 真實錄製 / Recorded from the live API

這三份來自實際呼叫幣安的**公開端點**(不需要金鑰)。

| 檔案 | 來源 | 取得日期 (UTC) |
| --- | --- | --- |
| `exchangeInfo-mainnet.json` | `GET https://fapi.binance.com/fapi/v1/exchangeInfo` | 2026-09-11 |
| `exchangeInfo-testnet.json` | `GET https://testnet.binancefuture.com/fapi/v1/exchangeInfo` | 2026-09-11 |
| `serverTime.json` | `GET https://fapi.binance.com/fapi/v1/time` | 2026-09-11 |

取得方式(可重現):

```
curl -s https://fapi.binance.com/fapi/v1/exchangeInfo -o exchangeInfo-mainnet.json
```

### 編輯內容

`exchangeInfo` 的完整回應在主網約 1.09 MB(897 個商品)、Testnet 約 0.86 MB(739 個商品),
整份放進 repo 只會讓每次 diff 都不可讀。因此**只保留部分商品**,其餘欄位一字未動:

- 每個商品節點都是原樣複製,包含 `filters` 陣列的原始順序(那個順序在同一份回應裡各商品並不一致,
  而解析器必須靠 `filterType` 認人而不是靠索引 —— 保留原順序才測得到這件事)。
- 頂層的 `timezone`、`serverTime`、`futuresType`、`rateLimits`、`exchangeFilters` 原樣保留。
- `assets` 只留下幾個常見資產。

保留的商品:

- **主網**:`BTCUSDT`、`ETHUSDT`、`XRPUSDT`、`DOGEUSDT`、`1000PEPEUSDT`、`OMGUSDT`
  (最後一個的 `status` 是 `SETTLING`,用來驗證 `IsTradingEnabled` 為 false 的路徑)
- **Testnet**:只留 `BTCUSDT`,它的存在只有一個目的 —— 證明同一個商品在兩個環境的交易規則不同。

### 原始完整回應的校驗值

若要重新取得完整回應核對,當時的檔案為:

| 來源 | 位元組 | SHA-256 |
| --- | --- | --- |
| 主網 `exchangeInfo` | 1,113,532 | `cd894a9968c186cc8dcb97649bb18ce689752e8749cbbc0433da857f5597db2d` |
| Testnet `exchangeInfo` | 898,455 | `cc4f76a54bbf540058b231586db7fea7970c739b5c7fb66fe526af99da20e289` |

交易所會持續調整交易規則,重新抓取**不會**得到相同的校驗值。這兩個值只用來證明上面的子集是從那兩份
檔案裁下來的,不是用來當回歸基準。

---

## 依官方文件手寫 / Hand-written from the official documentation

| 檔案 | 對應端點 |
| --- | --- |
| `account.json` | `GET /fapi/v2/account` |
| `positionRisk.json` | `GET /fapi/v2/positionRisk`(單向模式) |
| `positionRisk-hedge.json` | `GET /fapi/v2/positionRisk`(雙向模式,同一商品多空並存) |

**這三份不是錄製的。** 它們需要真實的 API 金鑰,而本階段沒有可用的 Testnet 金鑰,所以欄位名稱與型別
取自幣安官方文件的回應範例(USDⓈ-M Futures,擷取日期 2026-09-11),數值是編造的。

這件事有實質風險,不要當成形式上的免責聲明:**欄位名稱沒有被真實回應驗證過**。特別是未實現損益在
`positionRisk` 是大寫 R 的 `unRealizedProfit`、在 `account` 是小寫 r 的 `unrealizedProfit` ——
抄錯不會讓解析失敗,只會讓那個欄位永遠讀到零。拿到 Testnet 金鑰後的第一件事,應該是跑
`[TestCategory("Testnet")]` 的整合測試,把真實回應存下來取代這三份檔案。

數值全部是編造的,帳號、金鑰、識別碼一律不存在於任何檔案中。

---

## Recorded vs. hand-written (English summary)

The three `exchangeInfo` and `time` files are genuine recordings of Binance's **public** endpoints, trimmed to
a handful of symbols with every retained node copied verbatim — including the original ordering of each
symbol's `filters` array, which differs between symbols within a single response and is what forces the parser
to match on `filterType` rather than on index.

The `account.json` and `positionRisk*.json` files are **not** recordings. Those endpoints require real API
credentials, which were not available for this stage, so their field names and types come from the official
documentation's response examples (retrieved 2026-09-11) and their values are invented. The field names are
therefore unverified against a live response — a real risk, not boilerplate, given that `unRealizedProfit`
(capital R on `positionRisk`) and `unrealizedProfit` (lower-case r on `account`) fail silently as zero rather
than as a parse error. The first thing to do once Testnet credentials exist is to run the
`[TestCategory("Testnet")]` integration tests and replace these three files with what comes back.

No account identifiers, keys, or secrets appear in any fixture.
