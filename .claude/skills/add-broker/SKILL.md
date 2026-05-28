---
name: add-broker
description: Recipe for wiring a fifth broker backend into DaxAlgo Terminal behind the IBrokerClient seam, alongside the existing four (Interactive Brokers, NinjaTrader 8, cTrader, Alpaca). Use when the user asks to add a broker (e.g. "add Tradovate", "wire up Rithmic", "implement IBrokerClient for X"). Covers project layout, DI registration, login tile, options binding, fake vs real split, and the layering rules that must not be broken.
---

# Add a Broker

A broker is a plug-in. The shell, repository, view-models, and strategies must stay completely untouched. If you find yourself editing `MarketDataRepository`, `ConnectionManager`, or any view-model — stop.

The app already has four broker backends: IB / NT / cTrader / Alpaca. Each opens a concurrent session — login no longer picks one broker. Mirror the existing patterns.

## Recipe

1. **New folder** under `src/TradingTerminal.Infrastructure/<Broker>/` (e.g. `Tradovate/`).
2. **Implement `IBrokerClient`** twice:
   - `Real<Broker>Client` — talks to the actual SDK. If the SDK is a private DLL (not on NuGet), gate the file with `#if HAS_<BROKER>API` and add the resolution logic to `Infrastructure.csproj` (mirror the `HAS_IBAPI` / `HAS_NTAPI` blocks). If the SDK is a NuGet package, always wire it (mirror cTrader).
   - `Fake<Broker>Client` — synthetic data for tests/offline. Must work without the SDK present.
3. **Options class** — `<Broker>Options` in `Infrastructure/<Broker>/`. Read from `appsettings.json` via `IOptions<XxxOptions>`. `ConnectAsync` takes no parameters — it reads its own options.
4. **DI block** in `App.xaml.cs`:
   ```csharp
   services.Configure<TradovateOptions>(config.GetSection("Tradovate"));
   if (config.GetValue<bool>("Tradovate:UseRealClient")) {
       services.AddSingleton<IBrokerClient, RealTradovateClient>();
   } else {
       services.AddSingleton<IBrokerClient, FakeTradovateClient>();
   }
   ```
   Register alongside the existing IB/NT/cTrader blocks.
5. **Login form** — add a new `<Broker>LoginFormViewModel : IBrokerLoginForm` in `App/Login/Forms/`. Register it twice in `AppDependencyInjection.AddBrokerLoginForms` (as concrete + as `IBrokerLoginForm` factory delegate, mirroring the four existing forms). It pushes user-supplied creds into `<Broker>Options` before flipping `IBrokerSelector`.
6. **appsettings.json** — add a `"Tradovate": { "UseRealClient": false, ... }` section.
7. **Trade tape** — `SubscribeTradesAsync` returns `IAsyncEnumerable<TradeTick>`. If the SDK exposes per-print trade flow with an aggressor flag, wire it (mirror IB's `reqTickByTickData("AllLast")` pattern). Otherwise throw `NotSupportedException` and add the broker to the no-trade-tape capability matrix in [[project-strategy-ideas]].
8. **Instrument discovery** — if the broker has a symbol search / contract universe endpoint, register an `IInstrumentDiscoveryService` impl so the universe tab + dropdown can resolve canonical `InstrumentId`s.

## Hard rules

- **No broker SDK types in `Core` or `UI`.** Period. If `EClientSocket` / `NTDirect` / `OpenClient` / your new SDK leaks past `Infrastructure/`, the layering is broken.
- **Marshal to UI dispatcher inside `MarketDataRepository`** before touching observable collections — never inside the broker client itself, never inside a view-model.
- **Surface connection drops as `ConnectionState` transitions** via the `IObservable<ConnectionState>` stream. Don't crash, don't poll.
- **Cancellation flows end-to-end.** `IAsyncEnumerable<Bar>` / `IAsyncEnumerable<Tick>` use `[EnumeratorCancellation]`.
- **OMS stubs**: implement `PlaceOrderAsync` / `CancelOrderAsync` / `OrderEvents` as `throw new NotSupportedException()` until OMS lands. Don't half-implement them.

## What you DON'T touch

- `MarketDataRepository` — already broker-neutral.
- `ConnectionManager` — already routes by `IBrokerSelector`.
- Any view-model or `.xaml` outside the new login tile.
- Strategy projects.
- `Core/` — never depends on anything.

## Reference impls (read these first)

- `src/TradingTerminal.Infrastructure/Ib/` — `RealIbClient.cs` (socket, EWrapper threading, `HAS_IBAPI` gating).
- `src/TradingTerminal.Infrastructure/NinjaTrader/` — P/Invoke pattern, polling loops, `HAS_NTAPI` gating, history synth.
- `src/TradingTerminal.Infrastructure/CTrader/` — async TLS+protobuf, OAuth, `clientMsgId` correlation, depth events.
- `src/TradingTerminal.Infrastructure/Alpaca/` — REST + WebSocket via NuGet, eager stream auth, IEX feed pinning, multi-asset routing by `Contract.SecType`.

See also: [broker-gotchas](../broker-gotchas/SKILL.md) for the quirks of each existing backend.
