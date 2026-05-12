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

    /// <summary>
    /// Discord renders standard markdown in the <c>content</c> field, so we use bold/code spans
    /// rather than HTML. Discord also has no real text-escape, but doubling backticks in the
    /// symbol/message protects an inline code span from being broken by a stray backtick.
    /// </summary>
    public static string ToDiscordMarkdown(StrategyNotification n)
    {
        var icon = n.Kind switch
        {
            NotificationKind.Signal      => ":bell:",
            NotificationKind.IdleSignal  => ":eyes:",
            NotificationKind.AlgoArmed   => ":green_circle:",
            NotificationKind.AlgoStopped => ":red_circle:",
            NotificationKind.Trade       => ":chart_with_upwards_trend:",
            NotificationKind.Test        => ":test_tube:",
            _ => "•",
        };

        var headline = $"{icon} **{DiscordEscape(n.StrategyName)}**";
        var direction = string.IsNullOrEmpty(n.Direction) ? "" : $" {DiscordEscape(n.Direction)}";
        var time = n.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

        return $"{headline}{direction} — `{DiscordCode(n.Symbol)}`\n{DiscordEscape(n.Message)}\n*{time}*";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");

    private static string DiscordEscape(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("*", "\\*")
         .Replace("_", "\\_")
         .Replace("~", "\\~")
         .Replace("`", "\\`");

    private static string DiscordCode(string s) => s.Replace("`", "ˋ");
}
