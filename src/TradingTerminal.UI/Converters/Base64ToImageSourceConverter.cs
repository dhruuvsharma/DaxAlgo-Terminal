using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Binds a base64-encoded PNG string to a WPF <c>ImageSource</c>. Returns <c>null</c> on
/// empty / malformed input so the bound Image control collapses cleanly to its empty
/// state — used by the AI Analyst pane for the pattern + trend chart slots.
/// </summary>
public sealed class Base64ToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string base64 || string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = System.Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (FormatException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
