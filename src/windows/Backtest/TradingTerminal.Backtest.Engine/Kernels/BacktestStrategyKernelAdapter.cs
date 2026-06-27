using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Kernels;

/// <summary>
/// Runs an existing single-instrument <see cref="IBacktestStrategy"/> (the legacy contract the 12
/// shipped strategies implement) under the new <see cref="IStrategyKernel"/> seam. This is the bridge
/// that lets the new engine reuse all current strategy logic verbatim — no rewrite, one source of
/// truth with the live windows. The new callbacks carry an <see cref="InstrumentId"/> the legacy
/// strategy doesn't need, so it's dropped; clock and router are pulled from the context. The legacy
/// contract has no bar callback, so <see cref="OnBarAsync"/> stays a no-op.
/// </summary>
public sealed class BacktestStrategyKernelAdapter : IStrategyKernel
{
    private readonly Func<Contract, IBacktestStrategy>? _build;
    private IBacktestStrategy? _inner;

    /// <summary>Wrap an already-built legacy strategy (used by the parity test).</summary>
    public BacktestStrategyKernelAdapter(IBacktestStrategy inner) => _inner = inner;

    /// <summary>Defer construction until the run's instrument is known — the legacy strategies take a
    /// <see cref="Contract"/> in their constructor, supplied here from the universe's primary.</summary>
    public BacktestStrategyKernelAdapter(Func<Contract, IBacktestStrategy> build) => _build = build;

    private IBacktestStrategy Inner => _inner ?? throw new InvalidOperationException("Strategy not started.");

    public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
    {
        _inner ??= _build!(ctx.Universe.Primary.Contract);
        return _inner.OnStartAsync(ctx.Clock, ctx.Router, ct);
    }

    public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct) =>
        Inner.OnTickAsync(quote, ctx.Clock, ctx.Router, ct);

    public Task OnTradeAsync(InstrumentId instrument, TradePrint trade, IStrategyContext ctx, CancellationToken ct) =>
        Inner.OnTradeAsync(trade, ctx.Clock, ctx.Router, ct);

    public Task OnDepthAsync(InstrumentId instrument, DepthSnapshot depth, IStrategyContext ctx, CancellationToken ct) =>
        Inner.OnDepthAsync(depth, ctx.Clock, ctx.Router, ct);

    public Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct) =>
        Inner.OnOrderEventAsync(evt, ct);

    public Task OnEndAsync(IStrategyContext ctx, CancellationToken ct) =>
        Inner.OnEndAsync(ctx.Clock, ctx.Router, ct);
}
