using System.IO;
using System.Text.Json;

namespace TradingTerminal.UI.Updates;

/// <summary>
/// Remembers which released versions the user waved away, so "update available" is shown once per
/// release rather than at every start-up. Loaded once, flushed on write, and every IO/JSON failure
/// swallowed — the same best-effort contract as <see cref="LastInstrumentStore"/>.
///
/// <para>Failing open is the deliberate choice: an unreadable store means the notice reappears, a mild
/// annoyance, whereas failing closed would silently suppress a release the user never dismissed.</para>
///
/// <para>An instance rather than a static so the path is injectable — a static bound to
/// <c>%LOCALAPPDATA%</c> would make the dismissal rule untestable and let a test run write into the
/// developer's real profile. The app uses <see cref="Default"/>.</para>
/// </summary>
public sealed class DismissedUpdateStore(string filePath)
{
    /// <summary>The app-wide store, under the same profile root as the rest of the user's files.</summary>
    public static DismissedUpdateStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "dismissed-updates.json"));

    private readonly object _gate = new();
    private HashSet<string>? _cache;

    public bool IsDismissed(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        lock (_gate)
        {
            EnsureLoaded();
            return _cache!.Contains(version.Trim());
        }
    }

    public void Dismiss(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        lock (_gate)
        {
            EnsureLoaded();
            if (!_cache!.Add(version.Trim())) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, JsonSerializer.Serialize(_cache));
            }
            catch
            {
                // best-effort: an unwritable profile dir means we prompt again next launch.
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_cache is not null) return;
        try
        {
            _cache = File.Exists(filePath)
                ? new HashSet<string>(
                    JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath)) ?? [],
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _cache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
