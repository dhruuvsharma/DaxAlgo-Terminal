using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// The one strategy contract for the whole platform — backtest, live signal, and (via the polyglot
/// host) Python. Every market-data callback carries the <see cref="InstrumentId"/> it concerns, so a
/// single kernel serves both single-instrument and portfolio runs. All callbacks except
/// <see cref="OnStartAsync"/> default to no-ops, so a kernel implements only the event types it
/// actually consumes (a bar strategy ignores depth; an order-flow strategy ignores bars).
///
/// Replaces the old single-instrument <c>IBacktestStrategy</c>. The existing strategy classes are
/// not rewritten — a thin adapter wraps them to this interface so the new engine and the live
/// windows keep one source of truth for the math.
/// </summary>
public interface IStrategyKernel
{
    /// <summary>Called once before any market data. Read parameters, allocate per-instrument state.</summary>
    Task OnStartAsync(IStrategyContext ctx, CancellationToken ct);

    /// <summary>A bid/ask quote update for one instrument. The clock is already at the event time.</summary>
    Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>A trade print (signed tape) for one instrument. Override for order-flow strategies.</summary>
    Task OnTradeAsync(InstrumentId instrument, TradePrint trade, IStrategyContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>An L2 depth snapshot for one instrument. Override for book-aware strategies.</summary>
    Task OnDepthAsync(InstrumentId instrument, DepthSnapshot depth, IStrategyContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>A finished OHLCV bar for one instrument. Override for bar-driven strategies.</summary>
    Task OnBarAsync(InstrumentId instrument, OhlcvBar bar, IStrategyContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>An order lifecycle transition produced by the engine's order book.</summary>
    Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Called once after the last event. Flatten positions and emit any final state.</summary>
    Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
        => Task.CompletedTask;
}
