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
| Conditional orders (stop, take-profit, trailing) over the Algo Order endpoints | Done — a separate path from ordinary orders, see below |
| WebSocket market streams: klines and mark prices | Done |
| WebSocket user data stream: orders, fills, account deltas, margin calls, resync signals | Done |

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

## Market data

`BinanceMarketDataFeed` implements `IMarketDataFeed`: historical klines over REST, live klines and mark prices
over WebSocket. Register it after `AddBinanceFutures` — it is a separate call because market streams open
long-lived connections, and a host that only wants trading rules should not have to carry a WebSocket client.

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
        logger.LogWarning("Market stream: {Error}", item.Error);

        // A gap being repaired, or the end of the stream. IsTransient is what tells them apart.
        if (!item.Error!.IsTransient)
        {
            break;
        }

        continue;
    }

    // Only a closed candle is a candle. See below.
    if (!candle.IsClosed)
    {
        continue;
    }

    strategy.OnCandle(candle);
}
```

Market streams are public and need no credentials. Connection management — reconnection with backoff and
jitter, subscription replay after a reconnect, idle-timeout liveness detection, and bounded-queue backpressure
— comes from [`Ozakboy.WebSockets`](https://github.com/ozakboy/Ozakboy.WebSockets) and is not reimplemented
here. What this package owns is the Binance protocol: stream names, the path to dial, the envelope, and the
abbreviated field names.

Binance splits its futures WebSockets into three routes by kind of data: market data (klines, mark prices) on
`/market`, user data on `/private`, and order-book data (bookTicker, depth) on `/public`. The package appends the
route itself, so an endpoint override (`BinanceEndpoints.CreateOverride`) pointing at a proxy or a replay server
should supply the **host root**, or a proxy prefix, and never a route such as `/market`.

Tuning lives on `BinanceMarketStreamOptions`: the heartbeat interval, the idle timeout, the reconnect ceiling,
the queue capacity and backpressure strategy, and whether mark prices use the one-second stream.

## User data stream

`BinanceUserDataFeed` implements `IUserDataFeed`: order updates, conditional order updates, fills, account
deltas, margin calls, and reconciliation signals from the account's private stream. It needs API credentials and
is registered with its own call, after `AddBinanceFutures`:

```csharp
services.AddBinanceFutures(options => { /* environment and credentials, as above */ });
services.AddBinanceUserData();
```

**The order of operations matters: subscribe first, then `await feed.StartAsync()`, and only then place
orders.** Binance does not replay anything that happened before a subscription existed or before the connection
was live, so an order placed in that window can fill without the stream ever reporting it.

```csharp
var feed = provider.GetRequiredService<BinanceUserDataFeed>();

// 1. Subscribe. The subscriber is registered the moment enumeration begins, synchronously.
var orders = feed.SubscribeOrderUpdatesAsync(ct).GetAsyncEnumerator(ct);
var firstOrder = orders.MoveNextAsync();

// 2. Wait until the connection is actually live.
var started = await feed.StartAsync(ct);

if (started.IsFailure)
{
    logger.LogError("User data stream did not start: {Error}", started.Error);
    return;
}

// 3. Only now act. Every event from here on has somewhere to land.
await client.PlaceOrderAsync(request, ct);
```

Subscribing alone also starts the stream, which is enough when nothing you do next produces events of its own.
`StartAsync` exists for the moment something does.

The five `Subscribe` methods share **one** WebSocket and **one** listenKey, and each subscriber gets its own
bounded queue, so subscribing to the same event twice gives both subscriptions the complete set. What the feed
does for you:

- **The listenKey is managed end to end.** Created on the first subscription, renewed every 30 minutes, rebuilt
  automatically when it expires, and deleted on disposal. A failed renewal does not wait out the next thirty
  minutes: it is retried after a short backoff — one, two, then four minutes by default, see
  `BinanceUserDataStreamOptions.ListenKeyRenewalRetryBackoffs` — and a retry never pushes the scheduled renewal
  back.
- **The credential's lifecycle is visible.** A successful renewal, a failed one, and a `listenKeyExpired` each
  write a log line — the failure carrying the neutral error code alone, the expiry carrying when the credential
  was created, how long it lived, and when it was last renewed successfully — and the counts and instants are
  also exposed as a read-only `feed.ListenKeyStatus` snapshot you can render on a health screen. **Neither the
  log lines nor the snapshot carry the listenKey**, and structurally neither can.
- **Every gap is announced.** A reconnect or an expired credential raises a `ResyncRequired` on
  `SubscribeResyncSignalsAsync`. The exchange replays nothing from the gap, so both call for a full
  reconciliation.
- **A subscriber that falls behind ends with a failure rather than losing events in silence.** The failure's
  `IsTransient` is `false`, and its message says to resubscribe and reconcile in full.
- **Liveness uses the same heartbeat as the market streams.** A `LIST_SUBSCRIPTIONS` goes out every 30 seconds
  with a 90-second idle timeout, so a silent account is not mistaken for a dead socket, and a dead socket is
  reconnected like any other drop.
- **Account deltas are deltas.** `AccountUpdate.Positions` holds `PositionChange` and `Balances` holds
  `BalanceChange`. Both carry only what the event delivers, with no mark price, notional, or available balance.
  For exposure, query positions or multiply by the mark price stream.

`BinanceUserDataFeed` implements **only** `IAsyncDisposable`: disposal deletes the listenKey, which is a network
call. The generic host disposes asynchronously and needs nothing extra. A container you build yourself must be
disposed with `await using`, because a synchronous `Dispose()` on it throws for an async-only singleton:

```csharp
await using var provider = services.BuildServiceProvider();
```

Tuning lives on `BinanceUserDataStreamOptions`: the heartbeat interval and idle timeout (validated by the same
rules as the market streams), the listenKey renewal interval, the reconnect ceiling, and the connection and
per-subscriber queue capacities.

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

The same account call also fills the maintenance and initial margin: `AccountSnapshot.TotalMaintenanceMargin` and
`TotalInitialMargin` from `totalMaintMargin` and `totalInitialMargin`, and each `Balance`'s `MaintenanceMargin` and
`InitialMargin` from its `assets[]` entry. A margin ratio is `Balance.MarginBalance / MaintenanceMargin`. A field
Binance leaves out comes back as `null`, never as zero — zero is what Binance reports for a flat account, and a
guessed zero would read as no liquidation risk.

### Orders are never retried; everything else is

This is the one rule the package will not bend. **A timeout does not mean the exchange missed it**: the order
may already be resting, or filled, with only the reply lost in transit. Re-sending then opens twice the
intended position, in real money.

The request `PlaceOrderAsync` sends therefore does two things at once: it is marked with `AsNonIdempotent()`
and additionally pinned to `RetryPolicy.NoRetry`. The two guards are deliberate — the marker stops the
`Ozakboy.Http` retry handler, and the policy stops whoever swaps the default for a predicate that retries
everything.

Every retry is a new request: it waits for its own rate-limit permit, pays its own weight, and is signed afresh
with the current `timestamp`, so a retry after a long backoff is not rejected with `-1021`. A local rate-limit
timeout — the permit never came within `RateLimitAcquisitionTimeout` — is not retried.

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

### Conditional orders travel their own path, and `/fapi/v1/order` refuses them (`-4120`)

Binance moved `STOP_MARKET`, `TAKE_PROFIT_MARKET`, `STOP`, `TAKE_PROFIT`, and `TRAILING_STOP_MARKET` to the
Algo Service on 2025-12-09. Sending one to `/fapi/v1/order` answers `-4120 Order type not supported for this
endpoint. Please use the Algo Order API endpoints instead.` — measured against Testnet on 2026-09-11 and pinned
by a test, so the day it stops being true the test goes red.

They therefore go through `PlaceConditionalOrderAsync` and the four methods beside it, which call
`/fapi/v1/algoOrder` and its siblings. Three consequences are easy to miss, and every one of them is silent:

* **`GetOpenOrdersAsync` does not see them.** Reconciling with it alone concludes "no resting orders" while the
  stops sit safely on the other path — or are genuinely missing, which looks identical. Use
  `GetOpenConditionalOrdersAsync`, and pass the symbol: without one the weight is 40 rather than 1.
* **`SubscribeOrderUpdatesAsync` does not carry them.** A stop triggering appears only on
  `SubscribeConditionalOrderUpdatesAsync`, as an `ALGO_UPDATE`. What reaches the order stream is the fill of
  the order the trigger produced, linked back only by `ConditionalOrder.TriggeredOrderId`.
* **`CancelAllOrdersAsync` does not cancel them.** An emergency exit calls `CancelAllConditionalOrdersAsync`
  too, because a stop left behind after the position closes opens a new one in the opposite direction.

Two more things the documentation states and this client passes on. A conditional order is **not margin-checked
before it triggers**, so one that rested successfully can still end as `Rejected` at the trigger — and the
reason appears exactly once, in `ConditionalOrderUpdate.RejectReason`. And an **untriggered conditional order
cannot be modified**, so moving a stop means cancel and replace, with an unprotected gap in between.

### Quantities always align downwards

Every submission is normalised locally against the symbol's rules first: the quantity aligns down to the step
size and the price to the tick. Rounding up would make the real position larger than the size risk management
calculated, which is a hole in risk control rather than a rounding preference.

A result below the minimum quantity or notional fails here rather than travelling to the exchange to be
rejected, which saves a round trip and a unit of rate-limit quota.

### Only a closed candle is a candle

A live kline stream keeps pushing the same in-progress candle, each push carrying the latest price as its
close. `Kline.IsClosed` is `false` on every one of them and `true` only on the last. Feeding an unfinished
candle to an indicator produces a signal that flips back and forth inside one candle, and the strategy enters
and exits with it.

This is the easiest thing here to get wrong and the hardest to notice. Across the pushes of one candle
everything but the flag is identical, so hard-coding it leaves prices, volumes, and timestamps all correct and
no assertion failing — and a backtest, which runs on closed data, stays green through it. A missing `x` field
is therefore a parse failure rather than a default: guessing `false` makes a strategy skip every candle, which
is a silent shutdown, and guessing `true` makes it trade inside every one.

The same applies to `GetKlinesAsync`, whose REST response carries **no** closed flag at all and whose last
candle is usually still ticking. `IsClosed` is derived there by comparing the close time against the injected
time source. Treating the whole list as closed records "the latest price when the query ran" as a close, and
stored as history that makes later backtests run on a candle that never existed.

### Routes follow the official notice, and were verified on production

Binance splits its futures WebSockets into three routes, `/public`, `/market`, and `/private`
([official notice](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Important-WebSocket-Change-Notice)).
The old addresses without a route prefix were retired on 2026-04-23 and deliver public-class data only. They fail
in the worst possible way: measured on production on 2026-09-12, klines and mark prices on the unprefixed
`/stream` and `/ws/…` complete the handshake and then deliver not one frame. No error, no disconnect, just a price
that never moves. That is why 0.1.0 received nothing on production, which was misread at the time as a local
network problem. The testnet still honours the old addresses for market data, so testnet checks alone cannot
catch this, and a separate test connects to production public market data only. Routes and paths are
hard-coded rather than assembled from configuration.

Two more measured details. The envelope follows the **path**, not the number of streams: `…/stream` wraps every
frame in `{"stream":…,"data":…}` even for a single subscription, while `…/ws` wraps none even for ten. And the
separator inside `streams=` must be a slash; a comma fails the handshake outright.

### The user data stream always lists the events it wants

The user data stream dials `/private/ws?listenKey=…&events=…`. The `/ws/<listenKey>` that 0.1.0 dialled no longer
delivers any event on the testnet. `events` really filters, and event names are not validated: leave one out or
misspell it and that kind of event silently never arrives, while omitting `events` altogether gave inconsistent
results when measured. The package therefore always lists four — order and fill updates, account deltas, margin
calls, and credential expiry — taking the names from the same constants the reader dispatches on rather than
spelling them a second time.

### Liveness is a heartbeat, because a quiet market looks exactly like a dead socket

`ClientWebSocket` answers the peer's pings by itself, invisibly, so the only liveness signal available to the
application is how long it has been since any message arrived. But kline pushes only happen when trades
happen, and a quiet symbol can go minutes without one — at which point an idle timeout condemns a healthy
connection and the client settles into a disconnect-reconnect loop. A `LIST_SUBSCRIPTIONS` is therefore sent on
a timer: it always draws a reply, the reply refreshes the idle clock, and a genuinely dead connection is still
caught.

### A dropped message is republished as a gap

Under backpressure the connection layer drops messages and reports that on an event and in its statistics —
neither of which an `await foreach` can see. The message dropped may well be a closed candle, the only kind a
strategy trades on, and losing one raises no error at all; it just silently skips a trade that should have
happened. Drops are therefore re-published as a transient failure on the stream itself, saying how many were
lost.

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

The market data tests are the exception to the credential rule: market streams are public, so the
`[TestCategory("Testnet")]` tests that subscribe to klines and mark prices run with no key at all. One of them
waits for a one-minute candle to close and asserts on a live connection that `IsClosed` was `false` throughout
the candle and `true` on its closing push.

Production has exactly one test, marked `[TestCategory("MainnetPublic")]`: with no credential at all it connects
to production public market data only and must receive at least one BTCUSDT one-minute candle within 60 seconds.
The testnet still honours the old unprefixed address for market data, so this test is the only evidence that
the route fix works. It was run once with the address reverted to `/stream` and went red after 60 seconds; it
turned green only on `/market/stream`.

Unit tests never touch the network, streams included: `Ozakboy.WebSockets` factors connection creation behind
`IWebSocketConnectionFactory`, and a fake one drives the whole subscription path — register, connect, replay
the `SUBSCRIBE`, receive, parse, hand to the consumer — offline.

```
dotnet test --filter "TestCategory!=Testnet&TestCategory!=MainnetPublic"   # offline contract tests
dotnet test --filter "TestCategory=Testnet"                                # market data needs no key; trading needs credentials
dotnet test --filter "TestCategory=MainnetPublic"                          # production public market data, no credentials used
```

## Security

- The package ships no default credentials and writes none to files, logs, or `ToString`.
- `signature`, `X-MBX-APIKEY`, and `listenKey` are added to the log-masking list automatically.
- The user data stream's listenKey reaches the account's private data, so it never appears in an error
  message, `Error.Data`, an exception, or a stream identifier. Frames that carry it (`listenKeyExpired`, and
  every heartbeat reply) are never quoted, and heartbeat replies are recognised and dropped without being read.
- The listenKey is **registered as a known secret on the masker the moment it is obtained**, on both the create
  and the renewal, so wherever it turns up afterwards it comes out as the mask segment. This differs from the
  previous point in reach: a field-name rule sees only named fields of a structured payload, while literal
  replacement also catches positions that have no name — a path segment of an address, a message another package
  has already formatted, exception text. `AddBinanceUserData` wires it up automatically; when constructing by
  hand, use the overload taking a `SecretMasker`, obtained with
  `provider.GetOzakboyHttpMasker(BinanceConstants.HttpClientName)` — it has to be that same instance.
- The three credential-lifecycle log lines — renewal succeeded, renewal failed, `listenKeyExpired` — and the
  `ListenKeyStatus` snapshot **have nowhere to put a listenKey**: every value is an instant, a count, a
  duration, or a neutral error code. That is deliberate, because what this package logs itself does **not** pass
  through the `Ozakboy.Http` masker, which covers its own request logs and errors; never handing the credential
  over is the only protection at that layer. The failure line carries the neutral code alone and never quotes
  the exchange's response, whose body is the credential in the normal case.
- Credentials are injected by the host and carried by `Ozakboy.Http`'s `SigningOptions`; this package reads
  them only at the moment of signing.
- The API key and secret are registered with the client's masker, so an error returned by the REST client —
  its message, every `Error.Data` entry, and the exception text — never carries them, even when the exchange
  or a transport exception echoes them back.

## Licence

MIT. See [LICENSE](LICENSE).
