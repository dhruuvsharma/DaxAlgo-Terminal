using System.Globalization;
using System.Windows.Data;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Multi-binding converter that returns <c>true</c> iff the two bound values are reference-equal.
/// Used by the login window's broker-tile DataTemplate to highlight the active form's tile.
/// </summary>
public sealed class ReferenceEqualsConverter : IMultiValueConverter
{
    public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
