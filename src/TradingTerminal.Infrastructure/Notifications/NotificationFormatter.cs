using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications;

/// <summary>
/// Renders a <see cref="StrategyNotification"/> as a short, human-readable line.
/// Telegram in HTML parse mode — escapes the four characters Telegram cares about.
/// </summary>
internal static class NotificationFormatter
{
    public static string ToTelegramHtml(StrategyNotification n)
    {
        var icon = n.Kind switch
        {
            NotificationKind.Signal      => "🔔",
            NotificationKind.IdleSignal  => "👀",
            NotificationKind.AlgoArmed   => "🟢",
            NotificationKind.AlgoStopped => "🔴",
            NotificationKind.Trade       => "📈",
            NotificationKind.Test        => "🧪",
            _ => "•",
        };

        var headline = $"{icon} <b>{Escape(n.StrategyName)}</b>";
        var direction = string.IsNullOrEmpty(n.Direction) ? "" : $" {Escape(n.Direction)}";
        var time = n.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

        return $"{headline}{direction} — <code>{Escape(n.Symbol)}</code>\n{Escape(n.Message)}\n<i>{time}</i>";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");
}
