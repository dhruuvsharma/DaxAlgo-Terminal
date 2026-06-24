namespace TradingTerminal.Core.Research;

/// <summary>
/// Extrapolation of what a full replication would cost, derived from the minimal run (autoarxiv's
/// "replication cost"). Used to gate the budget-approved full run — never auto-escalate past this.
/// </summary>
public sealed record ReplicationCostEstimate(
    TimeSpan EstimatedWallClock,
    int EstimatedPeakMemoryMb,
    decimal EstimatedComputeCostUsd)
{
    /// <summary>A zero/unknown estimate (e.g. before any minimal run has completed).</summary>
    public static ReplicationCostEstimate Unknown => new(TimeSpan.Zero, 0, 0m);
}
