using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingTerminal.UI.Strategies;

public sealed class CatalogItemKindToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var expected = parameter switch
        {
            CatalogItemKind kind => kind,
            string text when Enum.TryParse<CatalogItemKind>(text, true, out var kind) => kind,
            _ => (CatalogItemKind?)null,
        };

        return value is CatalogItemKind actual && actual == expected
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
