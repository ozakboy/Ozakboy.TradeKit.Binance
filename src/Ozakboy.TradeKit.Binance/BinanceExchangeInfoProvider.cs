using Ozakboy.Http;

namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約的交易規則來源,內建每日更新的記憶體快取。
/// The Binance USDⓈ-M source of trading rules, with a built-in daily memory cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>快取以環境為鍵,而且不可能弄錯。</b> 這裡沒有以環境為鍵的字典,也沒有可以從外面塞進來的快取物件:
/// 每個實例在建構時就從 <see cref="BinanceOptions.ResolveEndpoints"/> 綁定<b>一組</b>端點,
/// 快取欄位是這個實例的私有狀態,而放進去的 <see cref="BinanceExchangeInfoSnapshot"/> 自己也記著來源端點。
/// 因此「拿 Testnet 的交易規則去主網下單」需要的前提 —— 兩個環境共用一份快取 —— 在這個型別裡不存在:
/// 沒有共用的容器可以放錯,也沒有 API 可以把別的環境的快照交給它。
/// <b>The cache is keyed by environment, and cannot be got wrong.</b> There is no dictionary keyed by
/// environment here and no cache object that can be handed in from outside: each instance binds to <b>one</b>
/// endpoint set at construction, the cache field is that instance's private state, and the
/// <see cref="BinanceExchangeInfoSnapshot"/> stored in it remembers its own source. The precondition for
/// trading production with Testnet rules — one cache shared between two environments — therefore does not
/// exist in this type: there is no shared container to mix up and no API through which another environment's
/// snapshot could be supplied.
/// </para>
/// <para>
/// 這一點之所以值得花力氣,是因為那個 bug 沒有症狀。實測 <c>BTCUSDT</c> 的 <c>stepSize</c> 在 Testnet 是
/// <c>0.0001</c>、主網是 <c>0.001</c>:用 Testnet 的規則校正出來的 <c>0.0015</c> 在主網會被拒,
/// 而回應只會說「參數不合法」。更糟的情況是它<b>沒有</b>被拒 —— 大部分數量在兩邊都合法,
/// 於是錯誤只在某些尾數上偶爾出現,看起來像網路問題。
/// The effort is warranted because that bug has no symptom. Measured: <c>BTCUSDT</c> has a <c>stepSize</c> of
/// <c>0.0001</c> on Testnet and <c>0.001</c> on production, so a quantity of <c>0.0015</c> normalised against
/// Testnet rules is rejected on production with nothing but "invalid parameter". Worse is when it is
/// <b>not</b> rejected: most quantities are legal under both, so the failures appear only on certain
/// fractional values and look like network trouble.
/// </para>
/// <para>
/// 執行緒安全。多執行緒同時遇到快取過期時,只有一個會真的去打 <c>exchangeInfo</c>,其餘等它完成後直接用結果
/// —— 交易規則是全系統共用的資料,讓 N 個策略各打一次只是白白吃掉限流額度。
/// Thread-safe. When several threads find the cache stale at once only one actually calls
/// <c>exchangeInfo</c> and the rest use its result: the rules are shared by the whole system, and letting N
/// strategies each fetch them merely spends rate-limit quota.
/// </para>
/// </remarks>
public sealed class BinanceExchangeInfoProvider : IExchangeInfoProvider, IDisposable
{
    private readonly BinanceApiClient _api;
    private readonly BinanceOptions _options;
    private readonly BinanceEndpoints _endpoints;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private BinanceExchangeInfoSnapshot? _snapshot;
    private bool _disposed;

    /// <summary>
    /// 建立交易規則來源。
    /// Creates the trading-rule provider.
    /// </summary>
    /// <param name="http">已組好管線的用戶端。The client with the pipeline already assembled.</param>
    /// <param name="options">連線設定。The connection settings.</param>
    /// <param name="timeProvider">時間來源,測試時可替換。The time source, replaceable in tests.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="http"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="http"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> 不合法時擲出。Thrown when <paramref name="options"/> is invalid.
    /// </exception>
    public BinanceExchangeInfoProvider(
        HttpPipelineClient http,
        BinanceOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _api = new BinanceApiClient(http, options, timeProvider);
        _options = options;
        _endpoints = _api.Endpoints;
    }

    /// <summary>
    /// 這個實例服務的是哪一組端點,也就是哪一個環境。
    /// Which endpoint set — and therefore which environment — this instance serves.
    /// </summary>
    public BinanceEndpoints Endpoints => _endpoints;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SymbolInfo>>> GetSymbolsAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        return snapshot.TryGetValue(out var value)
            ? Result.Success(value.SymbolInfos)
            : snapshot.ToFailure<IReadOnlyList<SymbolInfo>>();
    }

    /// <inheritdoc />
    public async Task<Result<SymbolInfo>> GetSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetSymbolDetailAsync(symbol, cancellationToken).ConfigureAwait(false);

        return detail.TryGetValue(out var value)
            ? Result.Success(value.Info)
            : detail.ToFailure<SymbolInfo>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 直接打 <c>/fapi/v1/time</c>,<b>不</b>讀快取。這個方法存在的目的就是偵測本機時鐘偏移,
    /// 回傳一個幾小時前快取下來的伺服器時間會讓它完全失去意義。
    /// This calls <c>/fapi/v1/time</c> and does <b>not</b> read the cache. The whole point of the method is to
    /// detect local clock drift, and answering with a server time cached hours ago would defeat it entirely.
    /// </remarks>
    public async Task<Result<DateTimeOffset>> GetServerTimeAsync(CancellationToken cancellationToken = default)
    {
        var body = await _api
            .GetPublicAsync(BinanceApiPaths.ServerTime, null, BinanceRequestWeights.ServerTime, cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceResponseReader.ReadServerTime(json)
            : body.ToFailure<DateTimeOffset>();
    }

    /// <summary>
    /// 取得單一商品的完整規則,含幣安專屬欄位。
    /// Gets one symbol's full rules, including the Binance-specific fields.
    /// </summary>
    /// <param name="symbol">交易對代碼,不分大小寫。The symbol code; case-insensitive.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 商品規則;代碼不存在時為 <see cref="TradeErrorCodes.SymbolNotFound"/> 的失敗,
    /// 代碼存在但交易規則讀不出來時則為當初排除它的那個原因。
    /// The rules; a <see cref="TradeErrorCodes.SymbolNotFound"/> failure when the code does not exist, or the
    /// original reason when the code exists but its trading rules could not be read.
    /// </returns>
    /// <remarks>
    /// 兩種失敗刻意分開。都回「查無此交易對」的話,一個規則還沒填好的新商品會被當成打錯字,
    /// 而真正該做的是去看交易所公告。
    /// The two failures are deliberately distinct: collapsing both into "no such symbol" turns a symbol whose
    /// rules are not filled in yet into a suspected typo.
    /// </remarks>
    public async Task<Result<BinanceSymbolDetail>> GetSymbolDetailAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return TradeErrors.InvalidQuery("交易對代碼不可為空白。The symbol must not be blank.");
        }

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (!snapshot.TryGetValue(out var value))
        {
            return snapshot.ToFailure<BinanceSymbolDetail>();
        }

        if (value.TryGetSymbol(symbol, out var detail) && detail is not null)
        {
            return Result.Success(detail);
        }

        // 「沒有這個代碼」與「這個代碼的規則有問題」要分開回答。都回 SymbolNotFound 的話,
        // 一個規則還沒填好的新商品會被當成打錯字,而真正該做的是去看交易所公告。
        // "No such code" and "this code's rules are broken" get different answers. Collapsing both into
        // SymbolNotFound turns a symbol mid-launch into a suspected typo.
        return value.TryGetRejection(symbol, out var rejection) && rejection is not null
            ? rejection.Reason.WithData(BinanceErrorDataKeys.Environment, _endpoints.DisplayName)
            : TradeErrors.SymbolNotFound(symbol).WithData(BinanceErrorDataKeys.Environment, _endpoints.DisplayName);
    }

    /// <summary>
    /// 取得目前的交易規則快照,必要時重新抓取。
    /// Gets the current trading-rule snapshot, refetching when necessary.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>交易規則快照,或失敗原因。The snapshot, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    public async Task<Result<BinanceExchangeInfoSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var current = Volatile.Read(ref _snapshot);

        if (IsUsable(current))
        {
            return Result.Success(current!);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 進閘門後再看一次:等待期間可能已經有人抓好了。
            // Check again inside the gate: someone may have finished fetching while this thread waited.
            current = Volatile.Read(ref _snapshot);

            if (IsUsable(current))
            {
                return Result.Success(current!);
            }

            var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);

            if (!fetched.TryGetValue(out var snapshot))
            {
                return fetched;
            }

            Volatile.Write(ref _snapshot, snapshot);
            return Result.Success(snapshot);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// 強制重新抓取交易規則,忽略快取是否過期。
    /// Forces a refetch of the trading rules regardless of cache age.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>新的交易規則快照,或失敗原因。The new snapshot, or the reason it failed.</returns>
    /// <exception cref="ObjectDisposedException">
    /// 實例已釋放時擲出。Thrown when the instance has been disposed.
    /// </exception>
    /// <remarks>
    /// 抓取失敗時<b>保留</b>舊快照,不清空。過期的規則仍然遠勝於沒有規則:清空之後每一次校正都會失敗,
    /// 等於交易所抖一下就讓整個系統停擺,而幣安並不會每天調整交易規則。
    /// A failed refetch <b>keeps</b> the previous snapshot rather than clearing it. Stale rules still beat no
    /// rules: clearing them makes every normalisation fail, so one hiccup at the exchange would stop the whole
    /// system — and Binance does not change trading rules daily.
    /// </remarks>
    public async Task<Result<BinanceExchangeInfoSnapshot>> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);

            if (fetched.TryGetValue(out var snapshot))
            {
                Volatile.Write(ref _snapshot, snapshot);
            }

            return fetched;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshGate.Dispose();
    }

    private bool IsUsable(BinanceExchangeInfoSnapshot? snapshot) =>
        snapshot is not null
        && snapshot.Endpoints == _endpoints
        && !snapshot.IsExpired(_api.UtcNow, _options.ExchangeInfoCacheTtl);

    private async Task<Result<BinanceExchangeInfoSnapshot>> FetchAsync(CancellationToken cancellationToken)
    {
        var body = await _api
            .GetPublicAsync(
                BinanceApiPaths.ExchangeInfo,
                null,
                BinanceRequestWeights.ExchangeInfo,
                cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetValue(out var json)
            ? BinanceExchangeInfoParser.Parse(json, _endpoints, _api.UtcNow)
            : body.ToFailure<BinanceExchangeInfoSnapshot>();
    }
}
