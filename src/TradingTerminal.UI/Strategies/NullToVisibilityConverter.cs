using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Collapses an element when its bound value is null or an empty/whitespace string;
/// otherwise <see cref="Visibility.Visible"/>. Used to hide the per-parameter help line
/// when a <see cref="Core.Strategies.Parameters.StrategyParameter"/> has no description.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
