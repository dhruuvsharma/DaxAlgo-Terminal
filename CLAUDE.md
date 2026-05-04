# DaxAlgo Terminal — Claude Working Guide

A modular WPF trading terminal. WPF + .NET 9 + IB TWS API. This file is the playbook for Claude in this repo. Read it once, work from it — don't re-derive conventions every session.

## Stack snapshot

- **Target framework**: `net9.0-windows` (no .NET 8 SDK on the box).
- **MVVM**: `CommunityToolkit.Mvvm` — `ObservableProperty`, `RelayCommand`, source generators.
- **Shell**: MahApps Metro chrome + AvalonDock VS2013 Dark theme.
- **Charts**: ScottPlot 5.
- **DI**: `Microsoft.Extensions.DependencyInjection`.
- **Logging**: Serilog with custom in-memory sink for the Logs pane.
- **Tests**: xUnit + FluentAssertions + NSubstitute. WPF-touching tests use `[WpfFact]` (`Xunit.StaFact`).
- **IB**: Real client compiled when CSharpAPI.dll resolves (lib/, MSBuild prop, or `C:\TWS API\…`). Otherwise `FakeIbClient` synthesizes bars.

## Solution graph (do not break this)

```
App        → Infrastructure, UI, Strategies.Example, Core
Strategies → UI, Core
Infra      → Core
UI         → Core
Core       → (nothing)
```

`Core` has zero deps on UI/IB. New domain types go there. New IB code goes in Infrastructure behind `IIbClient` / `IMarketDataRepository` — view-models never see `EClientSocket`/`EWrapper`.

## Architectural rules

1. **Strict MVVM.** No business logic in `.xaml.cs`. View-models inherit `ViewModelBase`. Use `[ObservableProperty]` and `[RelayCommand]`.
2. **Strategies are plug-ins.** Discovered via DI via `StrategyFactoryRegistration`. Adding a strategy = new project + one `services.AddMyStrategy()` line in `App.xaml.cs`. The shell never references strategy concretes.
3. **Threading is a one-layer concern.** IB callbacks marshal to the UI dispatcher inside the repository. View-models stay single-threaded from their POV. Don't sprinkle `Dispatcher.Invoke` in view-models.
4. **Async streaming via `IAsyncEnumerable<Bar>`.** Use `[EnumeratorCancellation]` on the `CancellationToken` parameter. Cancellation is the natural unsubscribe path.
5. **Connection state is observable.** `IObservable<ConnectionState>` flows from `ConnectionManager` → repository → view-models. Don't poll.
6. **Reconnect with exponential backoff.** Already wired (1s → 30s cap). When adding new IB calls, assume the socket can drop mid-call; surface failures as state transitions, don't crash.

## IB API gotchas (the stuff that bites)

- **TWS handles its own 2FA.** The API socket has no separate 2FA step. Don't add 2FA prompts to the login screen.
- **ClientId must be unique** across all simultaneously connected clients (Excel, Bookmap, another instance of this app). Error 326 = collision.
- **Default ports**: 7497 TWS Paper, 7496 TWS Live, 4002 Gateway Paper, 4001 Gateway Live.
- **Real client compiles only when `HAS_IBAPI` is defined** (set by `Infrastructure.csproj` when CSharpAPI.dll resolves). Code that touches `IBApi.*` types must be inside `#if HAS_IBAPI`.
- **The TWS API is not on NuGet.** Don't suggest `dotnet add package IBApi`.
- **EWrapper callbacks are on the IB reader thread.** Always marshal before touching observable collections.

## Build & run

```powershell
dotnet build
dotnet test
dotnet run --project src/TradingTerminal.App
```

Build prints `IB CSharpAPI resolved from: <path>` if the real client is compiled in. Absent line = synthetic data. The repo defaults to `UseRealClient: true` since `C:\TWS API\` is the user's standard install.

## Reasoning patterns (how to approach work in this repo)

| Task shape | Pattern |
|---|---|
| **Bug fix** | Read the failing code first. No plan needed for one- or two-file fixes. Reproduce mentally, then patch. |
| **New strategy** | Follow the 5-step recipe in README §"Adding a new strategy". Don't edit shell files beyond the one DI line. |
| **New IB call** | Add to `IIbClient` first, then `RealIbClient` (under `#if HAS_IBAPI`) and `FakeIbClient`, then expose via `IMarketDataRepository`. View-models last. |
| **XAML/binding fix** | Check `DataContext` flow. Confirm `[ObservableProperty]` generates the expected `PropertyName`. AvalonDock layouts can swallow binding errors silently — check Output window. |
| **Refactor across layers** | Plan first. Respect the project graph; never make `Core` depend on anything. |
| **Anything async + IB** | Cancellation tokens flow end-to-end. Test with `FakeIbClient` first. |

## Delegation (when to spawn a subagent)

Subagents have model overrides — the right one cuts cost without losing quality.

| Subagent | Model | Use for |
|---|---|---|
| `wpf-explorer` | Haiku | "Where is X defined?", "which files reference Y?", finding XAML resources, view-model lookups. Cheap reads — don't burn Sonnet on these. |
| `xaml-fixer` | Sonnet | Targeted XAML/binding/style fixes. Theming work. AvalonDock layout tweaks. |
| `ib-api-expert` | Opus | TWS API wiring, EWrapper callbacks, threading bugs in IB code, contract/historical-data subtleties. Hard problems where being wrong is expensive. |
| `dotnet-reviewer` | Sonnet | Pre-commit review of staged changes. Catches MVVM violations, layer-graph breaks, missing `ConfigureAwait(false)` in library code (none in WPF UI code), nullable issues. |

**Default**: do simple work in the main thread. Spawn an Explore subagent when a search will need 3+ rounds. Spawn ib-api-expert when touching `Infrastructure/Ib/` non-trivially.

## Code style preferences

- File-scoped namespaces.
- `internal` by default; `public` only at module boundaries.
- No comments unless the *why* is non-obvious. Identifiers should explain *what*.
- No defensive null-checks on internal calls — trust the type system. Validate at boundaries (config load, IB callbacks, user input).
- No `ConfigureAwait(false)` in WPF view-model code (we *want* to resume on the UI thread). Use it in pure-library code if any is added.
- Records for value types (`Bar`, `Contract`). Sealed by default.
- Prefer `IReadOnlyList<T>` over `List<T>` in public signatures.

## What NOT to do

- Don't add 2FA logic to the login window.
- Don't rename `net9.0-windows` to `net8.0-windows` (no .NET 8 SDK).
- Don't put `IBApi` types in `Core` or `UI`.
- Don't `new` strategies from the shell — go through `IStrategyFactory`.
- Don't add NuGet sources for IB — the DLL resolution is already wired.
- Don't add `--no-verify` to git commits to bypass hooks.

## When unsure

The architecture document (`docs/architecture.md`) has the full design rationale and key interface signatures. Check it before adding new abstractions — chances are there's already a slot to plug into.
