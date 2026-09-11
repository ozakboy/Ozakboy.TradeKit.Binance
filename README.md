# Ozakboy.TradeKit.Binance

A Binance USDⓈ-M perpetual futures client for .NET, with no third-party dependencies.

[繁體中文說明](README_zh-TW.md)

This package implements [`Ozakboy.TradeKit.Abstractions`](https://github.com/ozakboy/Ozakboy.TradeKit.Abstractions)
against Binance's USDⓈ-M futures API, on top of the signing, rate-limiting, retry, and log-masking pipeline in
[`Ozakboy.Http`](https://github.com/ozakboy/Ozakboy.Http). It exists because the project it serves does not use
community packages such as `Binance.Net`; every dependency here is either the .NET BCL, a first-party Microsoft
package, or another `Ozakboy.*` package.

## What this release covers

| Area | Status |
| --- | --- |
| Trading rules (`exchangeInfo`) with a per-environment daily cache | Done |
| Server time (`/fapi/v1/time`) | Done |
| Read-only account and position queries | Done |
| Binance error code mapping, including the transience verdict | Done |
| Request weight table and rate limiting | Done |
| Placing, cancelling, and querying orders; cancel-all and open orders | Done |
| Leverage and margin mode | Done |
| Conditional order parameter mapping (stop, take-profit, trailing) | Done, but the endpoint no longer accepts them — see below |
| WebSocket market streams and user data streams | Next stage |

The client implements the whole of `IExchangeClient`. Every price, quantity, and amount is a `decimal`, and
every timestamp is UTC.

## Install

```
dotnet add package Ozakboy.TradeKit.Binance
```

Targets `net10.0`.

## Quick start

```csharp
services.AddBinanceFutures(options =>
{
    options.Environment = BinanceEnvironment.Testnet;

    // Credentials come from the host, never from this package.
    options.ApiKey = configuration["BINANCE_API_KEY"]!;
    options.SecretKey = configuration["BINANCE_API_SECRET"]!;
});
```

```csharp
var client = provider.GetRequiredService<BinanceFuturesClient>();

var symbol = await client.GetSymbolAsync("BTCUSDT");

if (!symbol.TryGetValue(out var rules))
{
    logger.LogError("Could not read the trading rules: {Error}", symbol.Error);
    return;
}

// Normalise before sending. The rules are the only local defence against a rejection
// whose message says nothing more than "invalid parameter".
var sized = rules.NormalizeOrderSize(price: 62_800m, quantity: 0.0123456m);
```

Public endpoints need no credentials, so reading trading rules works with an empty key.

Placing an order looks like this:

```csharp
var request = new OrderRequest
{
    Symbol = "BTCUSDT",
    Side = OrderSide.Buy,
    OrderType = OrderType.Limit,
    Quantity = 0.01m,
    Price = 60_000m,

    // The idempotency key. The package generates one when it is left unset, but supplying it yourself means
    // the id reaches your own order log *before* the request leaves — which is what makes a timeout
    // recoverable.
    ClientOrderId = BinanceClientOrderId.Generate(),
};

var placed = await client.PlaceOrderAsync(request);

if (!placed.TryGetValue(out var order))
{
    // Never resend after a timeout or a failure: look the order up by its clientOrderId first.
    placed.Error!.TryGetData(BinanceErrorDataKeys.ClientOrderId, out var clientOrderId);
    logger.LogError("Submission failed; look up {ClientOrderId}: {Error}", clientOrderId, placed.Error);
    return;
}

await client.CancelOrderAsync(order.Symbol, order.GetIdentifier());
```

Normalising beforehand is optional: `PlaceOrderAsync` fetches the symbol's rules, aligns the quantity down to
the step size and the price to the tick, and fails outright when the result falls below the minimum quantity or
notional instead of sending it to be rejected.

## Design decisions worth knowing

### The environment is chosen as a set, never URL by URL

`BinanceEndpoints` has no public constructor. A set comes from `BinanceEndpoints.For(environment)` or from an
explicit `CreateOverride`, and it always carries both the REST and the WebSocket base together. A mismatched
pair — orders on Testnet while prices come from the real market — cannot be expressed. Such a mix throws
nothing and leaves a strategy looking perfectly healthy while trading one market on another's prices.

### The trading-rule cache is keyed by environment, structurally

Testnet and production do not share trading rules. Measured on 2026-09-11: `BTCUSDT` reports a `stepSize` of
`0.0001` on Testnet and `0.001` on production. A quantity validated against the wrong set is rejected with
nothing more than "invalid parameter" — and most quantities are legal under both, so the failures appear only
on certain fractional values and look like intermittent network trouble.

There is no dictionary keyed by environment here and no cache object that can be passed in from outside. Each
`BinanceExchangeInfoProvider` binds to one endpoint set at construction, its cache is private instance state,
and the snapshot inside remembers its own source. `BinanceFuturesClient` compares environments when a provider
is shared with it and refuses a mismatch at construction.

### One unusable symbol does not empty the rule set

Binance lists not-yet-launched symbols in `exchangeInfo` with placeholder values. In the Testnet snapshot of
2026-09-11, `ELSAUSDT` was `PENDING_TRADING` with a `tickSize` of `0`. Failing the whole rule set over one such
entry would stop the system for an instrument nobody can trade.

A symbol whose rules cannot be read is therefore excluded from the snapshot and recorded in
`RejectedSymbols` with the reason — never given a guessed default. Looking it up returns that reason rather
than a bare "no such symbol", so a symbol mid-launch does not look like a typo. When **every** symbol fails,
the parse fails outright: that is a format change rather than a data quirk, and an empty rule set would leave
the system looking healthy while unable to place a single order.

### Transience is decided by category, not by a per-code flag

`Error.IsTransient` is the only thing callers consult before retrying, and the two ways of getting it wrong
cost differently: giving up on a recoverable failure turns a traffic jam into an outage, while retrying an
unrecoverable one burns the rate-limit quota and earns an IP ban. Transience follows from `ErrorCategory`, so
getting the category right makes it right by construction.

Three codes get extra care:

| Binance | Neutral code | Transient | Why it matters |
| --- | --- | --- | --- |
| `-1021` | `trade.timestamp_out_of_sync` | No | Clock drift. Replaying the same skewed timestamp earns the same rejection; re-synchronise with `GetServerTimeAsync` instead. |
| `-1022` | `trade.invalid_signature` | No | A signature does not become correct on its own, and retrying only spends quota. |
| `-2015` | `trade.invalid_credentials` | No | Usually the IP rather than the key: every request turns into this after a home connection changes address. The message says to check the allowlist first. |

`BinanceErrorMapper.WithOutboundIpAddress` lets the application attach the address it looked up. Discovering it
means calling a third-party echo service, which a library should not do on the user's behalf.

### Signing follows five rules, all of them verified

1. **Parameter order changes the signature.** Parameters live in `QueryParameters`, an ordered container, never
   in a dictionary — dictionary enumeration order is not guaranteed, and the result is an intermittent `-1022`
   that passes on the next run.
2. **Encode, then sign.** The signed string is exactly the string that goes out, escaped with
   `Uri.EscapeDataString` so a space becomes `%20` rather than `+`.
3. **No exponent notation and no trailing zeros** in serialised decimals.
4. **Lower-case hexadecimal** signature; millisecond Unix epoch timestamp.
5. **Everything goes in the query string**, POST included. The mixed-mode example in Binance's futures
   documentation cannot be reproduced — every permutation of its eight parameters was tried and none yields the
   published signature — while the equivalent spot example reproduces exactly.

### Request weights are declared per call

Binance meters by weight rather than by request count: one `exchangeInfo` costs 1 while one `openOrders`
without a symbol costs 40. `BinanceRequestWeights` holds the table and every request declares its own weight,
because under-declaring is not caught locally but by an HTTP 429 that escalates to a 418 IP ban.

Only the `REQUEST_WEIGHT` bucket is configured. The order-rate limits count against the account rather than the
IP, and adding them as a second bucket would let ordinary queries eat the order allowance — worse than having
no order throttle at all.

Both environments default to production's ceiling of 2400 per minute even though Testnet advertises 6000, so a
pace that passes in testing also passes in production. Override with `RequestWeightPerMinute` when the larger
allowance is genuinely wanted.

### Account snapshots cost two calls on purpose

`GetAccountSnapshotAsync` calls `/fapi/v2/account` for balances and `/fapi/v2/positionRisk` for positions, for
a combined weight of 10. The account endpoint's own `positions[]` carries neither `markPrice` nor
`liquidationPrice`, and a `Position` built from it would have a zero notional — which reads as no position risk
at all.

### Orders are never retried; everything else is

This is the one rule the package will not bend. **A timeout does not mean the exchange missed it**: the order
may already be resting, or filled, with only the reply lost in transit. Re-sending then opens twice the
intended position, in real money.

The request `PlaceOrderAsync` sends therefore does two things at once: it is marked with `AsNonIdempotent()`
and additionally pinned to `RetryPolicy.NoRetry`. The two guards are deliberate — the marker stops the
`Ozakboy.Http` retry handler, and the policy stops whoever swaps the default for a predicate that retries
everything.

Everything else is idempotent and retries safely:

| Operation | Retryable | Why |
| --- | --- | --- |
| `PlaceOrderAsync` | **No** | A resend after a timeout is two orders and twice the position |
| `CancelOrderAsync` | Yes | Cancelling twice earns a `-2011`, meaning "it is no longer on the book" |
| `CancelAllOrdersAsync` | Yes | Having nothing to cancel counts as success |
| `GetOrderAsync` / `GetOpenOrdersAsync` | Yes | The worst outcome of repeating a query is one more unit of weight |
| `SetLeverageAsync` | Yes | Setting it to what it already is is not an error |
| `SetMarginModeAsync` | Yes | The `-4046` a resend earns already counts as success |

### The `clientOrderId` comes back whether the submission succeeds or fails

Every order carries a `newClientOrderId`, generated by `BinanceClientOrderId` when the caller supplies none.
What matters is that it **survives a failure**: in `Order.ClientOrderId` on success, and under the
`BinanceErrorDataKeys.ClientOrderId` key of `Error.Data` — and in the message — on failure.

Without that, an auto-generated id vanishes with the failure and the order becomes a position that can be
neither looked up nor cancelled. For more safety still, call `BinanceClientOrderId.Generate()` yourself and
write the id into your own order log *before* submitting.

### Parameters are added per type rather than added wholesale and pruned

Binance is far stricter than the abstraction about which parameters may appear: each `type` has its own
mandatory set, and sending one that does not belong is rejected just as firmly (`-1106`). `BinanceOrderMapper`
therefore adds them per type on demand: with the prune-afterwards approach, one missed prune is a rejected
order, and the rejection does not say which parameter was the extra one.

| Neutral type | Binance `type` | Mandatory parameters |
| --- | --- | --- |
| `Limit` | `LIMIT` | `quantity`, `price`, `timeInForce` |
| `Market` | `MARKET` | `quantity` (no `price`, no `timeInForce`) |
| `StopMarket` | `STOP_MARKET` | `stopPrice` plus `quantity` or `closePosition` |
| `StopLimit` | `STOP` | `quantity`, `price`, `stopPrice`, `timeInForce` |
| `TakeProfitMarket` | `TAKE_PROFIT_MARKET` | `stopPrice` plus `quantity` or `closePosition` |
| `TakeProfitLimit` | `TAKE_PROFIT` | `quantity`, `price`, `stopPrice`, `timeInForce` |
| `TrailingStopMarket` | `TRAILING_STOP_MARKET` | `quantity`, `callbackRate` (0.1 to 10) |

A few literals have to be copied rather than reasoned about: a stop-limit is `STOP` and not `STOP_LIMIT`, the
last traded price is `CONTRACT_PRICE` and not `LAST_PRICE`, and cross margin is `CROSSED` when **written**
while the `marginType` **read back** from a position query is a lower-case `cross`.

The package adds the handful of rules where Binance is stricter than the abstraction, all enforced locally:
the `clientOrderId` format and its 36-character ceiling, `closePosition` being limited to market-style
conditional orders, hedge mode refusing `reduceOnly`, and a trailing callback ceiling of 10 rather than the
abstraction's 100.

### Conditional orders are not currently accepted by `/fapi/v1/order` (`-4120`)

Measured against Testnet on 2026-09-11: `STOP_MARKET` and `TRAILING_STOP_MARKET` sent to `/fapi/v1/order`
answer `-4120 Order type not supported for this endpoint. Please use the Algo Order API endpoints instead.`

The parameter set itself is correct; the **endpoint** changed. The code therefore maps to
`TradeErrorCodes.NotSupported` rather than to an argument error, because the latter sends the reader back to
inspect arguments that no edit will make this endpoint accept. The conditional mapping is written and tested,
but **end to end it is only verified as far as "this endpoint refuses it"**: really placing a conditional order
means wiring up the Algo Order endpoints, which is outside this stage.

### Quantities always align downwards

Every submission is normalised locally against the symbol's rules first: the quantity aligns down to the step
size and the price to the tick. Rounding up would make the real position larger than the size risk management
calculated, which is a hole in risk control rather than a rounding preference.

A result below the minimum quantity or notional fails here rather than travelling to the exchange to be
rejected, which saves a round trip and a unit of rate-limit quota.

## Testing

Contract tests replay recorded responses and never touch the network. **Every fixture is a genuine recording**:
`exchangeInfo` and `time` come from the public endpoints, and the account, position, and order responses were
recorded on 2026-09-11 from real signed requests against Testnet. `tests/.../Fixtures/PROVENANCE.md` gives each
file's source endpoint, capture date, and what was trimmed, and explains why two `-open` files carry the
recorded structure with substituted values rather than being verbatim.

Integration tests that need real Testnet credentials are marked `[TestCategory("Testnet")]` and read
`BINANCE_TESTNET_API_KEY` and `BINANCE_TESTNET_API_SECRET` from the environment. Without them they report
Inconclusive rather than passing: a green test that never ran is worse than no test.

Those tests **really place orders**, so they run against Testnet only and never touch the production trading
endpoints. The discipline is: every test order rests about 4% below the mark price with GTC so that it cannot
fill; every one is cancelled in a `finally` block even when an assertion fails; every one carries a
`pulsetrade-test-` prefix so a stray order is traceable; and a closing test plus a `ClassCleanup` query **every
symbol** for open orders to confirm that nothing was left behind.

```
dotnet test                                    # offline contract tests
dotnet test --filter "TestCategory=Testnet"    # requires credentials
```

## Security

- The package ships no default credentials and writes none to files, logs, or `ToString`.
- `signature` and `X-MBX-APIKEY` are added to the log-masking list automatically.
- Credentials are injected by the host and carried by `Ozakboy.Http`'s `SigningOptions`; this package reads
  them only at the moment of signing.

## Licence

MIT. See [LICENSE](LICENSE).
