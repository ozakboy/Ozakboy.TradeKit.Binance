# Ozakboy.TradeKit.Binance

.NET 的幣安 USDⓈ-M 永續合約用戶端,不使用任何第三方套件。

[English](README.md)

這個套件是 [`Ozakboy.TradeKit.Abstractions`](https://github.com/ozakboy/Ozakboy.TradeKit.Abstractions)
的幣安 USDⓈ-M 合約實作,建構在
[`Ozakboy.Http`](https://github.com/ozakboy/Ozakboy.Http) 的簽章、限流、重試與脫敏日誌管線之上。
它之所以存在,是因為上層專案的政策是不使用 `Binance.Net` 這類第三方社群套件;
這裡的每一個相依都是 .NET BCL、Microsoft 官方套件,或另一個 `Ozakboy.*` 套件。

## 這一版做了什麼

| 範圍 | 狀態 |
| --- | --- |
| 交易規則(`exchangeInfo`)與以環境為鍵的每日快取 | 已完成 |
| 伺服器時間(`/fapi/v1/time`) | 已完成 |
| 唯讀的帳戶與持倉查詢 | 已完成 |
| 幣安錯誤碼對映,含暫時性判定 | 已完成 |
| 請求權重表與限流 | 已完成 |
| 下單、撤單、查單、改槓桿與保證金模式 | 下一階段 |
| WebSocket 行情與使用者資料串流 | 下一階段 |

因此用戶端目前實作的是 `IExchangeInfoProvider`,等交易方法補上之後才會擴充成完整的 `IExchangeClient`。
現有的方法簽章都不會變。

## 安裝

```
dotnet add package Ozakboy.TradeKit.Binance
```

目標框架 `net10.0`。

## 快速上手

```csharp
services.AddBinanceFutures(options =>
{
    options.Environment = BinanceEnvironment.Testnet;

    // 金鑰由宿主注入,套件本身不含任何預設憑證。
    options.ApiKey = configuration["BINANCE_API_KEY"]!;
    options.SecretKey = configuration["BINANCE_API_SECRET"]!;
});
```

```csharp
var client = provider.GetRequiredService<BinanceFuturesClient>();

var symbol = await client.GetSymbolAsync("BTCUSDT");

if (!symbol.TryGetValue(out var rules))
{
    logger.LogError("讀不到交易規則:{Error}", symbol.Error);
    return;
}

// 送單前先校正。交易規則是唯一的本地防線,拒單訊息只會說「參數不合法」,看不出是哪一項出問題。
var sized = rules.NormalizeOrderSize(price: 62_800m, quantity: 0.0123456m);
```

公開端點不需要憑證,所以不填金鑰也能讀交易規則。

## 幾個值得知道的設計決定

### 環境是成套選的,沒有「逐一填 URL」的入口

`BinanceEndpoints` 沒有公開建構式。端點組合只能來自 `BinanceEndpoints.For(environment)` 或明確的
`CreateOverride`,而且 REST 與 WebSocket 一定成對。「下單打 Testnet、行情接主網」這種組合在型別層面就寫不出來
—— 那種錯接不會拋任何例外,只會讓策略看起來一切正常地用錯市場的價格下錯市場的單。

### 交易規則快取以環境為鍵,而且是結構上的

Testnet 與主網的交易規則不同。2026-09-11 實測:`BTCUSDT` 的 `stepSize` 在 Testnet 是 `0.0001`、
主網是 `0.001`。用錯的那一份校正出來的數量會被拒,而回應只說「參數不合法」——
更糟的是大部分數量在兩邊都合法,於是錯誤只在某些尾數上偶爾出現,看起來像網路問題。

這裡沒有以環境為鍵的字典,也沒有可以從外面塞進來的快取物件。每個 `BinanceExchangeInfoProvider`
在建構時綁定一組端點,快取是這個實例的私有狀態,而放進去的快照自己也記著來源。
共用交易規則來源給 `BinanceFuturesClient` 時,它會比對兩者的環境,不一致就在建構時擲出例外。

### 一個不可用的商品不該倒掉整籃

幣安會把尚未上架的商品也放進 `exchangeInfo`,而且填的是佔位值:2026-09-11 的 Testnet 快照裡,
`ELSAUSDT` 的狀態是 `PENDING_TRADING`、`tickSize` 是 `0`。
讓這一筆把整份交易規則判定為失敗,系統就會因為一個根本不能交易的標的而完全無法運作。

因此規則讀不出來的商品會被排除在快照之外,並連同原因記進 `RejectedSymbols` ——
絕不填入猜測的預設值。查詢它時回傳的是那個原因,而不是一句「查無此交易對」,
否則一個上架中的新商品會被當成打錯字。但**全部**商品都失敗時整份解析仍然失敗:
那是格式改變而不是資料瑕疵,回傳一份空的交易規則會讓系統看起來運作正常卻一張單都下不出去。

### 暫時性由分類推得,不是逐碼設定的旗標

`Error.IsTransient` 是上層決定重不重試的唯一依據,而標錯的兩種方向代價完全不同:
該重試的放棄,是一次可自癒的塞車變成停擺;不該重試的一直重試,是把限流額度燒光之後被交易所封 IP。
暫時性由 `ErrorCategory` 推得,所以只要分類選對,暫時性就自動正確。

三個代碼另外處理:

| 幣安碼 | 中立代碼 | 暫時性 | 為什麼重要 |
| --- | --- | --- | --- |
| `-1021` | `trade.timestamp_out_of_sync` | 否 | 本機時鐘偏移。原封不動地重試只會再被拒一次,應該先用 `GetServerTimeAsync` 校時。 |
| `-1022` | `trade.invalid_signature` | 否 | 簽章不會自己變對,重試只是白白吃掉額度。 |
| `-2015` | `trade.invalid_credentials` | 否 | 多半是 IP 而不是金鑰:家用寬頻換到新的對外 IP 之後所有請求都會變成這一碼,訊息會提示先查白名單。 |

`BinanceErrorMapper.WithOutboundIpAddress` 可由應用層把查到的對外 IP 補進錯誤。
查詢對外 IP 必須向第三方服務發一次請求,函式庫不該在使用者不知情的狀況下對外連線。

### 簽章的五條規則,全部驗證過

1. **參數順序影響簽章。** 參數一律放在有序的 `QueryParameters`,絕不用字典 ——
   字典的列舉順序沒有保證,後果是「隨機出現 `-1022`、重跑又好」這種最難查的失敗。
2. **先編碼再簽。** 待簽字串與實際送出的字串完全一致,用 `Uri.EscapeDataString`,空白是 `%20` 而不是 `+`。
3. **十進位序列化不得有科學記號或尾隨零。**
4. **簽章轉小寫十六進位**,timestamp 用毫秒 Unix epoch。
5. **全部參數放查詢字串,POST 也一樣。** 幣安合約文件的混合模式範例算不出它自己刊登的簽章值
   (八個參數的全部排列都試過),而同型的現貨範例可完整重現。

### 權重逐次宣告

幣安以權重而非次數計算限流:一次 `exchangeInfo` 只算 1,一次不帶 `symbol` 的 `openOrders` 卻算 40。
`BinanceRequestWeights` 收著這張表,每個請求都宣告自己的權重 ——
少宣告的下場不是本地擋下來,而是交易所回 HTTP 429,再不停手就升級成 418 封鎖 IP。

只設一個 `REQUEST_WEIGHT` 桶。下單速率算在帳戶而非 IP 上,把它加成第二個桶會讓查詢請求也扣掉下單額度,
結果是行情查一查就下不了單 —— 那比沒有下單節流更危險。

兩個環境都預設採用主網的每分鐘 2400,即使 Testnet 自己宣告 6000,
這樣在測試環境跑得過的節奏在主網也跑得過。真要用滿 Testnet 的額度,用 `RequestWeightPerMinute` 明確覆寫。

### 帳戶快照刻意打兩個端點

`GetAccountSnapshotAsync` 用 `/fapi/v2/account` 取餘額、`/fapi/v2/positionRisk` 取持倉,合計權重 10。
帳戶端點自己的 `positions[]` 沒有 `markPrice` 也沒有 `liquidationPrice`,
照它建出來的 `Position` 名目價值會是零 —— 而「名目價值為零」在風控眼中等於「沒有部位風險」。

## 測試

合約測試以錄製的回應重播,全程不連網。`exchangeInfo` 與 `time` 的 fixture 是公開端點的真實錄製;
帳戶與持倉的 fixture 是依官方文件手寫的,因為那些端點需要真實憑證。
`tests/.../Fixtures/PROVENANCE.md` 明白寫出哪一份是哪一種,以及為什麼這個差別有實質風險。

需要真實 Testnet 憑證的整合測試標記 `[TestCategory("Testnet")]`,
從環境變數 `BINANCE_TESTNET_API_KEY` 與 `BINANCE_TESTNET_API_SECRET` 讀取憑證。
沒有憑證時回報 Inconclusive 而不是通過:一個「沒跑但綠燈」的測試比沒有測試更危險。

```
dotnet test                                    # 離線合約測試
dotnet test --filter "TestCategory=Testnet"    # 需要憑證
```

## 安全

- 套件不含任何預設憑證,也不會把憑證寫進檔案、日誌或 `ToString`。
- `signature` 與 `X-MBX-APIKEY` 會自動加進日誌脫敏清單。
- 憑證由宿主注入、由 `Ozakboy.Http` 的 `SigningOptions` 承載,本套件只在簽章當下讀取。

## 授權

MIT,見 [LICENSE](LICENSE)。
