using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Per-user JSON persistence for the archive settings tab. Layered into host configuration with
/// reloadOnChange so IOptionsMonitor sees edits without an app restart. Mirrors
/// <see cref="Notifications.NotificationsUserFile"/>.
/// </summary>
internal static class ArchiveUserFile
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "archive.json");

    public static void Save(ArchiveOptions archive, TelegramArchiveOptions telegram)
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

        root[ArchiveOptions.SectionName] = new JsonObject
        {
            ["Enabled"] = archive.Enabled,
            ["Period"] = archive.Period.ToString(),
            ["Tables"] = archive.Tables.ToString(),
            ["DailyCheckHourUtc"] = archive.DailyCheckHourUtc,
            ["MaxPartBytes"] = archive.MaxPartBytes,
            ["VerifyAfterUpload"] = archive.VerifyAfterUpload,
            ["DeleteLocalAfterArchive"] = archive.DeleteLocalAfterArchive,
            ["DefaultTargetKind"] = archive.DefaultTargetKind,
            ["DefaultTargetChatRef"] = archive.DefaultTargetChatRef,
            ["StagingDirectory"] = archive.StagingDirectory,
            ["ManifestDatabasePath"] = archive.ManifestDatabasePath,
        };

        root[TelegramArchiveOptions.SectionName] = new JsonObject
        {
            ["ApiId"] = telegram.ApiId,
            ["ApiHash"] = telegram.ApiHash,
            ["PhoneNumber"] = telegram.PhoneNumber,
            ["SessionFilePath"] = telegram.SessionFilePath,
        };

        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
