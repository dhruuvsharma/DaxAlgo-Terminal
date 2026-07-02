namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>Top-level surface mode — decides what the X/Y axes can be bound to. Both modes are
/// pure statistics of realized returns (no strategy/portfolio simulation).</summary>
public enum SurfaceMode
{
    /// <summary>X/Y are calendar buckets (hour of day × day of week, …); Z aggregates the
    /// returns that fell into each bucket. Seasonality surfaces.</summary>
    TemporalAggregation,
    /// <summary>X/Y bucket conditioning variables (prior-return bin, volatility bucket,
    /// volume decile, time lag); Z is a statistic of the NEXT-period returns in each cell.</summary>
    CrossSectional,
}

/// <summary>Which slot of the surface an axis option is offered for.</summary>
public enum SurfaceAxisRole { X, Y, Z, Color }

/// <summary>
/// One entry in an axis dropdown. <see cref="RangeEditable"/> gates the Min/Max/Step inputs
/// (linear bins are editable; calendar and quantile buckets are fixed). For Z/Color roles the
/// id is a <see cref="SurfaceMetricRegistry"/> metric id.
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

/// <summary>A temporal calendar bucket axis (fixed bucket count with labels).</summary>
public sealed record TemporalAxisDefinition(
    string Id,
    string Name,
    string[] Labels,
    Func<DateTime, int> Selector)
{
    public int BucketCount => Labels.Length;
}

/// <summary>What a cross-sectional axis conditions on.</summary>
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

/// <summary>A cross-sectional conditioning axis.</summary>
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
/// legal options. Z/Color options delegate to <see cref="SurfaceMetricRegistry"/> — bucket
/// aggregates first, then the statistical/risk functions.
/// </summary>
public static class SurfaceAxisCatalog
{
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
    /// options are metric-registry entries (aggregates first, then statistical).</summary>
    public static IReadOnlyList<SurfaceAxisOption> OptionsFor(SurfaceMode mode, SurfaceAxisRole role)
    {
        if (role is SurfaceAxisRole.X or SurfaceAxisRole.Y)
        {
            return mode switch
            {
                SurfaceMode.TemporalAggregation => TemporalAxes
                    .Select(a => new SurfaceAxisOption(a.Id, a.Name, "Calendar Buckets", SurfaceAxisFormat.Integer, false, 0, a.BucketCount - 1, 1))
                    .ToList(),
                _ => CrossSectionAxes
                    .Select(a => new SurfaceAxisOption(a.Id, a.Name, "Conditioning Variables", a.Format, a.RangeEditable, a.DefaultMin, a.DefaultMax, a.DefaultStep))
                    .ToList(),
            };
        }

        return SurfaceMetricRegistry.All
            .OrderBy(m => m.Category == SurfaceMetricCategory.Aggregate ? 0 : 1)
            .Select(m => new SurfaceAxisOption(m.Id, m.Name, CategoryLabel(m.Category), m.Format, false, 0, 0, 0))
            .ToList();
    }

    private static string CategoryLabel(SurfaceMetricCategory c) => c switch
    {
        SurfaceMetricCategory.Aggregate => "Bucket Aggregates",
        _ => "Statistical & Risk",
    };
}
