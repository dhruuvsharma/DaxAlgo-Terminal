using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.SigmaIcFlow;

public sealed class SigmaIcFlowStrategy : ITradingStrategy
{
    public string Id => "sigma.ic.flow";
    public string DisplayName => "Σ⁻¹·IC Order-Flow Optimizer (tape-primary composite)";
    public string Description =>
        "Tape-primary composite of 11 order-flow signals: Delta (bar Δ + acceleration z), VPIN (constant-volume buckets), Footprint stacked imbalance, Tape Speed (arrival-rate z with up-tick gate), Kyle-λ residual (ε_cum z-score, λ̂ as thinness feature), triple regression lines over buy/sell centroid + POC (initiative contrast, control coordinate ρ + dρ/dt, wedge w + dw/dt, value deviation z_p), CVD divergence, and OBI (live depth only). Weights: Ledoit-Wolf Σ⁻¹·IC; entry gate: isotonic g(C) ≥ spread + 2·fee + E[slip|C]; quarter-Kelly sizing, λ̂-scaled. Bootstrap mode active until calibration threshold is met. Degrades gracefully to synthetic L1 (q ≈ 0.4) when real trade tape is unavailable.";

    /// <summary>
    /// Requires L1 quotes (tick updates + synthetic fallback), OHLCV bars (warm-up seeding),
    /// L2 depth (OBI signal; degrades silently when absent), and the real trade tape
    /// (primary signal input via <c>OnTradeAsync</c>; engine falls back to L1 synthetic
    /// prints tagged <see cref="TradingTerminal.Core.MarketData.FeedQuality.SyntheticL1"/>
    /// when no tape arrives — all signal confidences are scaled by q ≈ 0.4 in that mode).
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars |
        StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;
}
