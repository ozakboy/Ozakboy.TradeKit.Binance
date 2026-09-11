using Ozakboy.Http;
using Ozakboy.Http.Signing;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 永續合約的用戶端。本版提供交易規則與唯讀的帳戶、持倉查詢。
/// The Binance USDⓈ-M perpetual futures client. This release provides trading rules plus read-only account and
/// position queries.
/// </summary>
/// <remarks>
/// <para>
/// 目前實作 <see cref="IExchangeInfoProvider"/>,尚未實作完整的 <see cref="IExchangeClient"/> ——
/// 下單、撤單、查單、改槓桿與保證金模式屬於下一階段。介面刻意先窄後寬:宣告一個做不到的介面,
/// 會讓呼叫端在編譯期看到方法、在執行期才發現它擲出「未實作」,那比編譯不過晚得多也貴得多。
/// This implements <see cref="IExchangeInfoProvider"/> and not yet the full
/// <see cref="IExchangeClient"/>: placing, cancelling, and querying orders, and changing leverage and margin
/// mode, belong to the next stage. The interface is deliberately narrow first. Declaring one that cannot be
/// honoured lets callers bind at compile time and discover a "not implemented" at run time, which is far later
/// and far more expensive than a build error.
/// </para>
/// <para>
/// 交易規則的部分整個委派給 <see cref="BinanceExchangeInfoProvider"/>,包含它的環境綁定快取。
/// 下一階段補上交易方法時,這個型別會改為實作 <see cref="IExchangeClient"/>,而現有的方法簽章都不會變 ——
/// 應用層現在寫的呼叫不需要跟著改。
/// The trading-rule half is delegated wholesale to <see cref="BinanceExchangeInfoProvider"/>, environment-bound
/// cache and all. When the next stage adds the trading methods this type will implement
/// <see cref="IExchangeClient"/> with none of the existing signatures changing, so calls written today need no
/// revision.
/// </para>
/// </remarks>
public sealed class BinanceFuturesClient : IExchangeInfoProvider, IDisposable
{
    private const string AccountOperation = "查詢帳戶資訊 / account query";
    private const string PositionOperation = "查詢持倉 / position query";

    private readonly BinanceApiClient _api;
    private readonly BinanceExchangeInfoProvider _exchangeInfo;
    private readonly bool _ownsExchangeInfo;
    private bool _disposed;

    /// <summary>
    /// 建立用戶端,並自行建立交易規則來源。
    /// Creates the client along with its own trading-rule provider.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="http"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="http"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public BinanceFuturesClient(HttpPipelineClient http, BinanceOptions options, TimeProvider? timeProvider = null)
        : this(http, options, new BinanceExchangeInfoProvider(http, options, timeProvider), ownsExchangeInfo: true, timeProvider)
    {
    }

    /// <summary>
    /// 建立用戶端,共用既有的交易規則來源。
    /// Creates the client sharing an existing trading-rule provider.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="exchangeInfo">交易規則來源。The trading-rule provider.</param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <exception cref="ArgumentNullException">
    /// 任一必要參數為 <see langword="null"/> 時擲出。Thrown when a required argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="exchangeInfo"/> 綁定的環境與 <paramref name="options"/> 不同時擲出。
    /// Thrown when <paramref name="exchangeInfo"/> is bound to a different environment than
    /// <paramref name="options"/>.
    /// </exception>
    /// <remarks>
    /// 共用是為了讓帳戶查詢與交易規則走同一份快取。建構時會比對兩者的環境:交易規則來自 Testnet
    /// 而帳戶查詢打主網的組合,會在這裡就擲出例外,而不是等到某張單因為步進值不符被拒才發現。
    /// Sharing keeps account queries and trading rules on one cache. The constructor compares the two
    /// environments: a provider on Testnet paired with account queries on production throws here rather than
    /// surfacing later as one order rejected for a step size that does not match.
    /// </remarks>
    public BinanceFuturesClient(
        HttpPipelineClient http,
        BinanceOptions options,
        BinanceExchangeInfoProvider exchangeInfo,
        TimeProvider? timeProvider = null)
        : this(http, options, exchangeInfo, ownsExchangeInfo: false, timeProvider)
    {
    }

    private BinanceFuturesClient(
        HttpPipelineClient http,
        BinanceOptions options,
        BinanceExchangeInfoProvider exchangeInfo,
        bool ownsExchangeInfo,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exchangeInfo);

        _api = new BinanceApiClient(http, options, timeProvider);

        if (exchangeInfo.Endpoints != _api.Endpoints)
        {
            throw new ArgumentException(
                $"交易規則來源綁定的是 {exchangeInfo.Endpoints.DisplayName},帳戶查詢設定的是 {_api.Endpoints.DisplayName},兩者必須是同一個環境。The trading-rule provider is bound to {exchangeInfo.Endpoints.DisplayName} while the account settings point at {_api.Endpoints.DisplayName}; both must be the same environment.",
                nameof(exchangeInfo));
        }

        _exchangeInfo = exchangeInfo;
        _ownsExchangeInfo = ownsExchangeInfo;
    }

    /// <summary>
    /// 這個用戶端連的是哪一組端點,也就是哪一個環境。
    /// Which endpoint set — and therefore which environment — this client talks to.
    /// </summary>
    public BinanceEndpoints Endpoints => _api.Endpoints;

    /// <summary>
    /// 交易規則來源。
    /// The trading-rule provider.
    /// </summary>
    public BinanceExchangeInfoProvider ExchangeInfo => _exchangeInfo;

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<SymbolInfo>>> GetSymbolsAsync(CancellationToken cancellationToken = default) =>
        _exchangeInfo.GetSymbolsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Result<SymbolInfo>> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default) =>
        _exchangeInfo.GetSymbolAsync(symbol, cancellationToken);

    /// <inheritdoc />
    public Task<Result<DateTimeOffset>> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
        _exchangeInfo.GetServerTimeAsync(cancellationToken);

    /// <summary>
    /// 取得帳戶快照:資產餘額、目前持倉與帳戶旗標。
    /// Gets an account snapshot: asset balances, current positions, and account flags.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>帳戶快照,或失敗原因。The snapshot, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 會打<b>兩個</b>端點:<c>/fapi/v2/account</c> 取餘額,<c>/fapi/v2/positionRisk</c> 取持倉,合計權重 10。
    /// 多花的 5 點是為了拿到 <c>markPrice</c> 與 <c>liquidationPrice</c> —— 帳戶端點的持倉沒有這兩項,
    /// 少了它們的 <see cref="Position.Notional"/> 會是零,而「名目價值為零」在風控眼中等於「沒有部位風險」。
    /// This calls <b>two</b> endpoints — <c>/fapi/v2/account</c> for balances and
    /// <c>/fapi/v2/positionRisk</c> for positions — for a combined weight of 10. The extra five buys
    /// <c>markPrice</c> and <c>liquidationPrice</c>, which the account endpoint's positions lack; without them
    /// <see cref="Position.Notional"/> comes out zero, and zero notional reads as no position risk at all.
    /// </remarks>
    public async Task<Result<AccountSnapshot>> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var positions = await GetPositionsAsync(cancellationToken).ConfigureAwait(false);

        if (!positions.TryGetValue(out var positionList))
        {
            return positions.ToFailure<AccountSnapshot>();
        }

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.Account,
                null,
                BinanceRequestWeights.Account,
                AccountOperation,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadAccountSnapshot(json, positionList, _api.UtcNow)
            : body.ToFailure<AccountSnapshot>();
    }

    /// <summary>
    /// 取得目前實際持有的部位。
    /// Gets the positions currently held.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>持倉清單,或失敗原因。The positions, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 空手的商品會被濾掉。幣安會把帳戶碰過的<b>每一個</b>商品都列出來,不濾的話一個只做三個標的的帳戶
    /// 也可能回上百筆全零的持倉,讓真正有部位的那幾筆淹沒在裡面。要看全部請用
    /// <see cref="GetPositionAsync"/>,它對空手的商品會回傳數量為零的持倉而不是失敗。
    /// Flat symbols are filtered out. Binance lists <b>every</b> symbol the account has touched, so without the
    /// filter an account trading three instruments can still return hundreds of all-zero rows that bury the few
    /// real ones. To ask about a specific flat symbol use <see cref="GetPositionAsync"/>, which returns a
    /// zero-quantity position rather than a failure.
    /// </remarks>
    public async Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.PositionRisk,
                null,
                BinanceRequestWeights.PositionRisk,
                PositionOperation,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadPositions(json, _api.UtcNow)
            : body.ToFailure<IReadOnlyList<Position>>();
    }

    /// <summary>
    /// 取得單一商品的持倉。空手時回傳數量為零的持倉,而不是失敗。
    /// Gets the position on one symbol, returning a flat position rather than a failure when there is none.
    /// </summary>
    /// <param name="symbol">交易對代碼。The symbol.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>持倉,或失敗原因。The position, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 雙向(避險)模式下同一商品可能同時有多空兩個部位,而中立模型一個 <see cref="Position"/> 表達不了兩個。
    /// 遇到這種情況時回傳失敗並說明原因,而不是挑一邊回傳 —— 挑一邊會讓平倉指令只平掉一半,
    /// 另一半留在市場上,那比查不到部位危險得多。
    /// In hedge mode a symbol can hold a long and a short position at once, which a single
    /// <see cref="Position"/> cannot express. That case returns an explicit failure rather than picking a side:
    /// picking one makes a close instruction flatten half the exposure and leave the rest live, which is far
    /// more dangerous than not finding a position.
    /// </remarks>
    public async Task<Result<Position>> GetPositionAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        var query = QueryParameters.CreateBuilder().Add(BinanceConstants.SymbolParameterName, symbol);

        var body = await _api
            .GetSignedAsync(
                BinanceApiPaths.PositionRisk,
                query,
                BinanceRequestWeights.PositionRisk,
                PositionOperation,
                cancellationToken)
            .ConfigureAwait(false);

        if (!body.TryGetValue(out var json))
        {
            return body.ToFailure<Position>();
        }

        var asOf = _api.UtcNow;
        var parsed = BinanceResponseReader.ReadPositions(json, asOf, includeFlat: true);

        if (!parsed.TryGetValue(out var positions))
        {
            return parsed.ToFailure<Position>();
        }

        // 就算已經帶了 symbol 參數,回來的清單仍要自己再篩一次。交易所回多回少不在這一側控制,
        // 而下面「有幾個未平部位」的判斷一旦把別的商品算進來,就會把單向模式誤判成雙向並整筆拒絕。
        // The list is filtered again even though the request carried a symbol. What the exchange returns is
        // not this side's decision, and letting another symbol into the open-position count below would read
        // one-way mode as hedge mode and refuse the whole query.
        var open = positions
            .Where(position => !position.IsFlat
                && string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return open.Count switch
        {
            0 => Result.Success(Position.Flat(symbol, asOf)),
            1 => Result.Success(open[0]),
            _ => TradeErrors.NotSupported(
                    $"{symbol} 在雙向模式下同時持有多空兩個部位,單一 Position 無法表示,請改用 GetPositionsAsync。{symbol} holds both a long and a short position in hedge mode, which a single Position cannot represent; use GetPositionsAsync instead.")
                .WithData(BinanceErrorDataKeys.Symbol, symbol),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsExchangeInfo)
        {
            _exchangeInfo.Dispose();
        }
    }
}
