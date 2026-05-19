using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

public sealed class ApexScalperStrategy : ITradingStrategy
{
    public string Id => "apex.scalper";
    public string DisplayName => "APEX microstructure scalper (composite, 8 signals)";
    public string Description =>
        "Weighted composite of 8 order-flow signals (Delta, VPIN, OBI shallow/deep, Footprint, Absorption, HVP, Tape Speed) with regime-adaptive weights, conflict filter, and dynamic HVP-anchored stops.";
}
