using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData.AdvancedRegime;

/// <summary>
/// Multi-timeframe regime dashboard provider. Mirrors <c>IInstrumentRegimeProvider</c> in shape:
/// scoped to one contract on a named broker, stateless beyond the snapshot it emits, with no
/// background refresh loop — the UI drives cadence via <see cref="AnalyseAsync"/>. The implementation
/// pulls a single base bar series from the broker via the market-data repository, resamples it per
/// requested timeframe (via <see cref="BarTimeframeAggregator"/>) and runs
/// <see cref="AdvancedRegimeCalculator"/>. It folds failures into a degraded
/// <see cref="AdvancedRegimeSnapshot.Empty"/>-like snapshot rather than throwing.
/// </summary>
public interface IAdvancedRegimeProvider
{
    Task<AdvancedRegimeSnapshot> AnalyseAsync(
        Contract contract,
        BrokerKind broker,
        string displaySymbol,
        IReadOnlyList<AdvancedTimeframe> timeframes,
        AdvancedRegimeSettings settings,
        CancellationToken cancellationToken = default);
}
