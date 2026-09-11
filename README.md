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
| Placing, cancelling, and querying orders; leverage and margin mode | Next stage |
| WebSocket market streams and user data streams | Next stage |

The client therefore implements `IExchangeInfoProvider` today and will widen to the full `IExchangeClient` when
the trading methods land. None of the existing signatures will change.

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

## Testing

Contract tests replay recorded responses and never touch the network. The `exchangeInfo` and `time` fixtures
are genuine recordings of the public endpoints; the account and position fixtures are hand-written from the
official documentation, because those endpoints need real credentials. See `tests/.../Fixtures/PROVENANCE.md`,
which is explicit about which is which and why it matters.

Integration tests that need real Testnet credentials are marked `[TestCategory("Testnet")]` and read
`BINANCE_TESTNET_API_KEY` and `BINANCE_TESTNET_API_SECRET` from the environment. Without them they report
Inconclusive rather than passing: a green test that never ran is worse than no test.

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
