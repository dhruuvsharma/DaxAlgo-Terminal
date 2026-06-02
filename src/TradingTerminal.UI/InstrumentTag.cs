using System.Windows.Media;

namespace TradingTerminal.UI;

/// <summary>
/// One coloured pill rendered next to an instrument in the dropdowns — a broker badge, an
/// asset-class badge, or a data-capability badge (BAR / L1 / L2 / TAPE). Produced by
/// <see cref="Converters.InstrumentTagsConverter"/>; <see cref="Background"/> / <see cref="Foreground"/>
/// are frozen brushes so they're cheap to reuse across hundreds of dropdown rows.
/// </summary>
public sealed record InstrumentTag(string Text, Brush Background, Brush Foreground);
