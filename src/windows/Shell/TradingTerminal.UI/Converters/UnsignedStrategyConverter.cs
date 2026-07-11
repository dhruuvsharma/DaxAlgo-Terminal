using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Drives the <b>DEV (unsigned)</b> badge on a strategy catalog card. A card's DataContext is an
/// <c>ITradingStrategy</c>, and whether that strategy came from an unsigned plugin lives on the shell
/// view-model, so the badge is a MultiBinding of [strategy id, the VM's unsigned-id set] → visibility.
/// This mirrors the badge already shown in the Plugin Manager, on the surface the user actually browses.
/// <para>XAML: <c>Converter="{StaticResource UnsignedStrategyConverter}"</c> — register once via
/// <see cref="EnsureConverterRegistered"/>.</para>
/// </summary>
public sealed class UnsignedStrategyConverter : IMultiValueConverter
{
    public const string ConverterKey = "UnsignedStrategyConverter";

    /// <param name="values">[0] the strategy id (string), [1] the unsigned-id set
    /// (<see cref="IReadOnlySet{String}"/>).</param>
    /// <returns><see cref="Visibility.Visible"/> when the id is in the set, else
    /// <see cref="Visibility.Collapsed"/>.</returns>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is [string id, IReadOnlySet<string> unsigned] && unsigned.Contains(id))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Registers a shared instance under <see cref="ConverterKey"/> in application resources.
    /// Idempotent; a no-op at design-time / headless hosts.</summary>
    public static void EnsureConverterRegistered()
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Resources.Contains(ConverterKey))
            app.Resources[ConverterKey] = new UnsignedStrategyConverter();
    }
}
