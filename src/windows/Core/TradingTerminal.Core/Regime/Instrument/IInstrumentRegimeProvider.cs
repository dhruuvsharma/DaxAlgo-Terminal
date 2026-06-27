using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// Per-instrument regime composite. Unlike <see cref="IMarketRegimeProvider"/> the result is
/// scoped to one contract; the service pulls recent bars from the named broker via the
/// market-data repository and (when the broker exposes depth) the latest L2 snapshot from the
/// hub. Implementations are stateless beyond the snapshot they emit — there is no background
/// refresh loop; the UI drives the cadence via <see cref="AnalyseAsync"/>.
/// </summary>
public interface IInstrumentRegimeProvider
{
    /// <summary>Pull recent bars + optional depth, compute and return a snapshot. Folds all
    /// failures into a degraded <see cref="InstrumentRegimeSnapshot.Empty"/>-like snapshot
    /// rather than throwing.</summary>
    Task<InstrumentRegimeSnapshot> AnalyseAsync(
        Contract contract,
        BrokerKind broker,
        string displaySymbol,
        BarSize timeframe,
        int barCount,
        CancellationToken ct = default);
}
