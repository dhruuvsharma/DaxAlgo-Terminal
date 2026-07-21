# DaxNewStrategy - DaxAlgo Terminal strategy

This repo is a strategy for DaxAlgo Terminal, scaffolded by `dotnet new daxalgo-strategy`. The nested
Engine project compiles once against **DaxAlgo.Sdk** (SDK `0.2.0-alpha`); that exact WPF-free assembly is
the source of live and backtest behavior. The outer project supplies Windows presentation and the legacy
plugin adapter. This file is the working context for an AI coding agent and is generated from the same
source as the in-app builder's context.

## What a strategy is here

- **`DaxNewStrategy/Engine/DaxNewStrategyKernel.cs`** - the strategy itself: an `IBacktestStrategy`
  receiving ticks and placing orders through `IOrderRouter`. ALL strategy math lives here.
- **`DaxNewStrategy/Engine/DaxNewStrategyFactory.cs`** - the manifest-named, public parameterless
  `IStrategyEngineFactory`. It declares parameters and data needs, then creates the kernel through the
  same activation seam used by live replay, single backtests, and optimizer sweeps.
- **`DaxNewStrategy/Engine/DaxNewStrategy.Engine.csproj`** - the deterministic, WPF-free canonical
  assembly. It never references the outer project, WPF, or UI assemblies.
- **`DaxNewStrategy/DaxNewStrategy.csproj`** - Windows presentation and legacy-host adapter. It may
  reference Engine; dependency direction never reverses.
- **`DaxNewStrategy/DaxNewStrategyPlugin.cs`** - legacy `IStrategyPlugin` registration and catalog
  descriptor. It contains no strategy math.
- **`DaxNewStrategy/plugin.json`** - read before code loads: id, version, stable `publisherId`,
  capabilities, and `targetSdkVersion` (pre-1.0 compatibility is exact major/minor).
- **`DaxNewStrategy.Tests/`** - offline harness referencing Engine directly and driving the kernel with
  synthetic ticks through a recording router. Keep it green; grow it with the strategy's invariants.
- **(`--ui` scaffolds only)** `DaxNewStrategyViewModel.cs` + `DaxNewStrategyWindow.xaml(.cs)` - a
  live strategy window. The VM derives from `LiveSignalStrategyViewModelBase` (host-provided: instrument
  picker, warm-up, start/stop, the signal feed, presets, Activity Log) and supplies just
  `DataRequirement` + `BuildStrategy(contract)` - which returns the SAME kernel the backtest runs, so
  live and backtest can't diverge. The outer project references `DaxAlgo.Sdk.Wpf` and registers the VM
  + window + a `StrategyFactoryRegistration`. A headless scaffold has none of this presentation code.

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

## Parameters (factory-owned; `StrategyParameterSchema.Empty` is valid)

The manifest-named factory owns the parameter schema, data requirements, and activation. Untunable
strategies use `StrategyParameterSchema.Empty`; tunable strategies declare their schema once and pass
the selected values into the same kernel used by live replay, single backtests, and optimizer sweeps:

```csharp
public StrategyParameterSchema Schema { get; } = new(
    StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
    StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1));

public StrategyDataRequirement DataRequirement =>
    StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

public IBacktestStrategy Create(Contract contract, StrategyParameters parameters) =>
    new DaxNewStrategyKernel(
        contract,
        parameters.GetInt("lookback"),
        parameters.GetDouble("threshold"));

public IBacktestStrategy Create(Contract contract) => Create(contract, Schema.CreateDefaults());
```

## Commands

```powershell
dotnet build DaxNewStrategy.slnx          # build engine, adapter, and tests
dotnet test  DaxNewStrategy.slnx          # offline kernel harness
./pack-strategy.ps1                        # -> DaxNewStrategy.daxstrategy (requires daxalgo-bundle)
./pack-plugin.ps1                          # -> legacy DaxNewStrategy.daxplugin
```

The new bundle can be inspected and signature-verified offline. Runtime bundle loading is a later host
integration. For current hosts, install the legacy `.daxplugin` through **Plugins -> Manage strategy
plugins... -> Install plugin...**, then restart.

## Definition of done for a change

Build green, tests green (including at least one test that exercises the changed behaviour),
`./pack-strategy.ps1` produces a passively inspectable bundle, `./pack-plugin.ps1` preserves current
host compatibility, and the kernel still flattens at end of run.

<!-- Generated by build/gen-ai-context.ps1 for SDK 0.2.0-alpha. Do not hand-edit - change the sources and regenerate. -->
