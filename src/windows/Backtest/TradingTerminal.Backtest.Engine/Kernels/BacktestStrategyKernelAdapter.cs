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
/// contract has no bar callback, so <see cref="IStrategyKernel.OnBarAsync"/> stays a no-op.
/// </summary>
public sealed class BacktestStrategyKernelAdapter : IStrategyKernel, IAsyncDisposable
{
    private readonly Func<Contract, IBacktestStrategy>? _build;
    private IBacktestStrategy? _inner;
    private int _disposed;

    /// <summary>Wrap an already-built legacy strategy (used by the parity test).</summary>
    public BacktestStrategyKernelAdapter(IBacktestStrategy inner) => _inner = inner;

    /// <summary>Defer construction until the run's instrument is known — the legacy strategies take a
    /// <see cref="Contract"/> in their constructor, supplied here from the universe's primary.</summary>
    public BacktestStrategyKernelAdapter(Func<Contract, IBacktestStrategy> build) => _build = build;

    private IBacktestStrategy Inner
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _inner ?? throw new InvalidOperationException("Strategy not started.");
        }
    }

    public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (inner is IDisposable disposable)
            disposable.Dispose();
    }
}
