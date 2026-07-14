namespace TradingTerminal.Charts;

/// <summary>
/// Which parts of the price-chart panel are switched on. Everything the standalone Charts window shows is
/// here, and every piece of it is optional — an embedded panel is only as heavy as the features a strategy
/// actually uses.
/// <para>
/// These are <b>build-time</b> gates, not user toggles. A feature that is off is not merely hidden: its
/// chrome is collapsed and the corresponding view-model toggle is forced off, so the indicator is never
/// computed and never pushed to the chart page. The rail's checkboxes still decide what a switched-ON
/// feature draws.
/// </para>
/// <para>The candles themselves are the panel and are always drawn.</para>
/// </summary>
public sealed record ChartsPanelFeatures
{
    /// <summary>Symbol/timeframe selectors, presets, pause/export, the ? help and the ⚙ rail toggle. Off
    /// when something else owns the instrument — a strategy window picks the instrument, so its panels
    /// must not fight it.</summary>
    public bool Toolbar { get; init; } = true;

    /// <summary>The ⚙ options rail: chart type and the indicator checkboxes.</summary>
    public bool OptionsRail { get; init; } = true;

    /// <summary>SMA / EMA / RSI / MACD. <b>Off means they are never computed</b> — an embedded chart that
    /// only needs price does not pay to maintain four indicator series per bar.</summary>
    public bool Indicators { get; init; } = true;

    /// <summary>The status line at the foot of the panel.</summary>
    public bool Status { get; init; } = true;

    /// <summary>Everything — what the standalone Charts window uses.</summary>
    public static ChartsPanelFeatures Full { get; } = new();

    /// <summary>Bare price chart: no chrome, no indicators. The cheapest way to show a strategy the bars
    /// it is trading.</summary>
    public static ChartsPanelFeatures ChartOnly { get; } = new()
    {
        Toolbar = false,
        OptionsRail = false,
        Indicators = false,
        Status = false,
    };

    /// <summary>Price + indicators, no chrome — the default an authored bar-based strategy gets embedded
    /// in its live window.</summary>
    public static ChartsPanelFeatures Embedded { get; } = new()
    {
        Toolbar = false,
        OptionsRail = false,
        Status = false,
    };
}
