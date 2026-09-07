using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI.Controls.Render;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// The instruments an authored unit's setup panel offers — the connected brokers' own universes,
/// each row resolved to the canonical id its feed will publish under.
///
/// <para><b>Why this is not the registry catalogue.</b> It was, and the result was a picker that
/// listed every instrument in the app under one broker's name. <c>SignalInstrumentCatalog.FromRegistry</c>
/// is deliberately broker-AGNOSTIC — every row's <c>Broker</c> is null, as its own documentation says —
/// so a rule of "the instrument's own venue when it is connected, else whichever is" sent every single
/// row down the else branch and attributed the lot to the first connected broker. A user with four
/// brokers connected saw four hundred instruments, all labelled Alpaca.</para>
///
/// <para><see cref="BrokerInstrumentUniverse"/> is what the charts, the order book and the footprint
/// have always used: each connected broker's own tradable list, tagged with the broker that supplied
/// it, falling back to the registry only when no broker contributed anything. Sharing it is the point —
/// two pickers built from two sources will eventually disagree about where a symbol comes from, and
/// the one an authored unit sees is the one that decides which feed gets started.</para>
///
/// <para>Held here rather than in a shell because both shells need it and each had grown its own copy.</para>
/// </summary>
public static class AuthoredUnitInstruments
{
    /// <summary>
    /// What the picker should list, or empty when no broker is connected — in which case the row falls
    /// back to the text editor rather than showing a dropdown that cannot be set.
    /// </summary>
    /// <param name="repository">Source of each broker's tradable list.</param>
    /// <param name="registry">The canonical registry, used when no broker contributed a universe.</param>
    /// <param name="selector">Which brokers are connected.</param>
    /// <param name="ingest">Resolves a contract to the id its feed publishes under.</param>
    /// <param name="logger">Optional; a failed universe load is logged and degrades to the registry.</param>
    public static async Task<IReadOnlyList<AuthoredUnitInstrument>> LoadAsync(
        IMarketDataRepository repository,
        IInstrumentRegistry registry,
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(ingest);

        var connected = selector.Connected;
        if (connected.Count == 0) return [];

        var universe = await BrokerInstrumentUniverse
            .LoadAsync(repository, registry, logger: logger, ct: ct)
            .ConfigureAwait(false);

        return Resolve(universe, selector, ingest, connected);
    }

    /// <summary>
    /// The resolution half on its own, over a universe the caller already holds — the part worth
    /// testing, and the part that was wrong.
    /// </summary>
    public static IReadOnlyList<AuthoredUnitInstrument> Resolve(
        IReadOnlyList<SignalInstrument> universe,
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        IReadOnlyList<BrokerKind>? connected = null)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(ingest);

        connected ??= selector.Connected;
        if (connected.Count == 0) return [];

        var offered = new List<AuthoredUnitInstrument>();
        var seen = new HashSet<int>();

        foreach (var instrument in universe)
        {
            // The row's own venue when it is connected. A row that names one is the normal case now
            // that the universe comes from the brokers themselves; the fallback is for registry rows,
            // which name none.
            var broker = instrument.Broker is { } declared && selector.IsConnected(declared)
                ? declared
                : connected[0];

            // Resolution touches the registry, and a symbol a venue does not carry can throw. One bad
            // symbol must not empty the whole list.
            try
            {
                var id = ingest.Resolve(instrument.Contract, broker);
                if (id.IsNone || !seen.Add(id.Value)) continue;

                offered.Add(new AuthoredUnitInstrument(id, instrument, broker));
            }
            catch (Exception)
            {
                // Skipped, not surfaced: a venue that cannot name one symbol is not a reason to deny
                // the user the other four hundred.
            }
        }

        return offered;
    }
}
