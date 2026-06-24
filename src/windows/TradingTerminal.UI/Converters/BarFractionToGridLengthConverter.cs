using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Turns a 0..1 fraction into a star <see cref="GridLength"/> so a proportional bar fills that fraction
/// of its row and auto-scales with the container. Pass <c>ConverterParameter=rest</c> on the
/// complementary spacer column to get <c>(1 - fraction)*</c>. Used by the Order Book depth ladder.
/// </summary>
public sealed class BarFractionToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d && d is >= 0 and <= 1 ? d : 0;
        var rest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
        // Floor the visible bar so non-zero sizes always show a sliver; the spacer takes the remainder.
        var fill = Math.Max(fraction, 0.0001);
        return new GridLength(rest ? 1 - fill : fill, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
