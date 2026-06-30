# Contributing

> Last updated: 2026-06-19

How to add new features without breaking the layering rules. The constraints come from [architecture.md](architecture.md) — read that first if you haven't.

> **Two trees.** The repo is two independent codebases — `src/windows` (WPF) and `src/linux`
> (Avalonia). Paths below are shown for the **Windows** tree; the Linux tree mirrors them under
> `src/linux/…`. **A change meant for both must be made in both** (see
> [architecture.md](architecture.md#two-independent-trees)). Build/test name a solution:
> `dotnet build TradingTerminal.Windows.slnx` or `…Linux.slnx`.

## The three plug-in seams

The codebase has three explicit extension points, each one a "plug-in" pattern:

| Seam | Interface | Project layout | DI extension |
|---|---|---|---|
| Strategies | `ITradingStrategy` + `IBacktestStrategy` | One `src/windows/Strategies/TradingTerminal.Strategies.<Name>/` project per strategy | `services.AddXxxStrategy()` |
| Brokers | `IBrokerClient` | Files under `src/windows/Pipeline/TradingTerminal.Infrastructure/<Broker>/` | Per-broker DI block in `DependencyInjection.cs` |
| Notifiers | `INotificationTransport` | Files under `src/windows/Pipeline/TradingTerminal.Infrastructure/Notifications/<Channel>/` | Single line in `NotificationsServiceCollectionExtensions` |

Stay inside these seams and you can't accidentally break MVVM, threading, or the layer graph.

## Adding a new strategy

See [strategies.md](strategies.md#adding-a-new-strategy) for the full recipe. Short version:

1. Copy an existing `src/windows/Strategies/TradingTerminal.Strategies.<Name>/` project, rename.
2. Implement `IBacktestStrategy` for the engine-side logic (in `Infrastructure/Backtest/Strategies/<Name>Strategy.cs`).
3. The new project's view-model extends `LiveSignalStrategyViewModelBase` and constructs the engine class in `BuildStrategy(contract)`.
4. Register in `App.xaml.cs` via `services.AddXxxStrategy()`.
5. Add to `BacktestStrategyCatalog` (UI dropdown) and the CLI's `ResolveStrategy`.

## Adding a new broker

1. Add a `BrokerKind` enum value in `Core/Brokers/BrokerKind.cs`.
2. Add an `XxxOptions` record in `Core/Configuration/`.
3. Implement `RealXxxClient : IBrokerClient` in `Infrastructure/Xxx/` — the actual integration. Gate behind a compile-time constant if it depends on a sideloaded DLL (mirror the `HAS_IBAPI` / `HAS_NTAPI` pattern in `Infrastructure.csproj`). There are **no per-broker synthetic fallbacks** — the always-registered `Simulated` broker covers offline runs, so a new broker just registers its real client (and isn't registered at all when its SDK is absent).
4. Register both `IBrokerClient` and `BrokerConnectionMode` for the new broker in `DependencyInjection.cs`. `BrokerSelector` auto-discovers them via `IEnumerable<IBrokerClient>`.
5. Add a tile + form panel to `LoginWindow.xaml` (alongside the existing IB / NT / cTrader / Alpaca tiles) and a corresponding `SelectXxx` command + form fields to `LoginViewModel`.

The `MarketDataRepository`, `ConnectionManager`, every view-model, and every strategy stay untouched — they talk to `IBrokerClient` exclusively.

### What to read first

- [brokers.md](brokers.md) for the capability matrix.
- The closest existing broker (`Infrastructure/CTrader/` is the cleanest cloud-broker reference; `Infrastructure/Alpaca/` is the easiest REST + WebSocket example).
- The `broker-gotchas` skill for the per-broker quirks index (callbacks, threading, scaling, depth events).

## Adding a new notification transport

1. Add a class implementing `INotificationTransport` in `src/windows/Pipeline/TradingTerminal.Infrastructure/Notifications/<Channel>/<Channel>Transport.cs`. Inject `IOptionsMonitor<NotificationsOptions>` and `IHttpClientFactory`.
2. Extend `NotificationsOptions` (Core) with a nested `<Channel>Options` record holding `Enabled`, channel-specific creds, and `IncludeIdleSignals`.
3. Register the named `HttpClient` and the transport in `NotificationsServiceCollectionExtensions.AddNotifications`. The dispatcher auto-discovers transports via `IEnumerable<INotificationTransport>`.
4. Add a Settings tab block in `NotificationsSettingsView.xaml` and `NotificationsSettingsViewModel`. The `notifications.json` writer auto-picks up the new section as long as it's a top-level key under `Notifications`.

See [notifications.md](notifications.md#adding-a-new-transport) and the Telegram / Discord transports as references.

## Layering rules (read before any structural change)

```
App        → MarketData, Infrastructure, UI, Login, Ai, Strategies.*, <tool projects>, Core
Strategies → Infrastructure, UI, Core
Infra      → MarketData, Core
MarketData → Core
UI         → Core
Core       → (nothing)
```

- `Core` has zero deps on UI, WPF, IB, NT, cTrader, Alpaca. New domain types go there.
- New broker code goes in `Infrastructure/<Broker>/` behind `IBrokerClient`. View-models never see `EClientSocket`, `NTDirect`, `OpenClient`, or `IAlpacaTradingClient`.
- New strategies are their own `TradingTerminal.Strategies.<Name>` project. The shell never references strategy concretes.

Adding a project reference that breaks this graph will fail review.

## MVVM rules (for any UI work)

- No business logic in `.xaml.cs`. View-models inherit `ViewModelBase`. Use `[ObservableProperty]` and `[RelayCommand]`.
- No `Dispatcher.Invoke` in view-models. Threading is owned by `MarketDataRepository`; everything above is single-threaded from its perspective.
- No `ConfigureAwait(false)` in WPF view-model code — we *want* to resume on the UI thread.
- File-scoped namespaces. `internal` by default; `public` only at module boundaries.

The `wpf-mvvm-rules` skill has the full list; read it before any non-trivial view-model edit.

## Threading rules (for any cross-thread work)

| Source | Marshalling |
|---|---|
| Broker callbacks (IB `EWrapper`, NT polling loop, cTrader `OpenClient`, Alpaca SDK events) | Per-request channels → `MarketDataRepository` → `Dispatcher.InvokeAsync` |
| Reconnect loop | State pushed via `BehaviorSubject<ConnectionState>` |
| `IMarketDataStore` writes | Background `Channel<>` consumer per backend; ingest hot path never blocks on disk |
| Regime refresh | `IHostedService` worker; snapshots pushed via `IObservable<MarketRegimeSnapshot>` |
| View-model updates | UI thread |

If you find yourself adding a `Dispatcher.Invoke` outside `MarketDataRepository`, stop — the marshal should happen one layer down.

## Tests

```powershell
dotnet test TradingTerminal.Windows.slnx   # or TradingTerminal.Linux.slnx
```

- `xUnit` + `FluentAssertions` + `NSubstitute`.
- WPF-touching tests use `[WpfFact]` (`Xunit.StaFact`).
- Postgres integration tests self-skip when Docker isn't running.
- Tests are broker-agnostic via the `IBrokerClient` seam — use synthetic substitutes (`SingleClientSelector`, `FlakyClient`, etc.) rather than spinning up real broker connections.

When adding a new abstraction, add a test that exercises the seam from the outside (consumer → seam → fake impl), not just the impl in isolation.

## Style preferences

- Records for value types (`Bar`, `Tick`, `Contract`). Sealed by default.
- Prefer `IReadOnlyList<T>` over `List<T>` in public signatures.
- No defensive null-checks on internal calls — trust the type system. Validate at boundaries (config load, broker callbacks, user input).
- No comments unless the *why* is non-obvious. Identifiers should explain the *what*.

## Commits and reviews

- Pre-commit hooks are wired. Don't pass `--no-verify`. If a hook fails, fix the underlying issue.
- Use the `dotnet-reviewer` subagent on staged changes before opening a PR — it catches MVVM violations, layer breaks, missing `ConfigureAwait(false)` in library code.
- For UI changes, run the app and verify the change visually. Type checks and unit tests don't catch feature-level regressions.

## Where the design docs live

- [architecture.md](architecture.md) — full design rationale, key interfaces, threading model.
- [polyglot.md](polyglot.md) — the subprocess + JSON seam for C++ and Python sidecars.
- This file — process / contribution rules.

For a topic-specific question, scan [docs/README.md](README.md) first — it's the index.
