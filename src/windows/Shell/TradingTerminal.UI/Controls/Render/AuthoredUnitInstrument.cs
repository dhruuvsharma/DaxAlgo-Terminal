using System.Globalization;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// One selectable instrument in an authored unit's setup panel.
///
/// <para>A pair rather than a bare <see cref="InstrumentId"/>, because the id is a registry surrogate
/// with no meaning to a person — the whole reason the free-text editor was unusable for this. The
/// window shows <see cref="DisplayName"/> and stores <see cref="IdText"/>.</para>
///
/// <para><see cref="IdText"/> exists so the picker can bind <c>SelectedValuePath</c> to the very
/// string the editor already keeps in <c>Value</c>. Choosing a row is then identical to typing its
/// id, and <c>TryParse</c> stays the single place a parameter is validated — a second conversion
/// path is a second thing to disagree with the first.</para>
/// </summary>
public sealed record AuthoredUnitInstrument(InstrumentId Id, string DisplayName)
{
    /// <summary>The id as the editor stores it: invariant, so it round-trips on any machine.</summary>
    public string IdText { get; } = Id.Value.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => DisplayName;
}
