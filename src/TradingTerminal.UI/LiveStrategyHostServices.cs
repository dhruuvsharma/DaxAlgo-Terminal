using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.UI;

/// <summary>
/// Bundle of canonical-pipeline dependencies that every live strategy host needs. Passed as a
/// single constructor parameter so per-strategy view-models stay one-liners — DI resolves the
/// bundle once and hands it to each VM. The bundle is intentionally a record (value semantics,
/// immutable) so callers can't accidentally swap pieces mid-stream.
///
/// <list type="bullet">
/// <item><see cref="Repository"/> — used for <see cref="IMarketDataRepository.ListInstrumentsAsync"/>
/// (the broker-introspection facade). The streaming/persistence path no longer goes through it.</item>
/// <item><see cref="Hub"/> — live in-memory bus of canonical records keyed by <c>InstrumentId</c>.
/// VMs subscribe here for quotes/bars/depth.</item>
/// <item><see cref="Ingest"/> — ref-counted broker-pump driver. VMs call <c>Subscribe</c> /
/// <c>SubscribeBars</c> to ensure the named broker's feed is started; the handle releases on dispose.</item>
/// <item><see cref="Store"/> — local history backing. VMs call <see cref="IMarketDataStore.GetRecentBarsAsync"/>
/// to warm the chart before live updates arrive.</item>
/// <item><see cref="Selector"/> — multi-broker registry. VMs use it to fall back to the first
/// connected broker when a picker row has no source broker (the static catalog fallback path),
/// and to gate Start on at least one broker being connected.</item>
/// </list>
/// </summary>
public sealed record LiveStrategyHostServices(
    IMarketDataRepository Repository,
    IMarketDataHub Hub,
    IMarketDataIngest Ingest,
    IMarketDataStore Store,
    IBrokerSelector Selector);
