namespace TradingTerminal.Core.Strategies.Apex;

/// <summary>
/// How the engine closes a bar.
/// <list type="bullet">
///   <item><see cref="Time"/> — fixed wall-clock candles (the classic mode). A bar rolls when the
///   candle interval elapses. Default 1-minute.</item>
///   <item><see cref="Volume"/> — constant-volume bars: a bar rolls once its accumulated traded
///   volume reaches <see cref="ApexV2Options.VolumeBarSize"/>. Bars then carry roughly equal
///   information content, which stabilises the flow estimators across quiet and busy regimes. The
///   selected candle interval still serves as the <em>reference span</em> for TTLs and the
///   √(span/span₀) distance scaling.</item>
/// </list>
/// </summary>
public enum ApexBarMode
{
    Time,
    Volume,
}

/// <summary>
/// TTL multiplier set for the v2 signals. TTLs are expressed as a dimensionless multiple α of the
/// reference bar span <see cref="ApexV2Options.ReferenceSpanSeconds"/>, so they scale with the bar
/// span rather than being hard-coded millisecond magic numbers: <c>TTL = α · span₀</c> (further
/// scaled by regime). Faster signals (OBI, tape speed) use α &lt; 1; slow structural signals
/// (POC, lines) use α &gt; 1.
/// </summary>
/// <param name="DeltaFootprint">α for the delta and footprint signals (default 1.5).</param>
/// <param name="ObiTapeSpeed">α for the OBI and tape-speed signals (default 0.5).</param>
/// <param name="PocLines">α for the POC and line-fit signals (default 3.0).</param>
public readonly record struct ApexTtlMultipliers(
    double DeltaFootprint = 1.5,
    double ObiTapeSpeed = 0.5,
    double PocLines = 3.0)
{
    /// <summary>The documented v2 defaults. Constructed explicitly because a record-struct
    /// parameterless <c>new()</c> zero-initialises and does <em>not</em> apply the primary
    /// constructor's default values — a bare <c>new()</c> would make every TTL 0, which marks every
    /// signal stale the instant it is computed and pins the composite at 0.</summary>
    public static ApexTtlMultipliers Default => new(DeltaFootprint: 1.5, ObiTapeSpeed: 0.5, PocLines: 3.0);
}

/// <summary>
/// Configuration for the Apex v2 strategy. <b>Defaults are dimensionless coefficients plus a single
/// reference span (span₀), not absolute magic numbers</b>, so the strategy self-scales to any bar
/// span and instrument via the documented scaling laws:
/// <list type="bullet">
/// <item><b>Price distances</b> scale like √(span/span₀) — Brownian diffusion: the typical move
/// over a bar grows as the square root of the bar's duration. Stop/target/wedge thresholds are
/// stored as coefficients on σ·√(span/span₀).</item>
/// <item><b>Volume thresholds</b> scale <b>linearly in median bar volume</b> — twice the volume per
/// bar ⇒ twice the absolute threshold. Stored as fractions of median bar volume.</item>
/// <item><b>TTLs</b> scale linearly in span via the α multipliers: TTL = α·span₀.</item>
/// </list>
/// The engine multiplies these coefficients by the live span₀, the rolling σ, and the median bar
/// volume to recover absolute magnitudes.
/// </summary>
public sealed record ApexV2Options
{
    // ── Reference span (span₀) ───────────────────────────────────────────────────────
    /// <summary>
    /// Reference bar span in seconds (span₀). Default 60s (1-minute); the MES/MNQ working range is
    /// 1m and up. All price-distance coefficients are defined relative to a bar of this span and
    /// rescaled by √(actualSpan/span₀). Sub-minute spans are no longer a supported default.
    /// </summary>
    public double ReferenceSpanSeconds { get; init; } = 60.0;

    // ── Bar mode (time vs constant-volume) ─────────────────────────────────────────────
    /// <summary>How a bar is closed — fixed time candles (default) or constant-volume bars.</summary>
    public ApexBarMode BarMode { get; init; } = ApexBarMode.Time;

    /// <summary>
    /// Contracts/shares per bar when <see cref="BarMode"/> is <see cref="ApexBarMode.Volume"/>. A bar
    /// rolls once its accumulated traded volume reaches this. Ignored in time mode. Default 2000.
    /// </summary>
    public long VolumeBarSize { get; init; } = 2_000;

    // ── TTL ──────────────────────────────────────────────────────────────────────────
    /// <summary>Per-signal TTL multipliers (α): TTL = α · span₀, then regime-scaled.</summary>
    public ApexTtlMultipliers TtlMultipliers { get; init; } = ApexTtlMultipliers.Default;

    // ── Estimator windows / forgetting ─────────────────────────────────────────────────
    /// <summary>EW regression forgetting factor δ ∈ (0, 1] (default 0.9).</summary>
    public double EwDelta { get; init; } = 0.9;

    /// <summary>
    /// Newey-West HAC lag. Null (default) ⇒ the automatic plug-in rule
    /// L = floor(4·(n/100)^(2/9)); a non-null value pins a fixed lag.
    /// </summary>
    public int? NeweyWestLag { get; init; }

    /// <summary>
    /// Rolling window (in bars) for the Ledoit-Wolf covariance and the information-coefficient
    /// estimation. Default 1500 — a long window stabilises the Σ⁻¹·IC weight vector.
    /// </summary>
    public int CovarianceWindow { get; init; } = 1_500;

    /// <summary>
    /// Recompute the Σ⁻¹·IC weights only every N completed bars (rather than every bar). Default 50:
    /// the weights drift slowly over the 1500-bar window, so refreshing every 50 bars keeps them
    /// stable and saves the per-bar Ledoit-Wolf + IC + linear-solve cost.
    /// </summary>
    public int WeightRecalcEveryBars { get; init; } = 50;

    /// <summary>Forward-return horizon (in bars) used by the IC and isotonic calibration.</summary>
    public int ForwardReturnHorizon { get; init; } = 5;

    /// <summary>Rolling window (in bars) for the Kyle-lambda 2SLS residual regression.</summary>
    public int KyleWindow { get; init; } = 50;

    // ── Isotonic calibration ───────────────────────────────────────────────────────────
    /// <summary>
    /// Minimum observations before the isotonic calibration map is trusted (else bootstrap mode).
    /// Default 500 — the calibration only earns authority once it has a robust sample.
    /// </summary>
    public int IsotonicMinSamples { get; init; } = 500;

    /// <summary>
    /// Total-sample threshold below which the calibration runs in bootstrap mode (g(C) blended
    /// toward a neutral prior). Configurable.
    /// </summary>
    public int BootstrapSampleThreshold { get; init; } = 500;

    // ── Sizing / risk (dimensionless) ──────────────────────────────────────────────────
    /// <summary>
    /// Default fraction of the full Kelly stake to deploy (default 0.25 — quarter-Kelly). Applies to
    /// the London-only and NY-only sessions; the overlap and Asian sessions use their own caps below.
    /// </summary>
    public double KellyFraction { get; init; } = 0.25;

    /// <summary>Kelly cap during the London/NY overlap — the most liquid window (default 0.25).</summary>
    public double KellyFractionOverlap { get; init; } = 0.25;

    /// <summary>Kelly cap during the (thinner, gappier) Asian session — sized down (default 0.10).</summary>
    public double KellyFractionAsian { get; init; } = 0.10;

    /// <summary>Risk fraction of equity per trade (e.g. 0.005 = 0.5%).</summary>
    public double RiskFraction { get; init; } = 0.005;

    // ── Footprint geometry ─────────────────────────────────────────────────────────────
    /// <summary>Target rows per footprint bar (default 20) ⇒ tickSize ≈ ATR_bar / 20.</summary>
    public int RowsPerBarTarget { get; init; } = 20;

    /// <summary>
    /// Explicit price-bucket size override (price units). Null (default) ⇒ derive from ATR via
    /// <see cref="RowsPerBarTarget"/>, clamped to the instrument tick.
    /// </summary>
    public double? TickSizeOverride { get; init; }

    /// <summary>Diagonal-imbalance ratio (default 3:1).</summary>
    public double ImbalanceRatio { get; init; } = 3.0;

    // ── VPIN / volume buckets ──────────────────────────────────────────────────────────
    /// <summary>VPIN lookback in volume buckets.</summary>
    public int VpinLookbackBuckets { get; init; } = 50;

    // ── Price-distance coefficients (× σ·√(span/span₀)) ────────────────────────────────
    /// <summary>Stop distance as a multiple of σ·√(span/span₀). Dimensionless.</summary>
    public double StopSigmaCoefficient { get; init; } = 1.5;

    /// <summary>Target distance as a multiple of σ·√(span/span₀). Dimensionless.</summary>
    public double TargetSigmaCoefficient { get; init; } = 2.25;

    /// <summary>
    /// Value-area half-width as a multiple of σ·√(span/span₀); the value-deviation z_p is
    /// price-distance / this width. Dimensionless.
    /// </summary>
    public double ValueAreaSigmaCoefficient { get; init; } = 1.0;

    // ── Volume-threshold fractions (× median bar volume) ───────────────────────────────
    /// <summary>Absorption volume floor as a fraction of median bar volume. Dimensionless.</summary>
    public double AbsorptionVolumeFraction { get; init; } = 0.5;

    // ── Gate / composite ───────────────────────────────────────────────────────────────
    /// <summary>Composite threshold |C| must exceed to trade.</summary>
    public double CompositeThreshold { get; init; } = 1.0;

    /// <summary>Regime multipliers applied to the composite (label → multiplier).</summary>
    public IReadOnlyDictionary<string, double> RegimeMultipliers { get; init; } =
        new Dictionary<string, double>
        {
            ["TrendingBull"] = 1.2,
            ["TrendingBear"] = 1.2,
            ["Ranging"] = 1.0,
            ["HighVolatility"] = 0.5,
            ["Undefined"] = 1.0,
        };

    // ── Session gates ──────────────────────────────────────────────────────────────────
    /// <summary>Trade the Asian session.</summary>
    public bool TradeAsian { get; init; }

    /// <summary>Trade the London session.</summary>
    public bool TradeLondon { get; init; } = true;

    /// <summary>Trade the New York session.</summary>
    public bool TradeNewYork { get; init; } = true;

    /// <summary>Trade the London/NY overlap.</summary>
    public bool TradeLondonNy { get; init; } = true;

    // ── Cooldown / kill-switch ─────────────────────────────────────────────────────────
    /// <summary>Minimum seconds between trades (cooldown).</summary>
    public int CooldownSeconds { get; init; } = 30;

    /// <summary>Kill-switch: max daily loss as a fraction of starting equity (e.g. 0.02 = 2%).</summary>
    public double MaxDailyLossFraction { get; init; } = 0.02;

    /// <summary>Kill-switch: max drawdown as a fraction of peak equity (e.g. 0.05 = 5%).</summary>
    public double MaxDrawdownFraction { get; init; } = 0.05;

    // ── Cost model ─────────────────────────────────────────────────────────────────────
    /// <summary>Per-side commission in price units (or currency, engine-interpreted).</summary>
    public double CommissionPerSide { get; init; }

    /// <summary>Assumed spread cost in ticks paid on entry (dimensionless, × tick size).</summary>
    public double SpreadCostTicks { get; init; } = 1.0;

    /// <summary>
    /// Slippage coefficient: expected slippage ≈ this × λ̂ × workingSize (Kyle linear-impact
    /// model). Dimensionless multiplier on the estimated impact.
    /// </summary>
    public double SlippageCoefficient { get; init; } = 1.0;

    // ── Predicted node migration (Kalman POC forecaster) ───────────────────────────────
    /// <summary>How many bars ahead the Kalman POC predictor forecasts (default 5).</summary>
    public int PredictedNodeHorizon { get; init; } = 5;

    /// <summary>
    /// Minimum prediction confidence (1 − σ²_pred/σ²_bar) before the predicted-node levels are used
    /// for dynamic stop/target placement. Below this the bracket falls back to the structure/ATR
    /// logic. Default 0.3.
    /// </summary>
    public double PredictionExitMinConfidence { get; init; } = 0.3;

    /// <summary>Kalman process (acceleration) noise q for the POC predictors. Larger ⇒ faster adapt.</summary>
    public double KalmanProcessNoise { get; init; } = 1e-4;

    /// <summary>Kalman measurement noise r for the POC observations. Larger ⇒ more smoothing.</summary>
    public double KalmanMeasurementNoise { get; init; } = 1e-2;

    // ── Hawkes tape-intensity parameters ───────────────────────────────────────────────
    /// <summary>Hawkes baseline rate μ (default 0 — a pure self-excitation measure for the z-score).</summary>
    public double HawkesBaselineMu { get; init; }

    /// <summary>Hawkes excitation α per trade. Keep α &lt; β for stationarity (default 0.3).</summary>
    public double HawkesAlpha { get; init; } = 0.3;

    /// <summary>Hawkes decay β (per second). Excitation half-life ≈ ln2/β seconds (default 0.5).</summary>
    public double HawkesBeta { get; init; } = 0.5;

    /// <summary>The v2 defaults.</summary>
    public static ApexV2Options Default => new();
}
