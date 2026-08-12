namespace TradingTerminal.OrderBook;

/// <summary>
/// Which parts of the Order Book panel are switched on. Everything the standalone window shows is here,
/// and every piece of it is optional — an embedded panel is only as heavy as the features a strategy
/// actually uses.
/// <para>
/// These are <b>build-time</b> gates, not user toggles. A feature that is off is not merely hidden: its
/// section is collapsed, its rendering pass is skipped, and (for the ML forecaster) its model is never
/// trained at all. The user-facing toggles in the options rail still live on the view-model and control
/// what a switched-ON feature draws.
/// </para>
/// </summary>
public sealed record OrderBookPanelFeatures
{
    /// <summary>Instrument picker, presets, pause/export/snapshot. Off when something else owns the
    /// instrument — a strategy window picks the instrument, so its panels must not fight it.</summary>
    public bool Toolbar { get; init; } = true;

    /// <summary>The microstructure strip: bid/ask/spread, imbalance, sweep cost, cumulative delta.</summary>
    public bool Analytics { get; init; } = true;

    /// <summary>The ML micro-forecast: event-probability chips, the scoreboard, and the violet predicted
    /// path in the heatmap gutter. <b>Off means the predictor is never created</b> — no training, no
    /// per-tick inference, no cost.</summary>
    public bool MlForecast { get; init; } = true;

    /// <summary>The time × price liquidity heatmap (with trade dots, microprice line, imbalance lane).
    /// The most expensive thing in the panel: it repaints thousands of cells per frame.</summary>
    public bool Heatmap { get; init; } = true;

    /// <summary>The classic depth ladder.</summary>
    public bool Ladder { get; init; } = true;

    /// <summary>The ⚙ options rail (per-feature user toggles, sweep size, heat window…).</summary>
    public bool OptionsRail { get; init; } = true;

    /// <summary>The status line at the foot of the panel.</summary>
    public bool Status { get; init; } = true;

    /// <summary>Everything — what the standalone Order Book window uses.</summary>
    public static OrderBookPanelFeatures Full { get; } = new();

    /// <summary>Just the ladder: the cheapest useful book view, for a strategy that only needs to see
    /// the levels it is trading against.</summary>
    public static OrderBookPanelFeatures LadderOnly { get; } = new()
    {
        Toolbar = false,
        Analytics = false,
        MlForecast = false,
        Heatmap = false,
        OptionsRail = false,
    };

    /// <summary>Ladder + heatmap + the microstructure strip, with no ML and no chrome — the default an
    /// authored depth strategy gets embedded in its live window.</summary>
    public static OrderBookPanelFeatures Embedded { get; } = new()
    {
        Toolbar = false,
        MlForecast = false,
        OptionsRail = false,
        Status = false,
    };
}
