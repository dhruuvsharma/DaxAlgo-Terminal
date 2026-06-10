namespace TradingTerminal.Core.MarketData.AdvancedRegime;

/// <summary>
/// One timeframe column of the Advanced Live Market Regime dashboard. <see cref="Bucket"/> is the
/// aggregation window applied to the base bars (a <see cref="TimeSpan"/> rather than a
/// <c>BarSize</c> because the dashboard supports 20m/30m buckets that <c>BarSize</c> doesn't model).
/// </summary>
/// <param name="Label">Short display label, e.g. "1m", "20m", "1H", "1D".</param>
/// <param name="Bucket">Aggregation window. 1D uses <c>TimeSpan.FromDays(1)</c>.</param>
/// <param name="Enabled">Whether this column is shown / computed.</param>
public sealed record AdvancedTimeframe(string Label, TimeSpan Bucket, bool Enabled)
{
    /// <summary>The eight default timeframe columns, all enabled.</summary>
    public static IReadOnlyList<AdvancedTimeframe> Defaults { get; } = new[]
    {
        new AdvancedTimeframe("1m",  TimeSpan.FromMinutes(1),  true),
        new AdvancedTimeframe("3m",  TimeSpan.FromMinutes(3),  true),
        new AdvancedTimeframe("5m",  TimeSpan.FromMinutes(5),  true),
        new AdvancedTimeframe("15m", TimeSpan.FromMinutes(15), true),
        new AdvancedTimeframe("20m", TimeSpan.FromMinutes(20), true),
        new AdvancedTimeframe("30m", TimeSpan.FromMinutes(30), true),
        new AdvancedTimeframe("1H",  TimeSpan.FromHours(1),    true),
        new AdvancedTimeframe("1D",  TimeSpan.FromDays(1),     true),
    };
}

/// <summary>
/// The 18 indicator rows of the dashboard, in display order (top to bottom). Each row maps to one
/// cell per timeframe column.
/// </summary>
public enum AdvancedIndicatorRow
{
    Rsi,
    Macd,
    Cci,
    Ma9,
    Ma21,
    Ma50,
    TripleMa,
    Vwap,
    SuperTrend,
    Atr,
    AtrRegression,
    Std,
    PocPosition,
    TrendRange,
    Delta,
    CumulativeDelta,
    VolumeBuySell,
    Trend,
}

/// <summary>Five-level directional classification for a single dashboard cell.</summary>
public enum CellSignal
{
    StrongUp,
    Up,
    Neutral,
    Down,
    StrongDown,
}

/// <summary>
/// One indicator cell at one timeframe. <see cref="Glyph"/> is the rendered direction text
/// (e.g. "▲", "▼ OB", "▲▲ Stack"); <see cref="Value"/> is the raw numeric (formatting is a UI
/// concern). <see cref="ValueSuffix"/> hints how the UI should format the value ("%", "/8", or
/// null for a bare number).
/// </summary>
public sealed record AdvancedRegimeCell(
    AdvancedIndicatorRow Row,
    string Glyph,
    double? Value,
    CellSignal Signal,
    string? ValueSuffix = null);

/// <summary>
/// One timeframe column: its cells (one per enabled indicator row, in enum order), the headline
/// trend score (-8..+8) and the gauge needle angle in degrees (-90..+90).
/// </summary>
public sealed record AdvancedRegimeColumn(
    AdvancedTimeframe Timeframe,
    IReadOnlyList<AdvancedRegimeCell> Cells,
    int TrendScore,
    int NeedleAngleDegrees);

/// <summary>
/// A full dashboard snapshot: one column per requested timeframe. Immutable — a refresh swaps a
/// whole new snapshot in. <see cref="Unavailable"/> marks the degraded <see cref="Empty"/> state.
/// </summary>
public sealed record AdvancedRegimeSnapshot(
    string Symbol,
    IReadOnlyList<AdvancedRegimeColumn> Columns,
    DateTime GeneratedAtUtc,
    bool Unavailable)
{
    public static AdvancedRegimeSnapshot Empty { get; } = new(
        Symbol: string.Empty,
        Columns: Array.Empty<AdvancedRegimeColumn>(),
        GeneratedAtUtc: DateTime.MinValue,
        Unavailable: true);
}
