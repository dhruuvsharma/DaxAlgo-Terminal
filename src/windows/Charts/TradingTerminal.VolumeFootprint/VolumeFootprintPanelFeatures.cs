namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// Which parts of the Volume Footprint panel are switched on. Everything the standalone window draws is
/// here, and every piece of it is optional — an embedded panel is only as heavy as the features a
/// strategy actually uses.
/// <para>
/// These are <b>build-time</b> gates, not user toggles. A feature that is off is not merely hidden: the
/// chrome is collapsed, the corresponding view-model toggle is forced off so the renderer skips that
/// pass entirely, and (for the ML forecaster) the model is never trained at all. The user-facing toggles
/// in the toolbar menus still decide what a switched-ON feature draws.
/// </para>
/// <para>
/// The cluster grid itself — price ladders, cells, POC boxes — is the panel and is always drawn.
/// </para>
/// </summary>
public sealed record VolumeFootprintPanelFeatures
{
    /// <summary>Instrument/timeframe selectors, the Regression · Overlays · Display menus, zoom, the ?
    /// help popup. Off when something else owns the instrument — a strategy window picks the instrument,
    /// so its panels must not fight it.</summary>
    public bool Toolbar { get; init; } = true;

    /// <summary>The floating stats overlay (POC slopes, CVD, ticks/sec, ML vs regression scoreboard).</summary>
    public bool Stats { get; init; } = true;

    /// <summary>The legend + status footer.</summary>
    public bool Legend { get; init; } = true;

    /// <summary>The regression fits over the POC path (linear/quadratic/cubic/Theil-Sen/exp/log/LOWESS)
    /// and the ghost bars that extrapolate them forward. Off skips the fitting and the ghost pass.</summary>
    public bool Regression { get; init; } = true;

    /// <summary>The violet ML ghost bars (online-learned next-bar POC/volume/delta forecast).
    /// <b>Off means the predictor is never created</b> — no warm-start replay over stored tape, no
    /// per-bar training, no inference.</summary>
    public bool MlForecast { get; init; } = true;

    /// <summary>Diagonal 3:1 bid/ask imbalance outlines and the stacked-run markers.</summary>
    public bool Imbalances { get; init; } = true;

    /// <summary>The ~70% value-area band (VAH ↔ VAL) shaded behind each bar.</summary>
    public bool ValueArea { get; init; } = true;

    /// <summary>The composite session volume profile in the right gutter.</summary>
    public bool VolumeProfile { get; init; } = true;

    /// <summary>Everything — what the standalone Volume Footprint window uses.</summary>
    public static VolumeFootprintPanelFeatures Full { get; } = new();

    /// <summary>Bare cluster grid: cells, POC and nothing else. The cheapest footprint, for a strategy
    /// that only needs to see the order flow it is reading.</summary>
    public static VolumeFootprintPanelFeatures ChartOnly { get; } = new()
    {
        Toolbar = false,
        Stats = false,
        Legend = false,
        Regression = false,
        MlForecast = false,
        Imbalances = false,
        ValueArea = false,
        VolumeProfile = false,
    };

    /// <summary>Cells + imbalances + value area + stats, with no chrome, no regression and no ML — the
    /// default an authored order-flow strategy gets embedded in its live window.</summary>
    public static VolumeFootprintPanelFeatures Embedded { get; } = new()
    {
        Toolbar = false,
        Legend = false,
        Regression = false,
        MlForecast = false,
    };
}
