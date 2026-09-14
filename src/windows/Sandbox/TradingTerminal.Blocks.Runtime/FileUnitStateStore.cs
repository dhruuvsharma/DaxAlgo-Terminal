using System.IO;
using System.Text.Json;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>
/// Keeps each unit's state in <c>&lt;root&gt;\&lt;unit id&gt;\state.json</c>, written atomically so a
/// crash mid-save leaves the previous state rather than half of the new one.
/// </summary>
public sealed class FileUnitStateStore(string root) : IUnitStateStore
{
    /// <summary>The terminal's default: <c>%LocalAppData%\DaxAlgo Terminal\units</c>.</summary>
    public static FileUnitStateStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgo Terminal", "units"));

    public IReadOnlyDictionary<string, string>? Load(string unitId)
    {
        var path = PathFor(unitId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable state is no state: the unit starts fresh rather than not at all.
            return null;
        }
    }

    public void Save(string unitId, IReadOnlyDictionary<string, string> values)
    {
        var path = PathFor(unitId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var staging = path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(values));
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>A unit id is user input and becomes a folder name, so nothing in it can climb out.</summary>
    private string PathFor(string unitId)
    {
        var safe = new string((unitId ?? string.Empty).Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
            .ToArray()).Trim('.');
        return Path.Combine(root, safe.Length == 0 ? "unit" : safe, "state.json");
    }
}
