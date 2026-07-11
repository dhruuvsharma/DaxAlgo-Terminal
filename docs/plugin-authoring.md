# Authoring a strategy plugin

> Last updated: 2026-07-12

This is the full walkthrough for building a DaxAlgo Terminal strategy plugin — from `dotnet new` to a
signed, submittable `.daxplugin`. For *installing* a plugin, see [plugins.md](plugins.md); for the trust
model, [plugin-security.md](plugin-security.md).

A plugin is a normal .NET assembly that references the **DaxAlgo SDK** (a NuGet package) and exposes one
`IStrategyPlugin`. The terminal loads it at runtime from its `plugins/` folder — no host recompile. Your
strategy owns its whole vertical (signal math, optional window) and depends only on the SDK.

> **Windows only (today).** The SDK and plugin system live in the Windows/WPF build.

---

## 1. Scaffold

```powershell
dotnet new install DaxAlgo.Templates                 # once
dotnet new daxalgo-strategy -n MyStrategy            # headless / backtest-only
dotnet new daxalgo-strategy -n MyStrategy --ui       # + a live strategy window
```

You get a complete, compiling, green-out-of-the-box project: a worked **EMA-cross** demo kernel, the
plugin entry point, a `plugin.json` manifest, an **offline test harness**, a `pack-plugin.ps1` packaging
script, and a **`CLAUDE.md`/`AGENTS.md` context pack** so an AI coding agent (Claude Code, Codex, Cursor)
opened in the folder already knows the contracts and rules.

The loop:

```powershell
dotnet build MyStrategy.slnx     # plugin + tests
dotnet test  MyStrategy.slnx     # offline kernel harness
./pack-plugin.ps1                # -> MyStrategy.daxplugin
```

> Prefer the template over copying `samples/DaxAlgo.SamplePlugin`. The sample is a deliberately minimal
> headless skeleton kept as an in-tree reference; the template is the canonical, maintained starting
> point (headless **and** `--ui`, harness, packaging, context pack).

---

## 2. The kernel — where your strategy lives

`Engine/MyStrategyKernel.cs` is an `IBacktestStrategy`: it receives market events and places orders
through an `IOrderRouter`. This is the *only* place with strategy math, and it is what both the backtest
and the live window run — so live and backtest can never diverge.

```csharp
public interface IBacktestStrategy
{
    Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct);
    Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct);
    Task OnDepthAsync(DepthSnapshot d, IClock c, IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
    Task OnTradeAsync(TradePrint t, IClock c, IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
    Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct);
    Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct);
}
```

The rules that keep a kernel honest (the demo follows all of them):

1. **All state in fields.** One kernel instance per run — no statics, no shared mutable state.
2. **Time only via `IClock`**, never `DateTime.UtcNow`: a backtest replays the past, and `DateTime.UtcNow`
   would silently read the wall clock.
3. **Orders only via `IOrderRouter`**, each with a **unique `ClientOrderId`** (the idempotency key — the
   demo suffixes a sequence number so identical timestamps don't collide).
4. **Flatten in `OnEndAsync`** so a run realizes its P&L rather than ending with an open position.
5. **Warm up** before trading (`_ticksSeen < SlowPeriod` in the demo); guard against zero/negative prices;
   keep per-tick work allocation-free where you can — `OnTickAsync` runs per tick.
6. **No file / network / registry / process access.** The host's policy scan blocks it and a curated
   review rejects it. A strategy consumes market data and emits orders — nothing else. (If you have a
   genuine need for file or network I/O, declare it in `plugin.json` `permissions` — see §7 — and expect
   it to be reviewed.)

Opt into depth or the trade tape by overriding `OnDepthAsync` / `OnTradeAsync`; declare the matching
`DataRequirement` (§5) so the host starts those pumps and gates brokers that can't supply them.

---

## 3. The plugin entry point

`MyStrategyPlugin.cs` is the single `IStrategyPlugin` the loader discovers. Its `Register` body is
line-for-line what a first-party `AddXxxStrategy()` does:

```csharp
public void Register(IPluginRegistrar registrar)
{
    // Catalog descriptor (the Strategies pane).
    registrar.Services.AddSingleton<ITradingStrategy, MyStrategyDescriptor>();

    // Backtestable engine entry — aggregated into the same registry the host uses, so it appears
    // in Backtest Studio and the daxalgo-backtest CLI with no host change.
    registrar.Services.AddSingleton(new BacktestStrategyOption(
        Id: "my.strategy",
        DisplayName: "My Strategy",
        Build: contract => new Engine.MyStrategyKernel(contract)));
}
```

`Register` receives a **guarded** service collection: you may add your own types and additional
`ITradingStrategy` / `BacktestStrategyOption` / `StrategyFactoryRegistration` entries, but you may not
replace a host service (the credential store, broker selector, …) — that quarantines the plugin. See
[plugin-security.md](plugin-security.md).

---

## 4. Parameters (optional, but they light up the UI for free)

Expose tunables with a declarative schema and a `Create(Contract, StrategyParameters)` factory. The
Backtest Studio param sweep and the live window's parameter editor render automatically from the schema —
no per-strategy UI:

```csharp
public static StrategyParameterSchema Schema { get; } = new(
    StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
    StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1),
    StrategyParameter.Bool("useTrail", "Trailing stop", false));

public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
    new MyStrategyKernel(contract, p.GetInt("lookback"), p.GetDouble("threshold"), p.GetBool("useTrail"));
```

Register it on the option with `Schema = MyStrategyKernel.Schema` and a `Create`-based `Build`. A
`BacktestBuild` override lets a backtest run different (e.g. lower-latency) settings than the live path.

---

## 5. Data-requirement & classification pills

These drive the coloured pills on the catalog card and gate which brokers the strategy offers. Set them on
the `ITradingStrategy` descriptor (and mirror `DataRequirement` on the `BacktestStrategyOption` so the
engine knows what to feed):

```csharp
public StrategyDataRequirement DataRequirement =>
    StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;

public IReadOnlyList<AssetClass> AssetClasses => [AssetClass.Equities, AssetClass.Crypto];
public StrategyAssetScope AssetScope => StrategyAssetScope.SingleAsset;   // or MultiAsset for a monitor
public string? ResearchPaperUrl => "https://arxiv.org/abs/…";            // adds a clickable paper pill
```

`SupportedBrokers` defaults to whatever can satisfy `DataRequirement` (e.g. a `TradeTape` strategy only
offers brokers with a tape) — override only to narrow it further.

---

## 6. The test harness

`MyStrategy.Tests/` drives the kernel with synthetic ticks through a recording router — no broker, no host,
no data files. It ships green with three checks (a trend opens-and-flattens, a flat series does nothing,
order ids are unique). **Grow it with your strategy's invariants**: they're the guard rail for AI-assisted
edits, and they're exactly what the marketplace's build-from-source review runs.

---

## 7. Package, version, sign, submit

### Package

`./pack-plugin.ps1` builds Release and produces `MyStrategy.daxplugin` — a zip of your plugin's files plus
a per-file sha256 integrity index the host verifies before any trust gate sees the content. It copies only
your `.dll` + `plugin.json` (+ pdb/deps.json); it never bundles `TradingTerminal.*` / `DaxAlgo.Sdk*` host
assemblies — `ExcludeAssets="runtime"` on the SDK reference keeps them out of your output, and a shipped
copy would break type identity with the host. If your plugin has a **private** dependency of its own, add
its dll to the pack list.

### Version policy — read this

The SDK is **pre-1.0**. The loader gate is **exact major.minor**: a plugin built against SDK `0.1.x` loads
only on a host with SDK `0.1.x`. **Every SDK minor bump orphans existing plugins until they're rebuilt.**
From 1.0, the gate relaxes to same-major compatibility. Practically:

- Pin `DaxAlgo.Sdk`(`.Wpf`) to the SDK version you build against, and set `plugin.json`'s
  `targetSdkVersion` to the same value (`SdkInfo.Version`).
- When the terminal ships a new SDK minor, bump both and rebuild.

### Sign & submit (curated distribution)

The shipped terminal defaults to **Curated** trust: a plugin loads if it's one the host pinned by hash, is
signed by a pinned publisher, or you explicitly consent to it (with a permanent **DEV (unsigned)** badge).
To distribute through a curated channel:

1. Authenticode-sign your `.dll`: `signtool sign /fd SHA256 /f cert.pfx MyStrategy.dll`.
2. Submit your signing-certificate thumbprint to be pinned, and ship the `.daxplugin`.

Unsigned is fine for **local** use and development — install it and consent when prompted. The full trust
& consent story, and what these layers do and don't protect, is in [plugin-security.md](plugin-security.md).

> **Licensing note.** The SDK is MIT, but it links the AGPL-3.0 `TradingTerminal.*` contract packages.
> Publishing your plugin's packages is fine; the linking exception needed for *proprietary closed-source*
> third-party plugins is tracked with the distribution channel and not yet in place — don't assume
> closed-source redistribution until it is.

---

## 8. `--ui` — a live strategy window

`--ui` adds a view-model and a `MetroWindow` and switches the SDK reference to `DaxAlgo.Sdk.Wpf` (which
bundles the WPF strategy-window base + MahApps). The VM derives from `LiveSignalStrategyViewModelBase`, so
the host supplies the instrument picker, warm-up bar loading, start/stop, the live signal feed, presets,
and the Activity Log — your VM supplies only two things:

```csharp
public sealed class MyStrategyViewModel : LiveSignalStrategyViewModelBase
{
    public MyStrategyViewModel(
        LiveStrategyHostServices services, INotificationPublisher notifications,
        IClock clock, ISignalGeneratorRouterFactory routerFactory, ILogger<MyStrategyViewModel> logger)
        : base("my.strategy", "My Strategy", services, notifications, clock, routerFactory, logger) { }

    protected override StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

    // The SAME kernel the backtest runs — live and backtest can't diverge.
    protected override IBacktestStrategy BuildStrategy(Contract contract) => new Engine.MyStrategyKernel(contract);
}
```

The plugin then registers the VM + window + a `StrategyFactoryRegistration` tying them to the strategy id
(the scaffold does this for you). The window binds to base-VM members (`Instruments`, `SelectedInstrument`,
`IsConfigured`, `Status`, `Signals`, `ContinueCommand`, `ToggleAlgoCommand`). Grow the XAML with your own
readouts; keep logic in the VM (strict MVVM — the code-behind is just `InitializeComponent`).

---

## 9. Memory-safety checklist (for live/UI plugins)

A live strategy consumes a hot stream, so the same leaks that bit the built-in windows can bite a plugin.
`LiveSignalStrategyViewModelBase` already handles subscription lifetime and coalesced redraw for you, but
if you add your own stream handling or a chart:

- **Bounded, batch-drained channels** — never an unbounded `Channel`/queue fed per tick.
- **Coalesced redraw** — a `DispatcherTimer` at a fixed cadence, not a redraw per trade (a hot tape floods
  the renderer otherwise).
- **No per-item UI marshal** — batch, then marshal once.
- **Dispose everything** — timers, subscriptions, event handlers — at window close; unhook what you hooked.
- **Bound every history buffer** — trim to a max; an ever-growing `List` is a slow leak.

This mirrors the host's `memory-safety` guidance; the built-in Volume Footprint window once reached 20 GB
by breaking these, so they matter.

---

See also: [plugins.md](plugins.md) · [plugin-security.md](plugin-security.md) ·
[backtesting.md](backtesting.md) · [architecture.md](architecture.md).
