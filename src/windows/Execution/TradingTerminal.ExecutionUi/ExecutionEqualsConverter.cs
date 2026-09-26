using System.Globalization;
using System.Windows.Data;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// Binds a one-of-many choice (a tab, a range, a side, an order type) to a group of radio buttons: checked when
/// the bound value equals the parameter, and checking one writes the parameter back — parsed as the enum when the
/// property is one.
/// </summary>
public sealed class ExecutionEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter?.ToString() is not { Length: > 0 } text)
            return Binding.DoNothing;

        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum ? Enum.Parse(type, text) : text;
    }
}
