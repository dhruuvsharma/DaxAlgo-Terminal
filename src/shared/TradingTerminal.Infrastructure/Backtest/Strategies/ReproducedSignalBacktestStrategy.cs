using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Research;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Single-instrument <see cref="IBacktestStrategy"/> view of a paper reproduction's replay, for the
/// <c>IBacktestStrategyRegistry</c> catalog (the Backtest tab / authoring registration path, which is
/// single-instrument by contract). Replays only the manifest signals for the contract it is built
/// against. The portfolio-shaped <see cref="ReproducedSignalStrategyKernel"/> is the authoritative
/// multi-instrument replay used by the Studio/CLI; this is the same sign-of-value rule projected onto
/// the legacy single-instrument seam (no position view, so it tracks its own target locally).
///
/// <para>Data/signals only — orders go to the injected <see cref="IOrderRouter"/>, never a broker.</para>
/// </summary>
public sealed class ReproducedSignalBacktestStrategy : IBacktestStrategy
{
    private readonly Contract _contract;
    private readonly IReadOnlyList<ReproducedSignal> _signals; // time-sorted, this instrument only
    private readonly long _qty;
    private int _cursor;
    private long _position; // signed target this strategy currently holds

    public ReproducedSignalBacktestStrategy(Contract contract, ReproSignalManifest manifest, InstrumentId instrument, long qty = 1)
    {
        _contract = contract;
        _qty = Math.Max(1, qty);
        _signals = manifest.Signals
            .Where(s => s.Instrument == instrument)
            .OrderBy(s => s.EventTimeUtc)
            .ToList();
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var nowUtc = clock.UtcNow;
        ReproducedSignal? due = null;
        while (_cursor < _signals.Count && _signals[_cursor].EventTimeUtc <= nowUtc)
            due = _signals[_cursor++];
        if (due is null) return;

        var target = Math.Sign(due.Value) * _qty;
        await ReconcileAsync(target, router, ct).ConfigureAwait(false);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
        ReconcileAsync(target: 0, router, ct);

    private async Task ReconcileAsync(long target, IOrderRouter router, CancellationToken ct)
    {
        var delta = target - _position;
        if (delta == 0) return;
        var side = delta > 0 ? OrderSide.Buy : OrderSide.Sell;
        await router.PlaceOrderAsync(
            new OrderRequest(Guid.NewGuid().ToString("N"), _contract, side, OrderType.Market, Math.Abs(delta)), ct)
            .ConfigureAwait(false);
        _position = target;
    }
}
