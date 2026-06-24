using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Converts a hex colour string to a <em>translucent</em> <see cref="SolidColorBrush"/> — the same hue
/// at a low alpha — so a vivid brand colour can be used as a soft tint (badge fills, chip washes) that
/// reads calmly on a dark theme instead of as a bright block. The opacity is the
/// <c>ConverterParameter</c> (0–1, default 0.16).
///
/// <para>Frozen and cached per (hex, alpha) so binding many elements doesn't allocate a mutable brush
/// per evaluation. Returns <see cref="Brushes.Transparent"/> for null/unparseable input.</para>
/// </summary>
public sealed class HexToSoftBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;

        var alpha = 0.16;
        if (parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
            alpha = Math.Clamp(a, 0, 1);

        var key = s + "|" + alpha.ToString("0.###", CultureInfo.InvariantCulture);
        return Cache.GetOrAdd(key, _ =>
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s)!;
                var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
                brush.Freeze();
                return brush;
            }
            catch { return (SolidColorBrush)Brushes.Transparent; }
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
