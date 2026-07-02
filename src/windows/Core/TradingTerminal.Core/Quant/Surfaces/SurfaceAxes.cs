namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>Top-level surface mode — decides what the X/Y/Z/W axes can be bound to.</summary>
public enum SurfaceMode
{
    /// <summary>Mode A: X/Y are strategy parameters swept over a range; Z is a performance
    /// metric of the simulated strategy; W (color) is a risk metric. Backtest landscapes.</summary>
    ParameterOptimization,
    /// <summary>Mode B: X/Y are calendar buckets (hour of day × day of week, …); Z aggregates
    /// the returns that fell into each bucket. Seasonality surfaces.</summary>
    TemporalAggregation,
    /// <summary>Mode C: X/Y bucket conditioning variables (prior-return bin, volatility bucket,
    /// volume decile, time lag); Z is a statistic of the NEXT-period returns in each cell.</summary>
    CrossSectional,
}

/// <summary>Which slot of the surface an axis option is offered for.</summary>
public enum SurfaceAxisRole { X, Y, Z, Color }

/// <summary>
/// One entry in an axis dropdown. <see cref="RangeEditable"/> gates the Min/Max/Step inputs
/// (parameter sweeps and linear bins are editable; calendar and quantile buckets are fixed).
/// For Z/Color roles the id is a <see cref="SurfaceMetricRegistry"/> metric id.
/// </summary>
public sealed record SurfaceAxisOption(
    string Id,
    string Name,
    string Category,
    SurfaceAxisFormat Format,
    bool RangeEditable,
    double DefaultMin,
    double DefaultMax,
    double DefaultStep);

/// <summary>A Mode-A strategy parameter: sweep range defaults plus the value used when the
/// parameter is NOT on an axis (0 disables optional filters/exits).</summary>
public sealed record StrategyParameterDefinition(
    string Id,
    string Name,
    double DefaultMin,
    double DefaultMax,
    double DefaultStep,
    double DefaultValue,
    bool IsInteger,
    SurfaceAxisFormat Format);

/// <summary>A Mode-B calendar bucket axis (fixed bucket count with labels).</summary>
public sealed record TemporalAxisDefinition(
    string Id,
    string Name,
    string[] Labels,
    Func<DateTime, int> Selector)
{
    public int BucketCount => Labels.Length;
}

/// <summary>What a Mode-C axis conditions on.</summary>
public enum CrossSectionVariable
{
    /// <summary>Previous-bar simple return, bucketed into linear bins (Min/Max/Step editable).</summary>
    PriorReturnBin,
    /// <summary>Rolling realized volatility (20-bar), bucketed into quantile deciles.</summary>
    VolatilityBucket,
    /// <summary>Bar volume, bucketed into quantile deciles.</summary>
    VolumeDecile,
    /// <summary>Time lag k (integer axis): the cell conditions on the return at t−k.</summary>
    TimeLag,
}

/// <summary>A Mode-C conditioning axis.</summary>
public sealed record CrossSectionAxisDefinition(
    string Id,
    string Name,
    CrossSectionVariable Kind,
    SurfaceAxisFormat Format,
    bool RangeEditable,
    double DefaultMin,
    double DefaultMax,
    double DefaultStep);

/// <summary>
/// The static catalog behind every Surface Lab dropdown: given (mode, role) it lists the
/// legal options. Z/Color options delegate to <see cref="SurfaceMetricRegistry"/>, filtered so
/// benchmark-dependent metrics (beta, correlation) only appear where a benchmark exists
/// (parameter mode, strategy vs underlying) and trade metrics lead in parameter mode.
/// </summary>
public static class SurfaceAxisCatalog
{
    /// <summary>Mode-A sweepable parameters. The simulator is a long/flat MA-cross kernel with
    /// optional RSI / ROC entry filters and stop-loss / take-profit / ATR-trail exits — every
    /// classic "two-parameter heatmap" lives inside this family.</summary>
    public static IReadOnlyList<StrategyParameterDefinition> Parameters { get; } = new StrategyParameterDefinition[]
    {
        new("fastma",     "Fast MA Period",        2,    60,   2,     10, true,  SurfaceAxisFormat.Integer),
        new("slowma",     "Slow MA Period",        10,   200,  5,     30, true,  SurfaceAxisFormat.Integer),
        new("rsilen",     "RSI Lookback (filter)", 2,    50,   2,     0,  true,  SurfaceAxisFormat.Integer),
        new("roclen",     "ROC Lookback (filter)", 2,    60,   2,     0,  true,  SurfaceAxisFormat.Integer),
        new("stoploss",   "Stop Loss %",           0.005, 0.10, 0.005, 0, false, SurfaceAxisFormat.Percent),
        new("takeprofit", "Take Profit %",         0.01,  0.20, 0.01,  0, false, SurfaceAxisFormat.Percent),
        new("atrmult",    "ATR Multiplier (trail)", 0.5,  6,    0.25,  0, false, SurfaceAxisFormat.Ratio),
    };

    public static StrategyParameterDefinition? ResolveParameter(string id) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<TemporalAxisDefinition> TemporalAxes { get; } = new TemporalAxisDefinition[]
    {
        new("hour",  "Hour of Day",
            Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(),
            t => t.Hour),
        new("dow",   "Day of Week",
            new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" },
            t => ((int)t.DayOfWeek + 6) % 7),
        new("dom",   "Day of Month",
            Enumerable.Range(1, 31).Select(d => d.ToString()).ToArray(),
            t => t.Day - 1),
        new("month", "Month of Year",
            new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
            t => t.Month - 1),
    };

    public static TemporalAxisDefinition? ResolveTemporal(string id) =>
        TemporalAxes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<CrossSectionAxisDefinition> CrossSectionAxes { get; } = new CrossSectionAxisDefinition[]
    {
        new("retbin",    "Prior-Return Bucket", CrossSectionVariable.PriorReturnBin,  SurfaceAxisFormat.Percent, true,  -0.05, 0.05, 0.01),
        new("volbucket", "Volatility Bucket",   CrossSectionVariable.VolatilityBucket, SurfaceAxisFormat.Integer, false, 1, 10, 1),
        new("voldecile", "Volume Decile",       CrossSectionVariable.VolumeDecile,     SurfaceAxisFormat.Integer, false, 1, 10, 1),
        new("lag",       "Time Lag (bars)",     CrossSectionVariable.TimeLag,          SurfaceAxisFormat.Integer, true,  1, 10, 1),
    };

    public static CrossSectionAxisDefinition? ResolveCrossSection(string id) =>
        CrossSectionAxes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The options legal for one dropdown. X/Y options depend on the mode; Z/Color
    /// options are metric-registry entries filtered by mode capability.</summary>
    public static IReadOnlyList<SurfaceAxisOption> OptionsFor(SurfaceMode mode, SurfaceAxisRole role)
    {
        if (role is SurfaceAxisRole.X or SurfaceAxisRole.Y)
        {
            return mode switch
            {
                SurfaceMode.ParameterOptimization => Parameters
                    .Select(p => new SurfaceAxisOption(p.Id, p.Name, "Strategy Parameters", p.Format, true, p.DefaultMin, p.DefaultMax, p.DefaultStep))
                    .ToList(),
                SurfaceMode.TemporalAggregation => TemporalAxes
                    .Select(a => new SurfaceAxisOption(a.Id, a.Name, "Calendar Buckets", SurfaceAxisFormat.Integer, false, 0, a.BucketCount - 1, 1))
                    .ToList(),
                _ => CrossSectionAxes
                    .Select(a => new SurfaceAxisOption(a.Id, a.Name, "Conditioning Variables", a.Format, a.RangeEditable, a.DefaultMin, a.DefaultMax, a.DefaultStep))
                    .ToList(),
            };
        }

        // Z / Color: metric registry entries valid for the mode. Trade-based and benchmark-based
        // metrics only make sense in parameter mode (there IS a strategy and an underlying there);
        // bucket modes get aggregates first, then the sample statistics.
        var isParamMode = mode == SurfaceMode.ParameterOptimization;
        IEnumerable<SurfaceMetricDefinition> metrics = SurfaceMetricRegistry.All;
        if (!isParamMode)
            metrics = metrics.Where(m => !m.RequiresBenchmark);

        metrics = isParamMode
            ? metrics.OrderBy(m => m.Category switch
            {
                SurfaceMetricCategory.Performance => 0,
                SurfaceMetricCategory.Statistical => 1,
                _ => 2,
            })
            : metrics.OrderBy(m => m.Category switch
            {
                SurfaceMetricCategory.Aggregate => 0,
                SurfaceMetricCategory.Statistical => 1,
                _ => 2,
            });

        return metrics
            .Select(m => new SurfaceAxisOption(m.Id, m.Name, CategoryLabel(m.Category), m.Format, false, 0, 0, 0))
            .ToList();
    }

    private static string CategoryLabel(SurfaceMetricCategory c) => c switch
    {
        SurfaceMetricCategory.Performance => "Performance Metrics",
        SurfaceMetricCategory.Statistical => "Statistical & Risk",
        _ => "Bucket Aggregates",
    };
}
