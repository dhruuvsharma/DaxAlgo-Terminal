using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Converts a hex colour string (e.g. "#E74C3C") to a <see cref="SolidColorBrush"/>. Lets a
/// view-model expose a colour decision as a plain string without referencing WPF brush types.
/// Returns <see cref="Brushes.Transparent"/> for null/unparseable input.
///
/// <para>Brushes are parsed once per distinct hex string and cached <em>frozen</em>. Callers that
/// bind hundreds of elements to a handful of colours (e.g. node/edge graphs) would otherwise
/// allocate a fresh mutable brush per binding evaluation — frozen, shared brushes let WPF skip
/// per-brush change tracking and render them on the composition thread.</para>
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            var brush = Cache.GetOrAdd(s, static hex =>
            {
                try
                {
                    var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
                    b.Freeze();
                    return b;
                }
                catch { return (SolidColorBrush)Brushes.Transparent; }
            });
            return brush;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
