namespace TradingTerminal.Core.Domain;

public enum BarSize
{
    OneMinute,
    ThreeMinutes,
    FiveMinutes,
    FifteenMinutes,
    OneHour,
    OneDay
}

public static class BarSizeExtensions
{
    /// <summary>The exact string the TWS API expects for <c>barSizeSetting</c>.</summary>
    public static string ToIbString(this BarSize size) => size switch
    {
        BarSize.OneMinute       => "1 min",
        BarSize.ThreeMinutes    => "3 mins",
        BarSize.FiveMinutes     => "5 mins",
        BarSize.FifteenMinutes  => "15 mins",
        BarSize.OneHour         => "1 hour",
        BarSize.OneDay          => "1 day",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
    };

    public static TimeSpan ToTimeSpan(this BarSize size) => size switch
    {
        BarSize.OneMinute       => TimeSpan.FromMinutes(1),
        BarSize.ThreeMinutes    => TimeSpan.FromMinutes(3),
        BarSize.FiveMinutes     => TimeSpan.FromMinutes(5),
        BarSize.FifteenMinutes  => TimeSpan.FromMinutes(15),
        BarSize.OneHour         => TimeSpan.FromHours(1),
        BarSize.OneDay          => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
    };

    public static string ToDisplayString(this BarSize size) => size switch
    {
        BarSize.OneMinute       => "1m",
        BarSize.ThreeMinutes    => "3m",
        BarSize.FiveMinutes     => "5m",
        BarSize.FifteenMinutes  => "15m",
        BarSize.OneHour         => "1h",
        BarSize.OneDay          => "1D",
        _ => size.ToString()
    };

    public static BarSize FromDisplayString(string s) => s switch
    {
        "1m"  => BarSize.OneMinute,
        "3m"  => BarSize.ThreeMinutes,
        "5m"  => BarSize.FiveMinutes,
        "15m" => BarSize.FifteenMinutes,
        "1h"  => BarSize.OneHour,
        "1D"  => BarSize.OneDay,
        _ => throw new ArgumentException($"Unknown bar size '{s}'.", nameof(s))
    };
}
