using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingTerminal.BacktestStudio;

public sealed class SignToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TryGetNumber(value, culture, out var number) || double.IsNaN(number))
            return ResolveBrush(parameter);

        var resourceKey = number >= 0 ? "Bullish.Brush" : "Bearish.Brush";
        return ResolveBrush(resourceKey, parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool TryGetNumber(object? value, CultureInfo culture, out double number)
    {
        if (value is null || value == DependencyProperty.UnsetValue)
        {
            number = 0;
            return false;
        }

        try
        {
            number = System.Convert.ToDouble(value, culture);
            return true;
        }
        catch (Exception) when (value is not IConvertible)
        {
            number = 0;
            return false;
        }
        catch (FormatException)
        {
            number = 0;
            return false;
        }
        catch (InvalidCastException)
        {
            number = 0;
            return false;
        }
        catch (OverflowException)
        {
            number = 0;
            return false;
        }
    }

    private static object ResolveBrush(object? candidate, object? fallback = null)
    {
        if (candidate is Brush brush)
            return brush;

        if (candidate is string key && Application.Current?.TryFindResource(key) is Brush resourceBrush)
            return resourceBrush;

        if (fallback is not null && !ReferenceEquals(candidate, fallback))
            return ResolveBrush(fallback);

        return DependencyProperty.UnsetValue;
    }
}
