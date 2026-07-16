using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Per-strategy catalog presentation overrides, persisted as one JSON map keyed by strategy id at
/// <c>%LocalAppData%/DaxAlgoTerminal/strategy-presentation.json</c>. Read when the catalog is built;
/// written when the user edits a card. A missing or corrupt file is treated as "no overrides", so a
/// bad file never takes the catalog down.
/// </summary>
public static class StrategyPresentationStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal", "strategy-presentation.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>The overrides for a strategy, or <see cref="StrategyPresentation.Empty"/> if none.</summary>
    public static StrategyPresentation Get(string strategyId) =>
        Load().TryGetValue(strategyId, out var presentation) ? presentation : StrategyPresentation.Empty;

    /// <summary>Persist a strategy's overrides. An all-blank set is stored as a removal, so "reset to
    /// default" leaves nothing behind and later code changes to the strategy show through again.</summary>
    public static void Save(string strategyId, StrategyPresentation presentation)
    {
        lock (Gate)
        {
            var map = Load();
            if (IsBlank(presentation)) map.Remove(strategyId);
            else map[strategyId] = presentation;
            Persist(map);
        }
    }

    public static void Remove(string strategyId)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.Remove(strategyId)) Persist(map);
        }
    }

    private static bool IsBlank(StrategyPresentation p) =>
        string.IsNullOrWhiteSpace(p.Name) && string.IsNullOrWhiteSpace(p.Description)
        && string.IsNullOrWhiteSpace(p.Formula) && string.IsNullOrWhiteSpace(p.ImagePath)
        && (p.Tags is null || p.Tags.Count == 0);

    private static Dictionary<string, StrategyPresentation> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, StrategyPresentation>>(json)
                ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static void Persist(Dictionary<string, StrategyPresentation> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, Json));
        }
        catch
        {
            // A read-only profile shouldn't crash the catalog — the edit just won't survive a restart.
        }
    }
}
