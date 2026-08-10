namespace TradingTerminal.Sandbox.Portfolio;

/// <summary>
/// An immutable committed-state diagnostic snapshot of the model portfolio.
/// It is a diagnostic projection only, not a protected-presentation snapshot.
/// </summary>
/// <param name="PositionUnits">The stored signed unit accumulator.</param>
/// <param name="PositionQuantity">The stored signed quantity used for profit and loss.</param>
/// <param name="AverageEntryPrice">The average entry price, or zero while flat.</param>
/// <param name="BarsHeld">The current position duration in bars, or zero while flat.</param>
/// <param name="Equity">The most recently sampled committed equity.</param>
/// <param name="RealizedGrossProfitLoss">Cumulative realised gross profit and loss.</param>
/// <param name="CommissionTotal">Cumulative commission charged by the model.</param>
/// <param name="SlippageTotal">Cumulative adverse slippage charged by the model.</param>
/// <param name="EquityPeak">The monotonic sampled equity peak.</param>
/// <param name="MaximumDrawdown">The maximum sampled peak-to-equity drawdown.</param>
/// <param name="LifetimeClosedTripCount">The cumulative number of closed trips across the whole run.</param>
/// <param name="LifetimeWinningTripCount">The cumulative number of positive-net closed trips across the whole run.</param>
/// <param name="LifetimeLosingTripCount">The cumulative number of negative-net closed trips across the whole run.</param>
/// <param name="RetainedTradeCount">The number of closed trips retained in the ring.</param>
/// <param name="Streak">The signed consecutive win or loss streak.</param>
/// <param name="IsCompleted">Whether final liquidation has completed the run.</param>
public readonly record struct ModelPortfolioSnapshot(
    double PositionUnits,
    double PositionQuantity,
    double AverageEntryPrice,
    long BarsHeld,
    double Equity,
    double RealizedGrossProfitLoss,
    double CommissionTotal,
    double SlippageTotal,
    double EquityPeak,
    double MaximumDrawdown,
    long LifetimeClosedTripCount,
    long LifetimeWinningTripCount,
    long LifetimeLosingTripCount,
    long RetainedTradeCount,
    long Streak,
    bool IsCompleted);
