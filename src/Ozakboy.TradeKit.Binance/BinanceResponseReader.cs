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
