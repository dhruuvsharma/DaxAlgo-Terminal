using TradingTerminal.Core.Domain;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Routing;

/// <summary>
/// Settings shared by every routed broker (the order routes registered through <c>IBrokerOrderRoute</c>).
/// Section <c>Execution:Routes</c>.
///
/// <para><b>Live is off until the owner turns it on.</b> <see cref="AllowLiveExecution"/> defaults to false and
/// is one of the three conditions the live gate requires, with real credentials and a persisted typed
/// confirmation for the exact broker account — the same gate the Alpaca, cTrader and Interactive Brokers
/// adapters use.</para>
/// </summary>
public sealed class RoutedExecutionOptions
{
    public const string SectionName = "Execution:Routes";

    /// <summary>The owner's opt-in to real-money orders on routed brokers. Default false.</summary>
    public bool AllowLiveExecution { get; set; }

    /// <summary>How often working orders are re-read from the broker.</summary>
    public int PollIntervalMilliseconds { get; set; } = 1_000;

    /// <summary>The local command budget per broker account, below every broker's own limit.</summary>
    public int MaximumCommandsPerMinute { get; set; } = 60;

    /// <summary>How many orders one book adapter keeps correlated in memory.</summary>
    public int MaximumTrackedOrders { get; set; } = 1_000;

    public RoutedExecutionOptions Snapshot() => (RoutedExecutionOptions)MemberwiseClone();

    /// <summary>A description of the first invalid setting, or null.</summary>
    public string? Validate() =>
        PollIntervalMilliseconds is < 200 or > 60_000 ? "PollIntervalMilliseconds must be between 200 and 60000."
        : MaximumCommandsPerMinute is < 1 or > 10_000 ? "MaximumCommandsPerMinute must be between 1 and 10000."
        : MaximumTrackedOrders is < 16 or > 100_000 ? "MaximumTrackedOrders must be between 16 and 100000."
        : null;
}

/// <summary>
/// An execution adapter a book can be attached to: bound to one instrument, able to refresh its
/// reconciliation snapshot and a reference price on demand. Alpaca and every routed broker implement it,
/// which is what lets one book runtime serve them all.
/// </summary>
public interface IBookableExecutionAdapter : IBrokerExecutionAdapter
{
    /// <summary>The broker's name for messages (<c>Alpaca</c>, <c>Binance</c>).</summary>
    string DisplayName { get; }

    InstrumentId Instrument { get; }

    /// <summary>The broker's symbol for <see cref="Instrument"/>.</summary>
    string Symbol { get; }

    /// <summary>Account-currency value of one engine unit moving one price point.</summary>
    ScaledRatio ContractMultiplier { get; }

    ScaledPrice? LatestReferencePrice { get; }

    DateTime? LatestReferencePriceObservedAtUtc { get; }

    DateTime? LatestReferencePriceFetchedAtUtc { get; }

    Task RefreshReconciliationAsync(CancellationToken cancellationToken = default);

    Task RefreshReferencePriceAsync(CancellationToken cancellationToken = default);
}
