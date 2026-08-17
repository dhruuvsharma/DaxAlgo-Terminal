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
/// Always shown on the Prove tab so Sharpe is never over-read.
/// </summary>
public sealed record AuthoringFidelityStrip(
    string Rung,
    string HonestFor,
    string NotHonestFor,
    string Detail)
{
    /// <summary>Before a run: Prove requires a real trade tape — no bar-synthetic fallback.</summary>
    public static AuthoringFidelityStrip ProveDefault { get; } = new(
        Rung: "Real trade tape · L1 quotes from prints · Latency 0 · 1 instrument · Session engine",
        HonestFor: "Tape / absorption / tick-rule flow (when the broker returns historical trades)",
        NotHonestFor: "Arbitrage latency races, L2 DepthWalk fills, multi-leg LocalStore (use BacktestEngine RunSpec)",
        Detail: "Hyperion Prove fetches real prints (no OHLC→fake ticks). Connect Binance (or a broker with RequestHistoricalTrades). Depth/OBI still excluded on this session path.");

    public static AuthoringFidelityStrip ForRealTapeRun(string symbol, string broker, int tradeCount) =>
        ProveDefault with
        {
            Detail =
                $"Last run: {tradeCount:N0} real prints on {symbol} ({broker}). " +
                "q = 1.0 tape path. DepthWalk / LatencyMs / multi-leg still need BacktestEngine.",
        };
}
