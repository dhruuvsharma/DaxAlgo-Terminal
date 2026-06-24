using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Research;

/// <summary>
/// The Settings → Research tab persists the Paper Lab reproduction options to a per-user JSON file.
/// The same file is layered into the host configuration with reloadOnChange, so
/// <c>IOptionsMonitor&lt;ResearchReproOptions&gt;</c> surfaces edits to the running ingest/resolver
/// clients without a restart. Mirrors <c>NotificationsUserFile</c>.
/// </summary>
internal static class ResearchUserFile
{
    /// <summary>Absolute path to <c>%LocalAppData%\DaxAlgo Terminal\research.json</c>. The directory is
    /// created on first write.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "research.json");

    /// <summary>Writes the <c>ResearchRepro</c> section (and the managed-sidecar auto-launch toggle under
    /// <c>Sidecar</c>), preserving any other keys in the file.</summary>
    public static void Save(ResearchReproOptions options, bool autoLaunchSidecar, int sidecarPort)
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

        root[ResearchReproOptions.SectionName] = new JsonObject
        {
            ["Enabled"] = options.Enabled,
            ["SidecarBaseUrl"] = options.SidecarBaseUrl,
            ["SidecarTimeoutSeconds"] = options.SidecarTimeoutSeconds,
            ["RetentionDays"] = options.RetentionDays,
        };

        // The managed-sidecar launcher reads the "Sidecar" section; keep it in the same per-user file so
        // toggling auto-launch here takes effect via reloadOnChange.
        root[SidecarOptions.SectionName] = new JsonObject
        {
            ["AutoStart"] = autoLaunchSidecar,
            ["Port"] = sidecarPort,
        };

        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
