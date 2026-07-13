# DaxNewStrategy - DaxAlgo Terminal strategy plugin

This repo is a **strategy plugin for DaxAlgo Terminal** (a WPF multi-broker trading terminal),
scaffolded by `dotnet new daxalgo-strategy`. It compiles against the **DaxAlgo.Sdk** NuGet package
(SDK `0.1.1-alpha`) and is loaded by the terminal at runtime from its `plugins/` folder - the host
never recompiles. This file is the working context for an AI coding agent; everything below is
load-bearing. (It is generated from the same source as the in-app builder's context, so it can't drift -
see `build/gen-ai-context.ps1`.)

## What a strategy is here

- **`DaxNewStrategy/Engine/DaxNewStrategyKernel.cs`** - the strategy itself: an `IBacktestStrategy`
  receiving ticks and placing orders through `IOrderRouter`. ALL strategy math lives here.
- **`DaxNewStrategy/DaxNewStrategyPlugin.cs`** - the `IStrategyPlugin` entry point (the host finds
  exactly one public implementation per assembly) plus the catalog descriptor. Registration is plain
  `IServiceCollection` calls.
- **`DaxNewStrategy/plugin.json`** - read by the host BEFORE any code loads: id, version, and
  `targetSdkVersion` (must stay compatible with the host SDK; pre-1.0 = exact major.minor).
- **`DaxNewStrategy.Tests/`** - offline harness driving the kernel with synthetic ticks through a
  recording router. Keep it green; grow it with the strategy's invariants.
- **(`--ui` scaffolds only)** `DaxNewStrategyViewModel.cs` + `DaxNewStrategyWindow.xaml(.cs)` - a
  live strategy window. The VM derives from `LiveSignalStrategyViewModelBase` (host-provided: instrument
  picker, warm-up, start/stop, the signal feed, presets, Activity Log) and supplies just
  `DataRequirement` + `BuildStrategy(contract)` - which returns the SAME kernel the backtest runs, so
  live and backtest can't diverge. The plugin then references `DaxAlgo.Sdk.Wpf` (not `DaxAlgo.Sdk`)
  and registers the VM + window + a `StrategyFactoryRegistration`. A headless scaffold has none of this.

## The engine contract (from DaxAlgo.Sdk - do not redeclare)

A strategy is an `IBacktestStrategy` - the host calls these as market events arrive:

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

public sealed record Tick(DateTime TimestampUtc, double Bid, double Ask, long BidSize, long AskSize);

public interface IClock { DateTime UtcNow { get; } }   // NEVER DateTime.UtcNow - backtests replay the past

public interface IOrderRouter
{
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
    IObservable<OrderEvent> OrderEvents { get; }
}

public sealed record OrderRequest(
    string ClientOrderId,        // caller-generated IDEMPOTENCY key - unique per order
    Contract Contract,
    OrderSide Side,              // Buy | Sell
    OrderType Type,              // Market | Limit | Stop | StopLimit
    long Quantity,
    double? LimitPrice = null,   // required for Limit/StopLimit
    double? StopPrice = null,    // required for Stop/StopLimit
    TimeInForce TimeInForce = TimeInForce.Day);
```

Depth (`OnDepthAsync`) and the trade tape (`OnTradeAsync`) are opt-in - override them only if your
strategy needs L2 or prints, and declare the matching `DataRequirement`.

## Hard rules

1. **All state in fields.** One instance per run - no `static` mutable state, no shared state.
2. **Time only via `IClock`** (the `clock` parameter). Never `DateTime.UtcNow` / `DateTime.Now` -
   a backtest replays historical time and the wall clock would be meaningless.
3. **Orders only via `IOrderRouter`**, each with a **unique `ClientOrderId`** (suffix a sequence
   counter so two orders at the same timestamp don't collide).
4. **Flatten in `OnEndAsync`** - close any open position so the run realizes its P&L.
5. **Warm up** before trading (e.g. skip until enough ticks/bars seen); guard against zero/negative
   prices; keep `OnTickAsync` allocation-light (it runs per tick).
6. **No file, network, registry, process, or reflection-emit access.** The host statically scans the
   compiled code and **blocks** any of these before it runs - a strategy that uses them will be refused,
   not merely warned. A strategy consumes market data and emits orders; nothing else.

## Parameters (optional)

Expose tunables with a static schema + a static `Create` factory; the backtest sweep and the live
window render an editor automatically:

```csharp
public static StrategyParameterSchema Schema { get; } = new(
    StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
    StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1),
    StrategyParameter.Bool("useTrail", "Trailing stop", false));

public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
    new MyKernel(contract, p.GetInt("lookback"), p.GetDouble("threshold"), p.GetBool("useTrail"));
```

## Commands

```powershell
dotnet build DaxNewStrategy.slnx          # build plugin + tests
dotnet test  DaxNewStrategy.slnx          # offline kernel harness
./pack-plugin.ps1                          # -> DaxNewStrategy.daxplugin (integrity-indexed zip)
```

Install into the terminal: **Plugins -> Manage strategy plugins... -> Install plugin...** and pick the
`.daxplugin` (or the built `.dll` for a quick dev drop-in), then restart the terminal. It appears in
Backtest Studio and the `daxalgo-backtest` CLI under the id in `plugin.json`. Loading problems show
in the Plugin Manager and the terminal's Activity Log with a classified reason.

## Definition of done for a change

Build green, tests green (including at least one test that exercises the changed behaviour),
`./pack-plugin.ps1` produces a package, and the kernel still flattens at end of run.

<!-- Generated by build/gen-ai-context.ps1 for SDK 0.1.1-alpha. Do not hand-edit - change the sources and regenerate. -->
