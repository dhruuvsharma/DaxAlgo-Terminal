using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingTerminal.UI.Converters;

/// <summary><see cref="Visibility.Visible"/> when the string is non-null and non-empty; otherwise <see cref="Visibility.Collapsed"/>.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
