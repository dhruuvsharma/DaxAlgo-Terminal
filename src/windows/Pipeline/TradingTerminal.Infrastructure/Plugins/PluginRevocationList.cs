using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>A withdrawn plugin build. Matched by assembly <see cref="Sha256"/> (the precise thing —
/// one bad build, not a whole publisher) and/or by plugin <see cref="Id"/> (the blunt thing — every
/// build of it).</summary>
public sealed record RevokedPlugin(
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// The local kill-list (<c>revoked.json</c> in the plugins root): builds the host refuses to load even
/// though they'd otherwise pass trust. This is how a plugin found to be malicious after the fact gets
/// switched off — the marketplace feed's <c>revoked[]</c> will sync into this file (distribution-channel
/// issue), and it is checked on EVERY load, before any plugin code runs.
/// <para>Absent or corrupt file = nothing revoked; it must never block startup.</para>
/// </summary>
public sealed class PluginRevocationList
{
    public const string FileName = "revoked.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly List<RevokedPlugin> _revoked;

    private PluginRevocationList(List<RevokedPlugin> revoked) => _revoked = revoked;

    public static PluginRevocationList Empty { get; } = new([]);

    public bool IsEmpty => _revoked.Count == 0;

    public static PluginRevocationList Load(string pluginsRoot)
    {
        var path = Path.Combine(pluginsRoot, FileName);
        if (!File.Exists(path)) return Empty;

        try
        {
            var dto = JsonSerializer.Deserialize<RevokedDto>(File.ReadAllText(path), JsonOptions);
            return dto?.Revoked is { Count: > 0 } ? new PluginRevocationList(dto.Revoked) : Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>True when this exact build (<paramref name="sha256"/>) or this plugin id is revoked.
    /// <paramref name="reason"/> is what the user is told.</summary>
    public bool IsRevoked(string sha256, string? pluginId, out string? reason)
    {
        foreach (var entry in _revoked)
        {
            var hashMatch = !string.IsNullOrWhiteSpace(entry.Sha256)
                && string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
            var idMatch = !string.IsNullOrWhiteSpace(entry.Id)
                && !string.IsNullOrWhiteSpace(pluginId)
                && string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase);

            if (hashMatch || idMatch)
            {
                reason = string.IsNullOrWhiteSpace(entry.Reason)
                    ? "this plugin build has been revoked"
                    : entry.Reason;
                return true;
            }
        }

        reason = null;
        return false;
    }

    /// <summary>Rewrites <see cref="FileName"/> to the union of what's already there and
    /// <paramref name="additional"/> (deduped by sha256+id; a later entry's reason text wins). This is how
    /// the marketplace feed's <c>revoked[]</c> is synced into the local kill-list — see
    /// <c>Feed.PluginRevocationSync</c>. Returns the number of entries now revoked. Best-effort and never
    /// throws: on an I/O or serialization failure the file is left untouched and the pre-existing count is
    /// returned.</summary>
    public static int Merge(string pluginsRoot, IEnumerable<RevokedPlugin> additional)
    {
        var existing = Load(pluginsRoot)._revoked;
        try
        {
            var byKey = new Dictionary<string, RevokedPlugin>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in existing.Concat(additional))
            {
                if (string.IsNullOrWhiteSpace(entry.Sha256) && string.IsNullOrWhiteSpace(entry.Id)) continue;
                byKey[$"{entry.Sha256}|{entry.Id}"] = entry;
            }

            var merged = byKey.Values.ToList();
            Directory.CreateDirectory(pluginsRoot);
            File.WriteAllText(
                Path.Combine(pluginsRoot, FileName),
                JsonSerializer.Serialize(new RevokedDto { Revoked = merged }, WriteOptions));
            return merged.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return existing.Count;
        }
    }

    private sealed class RevokedDto
    {
        [JsonPropertyName("revoked")] public List<RevokedPlugin>? Revoked { get; set; }
    }
}
