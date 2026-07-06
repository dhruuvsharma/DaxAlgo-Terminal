using System.IO;
using System.Text.Json;

namespace TradingTerminal.UI;

/// <summary>
/// Best-effort, file-backed store of the last instrument the user selected in each picker, keyed by a
/// per-window id (a strategy id or a tool key). Lets every dropdown reopen on the instrument last used
/// there instead of a hardcoded default. Mirrors <see cref="StrategyWindowPlacementStore"/>: any
/// IO/JSON failure is swallowed, the cache is loaded once and flushed on save, and it lives under the
/// same <c>%LOCALAPPDATA%/DaxAlgo Terminal</c> root the rest of the app uses for user files.
///
/// <para>The value is the canonical <c>Contract.Symbol</c> — the same key every picker already
/// re-matches on when a broker's universe replaces the static one — so a remembered pick resolves
/// against whatever list is live at reopen (registry, fallback, or a connected broker's universe).</para>
/// </summary>
public static class LastInstrumentStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "last-instruments.json");

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    /// <summary>The canonical symbol last selected under <paramref name="key"/>, or null if the window
    /// has never had a selection or the store can't be read.</summary>
    public static string? Load(string key)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _cache!.TryGetValue(key, out var symbol) ? symbol : null;
        }
    }

    /// <summary>Records the last selected <paramref name="symbol"/> for <paramref name="key"/> and
    /// flushes the store. No-ops on a null/blank symbol or an unchanged value; swallows any failure —
    /// losing a remembered instrument must never surface as an error on window close.</summary>
    public static void Save(string key, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        lock (Gate)
        {
            EnsureLoaded();
            if (_cache!.TryGetValue(key, out var current) && current == symbol) return;
            _cache[key] = symbol;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache));
            }
            catch
            {
                // best-effort: an unwritable profile dir must not break window close.
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;
        try
        {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                  ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
        }
        catch
        {
            _cache = new Dictionary<string, string>();
        }
    }
}
