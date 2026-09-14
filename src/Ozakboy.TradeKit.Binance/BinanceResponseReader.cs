using System.Globalization;
using System.Text.Json;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 把幣安帳戶與持倉端點的回應解析成交易所中立的模型。
/// Parses the responses of the Binance account and position endpoints into exchange-neutral models.
/// </summary>
/// <remarks>
/// <para>
/// 與 <see cref="BinanceExchangeInfoParser"/> 同樣以原始 JSON 字串為輸入,因此合約測試可以用錄製下來的
/// 回應重播,不必連線 —— 而這裡的端點需要真實金鑰與真實資金,能離線測試是必要條件而不是方便。
/// Like <see cref="BinanceExchangeInfoParser"/> this takes a raw JSON string, so contract tests can replay a
/// recorded response without a connection. For endpoints that need real credentials and real funds, offline
/// testing is a requirement rather than a convenience.
/// </para>
/// <para>
/// 欄位名有兩個必須逐字照抄的陷阱:<c>/fapi/v2/positionRisk</c> 的未實現損益是 <c>unRealizedProfit</c>
/// (大寫 R),而 <c>/fapi/v2/account</c> 的同一項是 <c>unrealizedProfit</c>(小寫 r)。
/// 抄錯的結果不是解析失敗,而是<b>讀到零</b>,讓風控看見一個永遠沒有浮動盈虧的帳戶。
/// Two field names have to be copied letter for letter: unrealised profit is <c>unRealizedProfit</c> with a
/// capital R on <c>/fapi/v2/positionRisk</c> and <c>unrealizedProfit</c> with a lower-case r on
/// <c>/fapi/v2/account</c>. Getting one wrong does not fail the parse — it <b>reads zero</b>, showing risk
/// management an account that never has any unrealised profit or loss.
/// </para>
/// </remarks>
public static class BinanceResponseReader
{
    private const string CrossMarginType = "cross";
    private const string LongPositionSide = "LONG";
    private const string ShortPositionSide = "SHORT";

    /// <summary>
    /// 幣安在回應本文裡用來表示「成功」的 <c>code</c> 值。
    /// The <c>code</c> value Binance uses inside a response body to mean success.
    /// </summary>
    private const int BinanceSuccessCode = 200;

    /// <summary>
    /// 解析 <c>/fapi/v1/time</c> 的回應。
    /// Parses the <c>/fapi/v1/time</c> response.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <returns>伺服器時間(UTC),或失敗原因。The server time in UTC, or the reason it failed.</returns>
    public static Result<DateTimeOffset> ReadServerTime(string json)
    {
        var parsed = TryParse(json, BinanceApiPaths.ServerTime);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<DateTimeOffset>();
        }

        using (document)
        {
            return BinanceJson.TryGetTimestamp(document.RootElement, "serverTime", out var serverTime)
                ? Result.Success(serverTime)
                : BinanceErrors.MissingField("serverTime", BinanceApiPaths.ServerTime);
        }
    }

    /// <summary>
    /// 解析 <c>/fapi/v2/positionRisk</c> 的回應。
    /// Parses the <c>/fapi/v2/positionRisk</c> response.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="asOf">這份回應對應的時刻,用於補上缺漏的更新時間。The moment it describes.</param>
    /// <param name="includeFlat">
    /// 是否保留數量為零的項目。幣安會把帳戶碰過的每個商品都列出來,多數是空手的。
    /// Whether to keep zero-quantity entries. Binance lists every symbol the account has touched, most of them
    /// flat.
    /// </param>
    /// <returns>持倉清單,或失敗原因。The positions, or the reason it failed.</returns>
    public static Result<IReadOnlyList<Position>> ReadPositions(
        string json,
        DateTimeOffset asOf,
        bool includeFlat = false)
    {
        var parsed = TryParse(json, BinanceApiPaths.PositionRisk);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<IReadOnlyList<Position>>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MalformedResponse(
                    "positionRisk 的回應不是 JSON 陣列。The positionRisk response is not a JSON array.");
            }

            var positions = new List<Position>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var position = ReadPosition(element, asOf);

                if (!position.TryGetValue(out var value))
                {
                    return position.ToFailure<IReadOnlyList<Position>>();
                }

                if (includeFlat || !value.IsFlat)
                {
                    positions.Add(value);
                }
            }

            return Result.Success<IReadOnlyList<Position>>(positions);
        }
    }

    /// <summary>
    /// 解析 <c>/fapi/v2/account</c> 的回應中的資產餘額與帳戶旗標。
    /// Parses the asset balances and account flags from the <c>/fapi/v2/account</c> response.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="positions">另行取得的持倉。The positions fetched separately.</param>
    /// <param name="takenAt">快照時刻。When the snapshot was taken.</param>
    /// <returns>帳戶快照,或失敗原因。The snapshot, or the reason it failed.</returns>
    /// <remarks>
    /// <para>
    /// 持倉<b>不</b>從這個回應讀,而是由呼叫端另外打 <c>/fapi/v2/positionRisk</c> 之後傳進來。
    /// 原因是 <c>/fapi/v2/account</c> 的 <c>positions[]</c> 沒有 <c>markPrice</c> 也沒有
    /// <c>liquidationPrice</c>:照它建出來的 <see cref="Position"/> 會有一個為零的
    /// <see cref="Position.MarkPrice"/>,而 <see cref="Position.Notional"/> 是用它算的 ——
    /// 風控會看到一個名目價值為零的持倉,也就是「沒有風險」。寧可多花 5 點權重,也不要送出那個數字。
    /// Positions are <b>not</b> read from this response; the caller fetches
    /// <c>/fapi/v2/positionRisk</c> separately and passes them in. The <c>positions[]</c> array of
    /// <c>/fapi/v2/account</c> carries neither <c>markPrice</c> nor <c>liquidationPrice</c>, so a
    /// <see cref="Position"/> built from it would have a zero <see cref="Position.MarkPrice"/> — and
    /// <see cref="Position.Notional"/> is derived from it, so risk management would see a position with zero
    /// notional, which reads as no risk at all. Five extra weight is cheaper than publishing that number.
    /// </para>
    /// <para>
    /// <see cref="AccountSnapshot.IsHedgeMode"/> 由持倉的 <c>positionSide</c> 推得:出現 <c>LONG</c> 或
    /// <c>SHORT</c> 就是雙向模式,全部是 <c>BOTH</c> 就是單向。直接查詢持倉模式的端點
    /// (<c>/fapi/v1/positionSide/dual</c>)權重高達 30,為了一個布林值多付六倍於整份帳戶查詢的代價並不划算。
    /// <see cref="AccountSnapshot.IsHedgeMode"/> is inferred from the positions' <c>positionSide</c>: any
    /// <c>LONG</c> or <c>SHORT</c> means hedge mode, all <c>BOTH</c> means one-way. The dedicated endpoint
    /// costs a weight of 30, six times the whole account query, which is poor value for one boolean.
    /// </para>
    /// </remarks>
    public static Result<AccountSnapshot> ReadAccountSnapshot(
        string json,
        IReadOnlyList<Position> positions,
        DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(positions);

        var parsed = TryParse(json, BinanceApiPaths.Account);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<AccountSnapshot>();
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return BinanceErrors.MalformedResponse(
                    "account 的回應不是 JSON 物件。The account response is not a JSON object.");
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MissingField("assets", BinanceApiPaths.Account);
            }

            var balances = new List<Balance>(assets.GetArrayLength());

            foreach (var element in assets.EnumerateArray())
            {
                var balance = ReadBalance(element);

                if (!balance.TryGetValue(out var value))
                {
                    return balance.ToFailure<AccountSnapshot>();
                }

                balances.Add(value);
            }

            return new AccountSnapshot
            {
                Balances = balances,
                Positions = positions,
                IsHedgeMode = positions.Any(static position => position.Side != PositionSide.Both),
                CanTrade = !BinanceJson.TryGetBoolean(root, "canTrade", out var canTrade) || canTrade,
                TakenAt = takenAt,
            };
        }
    }

    /// <summary>
    /// 解析單張委託的回應(<c>POST</c>、<c>GET</c>、<c>DELETE</c> <c>/fapi/v1/order</c> 三者同形)。
    /// Parses a single-order response; <c>POST</c>, <c>GET</c>, and <c>DELETE</c> on <c>/fapi/v1/order</c> all
    /// share one shape.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="asOf">回應對應的時刻,用於補上缺漏的時間。The moment it describes.</param>
    /// <returns>委託,或失敗原因。The order, or the reason it failed.</returns>
    public static Result<Order> ReadOrder(string json, DateTimeOffset asOf)
    {
        var parsed = TryParse(json, BinanceApiPaths.Order);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<Order>();
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadOrder(document.RootElement, asOf)
                : BinanceErrors.MalformedResponse(
                    "order 的回應不是 JSON 物件。The order response is not a JSON object.");
        }
    }

    /// <summary>
    /// 解析委託清單的回應(<c>GET /fapi/v1/openOrders</c>)。
    /// Parses an order list response (<c>GET /fapi/v1/openOrders</c>).
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="asOf">回應對應的時刻,用於補上缺漏的時間。The moment it describes.</param>
    /// <returns>委託清單,或失敗原因。The orders, or the reason it failed.</returns>
    public static Result<IReadOnlyList<Order>> ReadOrders(string json, DateTimeOffset asOf)
    {
        var parsed = TryParse(json, BinanceApiPaths.OpenOrders);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<IReadOnlyList<Order>>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MalformedResponse(
                    "openOrders 的回應不是 JSON 陣列。The openOrders response is not a JSON array.");
            }

            var orders = new List<Order>(document.RootElement.GetArrayLength());

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var order = ReadOrder(element, asOf);

                if (!order.TryGetValue(out var value))
                {
                    return order.ToFailure<IReadOnlyList<Order>>();
                }

                orders.Add(value);
            }

            return Result.Success<IReadOnlyList<Order>>(orders);
        }
    }

    /// <summary>
    /// 解析單張條件單的回應(<c>POST</c> 與 <c>GET</c> <c>/fapi/v1/algoOrder</c>)。
    /// Parses a single conditional order response (<c>POST</c> and <c>GET</c> on <c>/fapi/v1/algoOrder</c>).
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="asOf">回應對應的時刻,用於補上缺漏的時間。The moment it describes.</param>
    /// <returns>條件單,或失敗原因。The conditional order, or the reason it failed.</returns>
    /// <remarks>
    /// <c>POST</c> 與 <c>GET</c> 的回應<b>不同形</b>:<c>POST</c> 沒有 <c>actualOrderId</c>
    /// (那張單還沒觸發,自然沒有實際委託),<c>GET</c> 才有,而 <c>GET</c> 反過來不帶
    /// <c>activatePrice</c> 與 <c>callbackRate</c>。兩者共用這一支,靠的是這幾個欄位一律當成選填 ——
    /// 在這裡缺欄位是正常情況,不是解析失敗。
    /// The <c>POST</c> and <c>GET</c> responses are <b>not</b> the same shape: <c>POST</c> has no
    /// <c>actualOrderId</c>, since nothing has triggered yet and there is no real order, while <c>GET</c>
    /// carries it and in turn omits <c>activatePrice</c> and <c>callbackRate</c>. One reader serves both by
    /// treating all of those as optional: a missing field here is normal rather than a parse failure.
    /// </remarks>
    public static Result<ConditionalOrder> ReadConditionalOrder(string json, DateTimeOffset asOf)
    {
        var parsed = TryParse(json, BinanceApiPaths.AlgoOrder);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<ConditionalOrder>();
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadConditionalOrder(document.RootElement, asOf)
                : BinanceErrors.MalformedResponse(
                    "algoOrder 的回應不是 JSON 物件。The algoOrder response is not a JSON object.");
        }
    }

    /// <summary>
    /// 解析條件單清單的回應(<c>GET /fapi/v1/openAlgoOrders</c> 與 <c>GET /fapi/v1/allAlgoOrders</c>)。
    /// Parses a conditional order list response (<c>GET /fapi/v1/openAlgoOrders</c> and
    /// <c>GET /fapi/v1/allAlgoOrders</c>).
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="asOf">回應對應的時刻,用於補上缺漏的時間。The moment it describes.</param>
    /// <returns>條件單清單,或失敗原因。The conditional orders, or the reason it failed.</returns>
    public static Result<IReadOnlyList<ConditionalOrder>> ReadConditionalOrders(string json, DateTimeOffset asOf)
    {
        var parsed = TryParse(json, BinanceApiPaths.OpenAlgoOrders);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<IReadOnlyList<ConditionalOrder>>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MalformedResponse(
                    "openAlgoOrders 的回應不是 JSON 陣列。The openAlgoOrders response is not a JSON array.");
            }

            var orders = new List<ConditionalOrder>(document.RootElement.GetArrayLength());

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var order = ReadConditionalOrder(element, asOf);

                if (!order.TryGetValue(out var value))
                {
                    return order.ToFailure<IReadOnlyList<ConditionalOrder>>();
                }

                orders.Add(value);
            }

            return Result.Success<IReadOnlyList<ConditionalOrder>>(orders);
        }
    }

    /// <summary>
    /// 解析帳戶成交紀錄的回應(<c>GET /fapi/v1/userTrades</c>)。
    /// Parses the account trade list response (<c>GET /fapi/v1/userTrades</c>).
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <returns>成交清單,或失敗原因。The fills, or the reason it failed.</returns>
    /// <remarks>
    /// 這裡<b>不</b>接受「時刻拿不到就用現在時間頂替」的做法,與委託的解析不同。委託少一個時間欄位只是
    /// 顯示難看,成交的時刻卻是對帳補查的游標:一筆成交被記成「現在」,下一次補查的起點就被推到未來,
    /// 中間真正漏掉的成交從此再也查不回來,而且帳上看起來完全正常。
    /// Unlike the order readers, nothing here falls back to "use the current time when the timestamp is
    /// missing". A missing timestamp on an order is cosmetic; on a fill it is the reconciliation cursor. Record
    /// one fill as happening now and the next sweep starts in the future, so the fills genuinely missed in
    /// between are never recovered — and the books look entirely normal.
    /// </remarks>
    public static Result<IReadOnlyList<Trade>> ReadUserTrades(string json)
    {
        var parsed = TryParse(json, BinanceApiPaths.UserTrades);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<IReadOnlyList<Trade>>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MalformedResponse(
                    "userTrades 的回應不是 JSON 陣列。The userTrades response is not a JSON array.");
            }

            var trades = new List<Trade>(document.RootElement.GetArrayLength());

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var trade = ReadUserTrade(element);

                if (!trade.TryGetValue(out var value))
                {
                    return trade.ToFailure<IReadOnlyList<Trade>>();
                }

                trades.Add(value);
            }

            return Result.Success<IReadOnlyList<Trade>>(trades);
        }
    }

    /// <summary>
    /// 解析撤銷單張條件單的回應(<c>DELETE /fapi/v1/algoOrder</c>),取出被撤掉的那張的識別碼。
    /// Parses the cancel response of one conditional order (<c>DELETE /fapi/v1/algoOrder</c>) and returns the
    /// identifier of what was cancelled.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <returns>被撤掉的條件單識別碼,或失敗原因。The identifier, or the reason it failed.</returns>
    /// <remarks>
    /// <para>
    /// 這個端點<b>不回傳那張條件單</b>,只回
    /// <c>{"algoId":…,"clientAlgoId":…,"code":"200","msg":"success"}</c> —— 沒有方向、沒有類型、
    /// 沒有觸發價。要交出一個完整的 <see cref="ConditionalOrder"/>,只能撤完之後再查一次。
    /// This endpoint <b>does not return the conditional order</b>, only
    /// <c>{"algoId":…,"clientAlgoId":…,"code":"200","msg":"success"}</c>: no side, no type, no trigger price.
    /// Producing a complete <see cref="ConditionalOrder"/> means looking it up again after the cancellation.
    /// </para>
    /// <para>
    /// <c>code</c> 在這個端點是<b>字串</b> <c>"200"</c>,而撤銷全部條件單那個端點回的是<b>數值</b> 200。
    /// 同一個概念在相鄰兩個端點上型別不同,所以識別碼在這裡自己讀,不靠
    /// <see cref="ReadAcknowledgement"/> 判成敗 —— 那一支只認數值型的 <c>code</c>。
    /// The <c>code</c> is a <b>string</b> <c>"200"</c> here while the cancel-all endpoint answers with a
    /// <b>numeric</b> 200: one concept, two types, on adjacent endpoints. The identifier is therefore read
    /// here rather than leaning on <see cref="ReadAcknowledgement"/>, which recognises only a numeric
    /// <c>code</c>.
    /// </para>
    /// </remarks>
    public static Result<ConditionalOrderIdentifier> ReadConditionalOrderCancellation(string json)
    {
        var parsed = TryParse(json, BinanceApiPaths.AlgoOrder);

        if (!parsed.TryGetValue(out var document))
        {
            return parsed.ToFailure<ConditionalOrderIdentifier>();
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return BinanceErrors.MalformedResponse(
                    "algoOrder 撤單的回應不是 JSON 物件。The algoOrder cancel response is not a JSON object.");
            }

            if (BinanceJson.TryGetInt64(root, "algoId", out var algoId) && algoId > 0)
            {
                return Result.Success(
                    ConditionalOrderIdentifier.FromExchangeId(algoId.ToString(CultureInfo.InvariantCulture)));
            }

            return BinanceJson.TryGetString(root, "clientAlgoId", out var clientAlgoId)
                ? Result.Success(ConditionalOrderIdentifier.FromClientId(clientAlgoId))
                : BinanceErrors.MissingField("algoId", BinanceApiPaths.AlgoOrder);
        }
    }

    /// <summary>
    /// 檢查「只回報成敗、不回報內容」的端點回應是否真的成功(撤銷全部掛單、改槓桿、改保證金模式)。
    /// Checks whether an acknowledgement-only response really succeeded: cancel-all, leverage, and margin mode.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="endpoint">端點路徑,用於錯誤附註。The endpoint path, recorded on any failure.</param>
    /// <param name="endpoints">環境,用於錯誤附註。The environment, recorded on any failure.</param>
    /// <returns>成功,或已對映的失敗。Success, or a mapped failure.</returns>
    /// <remarks>
    /// <para>
    /// 這幾個端點成功時回的是 <c>{"code":200,"msg":"success"}</c> —— 本文裡確實有一個 <c>code</c> 欄位,
    /// 但 200 在這裡代表成功而不是錯誤。不特別判斷這個值,就得讓 HTTP 狀態碼一個人扛全部的判斷;
    /// 多檢查一次的成本是幾微秒,漏掉的成本是「以為撤乾淨了、其實沒有」,然後帶著殘留掛單去平倉。
    /// A success from these endpoints reads <c>{"code":200,"msg":"success"}</c>: the body does carry a
    /// <c>code</c>, but 200 there means success rather than an error. Without checking it the HTTP status
    /// carries the whole judgement alone, and the cost of being wrong is believing the book is clear and then
    /// closing a position with orders still resting on it.
    /// </para>
    /// <para>
    /// 沒有 <c>code</c> 欄位的本文(例如改槓桿回的 <c>{"leverage":10,...}</c>)一律視為成功:
    /// 走到這裡代表 HTTP 已經是 2xx,而那份回應沒有任何表示失敗的內容。
    /// A body without a <c>code</c> — the leverage endpoint's <c>{"leverage":10,...}</c>, for instance — counts
    /// as success: reaching here means the HTTP status was already 2xx and the body says nothing to the contrary.
    /// </para>
    /// </remarks>
    public static Result ReadAcknowledgement(string json, string endpoint, BinanceEndpoints? endpoints = null)
    {
        // 本文空白視為成功:HTTP 狀態碼已經表態,而空本文不帶任何相反的訊息。
        // An empty body counts as success: the status code has already spoken and an empty body says nothing
        // that contradicts it.
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result.Success();
        }

        return !BinanceErrorMapper.TryParseApiError(json, out var apiCode, out var apiMessage)
            || apiCode == BinanceSuccessCode
                ? Result.Success()
                : BinanceErrorMapper.Map(apiCode, apiMessage, endpoint, endpoints);
    }

    private static Result<Order> ReadOrder(JsonElement element, DateTimeOffset asOf)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                "委託清單的元素不是 JSON 物件。An element of the order list is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, "symbol", out var symbol))
        {
            return BinanceErrors.MissingField("symbol", BinanceApiPaths.Order);
        }

        if (!BinanceJson.TryGetString(element, "clientOrderId", out var clientOrderId))
        {
            return BinanceErrors.MissingField("clientOrderId", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var statusText = BinanceJson.TryGetString(element, "status", out var readStatus) ? readStatus : null;
        var status = BinanceOrderMapper.ParseOrderStatus(statusText);

        if (status == OrderStatus.Unspecified)
        {
            // 對不上的狀態不放行。既不算在簿上、也不算終態的委託,會讓部位追蹤永遠等不到結局。
            // An unmapped status is not let through: an order that counts as neither live nor final leaves
            // position tracking waiting for an outcome that never arrives.
            return BinanceErrors.MalformedResponse(
                    $"{symbol} 的委託狀態「{statusText}」無法對映到任何已知狀態。The order status \"{statusText}\" on {symbol} maps to no known state.")
                .WithData(BinanceErrorDataKeys.Field, "status")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var sideText = BinanceJson.TryGetString(element, "side", out var readSide) ? readSide : null;
        var side = BinanceOrderMapper.ParseOrderSide(sideText);

        if (side == OrderSide.Unspecified)
        {
            // 方向對不上同樣不放行。方向是「這張單會開出什麼部位」的全部資訊,猜錯就是反向部位。
            // An unmapped side is refused too: the side is the whole answer to which position this order
            // creates, and guessing wrong is an inverted position.
            return BinanceErrors.MalformedResponse(
                    $"{symbol} 的買賣方向「{sideText}」無法對映。The order side \"{sideText}\" on {symbol} maps to nothing.")
                .WithData(BinanceErrorDataKeys.Field, "side")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, "origQty", out var quantity))
        {
            return BinanceErrors.MissingField("origQty", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var updatedAt = BinanceJson.TryGetTimestamp(element, "updateTime", out var updateTime) ? updateTime : asOf;

        return new Order
        {
            Symbol = symbol,
            ClientOrderId = clientOrderId,

            // orderId 是數值,中立模型卻用字串裝 —— 別的交易所的訂單編號不一定是數字。
            // The id is numeric here while the neutral model stores a string, because other exchanges do not
            // necessarily use numbers.
            ExchangeOrderId = BinanceJson.TryGetInt64(element, "orderId", out var orderId)
                ? orderId.ToString(CultureInfo.InvariantCulture)
                : null,
            Side = side,

            // origType 是「當初送出的類型」。條件單觸發之後 type 會變成實際掛出去的那一種,
            // 拿它回報等於把使用者下的 STOP_MARKET 說成 MARKET。
            // origType is the type as submitted. Once a conditional order triggers, type becomes whatever was
            // actually placed, and reporting that turns the caller's STOP_MARKET into a MARKET.
            OrderType = BinanceOrderMapper.ParseOrderType(ReadOrderTypeText(element)),
            Status = status,
            PositionSide = BinanceOrderMapper.ParsePositionSide(
                BinanceJson.TryGetString(element, "positionSide", out var positionSide) ? positionSide : null),
            TimeInForce = BinanceOrderMapper.ParseTimeInForce(
                BinanceJson.TryGetString(element, "timeInForce", out var timeInForce) ? timeInForce : null),
            Quantity = quantity,
            FilledQuantity = BinanceJson.TryGetDecimal(element, "executedQty", out var executed) ? executed : 0m,
            AverageFillPrice = BinanceJson.TryGetDecimal(element, "avgPrice", out var averagePrice) ? averagePrice : 0m,

            // 幣安對「沒有這個價格」的表示是 0,不是省略欄位。市價單的 price 就是 "0",
            // 照抄下去會讓上層看到一張「限價零元」的委託。
            // Binance writes "no such price" as 0 rather than omitting the field: a market order's price is
            // "0", and copying that through shows the caller an order priced at zero.
            Price = ReadOptionalPrice(element, "price"),
            StopPrice = ReadOptionalPrice(element, "stopPrice"),
            ReduceOnly = BinanceJson.TryGetBoolean(element, "reduceOnly", out var reduceOnly) && reduceOnly,
            ClosePosition = BinanceJson.TryGetBoolean(element, "closePosition", out var closePosition) && closePosition,
            FilledNotional = BinanceJson.TryGetDecimal(element, "cumQuote", out var cumQuote) ? cumQuote : 0m,

            // 下單的回應只有 updateTime,查單的回應才另外帶 time。沒有 time 時以 updateTime 充當建立時間:
            // 那是這張單目前唯一知道的時刻,好過填一個 default(DateTimeOffset) 的西元 0001 年。
            // The place-order reply carries only updateTime while the query reply adds time. Without time the
            // update time stands in, being the only instant known about this order, which beats a year-0001 default.
            CreatedAt = BinanceJson.TryGetTimestamp(element, "time", out var createdAt) ? createdAt : updatedAt,
            UpdatedAt = updatedAt,
        };
    }

    private static Result<Trade> ReadUserTrade(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                "成交清單的元素不是 JSON 物件。An element of the trade list is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, "symbol", out var symbol))
        {
            return BinanceErrors.MissingField("symbol", BinanceApiPaths.UserTrades);
        }

        // 成交編號是去重的唯一依據。補查回來的清單一定會與串流已收到的重疊(起點取的是最後一筆的時間本身),
        // 沒有這個編號就只能靠「時間 + 價格 + 數量」猜,而同一毫秒同價同量的兩筆成交在合約上很常見。
        // The trade id is the only basis for de-duplication. A sweep always overlaps with what the stream
        // already delivered, and without the id the alternative is guessing from time, price, and quantity —
        // two fills sharing all three inside one millisecond are commonplace on a futures book.
        if (!BinanceJson.TryGetInt64(element, "id", out var tradeId))
        {
            return BinanceErrors.MissingField("id", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var side = ReadTradeSide(element);

        if (side == OrderSide.Unspecified)
        {
            // 方向對不上不放行,理由與委託相同:方向就是「這筆成交讓部位往哪邊動」的全部資訊。
            // An unmapped side is refused for the same reason as on an order: the side is the whole answer to
            // which way this fill moved the position.
            return BinanceErrors.MalformedResponse(
                    $"{symbol} 的成交方向無法對映,side 與 buyer 兩個欄位都讀不出方向。The side of a fill on {symbol} maps to nothing; neither side nor buyer yielded a direction.")
                .WithData(BinanceErrorDataKeys.Field, "side")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, "price", out var price))
        {
            return BinanceErrors.MissingField("price", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, "qty", out var quantity))
        {
            return BinanceErrors.MissingField("qty", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetTimestamp(element, "time", out var executedAt))
        {
            return BinanceErrors.MissingField("time", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var hasFee = BinanceJson.TryGetDecimal(element, "commission", out var fee);
        var hasFeeAsset = BinanceJson.TryGetString(element, "commissionAsset", out var feeAsset);

        if (hasFee && !hasFeeAsset)
        {
            // 手續費沒有幣別就是一個不能用的數字:合約帳戶可以用別的資產抵扣,把它當計價幣直接從損益裡扣,
            // 對帳會差一截,而差多少要看那個資產當天的價格。寧可在這裡失敗,也不要交出一個算得出來的錯數。
            // A fee without its currency is a number that cannot be used: a derivatives account may pay in a
            // different asset, and subtracting it from quote-currency P&L leaves a gap whose size depends on
            // that asset's price on the day. Failing here beats handing out a wrong figure that still adds up.
            return BinanceErrors.MissingField("commissionAsset", symbol)
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        return new Trade
        {
            Symbol = symbol,

            // 編號在幣安是數值,中立模型用字串裝 —— 別的交易所的成交編號不一定是數字。
            // The id is numeric here while the neutral model stores a string, because other exchanges do not
            // necessarily use numbers.
            TradeId = tradeId.ToString(CultureInfo.InvariantCulture),
            ExchangeOrderId = BinanceJson.TryGetInt64(element, "orderId", out var orderId)
                ? orderId.ToString(CultureInfo.InvariantCulture)
                : null,

            // 這個端點不回 clientOrderId。留成 null 而不是填空字串:空字串會讓「沒有這項資訊」
            // 看起來像「這張單的用戶端編號是空的」。
            // This endpoint does not return a clientOrderId. It stays null rather than empty, because an empty
            // string makes "not reported" look like "the order's client id was blank".
            ClientOrderId = null,
            Side = side,
            PositionSide = ReadPositionSide(element),
            Price = price,
            Quantity = quantity,
            Fee = hasFee ? fee : 0m,
            FeeAsset = hasFeeAsset ? feeAsset : string.Empty,
            RealizedPnl = BinanceJson.TryGetDecimal(element, "realizedPnl", out var realizedPnl) ? realizedPnl : 0m,
            IsMaker = BinanceJson.TryGetBoolean(element, "maker", out var maker) && maker,
            ExecutedAt = executedAt,
        };
    }

    /// <summary>
    /// 讀出一筆成交的買賣方向:先看 <c>side</c>,讀不到再退回布林的 <c>buyer</c>。
    /// Reads the side of a fill, preferring <c>side</c> and falling back to the boolean <c>buyer</c>.
    /// </summary>
    /// <remarks>
    /// 兩個欄位講的是同一件事,但幣安在不同時期的回應裡不一定兩個都給。先認 <c>side</c> 是因為它與委託
    /// 那邊同一套字彙;<c>buyer</c> 只在前者缺席時才用,而不是拿來覆蓋它 —— 兩個都讀、以其中一個為準,
    /// 才不會在欄位不一致時靜默選到另一個意思。
    /// The two fields say the same thing, but Binance has not always sent both. <c>side</c> comes first because
    /// it shares its vocabulary with the order endpoints; <c>buyer</c> only stands in when <c>side</c> is
    /// absent rather than overriding it, so an inconsistency between them cannot quietly flip the direction.
    /// </remarks>
    private static OrderSide ReadTradeSide(JsonElement element)
    {
        var side = BinanceOrderMapper.ParseOrderSide(
            BinanceJson.TryGetString(element, "side", out var sideText) ? sideText : null);

        if (side != OrderSide.Unspecified)
        {
            return side;
        }

        return BinanceJson.TryGetBoolean(element, "buyer", out var buyer)
            ? buyer ? OrderSide.Buy : OrderSide.Sell
            : OrderSide.Unspecified;
    }

    private static Result<ConditionalOrder> ReadConditionalOrder(JsonElement element, DateTimeOffset asOf)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                "條件單清單的元素不是 JSON 物件。An element of the conditional order list is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, "symbol", out var symbol))
        {
            return BinanceErrors.MissingField("symbol", BinanceApiPaths.AlgoOrder);
        }

        if (!BinanceJson.TryGetString(element, "clientAlgoId", out var clientAlgoId))
        {
            return BinanceErrors.MissingField("clientAlgoId", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var statusText = BinanceJson.TryGetString(element, "algoStatus", out var readStatus) ? readStatus : null;
        var status = BinanceAlgoOrderMapper.ParseAlgoStatus(statusText);

        if (status == ConditionalOrderStatus.Unspecified)
        {
            // 對不上的狀態不放行,理由與一般委託相同:既不算有效、也不算終態的停損,
            // 會讓對帳永遠等不到結局,而畫面上看起來一切正常。
            // An unmapped status is not let through, for the same reason as on an ordinary order: a stop that
            // counts as neither live nor final leaves reconciliation waiting for an outcome that never comes,
            // while everything on screen looks fine.
            return BinanceErrors.MalformedResponse(
                    $"{symbol} 的條件單狀態「{statusText}」無法對映到任何已知狀態。The algo status \"{statusText}\" on {symbol} maps to no known state.")
                .WithData(BinanceErrorDataKeys.Field, "algoStatus")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var sideText = BinanceJson.TryGetString(element, "side", out var readSide) ? readSide : null;
        var side = BinanceOrderMapper.ParseOrderSide(sideText);

        if (side == OrderSide.Unspecified)
        {
            return BinanceErrors.MalformedResponse(
                    $"{symbol} 的買賣方向「{sideText}」無法對映。The order side \"{sideText}\" on {symbol} maps to nothing.")
                .WithData(BinanceErrorDataKeys.Field, "side")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var typeText = BinanceJson.TryGetString(element, "orderType", out var readType) ? readType : null;
        var conditionalOrderType = BinanceAlgoOrderMapper.ParseConditionalOrderType(typeText);

        if (conditionalOrderType == ConditionalOrderType.Unspecified)
        {
            // 類型在這裡不能像一般委託那樣放過。查掛單會撈到手動下的單,那裡出現沒對映的委託類型是常態,
            // 所以那一側回 Unspecified；但這條路徑上的每一張都是條件單,而條件單的類型決定了
            // 「它會在什麼時候、以什麼方式動用部位」—— 不知道類型就等於不知道這張單會做什麼。
            // Unlike an ordinary order, the type cannot be let through here. An open-orders listing includes
            // hand-placed orders whose types this package does not model, which is why that side answers
            // Unspecified; but everything on this path is a conditional order, and its type is what says when
            // and how it will move the position. Not knowing the type is not knowing what the order will do.
            return BinanceErrors.MalformedResponse(
                    $"{symbol} 的條件單類型「{typeText}」無法對映到任何已知類型。The conditional order type \"{typeText}\" on {symbol} maps to no known type.")
                .WithData(BinanceErrorDataKeys.Field, "orderType")
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var updatedAt = BinanceJson.TryGetTimestamp(element, "updateTime", out var updateTime) ? updateTime : asOf;

        return new ConditionalOrder
        {
            Symbol = symbol,
            ClientConditionalOrderId = clientAlgoId,

            // algoId 是數值,中立模型用字串裝 —— 別的交易所的編號不一定是數字。
            // The id is numeric here while the neutral model stores a string, because other exchanges do not
            // necessarily use numbers.
            ExchangeConditionalOrderId = BinanceJson.TryGetInt64(element, "algoId", out var algoId)
                ? algoId.ToString(CultureInfo.InvariantCulture)
                : null,
            Side = side,
            ConditionalOrderType = conditionalOrderType,
            Status = status,
            PositionSide = BinanceOrderMapper.ParsePositionSide(
                BinanceJson.TryGetString(element, "positionSide", out var positionSide) ? positionSide : null),
            TimeInForce = BinanceOrderMapper.ParseTimeInForce(
                BinanceJson.TryGetString(element, "timeInForce", out var timeInForce) ? timeInForce : null),
            Quantity = BinanceJson.TryGetDecimal(element, "quantity", out var quantity) ? quantity : 0m,

            // 幣安對「沒有這個價格」的表示是 0 或空字串,不是省略欄位;市價型條件單的 price 就是 "0"。
            // 照抄下去會讓上層看到一張「限價零元」的停損。
            // Binance writes "no such price" as 0 or an empty string rather than omitting the field: a
            // market-style conditional order's price is "0". Copying that through shows a stop priced at zero.
            TriggerPrice = ReadOptionalPrice(element, "triggerPrice"),
            TriggerPriceType = ParseWorkingType(
                BinanceJson.TryGetString(element, "workingType", out var workingType) ? workingType : null),
            Price = ReadOptionalPrice(element, "price"),
            CallbackRate = ReadOptionalPrice(element, "callbackRate"),
            ActivationPrice = ReadOptionalPrice(element, "activatePrice"),
            ReduceOnly = BinanceJson.TryGetBoolean(element, "reduceOnly", out var reduceOnly) && reduceOnly,
            ClosePosition = BinanceJson.TryGetBoolean(element, "closePosition", out var closePosition)
                && closePosition,

            // actualOrderId 在未觸發時是空字串,不是省略,也不是 0。空字串代表「還沒有實際委託」,
            // 照抄成 "" 會讓上層拿一個空字串去查單。
            // actualOrderId is an empty string before the trigger rather than absent or zero. The empty string
            // means there is no real order yet, and passing it through sends the caller to look up "".
            TriggeredOrderId = ReadNonEmptyString(element, "actualOrderId"),

            // triggerTime 未觸發時是 0,而 0 在 Unix 毫秒是 1970 年 —— 直接轉換會讓一張還沒觸發的停損
            // 看起來像是五十年前就觸發過了。
            // triggerTime is 0 before the trigger, and zero in Unix milliseconds is 1970: converting it
            // directly makes an untriggered stop look as though it fired half a century ago.
            TriggeredAt = ReadOptionalTimestamp(element, "triggerTime"),
            CreatedAt = BinanceJson.TryGetTimestamp(element, "createTime", out var createdAt) ? createdAt : updatedAt,
            UpdatedAt = updatedAt,
        };
    }

    /// <summary>
    /// 把幣安的 <c>workingType</c> 轉回中立的觸發價種類。
    /// Converts a Binance <c>workingType</c> back into the neutral trigger price type.
    /// </summary>
    /// <param name="value">幣安回傳的字串。The string Binance returned.</param>
    /// <returns>
    /// 對映得到的種類;對不上時為 <see cref="TriggerPriceType.LastPrice"/>。
    /// The mapped type, falling back to <see cref="TriggerPriceType.LastPrice"/>.
    /// </returns>
    /// <remarks>
    /// 退路刻意是成交價而不是標記價,因為<b>幣安的預設就是 <c>CONTRACT_PRICE</c></b>。
    /// 退成標記價會讓一張實際看成交價的停損被回報成「看標記價」,而那正是「為什麼被一根影線掃掉」
    /// 查不出原因的來源。
    /// The fallback is deliberately the traded price rather than the mark price, because
    /// <b>Binance's own default is <c>CONTRACT_PRICE</c></b>. Falling back to the mark price would report a stop
    /// that really watches traded prices as watching the mark — and that is exactly what makes "why did a single
    /// wick take it out" impossible to answer.
    /// </remarks>
    private static TriggerPriceType ParseWorkingType(string? value) => value switch
    {
        "MARK_PRICE" => TriggerPriceType.MarkPrice,
        _ => TriggerPriceType.LastPrice,
    };

    private static string? ReadNonEmptyString(JsonElement element, string propertyName) =>
        BinanceJson.TryGetString(element, propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement element, string propertyName) =>
        BinanceJson.TryGetInt64(element, propertyName, out var milliseconds) && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    private static string? ReadOrderTypeText(JsonElement element)
    {
        if (BinanceJson.TryGetString(element, "origType", out var origType))
        {
            return origType;
        }

        return BinanceJson.TryGetString(element, "type", out var type) ? type : null;
    }

    private static decimal? ReadOptionalPrice(JsonElement element, string propertyName) =>
        BinanceJson.TryGetDecimal(element, propertyName, out var value) && value > 0m ? value : null;

    private static Result<Balance> ReadBalance(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                "assets 陣列的元素不是 JSON 物件。An element of the assets array is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, "asset", out var asset))
        {
            return BinanceErrors.MissingField("asset", BinanceApiPaths.Account);
        }

        if (!BinanceJson.TryGetDecimal(element, "walletBalance", out var walletBalance))
        {
            return BinanceErrors.MissingField("walletBalance", asset);
        }

        if (!BinanceJson.TryGetDecimal(element, "availableBalance", out var availableBalance))
        {
            return BinanceErrors.MissingField("availableBalance", asset);
        }

        // 未實現損益在 account 回應是小寫 r 的 unrealizedProfit(positionRisk 那邊是大寫 R)。
        // Unrealised profit is spelled with a lower-case r here and a capital R on positionRisk.
        if (!BinanceJson.TryGetDecimal(element, "unrealizedProfit", out var unrealizedProfit))
        {
            return BinanceErrors.MissingField("unrealizedProfit", asset);
        }

        return new Balance
        {
            Asset = asset,
            WalletBalance = walletBalance,
            AvailableBalance = availableBalance,
            UnrealizedPnl = unrealizedProfit,
        };
    }

    private static Result<Position> ReadPosition(JsonElement element, DateTimeOffset asOf)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                "positionRisk 陣列的元素不是 JSON 物件。An element of the positionRisk array is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, "symbol", out var symbol))
        {
            return BinanceErrors.MissingField("symbol", BinanceApiPaths.PositionRisk);
        }

        if (!BinanceJson.TryGetDecimal(element, "positionAmt", out var quantity))
        {
            return BinanceErrors.MissingField("positionAmt", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, "entryPrice", out var entryPrice))
        {
            return BinanceErrors.MissingField("entryPrice", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetDecimal(element, "markPrice", out var markPrice))
        {
            return BinanceErrors.MissingField("markPrice", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        // 大寫 R。抄成小寫不會報錯,只會讓每一筆持倉的浮動盈虧都是零。
        // Capital R. A lower-case copy does not fail — it just zeroes every position's unrealised P&L.
        if (!BinanceJson.TryGetDecimal(element, "unRealizedProfit", out var unrealizedProfit))
        {
            return BinanceErrors.MissingField("unRealizedProfit", symbol)
                .WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetInt32(element, "leverage", out var leverage) || leverage < 1)
        {
            return BinanceErrors.MissingField("leverage", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        if (!BinanceJson.TryGetString(element, "marginType", out var marginType))
        {
            return BinanceErrors.MissingField("marginType", symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
        }

        var isolatedMargin = BinanceJson.TryGetDecimal(element, "isolatedMargin", out var readIsolatedMargin)
            ? readIsolatedMargin
            : 0m;

        // 強平價為 0 代表「沒有強平價」(空手,或全倉而交易所未提供),不是「會在零元被強平」。
        // 用 null 表示才不會讓風控把 0 當成一個近在眼前的強平價。
        // A liquidation price of 0 means there is none — the position is flat, or it is cross-margined and the
        // exchange gives no figure — not that liquidation happens at zero. Null keeps risk management from
        // reading it as an imminent one.
        var liquidationPrice = BinanceJson.TryGetDecimal(element, "liquidationPrice", out var readLiquidation)
            && readLiquidation > 0m
                ? readLiquidation
                : (decimal?)null;

        return new Position
        {
            Symbol = symbol,
            Quantity = quantity,
            Side = ReadPositionSide(element),
            EntryPrice = entryPrice,
            MarkPrice = markPrice,
            UnrealizedPnl = unrealizedProfit,
            LiquidationPrice = liquidationPrice,
            Leverage = leverage,
            MarginMode = string.Equals(marginType, CrossMarginType, StringComparison.OrdinalIgnoreCase)
                ? MarginMode.Cross
                : MarginMode.Isolated,
            Margin = isolatedMargin,
            UpdatedAt = BinanceJson.TryGetTimestamp(element, "updateTime", out var updateTime) ? updateTime : asOf,
        };
    }

    private static PositionSide ReadPositionSide(JsonElement element)
    {
        if (!BinanceJson.TryGetString(element, "positionSide", out var side))
        {
            return PositionSide.Both;
        }

        return side switch
        {
            LongPositionSide => PositionSide.Long,
            ShortPositionSide => PositionSide.Short,
            _ => PositionSide.Both,
        };
    }

    private static Result<JsonDocument> TryParse(string json, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BinanceErrors.MalformedResponse($"{endpoint} 的回應是空的。The {endpoint} response was empty.");
        }

        try
        {
            return Result.Success(JsonDocument.Parse(json));
        }
        catch (JsonException exception)
        {
            return BinanceErrors.MalformedResponse(
                $"{endpoint} 的回應不是合法 JSON:{exception.Message} The {endpoint} response is not valid JSON: {exception.Message}");
        }
    }
}
