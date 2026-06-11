---
name: add-broker
description: Recipe for wiring a new broker backend into DaxAlgo Terminal behind the IBrokerClient seam, alongside the existing six (Interactive Brokers, NinjaTrader 8, cTrader, Alpaca, Ironbeam, Binance). Use when the user asks to add a broker (e.g. "add Tradovate", "wire up Rithmic", "implement IBrokerClient for X"). Covers project layout, DI registration, login tile, options binding, fake vs real split, and the layering rules that must not be broken.
---

# Add a Broker

A broker is a plug-in. The shell, repository, view-models, and strategies must stay completely untouched. If you find yourself editing `MarketDataRepository`, `ConnectionManager`, or any view-model — stop.

The app already has six backends: IB / NT / cTrader / Alpaca / Ironbeam (REST+WS, no SDK) / Binance (keyless public data), plus the always-on Simulated. Each opens a concurrent session — login no longer picks one broker. Mirror the existing patterns; for a no-SDK REST+WebSocket broker, `Infrastructure/IronBeam/` and `Infrastructure/Binance/` are the references.

## Recipe

1. **New folder** under `src/TradingTerminal.Infrastructure/<Broker>/` (e.g. `Tradovate/`).
2. **Implement `Real<Broker>Client : IBrokerClient`** — talks to the actual SDK. If the SDK is a private DLL (not on NuGet), gate the file with `#if HAS_<BROKER>API` and add the resolution logic to `Infrastructure.csproj` (mirror the `HAS_IBAPI` / `HAS_NTAPI` blocks); the client is then simply not registered when the DLL is absent. If the SDK is a NuGet package, always wire it (mirror cTrader). **There is no `Fake<Broker>Client`** — the project dropped per-broker synthetic fallbacks in favour of one always-registered `Simulated` broker (`SimulatedBrokerClient`) that covers all offline runs. Don't add a fake; if you need offline data, that's what `Simulated` is for.
3. **Options class** — `<Broker>Options` in `Core/Configuration/`. Read from `appsettings.json` via `IOptions<XxxOptions>`. `ConnectAsync` takes no parameters — it reads its own options.
4. **DI block** in `Infrastructure/DependencyInjection.cs` (not `App.xaml.cs` — that only `Configure`s the options section):
   ```csharp
   // gate on HAS_TRADOVATEAPI if it's a sideloaded DLL; omit the #if for a NuGet SDK
   services.AddSingleton<IBrokerClient>(sp =>
       new MeteredBrokerClient(
           ActivatorUtilities.CreateInstance<RealTradovateClient>(sp),
           sp.GetRequiredService<IBrokerApiMeter>()));

   services.AddSingleton<BrokerConnectionMode>(sp => new BrokerConnectionMode(
       BrokerKind.Tradovate, IsLive: true, DisplayName: "Tradovate", Description: "…"));
   ```
   Register alongside the existing IB/NT/cTrader/Alpaca/Simulated blocks. In `App.xaml.cs`, add the matching `services.Configure<TradovateOptions>(ctx.Configuration.GetSection(TradovateOptions.SectionName));`.
5. **Login form** — add a new `<Broker>LoginFormViewModel : IBrokerLoginForm` in `src/TradingTerminal.Login/Forms/`. Register it twice in `LoginServiceCollectionExtensions.AddLogin` (in `TradingTerminal.Login`) (as concrete + as `IBrokerLoginForm` factory delegate, mirroring the four existing forms). It pushes user-supplied creds into `<Broker>Options` before flipping `IBrokerSelector`.
6. **appsettings.json** — add a `"Tradovate": { ... }` section (no `UseRealClient` switch — availability is decided by SDK/DLL presence at build time).
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
