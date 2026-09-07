using System.Globalization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// One selectable instrument in an authored unit's setup panel: the row the picker shows, the
/// canonical id the unit's parameter is set to, and the broker the two were resolved against.
///
/// <para>A triple rather than a bare <see cref="InstrumentId"/>, because the id is a registry
/// surrogate with no meaning to a person — the whole reason the free-text editor was unusable for
/// this — and because <b>an id alone cannot be turned back into a feed</b>. A unit subscribes to the
/// hub, and the hub only carries what some broker was asked to stream; asking takes a contract and a
/// broker, which is exactly what an id had thrown away. A window whose picker resolved an id and
/// stopped there is a window that starts, subscribes to silence, and draws nothing.</para>
///
/// <para><see cref="Instrument"/> is the same <see cref="SignalInstrument"/> row every other picker in
/// the app shows, so an authored unit gets the shared control, its search and its broker /
/// asset-class / data pills rather than a second, poorer dropdown of its own.</para>
/// </summary>
/// <param name="Id">The canonical id the parameter is set to.</param>
/// <param name="Instrument">The picker row: contract, category and source broker.</param>
/// <param name="Broker">The broker <paramref name="Id"/> was resolved against, and the one its feed
/// must be started on. Held separately rather than read off <paramref name="Instrument"/>, because a
/// registry-fallback row carries no broker of its own and is resolved against whichever is connected.</param>
public sealed record AuthoredUnitInstrument(InstrumentId Id, SignalInstrument Instrument, BrokerKind Broker)
{
    /// <summary>The id as the editor stores it: invariant, so it round-trips on any machine.</summary>
    public string IdText { get; } = Id.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the row reads as.</summary>
    public string DisplayName => Instrument.DisplayName;

    /// <summary>
    /// A row for a host with no broker universe to offer — a preview pane, the verification harness, a
    /// test. It shows and can be picked; it carries no real venue, so nothing can start a feed from it.
    /// </summary>
    public static AuthoredUnitInstrument Unresolved(InstrumentId id, string displayName) =>
        new(id,
            new SignalInstrument(
                displayName,
                string.Empty,
                new Contract(displayName, "STK", string.Empty, string.Empty, string.Empty)),
            BrokerKind.Simulated);

    public override string ToString() => DisplayName;
}
