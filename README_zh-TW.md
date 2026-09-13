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
| 下單、撤單、撤銷全部掛單、查單、查未結委託 | 已完成 |
| 改槓桿與保證金模式 | 已完成 |
| 條件單(停損、停利、移動停損)的參數對映 | 已完成,但端點已不受理(見下方) |
| WebSocket 行情串流:K 線與標記價 | 完成 |
| WebSocket 使用者資料串流:委託、成交、帳戶增量、保證金追繳、對帳訊號 | 完成 |

用戶端已實作完整的 `IExchangeClient`。所有價格、數量與金額都是 `decimal`,所有時間都是 UTC。

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

送單的樣子:

```csharp
var request = new OrderRequest
{
    Symbol = "BTCUSDT",
    Side = OrderSide.Buy,
    OrderType = OrderType.Limit,
    Quantity = 0.01m,
    Price = 60_000m,

    // 冪等識別碼。留白時由套件產生,但自己給的話,送單「之前」就能寫進自己的委託紀錄 ——
    // 那是逾時之後唯一查得回那張單的方式。
    ClientOrderId = BinanceClientOrderId.Generate(),
};

var placed = await client.PlaceOrderAsync(request);

if (!placed.TryGetValue(out var order))
{
    // 逾時或失敗都不可以重送:先用 clientOrderId 查單確認那張單到底進去了沒有。
    placed.Error!.TryGetData(BinanceErrorDataKeys.ClientOrderId, out var clientOrderId);
    logger.LogError("送單失敗,請用 {ClientOrderId} 查單確認:{Error}", clientOrderId, placed.Error);
    return;
}

await client.CancelOrderAsync(order.Symbol, order.GetIdentifier());
```

價量不必自己先校正:`PlaceOrderAsync` 會取該商品的交易規則,把數量向下對齊步進、價格對齊跳動點,
並在校正後低於最小下單量或最小名目價值時直接失敗,不送出去換一次拒單。

## 行情資料

`BinanceMarketDataFeed` 實作 `IMarketDataFeed`:歷史 K 線走 REST,即時 K 線與標記價走 WebSocket。
註冊要接在 `AddBinanceFutures` 之後,而且是獨立的一個呼叫 —— 行情串流會開長命連線,
只想查交易規則的宿主不該被迫帶上一個 WebSocket 客戶端。

```csharp
services.AddBinanceFutures(options => options.Environment = BinanceEnvironment.Testnet);
services.AddBinanceMarketData();
```

```csharp
var feed = provider.GetRequiredService<IMarketDataFeed>();

await foreach (var item in feed.SubscribeKlinesAsync(["BTCUSDT", "ETHUSDT"], KlineInterval.FifteenMinutes, ct))
{
    if (!item.TryGetValue(out var candle))
    {
        logger.LogWarning("行情串流:{Error}", item.Error);

        // 「有缺口、還在重連」與「這條串流結束了」靠 IsTransient 分辨。
        if (!item.Error!.IsTransient)
        {
            break;
        }

        continue;
    }

    // 只有收盤的 K 線才是 K 線,理由見下。
    if (!candle.IsClosed)
    {
        continue;
    }

    strategy.OnCandle(candle);
}
```

行情是公開端點,不需要憑證。連線管理 —— 自動重連與退避抖動、重連後重放訂閱、閒置逾時存活偵測、
有界佇列背壓 —— 全部由 [`Ozakboy.WebSockets`](https://github.com/ozakboy/Ozakboy.WebSockets) 負責,
本套件不重做;本套件負責的是幣安的協定細節:串流名稱、路徑走哪一條、外層包裝怎麼拆、欄位縮寫怎麼對映。

幣安把合約 WebSocket 依資料類別拆成三條路由:行情(K 線、標記價)走 `/market`、使用者資料走 `/private`、
盤口(bookTicker、depth)走 `/public`。本套件自己接上路由,用端點覆寫(`BinanceEndpoints.CreateOverride`)
指向代理或重播伺服器時,請給**主機根位址**(或代理前綴),不要把 `/market` 之類的路由寫進去。

可調的參數在 `BinanceMarketStreamOptions`:心跳間隔、閒置逾時、重連次數上限、佇列容量與背壓策略,
以及標記價要不要用每秒更新的串流。

## 使用者資料串流

`BinanceUserDataFeed` 實作 `IUserDataFeed`:帳戶私有串流上的委託更新、成交、帳戶增量、保證金追繳與對帳訊號。
它需要 API 憑證,註冊是獨立的一個呼叫,接在 `AddBinanceFutures` 之後:

```csharp
services.AddBinanceFutures(options => { /* 環境與憑證,同上 */ });
services.AddBinanceUserData();
```

**順序很重要:先訂閱,再 `await feed.StartAsync()`,最後才下單。** 訂閱之前、以及連線就緒之前發生的事件,
交易所一律不補送;在那段空窗裡下的單,可能成交了串流卻一個字都不會說。

```csharp
var feed = provider.GetRequiredService<BinanceUserDataFeed>();

// 1. 先訂閱。列舉一開始,訂閱者就同步登記完成。
var orders = feed.SubscribeOrderUpdatesAsync(ct).GetAsyncEnumerator(ct);
var firstOrder = orders.MoveNextAsync();

// 2. 等連線真的就緒。
var started = await feed.StartAsync(ct);

if (started.IsFailure)
{
    logger.LogError("使用者資料串流沒有啟動:{Error}", started.Error);
    return;
}

// 3. 這時才動作。從這裡開始的每一則事件都有人接。
await client.PlaceOrderAsync(request, ct);
```

單純訂閱也會啟動串流,接下來要做的事如果本身不會產生事件,這樣就夠了;
`StartAsync` 是給「接下來要做的事會產生事件」的那一刻用的。

五個 `Subscribe` 方法共用**一條** WebSocket 與**一把** listenKey,每個訂閱者各有一份有界佇列,
所以同一種事件訂閱兩次,兩邊都拿到完整的一份。串流替你處理的事:

- **listenKey 全程代管。** 第一次訂閱時建立、每 30 分鐘續期、過期時自動重建、釋放時刪除。
- **每一個缺口都會通知。** 重連或憑證過期都會在 `SubscribeResyncSignalsAsync` 送出 `ResyncRequired`。
  缺口期間的事件交易所不補送,所以兩種都要做全量對帳。
- **跟不上的訂閱者以失敗結束,不會靜默漏事件。** 那筆失敗的 `IsTransient` 為 `false`,
  訊息會說明要重新訂閱並全量對帳。
- **存活偵測用的是與行情串流相同的心跳。** 每 30 秒送一次 `LIST_SUBSCRIPTIONS`,閒置逾時 90 秒,
  所以安靜的帳戶不會被當成死掉的連線,而真正死掉的連線會和一般斷線一樣重連。
- **帳戶增量就是增量。** `AccountUpdate.Positions` 裝的是 `PositionChange`,`Balances` 裝的是
  `BalanceChange`,兩者只含事件真的帶來的欄位,沒有標記價、名目價值或可用餘額。
  要評估曝險,請查持倉,或乘上標記價串流的價格。

`BinanceUserDataFeed` **只**實作 `IAsyncDisposable`:釋放時要刪除 listenKey,那是一次網路呼叫。
泛型主機(Generic Host)本來就以非同步方式釋放,不必另外處理;自己建的容器則必須用 `await using` 釋放 ——
對只實作非同步釋放的單例,容器同步的 `Dispose()` 會直接擲出例外:

```csharp
await using var provider = services.BuildServiceProvider();
```

可調的參數在 `BinanceUserDataStreamOptions`:心跳間隔與閒置逾時(驗證規則與行情串流相同)、
listenKey 續期週期、重連次數上限,以及連線層與每個訂閱者的佇列容量。

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

### 下單絕不重試,其餘都可以

這是整個套件最不能妥協的一條。**逾時不代表對方沒收到** —— 那張單可能已經在簿上,甚至已經成交,
只是回應在路上掉了。這時重送會開出兩倍的部位,那是真金白銀的損失。

因此 `PlaceOrderAsync` 送出的請求同時做兩件事:以 `AsNonIdempotent()` 標記,並額外釘上
`RetryPolicy.NoRetry`。兩道防線是刻意的 —— 標記擋的是 `Ozakboy.Http` 的重試處理器,
策略擋的是「有人把預設策略換成一個看什麼都重試的 predicate」。

每一次重試都是一個新請求:各自排隊等限流許可、各自付權重,並以當下的 `timestamp` 重新簽章,
退避再久也不會因為時間戳過期被以 `-1021` 拒絕。本地限流逾時(在 `RateLimitAcquisitionTimeout` 內拿不到許可)不會重試。

其餘的都是冪等的,可以安全重試:

| 操作 | 可否重試 | 理由 |
| --- | --- | --- |
| `PlaceOrderAsync` | **否** | 逾時後重送 = 兩張單、兩倍部位 |
| `CancelOrderAsync` | 是 | 重複撤同一張單只會得到 `-2011`,意思是「它已經不在簿上了」 |
| `CancelAllOrdersAsync` | 是 | 沒有掛單可撤也算成功 |
| `GetOrderAsync` / `GetOpenOrdersAsync` | 是 | 查詢重送最壞只是多花一次權重 |
| `SetLeverageAsync` | 是 | 設成已經是的值不會出錯 |
| `SetMarginModeAsync` | 是 | 重送換來的 `-4046` 已被當成成功 |

### `clientOrderId` 是逾時之後唯一的線索,所以成功失敗都帶得回來

每張單都帶 `newClientOrderId`;呼叫端沒指定時由 `BinanceClientOrderId` 產生一個。
重點在於它**失敗時也回得來** —— 成功時在 `Order.ClientOrderId`,失敗時在
`Error.Data` 的 `BinanceErrorDataKeys.ClientOrderId` 鍵,而且訊息裡也寫著。

少了這一項,自動產生的編號會隨著失敗一起消失,那張單就成了一個既查不到也撤不掉的部位。
要更保險,請在送單**之前**自行呼叫 `BinanceClientOrderId.Generate()` 並寫進自己的委託紀錄。

### 參數按類型加入,不是全部加入再清掉

幣安對「哪些參數該出現」比抽象層嚴格得多:每個 `type` 有自己的必填集合,而**多送**一個
不屬於該類型的參數同樣會被拒(`-1106`)。因此 `BinanceOrderMapper` 是逐型別按需加入 ——
全部加入再清掉的寫法只要漏清一個就是一張被拒的單,而拒單訊息不會告訴你是哪一個參數多了。

對映表:

| 抽象層類型 | 幣安 `type` | 必帶參數 |
| --- | --- | --- |
| `Limit` | `LIMIT` | `quantity`、`price`、`timeInForce` |
| `Market` | `MARKET` | `quantity`(不可帶 `price` 或 `timeInForce`) |
| `StopMarket` | `STOP_MARKET` | `stopPrice` 加 `quantity` 或 `closePosition` |
| `StopLimit` | `STOP` | `quantity`、`price`、`stopPrice`、`timeInForce` |
| `TakeProfitMarket` | `TAKE_PROFIT_MARKET` | `stopPrice` 加 `quantity` 或 `closePosition` |
| `TakeProfitLimit` | `TAKE_PROFIT` | `quantity`、`price`、`stopPrice`、`timeInForce` |
| `TrailingStopMarket` | `TRAILING_STOP_MARKET` | `quantity`、`callbackRate`(0.1 至 10) |

幾個必須逐字照抄、不能照語意改寫的字面值:停損限價單是 `STOP` 而不是 `STOP_LIMIT`;
「最新成交價」是 `CONTRACT_PRICE` 而不是 `LAST_PRICE`;全倉在**寫入**時是 `CROSSED`,
但持倉查詢**讀回來**的 `marginType` 是小寫的 `cross`。

本套件另外補上幾條幣安比抽象層更嚴的規則,全部在本地擋下:`clientOrderId` 的格式與 36 字上限、
`closePosition` 只能用於市價型條件單、雙向模式不可帶 `reduceOnly`、
移動停損的回撤比例上限是 10 而不是抽象層允許的 100。

### 條件單目前不被 `/fapi/v1/order` 受理(`-4120`)

2026-09-11 在 Testnet 實測:`STOP_MARKET` 與 `TRAILING_STOP_MARKET` 送到 `/fapi/v1/order`
會得到 `-4120 Order type not supported for this endpoint. Please use the Algo Order API endpoints instead.`

參數的組法本身沒有錯,是**端點**變了。因此這一碼對映成 `TradeErrorCodes.NotSupported` 而不是
「參數錯誤」—— 後者會讓人回頭反覆檢查參數,而參數再怎麼改都不會讓這個端點接受它。
條件單的參數對映已經寫好也測過,但**端到端只驗到「被這個端點拒絕」**;
真正要下條件單需要接 Algo Order 端點,那不在本階段的範圍內。

### 數量一律向下對齊

送單前一律以該商品的交易規則在本地校正:數量向下對齊步進、價格對齊跳動點。
向上對齊會讓實際部位大於風控算出來的規模,那是風控破口而不是四捨五入問題。

校正後低於最小下單量或最小名目價值時,直接回傳失敗而不是送出去被拒 ——
省的不只是一趟往返,還有一份限流額度。

### 只有收盤的 K 線才是 K 線

即時串流會不斷推送同一根還在跳動的 K 線,每一筆的收盤價都是當下最新價。
`Kline.IsClosed` 在這些推送上一律是 `false`,只有最後一筆是 `true`。
拿未收盤的 K 線去算指標,訊號會在同一根 K 線內反覆翻面,策略就跟著反覆進出場。

這是這一塊最容易出錯、也最難發現的一點。同一根 K 線的各筆推送除了旗標之外完全相同,
所以把旗標寫死之後,價格、成交量、時間全部照樣正確,沒有任何斷言會掉 ——
而回測用的是收盤資料,連回測都一路綠燈。因此 `x` 欄位缺席一律判為解析失敗,絕不填預設值:
猜 `false` 會讓策略永遠等不到收盤的 K 線(功能靜默停擺),猜 `true` 會讓它在每一根 K 線內反覆下單。

`GetKlinesAsync` 同理,而且更隱蔽:REST 回應**根本沒有**收盤旗標,最後一根通常還在跳動。
這裡的 `IsClosed` 是用收盤時間與注入的時間來源比對出來的。把整串都當成已收盤,
等於把「查詢當下的最新價」寫成收盤價;存進歷史之後,之後的回測會用一根從未存在的 K 線。

### 路由照官方公告走,而且在主網上實際驗過

幣安把合約 WebSocket 拆成 `/public`、`/market`、`/private` 三條路由
([官方公告](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Important-WebSocket-Change-Notice)),
沒帶路由前綴的舊位址 2026-04-23 起停用,只收得到 public 類資料。它的失敗方式最壞:2026-09-12 在主網實測,
不帶路由的 `/stream` 與 `/ws/…` 訂 K 線與標記價,握手成功之後一個 frame 都沒有。沒有錯誤、沒有斷線,
只有永遠不動的價格。0.1.0 在主網上零資料就是這個原因,當時卻被誤判成本機網路問題。
Testnet 對行情仍相容舊位址,只在 Testnet 上驗證抓不到這件事,所以另有一條只連主網公開行情的測試。
路由與路徑都寫死在程式碼裡,不由設定拼裝。

另外兩個實測到的細節。外層包裝由**路徑**決定,與訂閱幾檔無關:走 `…/stream` 就算只訂一檔,
每則訊息仍包在 `{"stream":…,"data":…}` 裡;走 `…/ws` 就算訂十檔也沒有包裝。
而 `streams=` 裡的分隔符號必須是斜線,換成逗號連握手都不會成功。

### 使用者資料串流一律明列要收的事件

使用者資料串流撥的是 `/private/ws?listenKey=…&events=…`。0.1.0 撥的 `/ws/<listenKey>` 在 Testnet 上
已經收不到任何事件。`events` 是真的過濾器,而且事件名稱不會被驗證:少列或拼錯一個,那一類事件就安靜地永遠不來;
省略 `events` 的連線實測時好時壞,不能依賴。所以本套件一律明列委託與成交、帳戶增量、保證金追繳、憑證失效四種,
名稱直接取自解析器分派事件時用的同一組常數,不另寫一份字串。

### 存活偵測靠心跳,因為冷清的市場和死掉的連線長得一模一樣

`ClientWebSocket` 會自動回應對方的 ping,應用層完全看不到,所以唯一能判斷連線死活的訊號是
「多久沒收到任何訊息」。但 K 線推送只在有成交時才來,冷門標的可以安靜好幾分鐘 ——
這時候閒置逾時會把一條健康的連線判成死的,然後進入「斷線、重連、又被判死」的無效迴圈。
因此這裡固定送 `LIST_SUBSCRIPTIONS`:它一定會有回覆,回覆會更新閒置計時,
而真正死掉的連線仍然抓得到。

### 被丟棄的訊息會以「缺口」的形式回到串流上

背壓之下連線層會丟訊息,並以事件與統計回報 —— 這兩者在 `await foreach` 裡都看不到。
但被丟掉的很可能正是一根已收盤的 K 線,而那是唯一會被策略拿去下單的那種;
少了它,策略不會報錯,只會少做一次該做的事。所以丟棄會被補成串流上的一筆暫時性失敗,
並寫明丟了幾則。

## 測試

合約測試以錄製的回應重播,全程不連網。**所有 fixture 都是真實錄製的**:
`exchangeInfo` 與 `time` 來自公開端點,帳戶、持倉與委託的回應來自 2026-09-11 對 Testnet 的實際簽章請求。
`tests/.../Fixtures/PROVENANCE.md` 寫明每一份的來源端點、取得日期與裁切內容,
也說明其中兩份 `-open` 檔案為什麼是「實錄結構、替換數值」而非逐字實錄。

需要真實 Testnet 憑證的整合測試標記 `[TestCategory("Testnet")]`,
從環境變數 `BINANCE_TESTNET_API_KEY` 與 `BINANCE_TESTNET_API_SECRET` 讀取憑證。
沒有憑證時回報 Inconclusive 而不是通過:一個「沒跑但綠燈」的測試比沒有測試更危險。

這些測試會**實際送單**,因此只打 Testnet,主網的交易端點在任何情況下都不碰。
測試單的紀律是:掛在標記價下方約 4% 且用 GTC,所以不會成交;每一張都在 `finally` 裡撤掉,
即使斷言失敗也一樣;每一張都帶 `pulsetrade-test-` 前綴,萬一留下來看得出來源;
收尾另有一條測試與一段 `ClassCleanup` 查詢**全部商品**的掛單,確認沒有殘留。

行情測試是「需要憑證」這條規則的例外:行情是公開端點,所以訂閱 K 線與標記價的
`[TestCategory("Testnet")]` 測試完全不需要金鑰。其中一條會等一根 1m K 線收盤,
在真實連線上斷言 `IsClosed` 在整根 K 線期間為 `false`、收盤那一筆為 `true`。

主網只有一條測試,標記 `[TestCategory("MainnetPublic")]`:不設任何憑證,只連主網的公開行情,
在 60 秒內收到至少一根 BTCUSDT 1m K 線。Testnet 對行情仍相容不帶路由的舊位址,所以這條是路由修正有效的
唯一證據。把位址暫時改回 `/stream` 跑過一次,它確實在 60 秒後紅燈;改回 `/market/stream` 才綠。

單元測試全程不連網,串流也不例外:`Ozakboy.WebSockets` 把建立連線抽成
`IWebSocketConnectionFactory`,用假工廠就能離線走完整條訂閱路徑
(登記訂閱 → 連線 → 重放 `SUBSCRIBE` → 收訊息 → 解析 → 交給消費端)。

```
dotnet test --filter "TestCategory!=Testnet&TestCategory!=MainnetPublic"   # 離線合約測試
dotnet test --filter "TestCategory=Testnet"                                # 行情不需金鑰,交易需要憑證
dotnet test --filter "TestCategory=MainnetPublic"                          # 主網公開行情,不使用任何憑證
```

## 安全

- 套件不含任何預設憑證,也不會把憑證寫進檔案、日誌或 `ToString`。
- `signature`、`X-MBX-APIKEY` 與 `listenKey` 會自動加進日誌脫敏清單。
- 使用者資料串流的 listenKey 能連上帳戶的私有資料,所以它不會出現在任何錯誤訊息、`Error.Data`、
  例外或串流識別字裡。帶著它的訊息(`listenKeyExpired` 與每一則心跳回覆)一律不轉述,
  心跳回覆一被認出來就直接丟棄,內容連讀都不讀。
- listenKey **一取得就登記成遮罩器的已知祕密**(建立與續期都是),之後它出現在哪裡都會被換成遮罩字串。
  這一道與上一道的差別在於作用範圍:欄位名規則只看得到結構化 payload 裡有名字的欄位,
  而字面替換連沒有名字的位置也攔得到 —— 位址的路徑段、其他套件已經格式化好的訊息、例外文字。
  走 `AddBinanceUserData` 會自動接上;自行建構時請用接受 `SecretMasker` 的建構式多載,
  遮罩器以 `provider.GetOzakboyHttpMasker(BinanceConstants.HttpClientName)` 取得(必須是同一個實例)。
- 憑證由宿主注入、由 `Ozakboy.Http` 的 `SigningOptions` 承載,本套件只在簽章當下讀取。
- API 金鑰與密鑰登記在這個用戶端的遮罩器上,REST 用戶端回傳的錯誤 —— 訊息、每一筆 `Error.Data`、例外文字 ——
  都不會帶著它們,即使交易所或傳輸層例外把它們 echo 回來也一樣。

## 授權

MIT,見 [LICENSE](LICENSE)。
