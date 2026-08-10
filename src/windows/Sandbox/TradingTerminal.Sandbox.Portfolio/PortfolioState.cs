namespace TradingTerminal.Sandbox.Portfolio;

/// <summary>The complete staged or committed core state.</summary>
internal struct PortfolioState
{
    public double PositionUnits;
    public double PositionQuantity;
    public double AverageEntryPrice;
    public long BarsHeld;

    public double RealizedGrossProfitLoss;
    public double CommissionTotal;
    public double SlippageTotal;
    public double LastSampledEquity;
    public double EquityPeak;
    public double MaximumDrawdown;

    public long LifetimeClosedTripCount;
    public long LifetimeWinningTripCount;
    public long LifetimeLosingTripCount;

    public double TripGrossProfitLoss;
    public double TripEntryCommission;
    public double TripEntrySlippage;
    public double TripExitCommission;
    public double TripExitSlippage;
    public double TripPeakAbsoluteUnits;
    public double TripPeakAbsoluteQuantity;

    public bool HasCapturedR;
    public double CapturedR;

    public bool HasStop;
    public double StopPrice;
    public bool HasTarget;
    public double TargetPrice;
    public bool HasTrail;
    public double TrailDistance;
    public double TrailActivationR;
    public double TrailHighWaterMark;
    public bool TrailArmed;

    public int TripRingNextIndex;
    public int TripRingCount;
    public long Streak;
}
