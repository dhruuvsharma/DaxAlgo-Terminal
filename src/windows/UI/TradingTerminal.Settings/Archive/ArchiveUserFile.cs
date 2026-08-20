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
/// <summary>The retention windows the settings screen can change, in days. 0 = keep forever.</summary>
public sealed record MarketDataRetentionSettings(
    bool Enabled,
    int QuoteDays,
    int TradeDays,
    int BarDays,
    int DepthDays);

public static class ArchiveUserFile
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "archive.json");

    public static void Save(
        ArchiveOptions archive,
        TelegramArchiveOptions telegram,
        bool? persistLiveData = null,
        MarketDataRetentionSettings? retention = null)
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

        // ApiHash and PhoneNumber are written as DPAPI-encrypted ciphertext; the plaintext
        // properties are left as null in the JSON so legacy readers don't see them. A post-
        // configure step (TelegramArchiveOptionsPostConfigure) decrypts them back into the
        // runtime options at startup.
        root[TelegramArchiveOptions.SectionName] = new JsonObject
        {
            ["ApiId"] = telegram.ApiId,
            ["ApiHash"] = (string?)null,
            ["PhoneNumber"] = (string?)null,
            ["ApiHashEncryptedBase64"] = TelegramArchiveCredentialProtection.Encrypt(telegram.ApiHash),
            ["PhoneNumberEncryptedBase64"] = TelegramArchiveCredentialProtection.Encrypt(telegram.PhoneNumber),
            ["SessionFilePath"] = telegram.SessionFilePath,
        };

        if (persistLiveData is not null || retention is not null)
        {
            // Merged into the existing section rather than replacing it: this file is layered over
            // appsettings.json, so writing a whole MarketDataStore object here would silently override
            // the provider, paths and batch sizes the user never touched on this screen.
            var store = root[MarketDataStoreOptions.SectionName] as JsonObject ?? new JsonObject();
            if (persistLiveData is { } persist)
                store["PersistLiveData"] = persist;

            if (retention is { } r)
            {
                store["RetentionSweepEnabled"] = r.Enabled;
                store["QuoteRetentionDays"] = r.QuoteDays;
                store["TradeRetentionDays"] = r.TradeDays;
                store["BarRetentionDays"] = r.BarDays;
                store["DepthRetentionDays"] = r.DepthDays;
            }
            root[MarketDataStoreOptions.SectionName] = store;
        }

        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
