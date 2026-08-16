using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Runs a real in-session backtest for an authored strategy (not the lifecycle smoke).
/// Returns the same <see cref="BacktestStatistics"/> shape Quick Backtest uses so the Hyperion
/// Prove pane can show return / Sharpe / MDD / the full metric strip without binding to one engine type.
/// </summary>
public interface IAuthoringProveService
{
    /// <summary>True when a data broker is available to fetch history.</summary>
    bool CanRun { get; }

    Task<AuthoringProveResult> RunAsync(string strategyOptionId, CancellationToken ct = default);
}

/// <summary>UI-facing snapshot of one prove run — metrics always present on success; equity optional.</summary>
public sealed record AuthoringProveResult(
    bool Ok,
    string Message,
    BacktestStatistics? Stats,
    double TotalPnl,
    string? FeedQuality,
    IReadOnlyList<EquityPoint>? EquityCurve = null);
