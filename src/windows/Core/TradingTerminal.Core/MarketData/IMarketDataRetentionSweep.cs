namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Runs the retention sweep on demand.
///
/// <para>Exists so the settings screen can apply a shortened window immediately. Without it a user who
/// drops depth from 14 days to 2 waits up to the sweep interval before anything happens, which reads
/// as the setting having been ignored.</para>
/// </summary>
public interface IMarketDataRetentionSweep
{
    /// <summary>
    /// Sweeps now. Returns rows deleted, or a negative number from a backend that drops whole
    /// partitions and cannot count them. Returns zero when a sweep is already running — this is a
    /// request, not a queue.
    /// </summary>
    Task<long> SweepAsync(CancellationToken ct = default);
}
