using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Converts a hex colour string (e.g. "#E74C3C") to a <see cref="SolidColorBrush"/>. Lets a
/// view-model expose a colour decision as a plain string without referencing WPF brush types.
/// Returns <see cref="Brushes.Transparent"/> for null/unparseable input.
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return (SolidColorBrush)new BrushConverter().ConvertFromString(s)!; }
            catch { /* fall through */ }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
