using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI.Controls.Render;
using TradingTerminal.UI;

namespace TradingTerminal.App;

/// <summary>
/// The instruments an authored unit's setup panel offers.
///
/// <para><b>The gap this closes was reported by a user looking at a generated window:</b> "how will
/// the user select the instrument, there is no instrument selector". The unit declared
/// <c>StrategyParameter.Instrument(…)</c> correctly and the chrome rendered it as free text whose
/// validator asked for a canonical surrogate id — a number from the registry that nobody knows.
/// Charts, OrderBook and VolumeFootprint have offered the shared picker all along; an authored unit
/// is the one window that did not.</para>
///
/// <para>Resolved through <see cref="IMarketDataIngest.Resolve"/> against a CONNECTED broker, exactly
/// as those windows do, so the id the unit receives is the same canonical id the feed will publish
/// under. Offering an instrument no connected broker can serve would produce a window that starts and
/// then shows nothing, which is the failure this whole area keeps having to fix.</para>
/// </summary>
internal static class AuthoredUnitInstruments
{
    /// <summary>
    /// What the picker should list, or empty when no broker is connected — in which case the row
    /// falls back to the text editor rather than showing a dropdown that cannot be set.
    /// </summary>
    public static IReadOnlyList<AuthoredUnitInstrument> Selectable(
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        IReadOnlyList<SignalInstrument>? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(ingest);

        var connected = selector.Connected;
        if (connected.Count == 0) return [];

        var offered = new List<AuthoredUnitInstrument>();
        var seen = new HashSet<int>();

        foreach (var instrument in catalogue ?? SignalInstrumentCatalog.All)
        {
            // The instrument's own venue when it is connected, else whatever is. Same rule the tool
            // windows use, so the two pickers cannot disagree about where a symbol comes from.
            var broker = instrument.Broker is { } declared && selector.IsConnected(declared)
                ? declared
                : connected[0];

            // Resolution touches the registry, and a symbol a venue does not carry can throw. One bad
            // symbol must not empty the whole list.
            try
            {
                var id = ingest.Resolve(instrument.Contract, broker);
                if (id.IsNone || !seen.Add(id.Value)) continue;

                offered.Add(new AuthoredUnitInstrument(id, $"{instrument.DisplayName} · {broker}"));
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
