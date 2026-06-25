using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.App.Avalonia.Shell;

/// <summary>Shell colour converters — Avalonia has no WPF-style DataTriggers, so the status dot,
/// API-meter accent, session badges and log-level pills map their bound value to a brush here.
/// Hex values mirror the palette (Bloomberg dark theme) 1:1.</summary>
internal static class ShellPalette
{
    public static readonly IBrush Bullish = Brush("#00C853");
    public static readonly IBrush Warning = Brush("#FFD600");
    public static readonly IBrush Danger = Brush("#FF1744");
    public static readonly IBrush Muted = Brush("#8A8A8A");
    public static readonly IBrush Accent = Brush("#FF8C00");
    public static readonly IBrush BullishSoft = Brush("#2600C853");
    public static readonly IBrush WarningSoft = Brush("#26FFD600");
    public static readonly IBrush BearishSoft = Brush("#26FF1744");
    public static readonly IBrush Neutral = Brush("#1AFFFFFF");
    public static readonly IBrush NeutralStrong = Brush("#22FFFFFF");
    public static readonly IBrush BorderStrong = Brush("#3A3A3A");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>Connection-state → status-dot brush (Connected=green, Connecting/Reconnecting=amber, else red).</summary>
public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => ShellPalette.Bullish,
                ConnectionState.Connecting or ConnectionState.Reconnecting => ShellPalette.Warning,
                _ => ShellPalette.Danger,
            }
            : ShellPalette.Danger;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>API-meter status → accent brush (Healthy=green, Warming=amber, Hot=red, else muted).</summary>
public sealed class ApiStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BrokerApiChipStatus s
            ? s switch
            {
                BrokerApiChipStatus.Healthy => ShellPalette.Bullish,
                BrokerApiChipStatus.Warming => ShellPalette.Warning,
                BrokerApiChipStatus.Hot => ShellPalette.Danger,
                _ => ShellPalette.Muted,
            }
            : ShellPalette.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Market-session open flag → badge brush. Parameter "bg" → soft green/neutral fill;
/// "border" → green/strong border; "fg" → green/muted text.</summary>
public sealed class SessionFlagToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var open = value is true;
        return (parameter as string) switch
        {
            "bg" => open ? ShellPalette.BullishSoft : ShellPalette.Neutral,
            "border" => open ? ShellPalette.Bullish : ShellPalette.BorderStrong,
            _ => open ? ShellPalette.Bullish : ShellPalette.Muted,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bool → one of two literal strings (e.g. "NYSE OPEN" / "NYSE CLOSED"). Replaces the
/// WPF DataTrigger that swapped the badge text on the session flag.</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = string.Empty;
    public string FalseText { get; set; } = string.Empty;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueText : FalseText;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Log-level → pill brush. Parameter "bg" → soft fill; otherwise foreground.
/// ERROR→red, WARN/Warning→amber, ENTRY→green, else neutral.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = (value as string ?? string.Empty).ToUpperInvariant();
        var bg = (parameter as string) == "bg";
        return level switch
        {
            "ERROR" => bg ? ShellPalette.BearishSoft : ShellPalette.Danger,
            "WARN" or "WARNING" => bg ? ShellPalette.WarningSoft : ShellPalette.Warning,
            "ENTRY" => bg ? ShellPalette.BullishSoft : ShellPalette.Bullish,
            _ => bg ? ShellPalette.NeutralStrong : ShellPalette.Muted,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
