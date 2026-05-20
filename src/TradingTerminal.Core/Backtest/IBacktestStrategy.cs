using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Engine-facing strategy contract. Implementations receive ticks in chronological order
/// and may submit orders through the injected <see cref="IOrderRouter"/>.
/// <see cref="OnOrderEventAsync"/> is called for every state transition produced by the
/// simulated order book.
///
/// Existing view-model strategies remain pure observers; they'll grow a thin adapter that
/// implements this interface when porting them to the backtester (Phase 7).
/// </summary>
public interface IBacktestStrategy
{
    /// <summary>Called once before any ticks. Use to read initial state or schedule warmup.</summary>
    Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct);

    /// <summary>Called for each tick in order. The clock has already been advanced to the tick's timestamp.</summary>
    Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct);

    /// <summary>
    /// Called for each L2 order-book snapshot, when the active broker supports depth and the
    /// host has subscribed (live signal mode). Default is a no-op so L1-only strategies are
    /// unaffected; book-aware strategies (e.g. the APEX scalper's OBI) override this to cache
    /// the latest <see cref="DepthSnapshot"/>. The backtester does not replay depth yet, so
    /// this is currently a live-only signal source.
    /// </summary>
    Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Called for each order event produced by the simulated order book.</summary>
    Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct);

    /// <summary>Called once after the last tick. Use to close out positions.</summary>
    Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct);
}
