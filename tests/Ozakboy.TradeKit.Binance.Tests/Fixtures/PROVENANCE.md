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

## 帳戶、持倉與委託:Testnet 實錄 / Recorded from the Testnet signed endpoints

以下檔案是 **2026-09-11** 對 `https://testnet.binancefuture.com` 實際發出簽章請求後錄下的回應。
每個保留下來的節點都是原樣複製,只挑選要留哪幾筆,不改任何欄位名稱與型別。

| 檔案 | 來源端點 | 環境 | 取得日期 (UTC) |
| --- | --- | --- | --- |
| `account.json` | `GET /fapi/v2/account` | Testnet | 2026-09-11 |
| `positionRisk.json` | `GET /fapi/v2/positionRisk`(單向模式) | Testnet | 2026-09-11 |
| `positionRisk-hedge.json` | `GET /fapi/v2/positionRisk`(雙向模式) | Testnet | 2026-09-11 |
| `order-new.json` | `POST /fapi/v1/order`(LIMIT GTC) | Testnet | 2026-09-11 |
| `order-query.json` | `GET /fapi/v1/order` | Testnet | 2026-09-11 |
| `order-canceled.json` | `DELETE /fapi/v1/order` | Testnet | 2026-09-11 |
| `openOrders.json` | `GET /fapi/v1/openOrders?symbol=BTCUSDT` | Testnet | 2026-09-11 |
| `cancelAll.json` | `DELETE /fapi/v1/allOpenOrders` | Testnet | 2026-09-11 |
| `leverage.json` | `POST /fapi/v1/leverage` | Testnet | 2026-09-11 |
| `orderTypeNotSupported.json` | `POST /fapi/v1/order`(STOP_MARKET,遭拒) | Testnet | 2026-09-11 |
| `marginTypeNoChange.json` | `POST /fapi/v1/marginType`(模式未變,遭拒) | Testnet | 2026-09-11 |
| `orderNotFound.json` | `GET /fapi/v1/order`(不存在的編號) | Testnet | 2026-09-11 |

### 裁切內容 / What was trimmed

完整回應對一個 740 個商品的帳戶來說,`account` 約 281 KB、`positionRisk` 約 288 KB、雙向模式的
`positionRisk` 約 576 KB(每個商品兩筆)。整份放進 repo 只會讓每次 diff 都不可讀,因此**只保留部分元素**:

- `account.json`:頂層欄位原樣保留;`assets` 只留 `USDT`、`USDC`、`BTC`、`BNB`;
  `positions` 只留 `BTCUSDT`、`ETHUSDT`、`DOGEUSDT`。每一筆都是原樣複製。
- `positionRisk.json`:保留 `BTCUSDT`、`ETHUSDT`、`DOGEUSDT`、`XRPUSDT`、`1000PEPEUSDT`、`ADAUSDT`。
- `positionRisk-hedge.json`:保留 `BTCUSDT` 與 `ETHUSDT` 的 `LONG`、`SHORT` 各一筆。
- 委託相關的六份是單一請求的**完整回應**,一字未改。

錄製當時的完整回應校驗值(UTF-8 位元組):

| 來源 | 位元組 | SHA-256 |
| --- | --- | --- |
| `account`(Testnet) | 280,645 | `afdc929e9dd56b3e5e681d6bf8d1caab34dfa894705beeb78e88f365d495c132` |
| `positionRisk`(Testnet,單向) | 287,905 | `8171d4e323453e4c437659b288330da4dc191ffe023ed30c4417f528703c357c` |
| `positionRisk`(Testnet,雙向) | 576,549 | `c45c53b13722de38b7136879ee82c948e4ba08fd56d1e6bc88b259bac73f9a6a` |

### 欄位名稱的驗證結果 / What the recording confirmed

上一階段的手寫版本把未實現損益寫成 `positionRisk` 的 `unRealizedProfit`(大寫 R)與 `account` 的
`unrealizedProfit`(小寫 r)。**實錄回應確認兩者都正確**,解析器讀得到的每一個欄位在真實回應裡都存在:
`symbol`、`positionAmt`、`entryPrice`、`markPrice`、`unRealizedProfit`、`leverage`、`marginType`、
`isolatedMargin`、`liquidationPrice`、`positionSide`、`updateTime`,以及 `account` 的 `asset`、
`walletBalance`、`availableBalance`、`unrealizedProfit`、`canTrade`。

實錄同時揭露了兩件手寫版看不出來的事:

1. `positionRisk` 的真實回應另有 `isolated`(布林)與 `adlQuantile`(數值)兩個欄位,手寫版沒有。
   兩者都不是解析器讀的欄位,多出來不影響,但手寫版確實不等於真實形狀。
2. `account` 的 `positions[]` **沒有** `markPrice` 也沒有 `liquidationPrice`,欄位名也與 `positionRisk`
   不同(`maxNotional` 對 `maxNotionalValue`、布林的 `isolated` 對字串的 `marginType`)。
   這正是 `GetAccountSnapshotAsync` 多花 5 點權重另外打 `positionRisk` 的理由 —— 現在有實錄為證。

### 為什麼還有 `-open` 兩份 / Why the two `-open` files exist

`positionRisk-open.json` 與 `positionRisk-hedge-open.json` **不是**逐字實錄:它們的結構取自上面兩份實錄,
數值被替換成「帳戶持有部位」的情境。

原因是錄製當時 Testnet 帳戶是空手的,每一筆持倉的數量、損益、強平價都是零 ——
那種 fixture 證明得了欄位名稱正確(欄位不在,解析就會失敗),卻證明不了
「非零的損益有被讀出來」,而斷言 `UnrealizedPnl == 0` 是一句永遠成立的空話。

因此兩種都留:實錄那兩份負責「形狀與欄位名是真的」,`-open` 這兩份負責「數值真的有被讀進模型」。
替換過的欄位限於數值(`positionAmt`、`entryPrice`、`markPrice`、`unRealizedProfit`、`liquidationPrice`、
`leverage`、`marginType`／`isolated`、`isolatedMargin`、`isolatedWallet`、`notional`、`updateTime`),
欄位名稱與整體結構一字未動。

**尚未驗證的部分要說清楚**:非零的未實現損益沒有經過真實回應驗證,因為那需要在 Testnet 開一個會成交的
部位,而本階段的紀律是「測試單一律不得成交」。欄位名稱本身已由實錄證實,抄錯會讓解析直接失敗而不是讀到零。

### 遮蔽 / Masking

實錄的回應中不含任何帳號識別資訊:`account` 的頂層只有 `feeTier`、`tradeGroupId`(值為 -1)這類設定值,
沒有帳號編號、使用者代號或金鑰;`positionRisk` 與委託回應同樣沒有。因此沒有欄位需要遮蔽,
也沒有任何欄位被改動。委託回應中的 `orderId` 與 `clientOrderId` 是 Testnet 上一張已撤銷的測試單,
不具敏感性。餘額數字是 Testnet 的模擬資金。

---

## Recorded from the live API (English summary)

Every fixture in this directory is now a genuine recording. The `exchangeInfo` and `time` files come from the
public endpoints; the account, position, and order files were recorded on 2026-09-11 against
`https://testnet.binancefuture.com` with real signed requests. Retained nodes are copied verbatim and only the
selection of which rows to keep was edited, because the full responses run to hundreds of kilobytes.

The recording settled the open question from the previous stage: the hand-written field names were **correct**,
including `unRealizedProfit` with a capital R on `positionRisk` and `unrealizedProfit` with a lower-case r on
`account`. It also showed two things the hand-written version could not: the real `positionRisk` rows carry
extra `isolated` and `adlQuantile` fields, and the `positions[]` array inside `account` carries neither
`markPrice` nor `liquidationPrice` — which is exactly why the snapshot spends five more weight on a separate
`positionRisk` call.

The two `-open` files are **not** verbatim recordings: they take the recorded structure and substitute values
for an account that holds positions. The Testnet account was flat when recorded, so every quantity and P&L in
the verbatim files is zero, and an assertion that a zero field reads zero proves nothing about whether a
non-zero one is read at all. The verbatim files therefore carry the proof of shape and field names, and the
`-open` files carry the proof that values reach the model. Only numeric values were substituted; no field name
or structure was touched. A non-zero unrealised P&L remains unverified against a live response, because
producing one means opening a position that fills, and the discipline for this stage is that no test order may
fill.

No account identifiers, keys, or secrets appear in any fixture; the responses contained none to mask.
