using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingTerminal.Infrastructure.Notifications;

namespace TradingTerminal.App.Notifications;

/// <summary>
/// The Settings tab persists notification options to a per-user JSON file. The same file
/// is layered into the host configuration with reloadOnChange, so IOptionsMonitor surfaces
/// edits to running transports without a restart.
/// </summary>
internal static class NotificationsUserFile
{
    /// <summary>
    /// Absolute path to <c>%LocalAppData%\DaxAlgo Terminal\notifications.json</c>.
    /// The directory is created on first write.
    /// </summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "notifications.json");

    /// <summary>Writes the notifications section, preserving any other keys that may exist.</summary>
    public static void Save(NotificationsOptions options)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        JsonObject root;
        if (File.Exists(Path))
        {
            var existing = File.ReadAllText(Path);
            root = string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        root[NotificationsOptions.SectionName] = new JsonObject
        {
            ["QueueCapacity"] = options.QueueCapacity,
            ["Telegram"] = new JsonObject
            {
                ["Enabled"] = options.Telegram.Enabled,
                ["BotToken"] = options.Telegram.BotToken,
                ["ChatId"] = options.Telegram.ChatId,
                ["IncludeIdleSignals"] = options.Telegram.IncludeIdleSignals,
            },
            ["Discord"] = new JsonObject
            {
                ["Enabled"] = options.Discord.Enabled,
                ["WebhookUrl"] = options.Discord.WebhookUrl,
                ["Username"] = options.Discord.Username,
                ["IncludeIdleSignals"] = options.Discord.IncludeIdleSignals,
            },
        };

        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
