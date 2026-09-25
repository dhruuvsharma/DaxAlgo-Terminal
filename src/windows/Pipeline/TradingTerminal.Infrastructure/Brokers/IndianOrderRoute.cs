using System.Net.Http;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// What the Indian brokers' order routes share (Zerodha, Upstox, Angel One, Dhan, Fyers, 5paisa, Alice Blue,
/// ICICI Breeze).
///
/// <para><b>Live only.</b> None of them offers a paper environment on its order API that behaves like the
/// account, so each is a LIVE-only card behind the owner option, the stored session and the typed
/// confirmation.</para>
///
/// <para><b>A day's session.</b> SEBI's rules make every sign-in end daily; the session the login window
/// stores is read on each request, and a missing one is a refusal telling the user to sign in — nothing was
/// sent.</para>
///
/// <para><b>Delivery equity.</b> Orders are placed as delivery (<c>CNC</c>/<c>DELIVERY</c>), which settles into
/// the demat account and cannot be sold short — a sell beyond what is held is refused by the broker. The
/// position is the holding plus the day's delivery trades. Prices are in rupees on a paisa grid: NSE's
/// coarser ticks for dearer stocks are the broker's to enforce, so an off-grid limit is refused there.</para>
/// </summary>
internal abstract class IndianOrderRoute : OrderRouteBase
{
    protected IndianOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider? time = null, HttpMessageHandler? handler = null)
        : base(credentials, logger, time, handler) { }

    public sealed override string? PaperEnvironmentName => null;

    /// <summary>Refuses any environment but live — these brokers have no paper one.</summary>
    protected void RequireLive(RouteEnvironment environment)
    {
        if (environment != RouteEnvironment.Live)
            throw new BrokerOrderRouteException($"{DisplayName} has no paper environment.", isRejection: true);
    }

    /// <summary>Today's session token, or a refusal to sign in.</summary>
    protected string Session => Credential.Session is { Length: > 0 } session
        ? session
        : throw new BrokerOrderRouteException($"{DisplayName}: no session is stored — sign in for today in the login window.", isRejection: true);

    /// <summary><c>EXCHANGE:CODE</c> → its two halves; a bare code is on <paramref name="exchange"/>.</summary>
    protected static (string Exchange, string Code) Split(string symbol, string exchange = "NSE")
    {
        var trimmed = symbol.Trim();
        var colon = trimmed.IndexOf(':');
        return colon > 0 ? (trimmed[..colon].ToUpperInvariant(), trimmed[(colon + 1)..]) : (exchange, trimmed);
    }

    /// <summary>The rules for a delivery equity: whole shares, a rupee a point, a paisa grid.</summary>
    protected static RouteInstrument Delivery(string symbol) =>
        new(symbol, 1m, 1m, 0.01m, 1, 10_000_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.ImmediateOrCancel,
            SupportsReplace: false, "INR");
}

/// <summary>Indian brokers' timestamps, which are Indian Standard Time without saying so.</summary>
internal static class IndianTime
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "dd-MM-yyyy HH:mm:ss", "dd-MMM-yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff", "dd-MMM-yyyy HH:mm", "MM/dd/yyyy HH:mm:ss",
    ];

    /// <summary>A time as UTC: one that carries its own offset or <c>Z</c> is taken as written; a bare one is
    /// Indian time (UTC+05:30, no daylight saving). <paramref name="fallback"/> when it cannot be read.</summary>
    public static DateTime ToUtc(string? text, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var trimmed = text.Trim();
        if ((trimmed.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"[+-]\d{2}:?\d{2}$")) &&
            DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var stamped))
            return stamped.UtcDateTime;
        if (DateTime.TryParseExact(trimmed, Formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var local) ||
            DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out local))
            return DateTime.SpecifyKind(local - Offset, DateTimeKind.Utc);
        return fallback;
    }
}
