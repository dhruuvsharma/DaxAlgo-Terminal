using System.Globalization;
using System.Windows.Data;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.UI.Converters;

/// <summary>Maps a <see cref="BrokerKind"/> to the two-letter monogram shown on its login tile.</summary>
public sealed class BrokerInitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BrokerKind k ? k switch
        {
            BrokerKind.InteractiveBrokers => "IB",
            BrokerKind.NinjaTrader => "NT",
            BrokerKind.CTrader => "cT",
            _ => "?",
        } : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="BrokerKind"/> to the muted subtitle shown under the broker name on the tile.</summary>
public sealed class BrokerSubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BrokerKind k ? k switch
        {
            BrokerKind.InteractiveBrokers => "TWS / IB Gateway",
            BrokerKind.NinjaTrader => "NinjaTrader 8 (NTDirect)",
            BrokerKind.CTrader => "Spotware Open API",
            _ => string.Empty,
        } : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
