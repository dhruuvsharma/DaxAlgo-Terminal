using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

public sealed class ApexScalperStrategy : ITradingStrategy
{
    public string Id => "apex.scalper";
    public string DisplayName => "APEX microstructure scalper (composite, 8 signals)";
    public string Description =>
        "Weighted composite of 8 order-flow signals (Delta, VPIN, OBI shallow/deep, Footprint, Absorption, HVP, Tape Speed) with regime-adaptive weights, conflict filter, and dynamic HVP-anchored stops.";

    /// <summary>
    /// Requires L1 quotes and OHLCV bars (base signals), plus L2 depth for the
    /// OBI shallow/deep signals (<c>OnDepthAsync</c> is overridden in the engine-side
    /// <c>Infrastructure.Backtest.Strategies.ApexScalperStrategy</c>). Trade tape is
    /// not consumed — no <c>OnTradeAsync</c> override exists.
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth;
}
