using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Maps a correlation value in [-1, 1] to a diverging heat colour: saturated red at -1, a muted
/// neutral near 0, and saturated green at +1, interpolating linearly by magnitude. White text stays
/// legible against every shade. Brushes are cached (and frozen) per rounded value so a large matrix
/// doesn't allocate one brush per cell on every layout pass.
/// </summary>
public sealed class CorrelationToBrushConverter : IValueConverter
{
    // Endpoints and the zero-correlation neutral. Neutral is a dark surface tone so a sea of
    // near-zero cells reads as "background" and the strong correlations pop.
    private static readonly Color Negative = Color.FromRgb(0xC6, 0x28, 0x28); // red  (-1)
    private static readonly Color Neutral  = Color.FromRgb(0x2D, 0x2D, 0x30); // grey ( 0)
    private static readonly Color Positive = Color.FromRgb(0x2E, 0x7D, 0x32); // green (+1)

    private static readonly ConcurrentDictionary<int, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value switch
        {
            double d => d,
            float f => f,
            _ => 0.0,
        };

        if (double.IsNaN(v))
            v = 0.0;
        v = Math.Clamp(v, -1.0, 1.0);

        // Bucket to 2 decimals so we reuse brushes aggressively (201 possible keys).
        int key = (int)Math.Round(v * 100.0);
        return Cache.GetOrAdd(key, k =>
        {
            double t = k / 100.0;
            Color target = t >= 0 ? Positive : Negative;
            var color = Lerp(Neutral, target, Math.Abs(t));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        });
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
