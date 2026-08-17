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
    IReadOnlyList<EquityPoint>? EquityCurve = null,
    AuthoringFidelityStrip? Fidelity = null);

/// <summary>
/// Honest "which rung are we on?" for Hyperion Prove — not engine marketing.
/// Always shown on the Prove tab so Sharpe is never read as Nautilus-grade.
/// </summary>
public sealed record AuthoringFidelityStrip(
    string Rung,
    string HonestFor,
    string NotHonestFor,
    string Detail)
{
    /// <summary>What Hyperion Prove actually runs today (session + bar-synthetic L1).</summary>
    public static AuthoringFidelityStrip ProveDefault { get; } = new(
        Rung: "Bar-synthetic L1 · Latency 0 · 1 instrument · Session engine",
        HonestFor: "Bar / indicator sniff, rough direction, fee-aware P&L shape",
        NotHonestFor: "Arbitrage, spread races, order-book imbalance, co-lo latency, true tape absorption",
        Detail: "Hyperion Prove uses the session path (bar→ticks). New BacktestEngine also supports MidPrice / NextBarOpen / EveryTickFromBars / LatencyMs / DepthWalk / multi-instrument LocalStore — pick that rung when you need microstructure honesty.");

    public static AuthoringFidelityStrip ForProveRun(string symbol, string broker, int barCount, string barSize) =>
        ProveDefault with
        {
            Detail =
                $"Last run: {barCount}×{barSize} bars → synthetic L1 on {symbol} ({broker}). " +
                "Not honest for arb / OBI until latency + depth fills land.",
        };
}
