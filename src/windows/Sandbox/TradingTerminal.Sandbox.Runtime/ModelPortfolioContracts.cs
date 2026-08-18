using TradingTerminal.Sandbox.Portfolio;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>Read-only committed model-portfolio state exposed to host consumers.</summary>
public interface IModelPortfolio
{
    InstrumentId Instrument { get; }
    double PositionUnits { get; }
    double PositionQuantity { get; }
    double AverageEntryPrice { get; }
    long BarsHeld { get; }
    double Equity { get; }
    double RealizedGrossProfitLoss { get; }
    double CommissionTotal { get; }
    double SlippageTotal { get; }
    double EquityPeak { get; }
    double MaximumDrawdown { get; }
    long LifetimeClosedTripCount { get; }
    long LifetimeWinningTripCount { get; }
    long LifetimeLosingTripCount { get; }
    long RetainedTradeCount { get; }
    long Streak { get; }
    bool IsComplete { get; }
    double? ProtectiveStopPrice => null;
    double? ProfitTargetPrice => null;

    /// <summary>The resting entry waiting to fire, if one is armed.</summary>
    PendingEntryState? PendingEntry => null;
}

/// <summary>A resting entry the book is waiting to fire.</summary>
/// <param name="TriggerPrice">The exact price the entry waits for.</param>
/// <param name="SignedTargetUnits">The signed position to take when it fires.</param>
/// <param name="IsStop">False for a limit entry, true for a stop entry.</param>
public readonly record struct PendingEntryState(
    double TriggerPrice,
    double SignedTargetUnits,
    bool IsStop);

/// <summary>Observable source of committed model-portfolio snapshots.</summary>
public interface IModelPortfolioSource
{
    IModelPortfolio? CurrentSnapshot { get; }

    event Action<IModelPortfolio>? SnapshotChanged;
}

/// <summary>Immutable projection of one committed model-portfolio snapshot.</summary>
public readonly record struct SandboxPortfolioSnapshot(
    InstrumentId Instrument,
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
    bool IsComplete,
    double? ProtectiveStopPrice = null,
    double? ProfitTargetPrice = null,
    PendingEntryState? PendingEntry = null) : IModelPortfolio;

/// <summary>Bounded configuration used to create the wrapped account engine.</summary>
public sealed record ModelPortfolioAccountConfig(
    int MaxAbsoluteUnits = 100,
    int RetainedClosedTrips = 64);

/// <summary>Host lifecycle for reconciling declarative virtual-book targets into an account.</summary>
public interface IModelPortfolioAccount
{
    IVirtualBook Book { get; }
    ModelPortfolioFault LastFault { get; }
    SandboxPortfolioSnapshot Snapshot { get; }

    void BeginBar(double close);
    void BeginTick(double bid, double ask, double last);
    void ReconcileToTargets();
    void Commit();
    void Rollback();
    void Complete();
}
