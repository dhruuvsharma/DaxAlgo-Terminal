# DaxNewStrategy — DaxAlgo Terminal strategy plugin

This repo is a **strategy plugin for DaxAlgo Terminal** (a WPF multi-broker trading terminal),
scaffolded by `dotnet new daxalgo-strategy`. It compiles against the **DaxAlgo.Sdk** NuGet package
and is loaded by the terminal at runtime from its `plugins/` folder — the host never recompiles.
This file is the working context for an AI coding agent; everything below is load-bearing.

## What a strategy is here

- **`DaxNewStrategy/Engine/DaxNewStrategyKernel.cs`** — the strategy itself: an `IBacktestStrategy`
  receiving ticks and placing orders through `IOrderRouter`. ALL strategy math lives here.
- **`DaxNewStrategy/DaxNewStrategyPlugin.cs`** — the `IStrategyPlugin` entry point (the host finds
  exactly one public implementation per assembly) plus the catalog descriptor. Registration is
  plain `IServiceCollection` calls.
- **`DaxNewStrategy/plugin.json`** — read by the host BEFORE any code loads: id, version, and
  `targetSdkVersion` (must stay compatible with the host SDK; pre-1.0 = exact major.minor).
- **`DaxNewStrategy.Tests/`** — offline harness driving the kernel with synthetic ticks through a
  recording router. Keep it green; grow it with the strategy's invariants.

## Contracts (from DaxAlgo.Sdk — do not redeclare)

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

public interface IOrderRouter
{
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
    IObservable<OrderEvent> OrderEvents { get; }
}

public sealed record OrderRequest(
    string ClientOrderId,        // caller-generated IDEMPOTENCY key — must be unique per order
    Contract Contract,
    OrderSide Side,              // Buy | Sell
    OrderType Type,              // Market | Limit | Stop | StopLimit
    long Quantity,
    double? LimitPrice = null,   // required for Limit/StopLimit
    double? StopPrice = null,    // required for Stop/StopLimit
    TimeInForce TimeInForce = TimeInForce.Day);

public interface IClock { DateTime UtcNow { get; } }   // NEVER DateTime.UtcNow — backtests replay the past
```

## Hard rules

1. **All state in kernel fields** — one kernel instance per run, no statics, no shared mutable state.
2. **Time only via `IClock`**, orders only via `IOrderRouter`, unique `ClientOrderId` per order.
3. **No file, network, registry, or process access** — the host's install-time policy scan flags
   these and a curated marketplace review rejects them. A strategy consumes market data and emits
   orders; nothing else.
4. **Flatten in `OnEndAsync`** so a run's P&L is realized, not an open position.
5. **Never add references to `TradingTerminal.*` projects or copy host DLLs into the output** —
   the `DaxAlgo.Sdk` PackageReference (with `ExcludeAssets="runtime"`) is the entire surface.
   The host shares its own contract assemblies into the plugin's load context at runtime.
6. Warm up indicators before trading (see `_ticksSeen < SlowPeriod` in the demo); guard against
   zero/negative prices; keep per-tick work allocation-free where possible (this runs per tick).

## Commands

```powershell
dotnet build DaxNewStrategy.slnx          # build plugin + tests
dotnet test  DaxNewStrategy.slnx          # offline kernel harness
./pack-plugin.ps1                          # -> DaxNewStrategy.daxplugin (integrity-indexed zip)
```

Install into the terminal: **Plugins → Manage strategy plugins… → Install plugin…** and pick the
`.daxplugin` (or the built `.dll` for a quick dev drop-in), then restart the terminal. It appears
in Backtest Studio and the `daxalgo-backtest` CLI under the id in `plugin.json`. Loading problems
show in the Plugin Manager and the terminal's Activity Log with a classified reason.

## Definition of done for a change

Build green, tests green (including at least one test that exercises the changed behaviour),
`./pack-plugin.ps1` produces a package, and the kernel still flattens at end of run.
