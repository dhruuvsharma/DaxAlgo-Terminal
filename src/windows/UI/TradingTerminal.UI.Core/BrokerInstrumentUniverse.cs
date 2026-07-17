using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.UI;

/// <summary>
/// Builds the instrument universe every picker in the app shows: the connected brokers' own tradable
/// lists, tagged with their source broker, falling back to the canonical instrument registry when no
/// broker contributed anything.
///
/// <para>This exists because the same twenty lines were copy-pasted into ten view-models
/// (<see cref="LiveSignalStrategyViewModelBase"/>, the chart tools, the regime tool, correlation…),
/// and the windows that <i>didn't</i> get the copy — the recorder and Quick backtest — fell back to
/// <see cref="SignalInstrumentCatalog.All"/>. That is registry-backed rather than hardcoded, so it is
/// not empty — but its rows are broker-<i>agnostic</i>: <c>Broker</c> is null, so there's no broker
/// pill, no "· IB" disambiguation of the same ticker across two brokers, and no reflection of what a
/// given broker actually offers right now. One implementation means that drift can't recur.</para>
///
/// <para>Pair it with <see cref="IBrokerSelector.StateChanged"/>: a tool window usually opens
/// <i>before</i> a broker has finished connecting (IB + 2FA especially), so without a reload on
/// Connected the fallback list would be pinned for the window's whole life.</para>
/// </summary>
public static class BrokerInstrumentUniverse
{
    /// <summary>
    /// The connected brokers' instruments as picker rows, or the registry's rows when the brokers
    /// returned nothing. Never throws: on failure it logs and returns the registry rows, because a
    /// dropdown that fails to populate must not take the window down with it.
    /// </summary>
    /// <param name="only">Restrict to one broker's universe (Quick backtest scopes its picker to the
    /// broker it will pull history from). Null = every connected broker, each row tagged by source.</param>
    public static async Task<IReadOnlyList<SignalInstrument>> LoadAsync(
        IMarketDataRepository repository,
        IInstrumentRegistry registry,
        BrokerKind? only = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            var list = await repository.ListInstrumentsAsync(ct).ConfigureAwait(false);
            var brokerRows = (list ?? Array.Empty<TradableInstrument>())
                .Where(i => only is null || i.Broker == only)
                .Select(i => new SignalInstrument(
                    $"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}",
                    i.Category,
                    i.Contract,
                    i.Broker))
                .ToList();

            // Broker-tagged rows are richer (the pills show the source broker); fall back to the
            // registry when a broker contributed nothing so the picker still reflects the discovered
            // universe rather than the curated hardcoded list.
            return brokerRows.Count > 0 ? brokerRows : SignalInstrumentCatalog.FromRegistry(registry);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Instrument universe load failed; falling back to the registry");
            return SignalInstrumentCatalog.FromRegistry(registry);
        }
    }

    /// <summary>Short broker label appended to instrument rows so users can disambiguate the same
    /// ticker exposed by multiple connected brokers (e.g. "ES · IB" vs "ES · cTrader").</summary>
    public static string BrokerLabel(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    /// <summary>Re-picks the user's instrument after a reload. Matches on the canonical
    /// <c>Contract.Symbol</c> rather than the row identity, because the broker-tagged DisplayName
    /// differs from the registry row the user may have been on before the broker connected.</summary>
    public static SignalInstrument? Reselect(
        IReadOnlyList<SignalInstrument> universe, SignalInstrument? previous) =>
        previous?.Contract.Symbol is { } symbol
            ? universe.FirstOrDefault(i => i.Contract.Symbol == symbol) ?? universe.FirstOrDefault()
            : universe.FirstOrDefault();
}
