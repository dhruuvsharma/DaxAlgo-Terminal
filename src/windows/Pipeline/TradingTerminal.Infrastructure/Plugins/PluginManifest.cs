using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Optional <c>plugin.json</c> next to a plugin assembly — publisher-authored metadata the host can
/// read BEFORE loading any plugin code (id / name / version / target SDK / publisher). The actual
/// trust decision rests on the assembly's verified signature (<see cref="PluginSignature"/>); the
/// manifest is descriptive provenance and lets a curated policy require a declared publisher.
/// </summary>
/// <param name="Permissions">Warn-level capabilities the plugin DECLARES it uses (<c>fileIo</c>,
/// <c>network</c>, <c>environment</c> — the rule ids from <see cref="PluginPolicyScanner"/>). A
/// declared capability is disclosed rather than flagged. Block-level capabilities (P/Invoke, process,
/// registry, Reflection.Emit, assembly loading) can NEVER be self-granted here — that takes human
/// review, or the scan would be pointless.</param>
public sealed record PluginManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("targetSdkVersion")] string TargetSdkVersion,
    [property: JsonPropertyName("publisher")] string? Publisher = null,
    [property: JsonPropertyName("permissions")] IReadOnlyList<string>? Permissions = null)
{
    public const string FileName = "plugin.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads <see cref="FileName"/> from a plugin folder, or returns null when absent. Throws
    /// <see cref="InvalidDataException"/> on malformed JSON (a present-but-broken manifest is a fault,
    /// not a silent skip).</summary>
    public static PluginManifest? TryRead(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException($"Plugin manifest is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Malformed plugin manifest: {path}", ex);
        }
    }
}
