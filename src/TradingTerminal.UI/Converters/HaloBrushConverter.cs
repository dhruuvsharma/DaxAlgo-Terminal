using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Converts a hex colour string (e.g. "#16C784") into a soft radial-gradient "halo" brush — the
/// colour at full strength in the centre fading to transparent at the rim. Used to give graph nodes
/// a glow without a <c>BlurEffect</c>, which would force WPF to rasterize an offscreen surface per
/// node on every pan/zoom frame. Brushes are built once per distinct colour and cached frozen so
/// they render on the composition thread and are shared across every node.
/// </summary>
public sealed class HaloBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, Brush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
            return Cache.GetOrAdd(s, BuildHalo);
        return Brushes.Transparent;
    }

    private static Brush BuildHalo(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(color, 0.0),
                    new GradientStop(Color.FromArgb(0x66, color.R, color.G, color.B), 0.55),
                    new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0),
                },
            };
            brush.Freeze();
            return brush;
        }
        catch { return Brushes.Transparent; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
