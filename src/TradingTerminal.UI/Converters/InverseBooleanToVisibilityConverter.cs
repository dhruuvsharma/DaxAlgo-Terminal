using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingTerminal.UI.Converters;

/// <summary><see cref="Visibility.Collapsed"/> when the value is true; <see cref="Visibility.Visible"/> otherwise.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}
