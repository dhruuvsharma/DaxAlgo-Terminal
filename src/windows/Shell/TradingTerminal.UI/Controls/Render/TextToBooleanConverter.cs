using System.Globalization;
using System.Windows.Data;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// Binds a <see cref="CheckBox"/> to a parameter row's text value.
///
/// <para>The row keeps every value as a string, because that is what a text editor produces and what
/// the invariant parse on apply consumes — one representation, one place it is parsed. A toggle is
/// the exception: it has no text editor, so it needs the two ends joined here rather than a second
/// typed property on the row that the other five kinds would leave null.</para>
///
/// <para>Writes <c>true</c>/<c>false</c> lower-case and invariant, which is what
/// <c>AuthoredUnitParameter.TryParse</c> reads back.</para>
/// </summary>
public sealed class TextToBooleanConverter : IValueConverter
{
    /// <summary>The single instance, referenced from XAML with <c>{x:Static}</c>.</summary>
    public static TextToBooleanConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && bool.TryParse(text.Trim(), out var flag) && flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "true" : "false";
}
