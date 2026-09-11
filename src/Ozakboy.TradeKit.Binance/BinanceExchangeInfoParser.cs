using System.Text.Json;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 把 <c>/fapi/v1/exchangeInfo</c> 的回應解析成交易規則快照。
/// Parses a <c>/fapi/v1/exchangeInfo</c> response into a trading-rule snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>缺欄位一律拒絕該商品,絕不以預設值代替。</b> 交易規則是下單前唯一的本地防線:<c>tickSize</c> 猜錯會拿到
/// <c>-4014</c>、<c>stepSize</c> 猜錯會拿到 <c>-1111</c>、<c>minNotional</c> 猜錯會拿到 <c>-4164</c>,
/// 而這三個拒單訊息都不會說出是哪一項出問題。更糟的是「猜對了大部分商品」——
/// 那會讓錯誤集中在少數冷門標的上,看起來像偶發的網路問題。
/// <b>A missing field always rejects that symbol; nothing is substituted.</b> Trading rules are the only local
/// defence before an order goes out: a guessed <c>tickSize</c> earns <c>-4014</c>, a guessed <c>stepSize</c>
/// earns <c>-1111</c>, a guessed <c>minNotional</c> earns <c>-4164</c>, and none of those rejections names the
/// offending rule. Worse is guessing right for most symbols, which concentrates the failures on a few thin
/// markets and makes them look like intermittent network trouble.
/// </para>
/// <para>
/// 拒絕的範圍是<b>那一個商品</b>,不是整份快照。幣安會把尚未上架的商品也放進 <c>exchangeInfo</c> 並填佔位值
/// —— 2026-09-11 的 Testnet 快照裡 <c>ELSAUSDT</c> 就是 <c>PENDING_TRADING</c> 且 <c>tickSize</c> 為 <c>0</c>。
/// 讓這一筆把其餘 738 個商品一起判定為失敗,等於因為一個根本不能交易的標的而讓整個系統停擺。
/// 被拒絕的商品會連同原因記進 <see cref="BinanceExchangeInfoSnapshot.RejectedSymbols"/>,不會無聲消失。
/// The rejection covers <b>that symbol</b> and not the whole snapshot. Binance lists not-yet-launched symbols
/// with placeholder values — <c>ELSAUSDT</c> was <c>PENDING_TRADING</c> with a <c>tickSize</c> of <c>0</c> in
/// the Testnet snapshot of 2026-09-11 — and letting that one entry fail the other 738 would stop the system
/// over an instrument nobody can trade. Rejected symbols are recorded with their reason in
/// <see cref="BinanceExchangeInfoSnapshot.RejectedSymbols"/> rather than vanishing quietly.
/// </para>
/// <para>
/// 但<b>全部</b>商品都被拒絕時,整份解析仍然失敗。那不是資料瑕疵而是格式改變或對映寫錯,
/// 這種時候回傳一份空的交易規則,會讓系統看起來運作正常卻一張單都下不出去。
/// When <b>every</b> symbol is rejected the parse fails outright. That is not a data quirk but a format change
/// or a broken mapping, and returning an empty rule set would leave the system looking healthy while unable to
/// place a single order.
/// </para>
/// <para>
/// 解析的來源是<b>原始 JSON 字串</b>而不是 <see cref="HttpResponseMessage"/>,所以同一段程式碼
/// 既服務即時請求,也服務「把當日快照存進資料庫、隔天再讀回來」的路徑(§12.3)。
/// 環境是必填參數,重建快照時同樣跑不掉。
/// The input is a <b>raw JSON string</b> rather than an <see cref="HttpResponseMessage"/>, so the same code
/// serves both a live request and the "store today's snapshot, read it back tomorrow" path. The environment is
/// a required argument, so rebuilding a snapshot cannot lose track of where the rules came from.
/// </para>
/// </remarks>
public static class BinanceExchangeInfoParser
{
    private const string PriceFilterType = "PRICE_FILTER";
    private const string LotSizeFilterType = "LOT_SIZE";
    private const string MarketLotSizeFilterType = "MARKET_LOT_SIZE";
    private const string MinNotionalFilterType = "MIN_NOTIONAL";
    private const string MaxNumOrdersFilterType = "MAX_NUM_ORDERS";

    private const string TradingStatus = "TRADING";

    /// <summary>
    /// 解析 <c>exchangeInfo</c> 的回應。
    /// Parses an <c>exchangeInfo</c> response.
    /// </summary>
    /// <param name="json">回應本文。The response body.</param>
    /// <param name="endpoints">這份回應來自哪一組端點。Which endpoint set produced it.</param>
    /// <param name="fetchedAt">本機取得的時間。When it was fetched locally.</param>
    /// <returns>交易規則快照,或說明哪裡看不懂的失敗。The snapshot, or a failure saying what could not be read.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoints"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="endpoints"/> is <see langword="null"/>.
    /// </exception>
    public static Result<BinanceExchangeInfoSnapshot> Parse(
        string json,
        BinanceEndpoints endpoints,
        DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (string.IsNullOrWhiteSpace(json))
        {
            return BinanceErrors.MalformedResponse(
                "exchangeInfo 的回應是空的。The exchangeInfo response was empty.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return BinanceErrors.MalformedResponse(
                $"exchangeInfo 的回應不是合法 JSON:{exception.Message} The exchangeInfo response is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return BinanceErrors.MalformedResponse(
                    "exchangeInfo 的回應不是 JSON 物件。The exchangeInfo response is not a JSON object.");
            }

            if (!BinanceJson.TryGetTimestamp(root, "serverTime", out var serverTime))
            {
                return BinanceErrors.MissingField("serverTime", BinanceApiPaths.ExchangeInfo);
            }

            if (!root.TryGetProperty("symbols", out var symbolsElement)
                || symbolsElement.ValueKind != JsonValueKind.Array)
            {
                return BinanceErrors.MissingField("symbols", BinanceApiPaths.ExchangeInfo);
            }

            var symbols = new List<BinanceSymbolDetail>(symbolsElement.GetArrayLength());
            var rejected = new List<BinanceRejectedSymbol>();
            var index = 0;

            foreach (var symbolElement in symbolsElement.EnumerateArray())
            {
                var parsed = ParseSymbol(symbolElement);

                if (parsed.TryGetValue(out var detail))
                {
                    symbols.Add(detail);
                }
                else
                {
                    rejected.Add(new BinanceRejectedSymbol(DescribeSymbol(symbolElement, index), parsed.Error!));
                }

                index++;
            }

            if (symbols.Count == 0 && rejected.Count > 0)
            {
                // 一顆壞蘋果是資料瑕疵,整籃都壞是格式變了。後者回傳空清單會讓系統安靜地停擺。
                // One bad apple is a data quirk; the whole basket means the format changed, and an empty list
                // would stop the system silently.
                return BinanceErrors.MalformedResponse(
                        $"exchangeInfo 的 {rejected.Count} 個商品全部無法解析,研判是回應格式改變而非個別商品的資料問題。第一筆的原因:{rejected[0].Reason.Message} All {rejected.Count} symbols in exchangeInfo failed to parse, which points at a changed response format rather than individual bad data. The first reason was: {rejected[0].Reason.Message}")
                    .WithData(rejected[0].Reason.Data ?? new Dictionary<string, string>(StringComparer.Ordinal));
            }

            return new BinanceExchangeInfoSnapshot(endpoints, serverTime, fetchedAt, symbols, rejected);
        }
    }

    /// <summary>
    /// 解析單一商品的節點。
    /// Parses one symbol element.
    /// </summary>
    /// <param name="element">商品節點。The symbol element.</param>
    /// <returns>商品規則,或說明缺了什麼的失敗。The symbol rules, or a failure saying what is missing.</returns>
    internal static Result<BinanceSymbolDetail> ParseSymbol(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BinanceErrors.MalformedResponse(
                "symbols 陣列的元素不是 JSON 物件。An element of the symbols array is not a JSON object.");
        }

        if (!BinanceJson.TryGetString(element, "symbol", out var name))
        {
            return BinanceErrors.MissingField("symbol", BinanceApiPaths.ExchangeInfo);
        }

        if (!BinanceJson.TryGetString(element, "baseAsset", out var baseAsset))
        {
            return BinanceErrors.MissingField("baseAsset", name).WithData(BinanceErrorDataKeys.Symbol, name);
        }

        if (!BinanceJson.TryGetString(element, "quoteAsset", out var quoteAsset))
        {
            return BinanceErrors.MissingField("quoteAsset", name).WithData(BinanceErrorDataKeys.Symbol, name);
        }

        if (!BinanceJson.TryGetString(element, "status", out var status))
        {
            return BinanceErrors.MissingField("status", name).WithData(BinanceErrorDataKeys.Symbol, name);
        }

        if (!element.TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Array)
        {
            return BinanceErrors.MissingField("filters", name).WithData(BinanceErrorDataKeys.Symbol, name);
        }

        var rules = ReadFilters(filters, name);

        if (!rules.TryGetValue(out var filterValues))
        {
            return rules.ToFailure<BinanceSymbolDetail>();
        }

        // 顯示精度缺漏不致命:校正一律以 tickSize / stepSize 為準,精度只用於顯示。
        // 讀不到就填 -1,讓「沒有這項資料」與「精度就是 0」看得出差別。
        // A missing display precision is not fatal: normalisation always uses tickSize and stepSize, and the
        // precision is only shown to humans. A miss becomes -1 so that "no such value" stays distinguishable
        // from "the precision really is zero".
        var pricePrecision = BinanceJson.TryGetInt32(element, "pricePrecision", out var readPricePrecision)
            ? readPricePrecision
            : -1;

        var quantityPrecision = BinanceJson.TryGetInt32(element, "quantityPrecision", out var readQuantityPrecision)
            ? readQuantityPrecision
            : -1;

        var info = new SymbolInfo
        {
            Name = name,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            TickSize = filterValues.TickSize,
            StepSize = filterValues.StepSize,
            MinQuantity = filterValues.MinQuantity,
            MaxQuantity = filterValues.MaxQuantity,
            MinPrice = filterValues.MinPrice,
            MaxPrice = filterValues.MaxPrice,
            MinNotional = filterValues.MinNotional,
            IsTradingEnabled = string.Equals(status, TradingStatus, StringComparison.Ordinal),

            // MaxLeverage 維持抽象層的預設 1:exchangeInfo 不含這項資料,它在需要簽章的
            // /fapi/v1/leverageBracket。由 requiredMarginPercent 反推得到的是預設分層的槓桿而非上限
            // (BTCUSDT 反推 20,實際上限 125),填進去就是以錯的值冒充正確資料。
            // MaxLeverage keeps the abstraction's default of 1: exchangeInfo does not carry it, and deriving it
            // from requiredMarginPercent yields the default tier rather than the ceiling (20 for BTCUSDT, whose
            // real ceiling is 125), which would pass a wrong number off as fact.
        };

        var validation = info.Validate();

        if (validation.IsFailure)
        {
            // SymbolInfo.Validate 的訊息已經指名哪一條規則不合法,但沒有說是哪個商品的哪個來源,
            // 補上代碼才不必回頭翻一百萬行的回應。
            // The message from SymbolInfo.Validate already names the broken rule but not the symbol it came
            // from; adding the code saves someone a search through a million-line response.
            return validation.Error.WithData(BinanceErrorDataKeys.Symbol, name);
        }

        return new BinanceSymbolDetail
        {
            Info = info,
            Status = status,
            ContractType = BinanceJson.TryGetString(element, "contractType", out var contractType)
                ? contractType
                : string.Empty,
            MarginAsset = BinanceJson.TryGetString(element, "marginAsset", out var marginAsset)
                ? marginAsset
                : quoteAsset,
            MarketMinQuantity = filterValues.MarketMinQuantity,
            MarketMaxQuantity = filterValues.MarketMaxQuantity,
            MarketStepSize = filterValues.MarketStepSize,
            MaxOpenOrders = filterValues.MaxOpenOrders,
            PricePrecision = pricePrecision,
            QuantityPrecision = quantityPrecision,
        };
    }

    private static Result<FilterValues> ReadFilters(JsonElement filters, string symbol)
    {
        decimal? tickSize = null;
        decimal? minPrice = null;
        decimal? maxPrice = null;
        decimal? stepSize = null;
        decimal? minQuantity = null;
        decimal? maxQuantity = null;
        decimal? marketStepSize = null;
        decimal? marketMinQuantity = null;
        decimal? marketMaxQuantity = null;
        decimal? minNotional = null;
        int? maxOpenOrders = null;

        // 一個商品帶七種以上的篩選器,而且陣列順序不固定(同一份回應裡,不同商品的 filters 排列都不一樣),
        // 所以一律以 filterType 認人,不能靠索引。
        // A symbol carries seven or more filters in no fixed order — even within one response, different
        // symbols order their filters differently — so each is identified by filterType rather than by index.
        foreach (var filter in filters.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.Object
                || !BinanceJson.TryGetString(filter, "filterType", out var filterType))
            {
                continue;
            }

            switch (filterType)
            {
                case PriceFilterType:
                    tickSize = ReadDecimal(filter, "tickSize");
                    minPrice = ReadDecimal(filter, "minPrice");
                    maxPrice = ReadDecimal(filter, "maxPrice");
                    break;

                case LotSizeFilterType:
                    stepSize = ReadDecimal(filter, "stepSize");
                    minQuantity = ReadDecimal(filter, "minQty");
                    maxQuantity = ReadDecimal(filter, "maxQty");
                    break;

                case MarketLotSizeFilterType:
                    marketStepSize = ReadDecimal(filter, "stepSize");
                    marketMinQuantity = ReadDecimal(filter, "minQty");
                    marketMaxQuantity = ReadDecimal(filter, "maxQty");
                    break;

                case MinNotionalFilterType:
                    minNotional = ReadDecimal(filter, "notional");
                    break;

                case MaxNumOrdersFilterType:
                    maxOpenOrders = BinanceJson.TryGetInt32(filter, "limit", out var limit) ? limit : null;
                    break;

                default:
                    // PERCENT_PRICE 與 POSITION_RISK_CONTROL 這些本階段用不到,略過而不是報錯:
                    // 幣安新增篩選器類型時,不該讓整份交易規則變成解析失敗。
                    // PERCENT_PRICE, POSITION_RISK_CONTROL and the like are unused here and are skipped rather
                    // than rejected: a new filter type from Binance should not fail the whole rule set.
                    break;
            }
        }

        // 必要欄位逐一點名。缺哪一個就說哪一個 —— 一句「交易規則不完整」在九百多個商品裡等於沒說。
        // Each required field is named. Saying which one is missing matters: "incomplete trading rules" tells
        // nobody anything when there are nine hundred symbols.
        (string Name, decimal? Value)[] required =
        [
            ($"{PriceFilterType}.tickSize", tickSize),
            ($"{PriceFilterType}.minPrice", minPrice),
            ($"{PriceFilterType}.maxPrice", maxPrice),
            ($"{LotSizeFilterType}.stepSize", stepSize),
            ($"{LotSizeFilterType}.minQty", minQuantity),
            ($"{LotSizeFilterType}.maxQty", maxQuantity),
            ($"{MarketLotSizeFilterType}.stepSize", marketStepSize),
            ($"{MarketLotSizeFilterType}.minQty", marketMinQuantity),
            ($"{MarketLotSizeFilterType}.maxQty", marketMaxQuantity),
            ($"{MinNotionalFilterType}.notional", minNotional),
        ];

        foreach (var (fieldName, value) in required)
        {
            if (value is null)
            {
                return BinanceErrors.MissingField(fieldName, symbol).WithData(BinanceErrorDataKeys.Symbol, symbol);
            }
        }

        return new FilterValues(
            tickSize!.Value,
            minPrice!.Value,
            maxPrice!.Value,
            stepSize!.Value,
            minQuantity!.Value,
            maxQuantity!.Value,
            marketStepSize!.Value,
            marketMinQuantity!.Value,
            marketMaxQuantity!.Value,
            minNotional!.Value,
            maxOpenOrders);
    }

    private static string DescribeSymbol(JsonElement element, int index) =>
        BinanceJson.TryGetString(element, "symbol", out var name)
            ? name
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{index}");

    private static decimal? ReadDecimal(JsonElement element, string propertyName) =>
        BinanceJson.TryGetDecimal(element, propertyName, out var value) ? value : null;

    private readonly record struct FilterValues(
        decimal TickSize,
        decimal MinPrice,
        decimal MaxPrice,
        decimal StepSize,
        decimal MinQuantity,
        decimal MaxQuantity,
        decimal MarketStepSize,
        decimal MarketMinQuantity,
        decimal MarketMaxQuantity,
        decimal MinNotional,
        int? MaxOpenOrders);
}
