using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Research;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Replays a <see cref="ReproSignalManifest"/> (a paper reproduction's bridged, time-sorted signals)
/// against the engine's quote/bar stream. This is the ONLY way a reproduced paper reaches the engine —
/// data/signals only, NO live order path: it submits backtest orders through
/// <see cref="IStrategyContext.Router"/> exactly like <c>MeanReversionKernel</c>, never a broker.
///
/// <para><b>Replay rule.</b> Per instrument the kernel keeps a cursor over that instrument's signals
/// (already time-sorted by the bridge). On each quote/bar whose timestamp crosses the next pending
/// signal(s), it advances to the latest signal at-or-before the event time and sets the target position
/// from <c>sign(value)</c> scaled by <see cref="_qty"/> (flat at 0). It then submits the delta between
/// the engine's current position and that target as a single market order. Deterministic and simple —
/// no smoothing, no sizing model beyond sign.</para>
///
/// <para><b>Provenance.</b> The manifest carries the paper id + repo commit + env hash; this kernel
/// trades only against signals stamped with that triple. The provenance survives onto the
/// <see cref="BacktestStrategyOption"/>/<see cref="StrategyKernelDescriptor"/> the factory builds.</para>
///
/// <para><b>Capability.</b> Signal replay needs only L1/Bars to know the clock and route market orders;
/// it consumes no tape or depth. The descriptor/option the factory builds advertises
/// <c>L1 | Bars</c> so the engine feed is never asked for data it can't supply.</para>
/// </summary>
public sealed class ReproducedSignalStrategyKernel : IStrategyKernel
{
    private readonly ReproSignalManifest _manifest;
    private readonly long _qty;

    // Per-instrument time-sorted signal lists + the cursor into each (index of the NEXT unprocessed signal).
    private readonly Dictionary<InstrumentId, IReadOnlyList<ReproducedSignal>> _byInstrument = new();
    private readonly Dictionary<InstrumentId, int> _cursor = new();

    public ReproducedSignalStrategyKernel(ReproSignalManifest manifest, long qty = 1)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _qty = Math.Max(1, qty);
    }

    public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
    {
        // Fail loudly if a reproduced signal targets an instrument the run's universe can't feed — the
        // engine has no quotes/bars for it, so the signal would be silently dropped. This mirrors the
        // trade-tape capability check (refuse rather than run on data the feed can't supply).
        var missing = _manifest.Instruments.Where(id => ctx.Universe.Find(id) is null).ToList();
        if (missing.Count > 0)
            throw new NotSupportedException(
                "Reproduced manifest has signals for instruments absent from the backtest universe: " +
                string.Join(", ", missing.Select(m => m.ToString())) +
                ". Add them to the run's universe so the engine can feed them.");

        // Group the manifest's signals by instrument; each group stays in the manifest's global time order.
        foreach (var group in _manifest.Signals.GroupBy(s => s.Instrument))
        {
            _byInstrument[group.Key] = group.ToList();
            _cursor[group.Key] = 0;
        }
        return Task.CompletedTask;
    }

    public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct) =>
        AdvanceAndTradeAsync(instrument, ctx, ct);

    public Task OnBarAsync(InstrumentId instrument, OhlcvBar bar, IStrategyContext ctx, CancellationToken ct) =>
        AdvanceAndTradeAsync(instrument, ctx, ct);

    /// <summary>Advance this instrument's cursor to the latest signal at-or-before the engine clock and
    /// reconcile the position to that signal's target. Uses <see cref="IStrategyContext.Clock"/> (the
    /// engine sets it to the current event time before each callback), so quotes and bars share one
    /// time source regardless of their per-record timestamp field. No-op when no signal has come due.</summary>
    private async Task AdvanceAndTradeAsync(InstrumentId instrument, IStrategyContext ctx, CancellationToken ct)
    {
        var nowUtc = ctx.Clock.UtcNow;
        if (!_byInstrument.TryGetValue(instrument, out var signals)) return;

        int i = _cursor[instrument];
        ReproducedSignal? due = null;
        while (i < signals.Count && signals[i].EventTimeUtc <= nowUtc)
        {
            due = signals[i];
            i++;
        }
        _cursor[instrument] = i;
        if (due is null) return; // no new signal has crossed the clock

        var target = TargetPosition(due.Value);
        await ReconcileAsync(instrument, target, ctx, ct).ConfigureAwait(false);
    }

    /// <summary>Submit the delta between the current engine position and the desired target as one
    /// market order. Mirrors <c>MeanReversionKernel</c>'s order-submission model.</summary>
    private static async Task ReconcileAsync(InstrumentId instrument, long target, IStrategyContext ctx, CancellationToken ct)
    {
        var contract = ctx.Universe.Find(instrument)?.Contract;
        if (contract is null) return; // signal for an instrument not in this run's universe — skip

        var current = ctx.Portfolio.PositionOf(instrument).Quantity;
        var delta = target - current;
        if (delta == 0) return;

        var side = delta > 0 ? OrderSide.Buy : OrderSide.Sell;
        await Market(ctx, contract, side, Math.Abs(delta), ct).ConfigureAwait(false);
    }

    /// <summary>Flatten every open position the replay built — leave the account square.</summary>
    public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
    {
        foreach (var instrument in _byInstrument.Keys)
            await ReconcileAsync(instrument, target: 0, ctx, ct).ConfigureAwait(false);
    }

    private long TargetPosition(double value) => Math.Sign(value) * _qty;

    private static Task Market(IStrategyContext ctx, Contract contract, OrderSide side, long qty, CancellationToken ct) =>
        ctx.Router.PlaceOrderAsync(
            new OrderRequest(Guid.NewGuid().ToString("N"), contract, side, OrderType.Market, qty), ct);
}
