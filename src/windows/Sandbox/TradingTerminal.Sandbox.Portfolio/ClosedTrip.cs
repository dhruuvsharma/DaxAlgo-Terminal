namespace TradingTerminal.Sandbox.Portfolio;

/// <summary>One bounded closed-trip record.</summary>
internal readonly record struct ClosedTrip(
    double GrossProfitLoss,
    double PeakAbsoluteUnits,
    double PeakAbsoluteQuantity,
    long BarsHeld,
    bool HasR,
    double R);
