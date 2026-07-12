# DaxAlgo Terminal â€” strategy authoring context (SDK 0.1.0-alpha)

You are writing a trading strategy for **DaxAlgo Terminal**, a WPF multi-broker terminal. A strategy is
pure signal logic that consumes market data and emits orders through a router. This document is the
complete contract; follow it exactly. **It targets SDK `0.1.0-alpha` â€” code you write must compile
against that SDK.**

> This build is **data / signals only** â€” there is no live order-execution path. Orders you place are
> simulated by the backtest engine and surfaced as signals. Do not attempt real trading.

---

## The engine contract

A strategy is an `IBacktestStrategy` â€” the host calls these as market events arrive:

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

public interface IClock { DateTime UtcNow { get; } }   // NEVER DateTime.UtcNow â€” backtests replay the past

public interface IOrderRouter
{
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
    IObservable<OrderEvent> OrderEvents { get; }
}

public sealed record OrderRequest(
    string ClientOrderId,        // caller-generated IDEMPOTENCY key â€” unique per order
    Contract Contract,
    OrderSide Side,              // Buy | Sell
    OrderType Type,              // Market | Limit | Stop | StopLimit
    long Quantity,
    double? LimitPrice = null,   // required for Limit/StopLimit
    double? StopPrice = null,    // required for Stop/StopLimit
    TimeInForce TimeInForce = TimeInForce.Day);
```

Depth (`OnDepthAsync`) and the trade tape (`OnTradeAsync`) are opt-in â€” override them only if your
strategy needs L2 or prints, and declare the matching `DataRequirement`.

## Hard rules (a generated strategy that breaks any of these is wrong)

1. **All state in fields.** One instance per run â€” no `static` mutable state, no shared state.
2. **Time only via `IClock`** (the `clock` parameter). Never `DateTime.UtcNow` / `DateTime.Now` â€”
   a backtest replays historical time and the wall clock would be meaningless.
3. **Orders only via `IOrderRouter`**, each with a **unique `ClientOrderId`** (suffix a sequence
   counter so two orders at the same timestamp don't collide).
4. **Flatten in `OnEndAsync`** â€” close any open position so the run realizes its P&L.
5. **Warm up** before trading (e.g. skip until enough ticks/bars seen); guard against zero/negative
   prices; keep `OnTickAsync` allocation-light (it runs per tick).
6. **No file, network, registry, process, or reflection-emit access.** The host statically scans the
   compiled code and **blocks** any of these before it runs â€” a strategy that uses them will be refused,
   not merely warned. A strategy consumes market data and emits orders; nothing else.

## Parameters (optional â€” makes the strategy tunable in the UI)

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

## Data-requirement flags

`StrategyDataRequirement` is a `[Flags]` enum: `L1` (best bid/ask), `Bars`, `Depth` (L2),
`TradeTape` (prints). Default is `L1 | Bars`. Declare exactly what you consume â€” the host starts
those pumps and only offers brokers that can supply them.

## Quant cheatsheet (numerically-stable forms)

- **EMA(period)**: `ema += 2.0 / (period + 1) * (x - ema)` â€” seed `ema = x` on the first sample.
- **Rolling mean/var**: keep a fixed-size buffer or Welford's online variance; never re-sum a window.
- **Mid price**: `(bid + ask) / 2` â€” reject when `<= 0`.
- **Returns**: log return `Math.Log(p / prevP)` for stability across scales.
- **Z-score**: `(x - mean) / Math.Max(1e-9, stdev)` â€” floor the denominator.

## Memory-safety (only if you add a live window / your own stream handling)

Bounded, batch-drained channels (never unbounded, never one-UI-marshal-per-item); one coalesced redraw
on a `DispatcherTimer` (not a redraw per event); dispose timers + subscriptions at close; bound every
history buffer. `LiveSignalStrategyViewModelBase` already does this â€” only your additions need care.

---

## Worked example â€” the demo kernel (verbatim from the `dotnet new daxalgo-strategy` template)

This compiles and backtests as-is; it is the shape to imitate.

```csharp
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace DaxNewStrategy.Engine;

/// <summary>
/// The strategy's engine kernel â€” pure signal logic against the backtest contracts, no UI. This
/// demo goes long when a fast EMA of the mid-price crosses above a slow EMA, short on the opposite
/// cross, and flattens at the end of the run. Replace the math; keep the shape:
/// <list type="bullet">
///   <item>own ALL state in fields (one kernel instance per run â€” no statics),</item>
///   <item>orders only via <see cref="IOrderRouter"/> with idempotent ClientOrderIds,</item>
///   <item>time only via <see cref="IClock"/> (never DateTime.UtcNow â€” backtests replay the past),</item>
///   <item>no file/network/process access â€” the host's install-time policy scan flags it.</item>
/// </list>
/// </summary>
public sealed class DaxNewStrategyKernel(Contract contract) : IBacktestStrategy
{
    private const int FastPeriod = 20;
    private const int SlowPeriod = 80;
    private const long Quantity = 1;

    private readonly Contract _contract = contract;
    private double _fastEma;
    private double _slowEma;
    private int _ticksSeen;
    private long _position; // signed units of Quantity: -1 short, 0 flat, +1 long
    private int _orderSeq;  // makes ClientOrderIds unique even at identical timestamps

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
        Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) / 2.0;
        if (mid <= 0) return;

        _ticksSeen++;
        if (_ticksSeen == 1)
        {
            _fastEma = _slowEma = mid;
            return;
        }

        _fastEma += 2.0 / (FastPeriod + 1) * (mid - _fastEma);
        _slowEma += 2.0 / (SlowPeriod + 1) * (mid - _slowEma);
        if (_ticksSeen < SlowPeriod) return; // warm-up

        var want = _fastEma > _slowEma ? 1L : _fastEma < _slowEma ? -1L : _position;
        if (want == _position) return;

        // One order moves straight to the target (a reversal is a single 2Ã—Quantity order).
        var delta = (want - _position) * Quantity;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: NextOrderId(clock),
            Contract: _contract,
            Side: delta > 0 ? OrderSide.Buy : OrderSide.Sell,
            Type: OrderType.Market,
            Quantity: Math.Abs(delta)), ct);
        _position = want;
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;

        // Flatten so the run's P&L is realized, not an open position.
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: NextOrderId(clock),
            Contract: _contract,
            Side: _position > 0 ? OrderSide.Sell : OrderSide.Buy,
            Type: OrderType.Market,
            Quantity: Math.Abs(_position) * Quantity), ct);
        _position = 0;
    }

    private string NextOrderId(IClock clock) =>
        $"dax.new.strategy-{clock.UtcNow:yyyyMMddHHmmssfff}-{_orderSeq++}";
}
```

---

## OUTPUT CONTRACT (a) â€” single-file kernel (in-app AI pane)

The in-app builder compiles a **single C# file** through Roslyn. When asked for a kernel this way:

- Return **exactly one** public class implementing `IBacktestStrategy` with a public `(Contract)`
  constructor. Optionally add a static `Schema` and a static `Create(Contract, StrategyParameters)`.
- **Do NOT** write a namespace or `using` directives â€” these are ambient (already imported):
  `System`, `System.Collections.Generic`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks`, `TradingTerminal.Core.Domain`, `TradingTerminal.Core.Trading`,
  `TradingTerminal.Core.Time`, `TradingTerminal.Core.Backtest`, `TradingTerminal.Core.MarketData`,
  `TradingTerminal.Core.Strategies.Parameters`.
- No file/network/process/reflection-emit APIs (they are blocked).
- Output **only** the code â€” no prose, no markdown fence â€” unless the caller asks for an explanation.

## OUTPUT CONTRACT (b) â€” full plugin project (template / CLI)

When working inside a scaffold from `dotnet new daxalgo-strategy` (its `CLAUDE.md`/`AGENTS.md`
carry the same rules), you edit files rather than emit one blob:

- Put ALL strategy math in `<Name>/Engine/<Name>Kernel.cs` (the `IBacktestStrategy`).
- Keep the plugin entry point (`<Name>Plugin.cs`), the `plugin.json` manifest, and â€” for a
  `--ui` scaffold â€” the view-model (on `LiveSignalStrategyViewModelBase`) + window.
- Grow `<Name>.Tests/` with real invariants; `dotnet build` + `dotnet test` must stay green.
- Never reference `TradingTerminal.*` projects or ship host DLLs â€” the `DaxAlgo.Sdk` package
  (`ExcludeAssets="runtime"`) is the whole surface.

---

*Generated by `build/gen-ai-context.ps1` for SDK `0.1.0-alpha`. Do not hand-edit â€” change the
sources (SdkInfo.cs, the template kernel, this generator) and regenerate.*
