using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// Everything a strategy kernel is handed to do its job, bundled into one argument so the
/// per-event callbacks stay stable as the platform grows (adding a capability is a new property
/// here, not a breaking change to every <c>On…Async</c> signature).
///
/// The same context shape is presented in live signal mode, so a kernel written against it runs
/// unchanged in the backtester and against a live feed — the only difference is which
/// <see cref="IOrderRouter"/> and <see cref="IClock"/> back it.
/// </summary>
public interface IStrategyContext
{
    /// <summary>Simulated clock in backtests, wall clock live. Never call <c>DateTime.UtcNow</c> directly.</summary>
    IClock Clock { get; }

    /// <summary>The only way to submit or cancel orders. Strategies never see a broker client.</summary>
    IOrderRouter Router { get; }

    /// <summary>Read-only account state for sizing and exposure checks.</summary>
    IPortfolioView Portfolio { get; }

    /// <summary>The instruments this run trades, with their per-instrument economics.</summary>
    Universe Universe { get; }

    /// <summary>The parameter bag for this run — what the optimizer sweeps and the author tunes.</summary>
    StrategyParameters Parameters { get; }
}
