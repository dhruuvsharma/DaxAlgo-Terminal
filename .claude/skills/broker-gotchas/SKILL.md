---
name: broker-gotchas
description: Per-broker quirks for DaxAlgo Terminal's four IBrokerClient backends — Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), cTrader (Spotware Open API 2.0 protobuf), and Alpaca (REST + WebSocket via Alpaca.Markets NuGet). Use BEFORE editing anything under src/TradingTerminal.Infrastructure/Ib/, /Ninja/, /CTrader/, or /Alpaca/, or when diagnosing connection/threading/error-code issues against a specific broker. Skip for broker-neutral code (Core, UI, repository layer).
---

# Broker Gotchas

Each backend has its own footguns. The seam (`IBrokerClient`) hides them from the rest of the app — keep them hidden.

## Interactive Brokers

- **TWS handles its own 2FA.** API socket has no separate 2FA step. Don't add 2FA prompts to the login screen.
- **ClientId must be unique** across all simultaneously connected clients (Excel, Bookmap, another instance). Error 326 = collision.
- **Default ports**: 7497 TWS Paper, 7496 TWS Live, 4002 Gateway Paper, 4001 Gateway Live.
- **Real client compiles only when `HAS_IBAPI` is defined** (set by `Infrastructure.csproj` when `CSharpAPI.dll` resolves from `lib/`, MSBuild prop, or `C:\TWS API\`). Code touching `IBApi.*` must be inside `#if HAS_IBAPI`.
- **The TWS API is not on NuGet.** Don't suggest `dotnet add package IBApi`.
- **EWrapper callbacks are on the IB reader thread.** Always marshal to UI dispatcher inside `MarketDataRepository` before touching observable collections.
- **Error 502** = TWS not running / wrong port. **Error 200** = no security found for contract. **Error 326** = clientId collision.
- The IB client is wired purely by build-time DLL resolution (`HAS_IBAPI`) — there's no `UseRealClient` switch and no synthetic IB fallback. For offline runs use the always-registered `Simulated` broker instead. (The `UseRealClient` key still in `appsettings.json` is vestigial and ignored.)

When in doubt, escalate to the `ib-api-expert` subagent.

## NinjaTrader 8

- **NT must be running first.** `NTDirect.Connected(0)` returns 0 only when NT 8 is up with **Tools → Options → AT Interface → AT Interface enabled**.
- **Real client compiles only when `HAS_NTAPI` is defined** (set when `NTDirect.dll` resolves from `lib/`, MSBuild prop, or `%USERPROFILE%\Documents\NinjaTrader 8\bin64\`). Code touching `NTDirect` must be inside `#if HAS_NTAPI`.
- **NTDirect doesn't expose historical bars.** `RequestHistoricalBarsAsync` synthesizes them with a warning log. Don't promise real history without a NinjaScript bridge add-on.
- **NTDirect doesn't expose L1 sizes** via the simple `Bid`/`Ask` calls. `Tick.BidSize`/`AskSize` are 0 in NT mode.
- **Polling, not callbacks.** Tick stream polls `Bid`/`Ask` at 200 ms; bar stream aggregates polled `LastPrice`.
- NT defaults to `false` (must be explicitly enabled in appsettings.json).

## cTrader

- **Always wired** — the `cTrader.OpenAPI.Net` NuGet package restores unconditionally. There's no `HAS_CTRADERAPI` gate.
- **Real client requires OAuth credentials** (clientId + clientSecret + accessToken + ctidTraderAccountId). When missing, `ConnectAsync` reports `ConnectionState.Failed` with a clear log. Don't add a synthetic fallback in the DI graph; the `Failed` state IS the affordance.
- **Access tokens expire** (~30 days). Surface refresh failures as a clear error pointing at OAuth — don't suppress them.
- **Wire prices are `ulong`** scaled by `10^Digits` per symbol. We resolve `Digits` lazily via `ProtoOASymbolByIdReq` on first subscribe.
- **Trendbars use relative encoding** (Low + DeltaOpen/DeltaHigh/DeltaClose). Reconstruct OHLC then divide by `10^Digits`.
- **Per-call correlation via `clientMsgId`** + a `Dictionary<string, TaskCompletionSource<IMessage>>`. Spot events bypass the request/response router; subscribers filter the OpenClient's stream by `SymbolId` directly.
- **Depth events** (`ProtoOADepthEvent`) follow the same pattern — filter on SymbolId, but cast `(long)e.SymbolId` because depth events declare it as `uint64` (unlike spots' `int64`).
- **Depth (L2)** wired via `ProtoOASubscribeDepthQuotesReq` + `ProtoOADepthEvent`. Events carry `NewQuotes` (add/replace by `Id`) and `DeletedQuotes` (remove by `Id`). Each `ProtoOADepthQuote` has either `Bid` or `Ask` set (not both) — route to the right side's `Dictionary<ulong, DepthLevel>`. After each event, emit a consistent top-N `DepthSnapshot`.
- **Spot tick enrichment**: top-of-book size comes from the depth cache, not the spot event itself (commit `ad68d9e`). If a strategy needs L1 sizes from cTrader, subscribe to depth alongside spots.
- **No trade tape.** `SubscribeTradesAsync` throws `NotSupportedException` — the protocol has no per-print trade channel. Not fixable. Trade-tape strategies must check broker capability at Continue and route through IB instead.

## Alpaca

- **Always wired** — `Alpaca.Markets` NuGet restores unconditionally. No `HAS_ALPACAAPI` gate.
- **Auth = API key + secret.** No OAuth. `IsLive` toggle picks `Environments.Live` vs `Environments.Paper`.
- **Three asset classes** multiplexed by `Contract.SecType`:
  - `"STK"` → stock data client + stock streaming client.
  - `"CRYPTO"` → crypto data client + crypto streaming client.
  - `"OPT"` → not yet supported (Alpaca's options SDK is still stabilising).
- **Live stock stream pinned to IEX** — the SDK default targets SIP, which the free/paper plan can't subscribe to (HTTP 403 — entitlement wall). We rebase the data-stream endpoint to `/v2/iex` so the same code works on free accounts. Don't revert this.
- **Eager auth on both streams.** `ConnectAndAuthenticateAsync` is called for stock + crypto streams during `ConnectAsync` so first-subscribe doesn't pay the auth round-trip. If auth fails (`AuthStatus != Authorized`), throw — don't silently leave a half-connected client.
- **No L2 depth.** `SubscribeDepthAsync` throws `NotSupportedException`. Strategies that need depth must route through IB or cTrader.
- **No OMS yet.** `PlaceOrderAsync` / `CancelOrderAsync` throw `NotSupportedException` like the other brokers. The trading client is constructed but not wired for orders.
- **Trade tape is wireable.** Alpaca's WebSocket has a native trade channel with `taker_side`. It's the highest-leverage next broker to add trade-tape support to (cf. IB which is the only one wired today).
- **Instrument discovery** pins data feed to IEX in `Infrastructure/Alpaca/` (commit `805462b`).

## Cross-broker rules

- `IBrokerClient.ConnectAsync` takes **no params**. Each impl reads its own `IOptions<XxxOptions>`. The login form pushes user-supplied values into options before flipping the selector.
- Reconnect uses exponential backoff (1s → 30s cap). New broker calls must assume the connection can drop mid-call; surface failures as `ConnectionState` transitions, don't crash.
- View-models never see `EClientSocket` / `NTDirect` / `OpenClient`. If you're tempted to import a broker SDK type into UI or Core, stop and re-read the layer graph.
- `IBrokerClient.PlaceOrderAsync` / `CancelOrderAsync` / `OrderEvents` exist on the interface but all three real clients throw `NotSupportedException`. That's the seam OMS will fill — don't pretend it works.
