using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Strategies.Apex;

/// <summary>
/// Per-signal score line for the v2 Apex dashboard. Carries the score, its confidence, the
/// inferred direction, validity, and the live TTL state so the UI can show how much of each
/// signal's freshness budget remains before it decays out of the composite.
/// </summary>
/// <param name="Name">Signal name (e.g. "DELTA", "FOOTPRINT", "OBI", "TAPE_SPEED", "KYLE").</param>
/// <param name="Score">Signed signal score.</param>
/// <param name="Confidence">Confidence ∈ [0, 1].</param>
/// <param name="Direction">+1 / 0 / −1.</param>
/// <param name="IsValid">Whether the signal currently contributes (warm and not stale).</param>
/// <param name="AgeMs">Milliseconds since the signal was last generated.</param>
/// <param name="TtlMs">The signal's TTL budget in ms (regime-scaled by the α multipliers).</param>
public sealed record ApexSignalState(
    string Name,
    double Score,
    double Confidence,
    int Direction,
    bool IsValid,
    double AgeMs,
    double TtlMs)
{
    /// <summary>Fraction of the TTL budget remaining ∈ [0, 1]; 0 once stale.</summary>
    public double TtlRemaining => TtlMs > 1e-9 ? Math.Clamp(1.0 - AgeMs / TtlMs, 0.0, 1.0) : 0.0;
}

/// <summary>
/// Flat v2 diagnostic snapshot of the Apex strategy state, built per tick for the live dashboard
/// and pushed into a history ring on each completed bar. This is the Core contract the engine
/// emits and the view-model binds; it intentionally drops the removed v1 signals (HVP slope,
/// delta-divergence sub-score, the Delta-vs-VPIN conflict flag) and adds the v2 estimator outputs
/// (Kyle λ, ε_cum + z, the buy/sell/POC line-fit triple, wedge/control geometry, value deviation,
/// the estimated weight vector, the isotonic g(C) and cost/EV fields).
/// </summary>
/// <param name="TimestampUtc">Snapshot time.</param>
/// <param name="Mid">Mid price at the snapshot.</param>
/// <param name="Signals">Per-signal state lines (score + confidence + TTL).</param>
/// <param name="Composite">Composite score C.</param>
/// <param name="CompositeDirection">+1 / 0 / −1 from the composite.</param>
/// <param name="Regime">Regime label.</param>
/// <param name="FeedQuality">Quality q of the footprint feed feeding the snapshot.</param>
/// <param name="BootstrapMode">
/// True when the isotonic calibration has too little history (sample counts below the configured
/// threshold) and g(C) is operating in bootstrap / low-confidence mode.
/// </param>
/// <param name="KyleLambda">λ̂: estimated price impact (return per unit signed flow).</param>
/// <param name="EpsilonCum">ε_cum: cumulative Kyle residual (move unexplained by flow).</param>
/// <param name="EpsilonCumZ">z-score of <see cref="EpsilonCum"/> against its rolling distribution.</param>
/// <param name="BuyLine">Line fit of the buy-volume centroid b̄ across recent bars.</param>
/// <param name="SellLine">Line fit of the sell-volume centroid s̄ across recent bars.</param>
/// <param name="PocLine">Line fit of the POC across recent bars.</param>
/// <param name="ControlCoordinate">ρ: the control coordinate (position within the buy/sell wedge).</param>
/// <param name="ControlVelocity">dρ/dt: rate of change of the control coordinate.</param>
/// <param name="WedgeWidth">w: vertical gap between the buy and sell centroid lines.</param>
/// <param name="WedgeWidthVelocity">dw/dt: rate of change of the wedge width (expanding/contracting).</param>
/// <param name="ValueDeviationZ">z_p: deviation of price from the value area, in σ units.</param>
/// <param name="Weights">Estimated combination weights, name → weight (Σ|wᵢ| = 1; negatives allowed).</param>
/// <param name="CalibratedExpectedReturn">g(C): isotonic-calibrated expected forward return at C.</param>
/// <param name="ConditionalSlippage">Estimated slippage for the working size (price units).</param>
/// <param name="SessionPnl">Cost-inclusive session P&amp;L.</param>
/// <param name="TradeAllowed">Whether the confirmation gate currently permits a trade.</param>
/// <param name="KillSwitch">Whether the kill-switch has tripped.</param>
/// <param name="Position">Signed position (+ long / − short / 0 flat).</param>
/// <param name="PredictedBuyPoc">Kalman-forecast buy-POC price <see cref="PredictionHorizonBars"/> bars ahead (0 if not yet predictable).</param>
/// <param name="PredictedSellPoc">Kalman-forecast sell-POC price ahead.</param>
/// <param name="PredictedTotalPoc">Kalman-forecast total-POC price ahead.</param>
/// <param name="PredictedWedgeWidth">Forecast wedge width Ŵ = P̂_buy − P̂_sell (expanding &gt; current ⇒ trend; converging ⇒ coil).</param>
/// <param name="PredictionConfidence">Prediction confidence ∈ [0, 1] = 1 − σ²_pred/σ²_bar, 0 when the forecast variance is too high.</param>
/// <param name="PredictionHorizonBars">Forecast horizon n (bars) used for the predicted POCs.</param>
public sealed record ApexSnapshotV2(
    DateTime TimestampUtc,
    double Mid,
    IReadOnlyList<ApexSignalState> Signals,
    double Composite,
    int CompositeDirection,
    string Regime,
    FeedQuality FeedQuality,
    bool BootstrapMode,
    double KyleLambda,
    double EpsilonCum,
    double EpsilonCumZ,
    ApexLineFit BuyLine,
    ApexLineFit SellLine,
    ApexLineFit PocLine,
    double ControlCoordinate,
    double ControlVelocity,
    double WedgeWidth,
    double WedgeWidthVelocity,
    double ValueDeviationZ,
    IReadOnlyDictionary<string, double> Weights,
    double CalibratedExpectedReturn,
    double ConditionalSlippage,
    double SessionPnl,
    bool TradeAllowed,
    bool KillSwitch,
    long Position,
    double PredictedBuyPoc = 0,
    double PredictedSellPoc = 0,
    double PredictedTotalPoc = 0,
    double PredictedWedgeWidth = 0,
    double PredictionConfidence = 0,
    int PredictionHorizonBars = 0);
