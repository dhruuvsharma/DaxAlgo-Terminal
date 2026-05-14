---
name: broker-gotchas
description: Per-broker quirks for DaxAlgo Terminal's three IBrokerClient backends — Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), and cTrader (Spotware Open API 2.0 protobuf). Use BEFORE editing anything under src/TradingTerminal.Infrastructure/Ib/, /Ninja/, or /CTrader/, or when diagnosing connection/threading/error-code issues against a specific broker. Skip for broker-neutral code (Core, UI, repository layer).
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
- Real client is preferred over fake by default (`InteractiveBrokers:UseRealClient: true` in appsettings.json) since `C:\TWS API\` is the standard install path.

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
- **Depth (L2)** wired via `ProtoOASubscribeDepthQuotesReq` + `ProtoOADepthEvent`. Events carry `NewQuotes` (add/replace by `Id`) and `DeletedQuotes` (remove by `Id`). Each `ProtoOADepthQuote` has either `Bid` or `Ask` set (not both) — route to the right side's `Dictionary<ulong, DepthLevel>`. After each event, emit a consistent top-N `DepthSnapshot`. Compile-tested in this build; **the live runtime is unverified** — has not been run against a live cTrader account yet.

## Cross-broker rules

- `IBrokerClient.ConnectAsync` takes **no params**. Each impl reads its own `IOptions<XxxOptions>`. The login form pushes user-supplied values into options before flipping the selector.
- Reconnect uses exponential backoff (1s → 30s cap). New broker calls must assume the connection can drop mid-call; surface failures as `ConnectionState` transitions, don't crash.
- View-models never see `EClientSocket` / `NTDirect` / `OpenClient`. If you're tempted to import a broker SDK type into UI or Core, stop and re-read the layer graph.
- `IBrokerClient.PlaceOrderAsync` / `CancelOrderAsync` / `OrderEvents` exist on the interface but all three real clients throw `NotSupportedException`. That's the seam OMS will fill — don't pretend it works.
