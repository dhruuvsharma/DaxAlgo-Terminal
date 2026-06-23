namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Classification of a 1-minute order-flow pressure event, emitted per instrument by the
/// "1-Minute Order Flow Pressure Map" tool. Absorption = heavy volume that the price barely
/// moves through (liquidity soaking the flow); breakthrough/breakdown = heavy volume that
/// drives price through a level in the trade direction.
/// </summary>
public enum PressureSignal
{
    /// <summary>No notable pressure this candle.</summary>
    Neutral,

    /// <summary>Heavy buying absorbed near the highs — price held up despite supply.</summary>
    BullishAbsorption,

    /// <summary>Heavy selling absorbed near the lows — price held despite the offer.</summary>
    BearishAbsorption,

    /// <summary>Heavy volume drove price up through the candle (demand overpowered).</summary>
    BullishBreakthrough,

    /// <summary>Heavy volume drove price down through the candle (supply overpowered).</summary>
    BearishBreakdown,
}

/// <summary>Which constituent universe the pressure map scans.</summary>
public enum PressureUniverse
{
    /// <summary>The ~100 S&amp;P 100 large-caps (default — fits the subscription budget).</summary>
    Sp100,

    /// <summary>The broader S&amp;P 500 set (needs a larger subscription budget).</summary>
    Sp500,
}

/// <summary>UI filter for which classes of pressure signal to surface.</summary>
public enum SignalTypeFilter
{
    /// <summary>Show every classified signal.</summary>
    All,

    /// <summary>Absorption events only (bullish or bearish).</summary>
    Absorption,

    /// <summary>Bullish breakthrough events only.</summary>
    Breakthrough,

    /// <summary>Bearish breakdown events only.</summary>
    Breakdown,
}

/// <summary>
/// Strongly-typed settings for the "1-Minute Order Flow Pressure Map" tool, bound from the
/// <see cref="SectionName"/> configuration section. The tool tracks the last N completed
/// 1-minute candles across an S&amp;P universe and classifies each instrument's most recent
/// candle into a <see cref="PressureSignal"/> using relative volume, candle close position,
/// and a price-impact ratio (range moved per unit of relative volume).
/// </summary>
public sealed record OrderFlowPressureMapOptions
{
    /// <summary>Configuration section this options record binds from.</summary>
    public const string SectionName = "OrderFlowPressureMap";

    /// <summary>Which constituent universe to scan. Defaults to the S&amp;P 100 so the default
    /// subscription budget (<see cref="MaxConcurrentSubscriptions"/>) covers the whole set.</summary>
    public PressureUniverse Universe { get; init; } = PressureUniverse.Sp100;

    /// <summary>Minimum relative volume (current 1m volume vs the per-minute baseline) for a
    /// candle to be a pressure candidate. 2.0 = at least twice the typical volume.</summary>
    public double MinRelativeVolume { get; init; } = 2.0;

    /// <summary>UI filter for which signal classes to display.</summary>
    public SignalTypeFilter SignalFilter { get; init; } = SignalTypeFilter.All;

    /// <summary>When true, only instruments whose latest candle is non-neutral are listed.</summary>
    public bool ShowOnlyActive { get; init; } = false;

    /// <summary>Throttle (ms) for refreshing the grid/heatmap on the UI thread.</summary>
    public int GuiRefreshMs { get; init; } = 1000;

    /// <summary>Number of completed 1-minute candles shown across the map (last 60 minutes).</summary>
    public int DisplayWindowColumns { get; init; } = 60;

    /// <summary>Lookback (trading days) used to compute each instrument's per-minute volume
    /// baseline (the "normal" minute volume that relative volume is measured against).</summary>
    public int BaselineDays { get; init; } = 20;

    /// <summary>Short rolling window (minutes) used as an intraday baseline that adapts to the
    /// current session's pace, blended with the longer <see cref="BaselineDays"/> baseline.</summary>
    public int ShortBaselineMinutes { get; init; } = 30;

    /// <summary>Period for the ATR(14) used to normalise candle range into price impact.</summary>
    public int Atr14Period { get; init; } = 14;

    /// <summary>Hard cap on concurrent live subscriptions. The default 100 matches the S&amp;P 100
    /// universe; raise it before switching <see cref="Universe"/> to <see cref="PressureUniverse.Sp500"/>.</summary>
    public int MaxConcurrentSubscriptions { get; init; } = 100;

    /// <summary>Subscribe to L2 depth for book-imbalance scoring. Off by default — depth is
    /// expensive and only IB carries it broadly.</summary>
    public bool EnableDepth { get; init; } = false;

    // ---- Classification thresholds ---------------------------------------------------------

    /// <summary>Price-impact ratio at or below which heavy volume counts as absorption (price
    /// barely moved per unit of relative volume).</summary>
    public double AbsorptionMaxPriceImpact { get; init; } = 0.35;

    /// <summary>Price-impact ratio at or above which heavy volume counts as a breakthrough/breakdown
    /// (price moved decisively per unit of relative volume).</summary>
    public double BreakthroughMinPriceImpact { get; init; } = 0.60;

    /// <summary>Candle close position (0 = low, 1 = high) at or above which absorption is bullish.</summary>
    public double BullishAbsorptionMinCandlePosition { get; init; } = 0.55;

    /// <summary>Candle close position at or below which absorption is bearish.</summary>
    public double BearishAbsorptionMaxCandlePosition { get; init; } = 0.45;

    /// <summary>Candle close position at or above which a breakthrough is bullish.</summary>
    public double BullishBreakthroughMinCandlePosition { get; init; } = 0.75;

    /// <summary>Candle close position at or below which a breakdown is bearish.</summary>
    public double BearishBreakdownMaxCandlePosition { get; init; } = 0.25;

    /// <summary>Minimum absolute book imbalance (bid vs ask depth, -1..1) to factor into the
    /// classification when <see cref="EnableDepth"/> is on.</summary>
    public double BookImbalanceThreshold { get; init; } = 0.10;
}
