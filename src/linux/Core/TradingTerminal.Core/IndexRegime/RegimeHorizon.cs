namespace TradingTerminal.Core.IndexRegime;

/// <summary>
/// The prediction horizon <c>t</c> — "will the index go up or down over the next …". The horizon
/// only changes how the per-timeframe regime scores of a constituent are blended into its single
/// directional score: a short horizon leans on the fast columns (1m/3m/5m), a long horizon on the
/// slow ones (1H/1D). It does not change which timeframes are computed (all eight always are).
/// </summary>
public enum RegimeHorizon
{
    /// <summary>Minutes — weights the fastest columns.</summary>
    Scalp,

    /// <summary>The rest of the session (hours) — weights the mid columns.</summary>
    Intraday,

    /// <summary>A few days — weights the slow columns.</summary>
    Swing,

    /// <summary>Weeks — weights the daily column hardest.</summary>
    Position,
}

/// <summary>
/// Per-horizon blend weights over the eight Advanced-Regime timeframe labels
/// (<c>1m, 3m, 5m, 15m, 20m, 30m, 1H, 1D</c>). The aggregator looks each column's weight up by its
/// <c>AdvancedTimeframe.Label</c> and renormalises over whichever columns are actually present, so
/// the raw numbers below only need to be relative, not sum-to-one.
/// </summary>
public static class TimeframeWeighting
{
    /// <summary>Human label + the rough <c>t</c> window each horizon answers for.</summary>
    public static string Describe(RegimeHorizon horizon) => horizon switch
    {
        RegimeHorizon.Scalp    => "Scalp (minutes)",
        RegimeHorizon.Intraday => "Intraday (hours)",
        RegimeHorizon.Swing    => "Swing (days)",
        RegimeHorizon.Position => "Position (weeks)",
        _ => horizon.ToString(),
    };

    /// <summary>Relative blend weight per timeframe label for the given horizon. Labels not present
    /// here fall back to a small floor weight in the aggregator so an unexpected column still counts
    /// a little rather than vanishing.</summary>
    public static IReadOnlyDictionary<string, double> For(RegimeHorizon horizon) => horizon switch
    {
        RegimeHorizon.Scalp => new Dictionary<string, double>
        {
            ["1m"] = 0.30, ["3m"] = 0.25, ["5m"] = 0.20, ["15m"] = 0.12,
            ["20m"] = 0.06, ["30m"] = 0.04, ["1H"] = 0.02, ["1D"] = 0.01,
        },
        RegimeHorizon.Intraday => new Dictionary<string, double>
        {
            ["1m"] = 0.05, ["3m"] = 0.10, ["5m"] = 0.15, ["15m"] = 0.22,
            ["20m"] = 0.18, ["30m"] = 0.15, ["1H"] = 0.10, ["1D"] = 0.05,
        },
        RegimeHorizon.Swing => new Dictionary<string, double>
        {
            ["1m"] = 0.02, ["3m"] = 0.03, ["5m"] = 0.05, ["15m"] = 0.10,
            ["20m"] = 0.10, ["30m"] = 0.15, ["1H"] = 0.30, ["1D"] = 0.25,
        },
        RegimeHorizon.Position => new Dictionary<string, double>
        {
            ["1m"] = 0.01, ["3m"] = 0.02, ["5m"] = 0.03, ["15m"] = 0.05,
            ["20m"] = 0.06, ["30m"] = 0.10, ["1H"] = 0.28, ["1D"] = 0.45,
        },
        _ => new Dictionary<string, double>(),
    };

    /// <summary>Floor weight applied to a timeframe label the horizon table doesn't list.</summary>
    public const double FloorWeight = 0.01;
}
