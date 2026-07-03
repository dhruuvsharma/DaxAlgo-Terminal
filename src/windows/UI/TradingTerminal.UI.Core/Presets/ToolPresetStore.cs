using System.Text.Json;

namespace TradingTerminal.UI.Presets;

/// <summary>
/// Named view presets for one tool window, persisted as a single JSON file per tool under
/// <c>%LocalAppData%\DaxAlgo Terminal\tool-presets\{toolKey}.json</c> (the same per-file
/// convention the Settings surface uses for its user files). <typeparamref name="T"/> is the
/// tool's own preset DTO — a small record of its view options — so each window owns its shape
/// and older files with unknown fields still deserialize.
///
/// WPF-free and dependency-free so it lives in UI.Core and is shared by both UI heads. A corrupt
/// or unreadable file degrades to an empty store rather than throwing into the window. Not
/// intended for concurrent multi-process access — last writer wins, like the other user files.
/// </summary>
public sealed class ToolPresetStore<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, T>? _presets;

    public ToolPresetStore(string toolKey)
        : this(toolKey, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo Terminal", "tool-presets"))
    {
    }

    /// <summary>Test seam: redirect the store directory.</summary>
    public ToolPresetStore(string toolKey, string directory)
    {
        if (string.IsNullOrWhiteSpace(toolKey)) throw new ArgumentException("Tool key is required.", nameof(toolKey));
        _path = Path.Combine(directory, toolKey + ".json");
    }

    /// <summary>Preset names, sorted for a stable picker list.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate) return Load().Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public T? Get(string name)
    {
        lock (_gate) return Load().TryGetValue(name, out var preset) ? preset : null;
    }

    /// <summary>Adds or replaces a named preset and persists immediately.</summary>
    public void Save(string name, T preset)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Preset name is required.", nameof(name));
        lock (_gate)
        {
            var presets = Load();
            presets[name.Trim()] = preset;
            Persist(presets);
        }
    }

    /// <summary>Removes a named preset; returns false when it didn't exist.</summary>
    public bool Delete(string name)
    {
        lock (_gate)
        {
            var presets = Load();
            if (!presets.Remove(name)) return false;
            Persist(presets);
            return true;
        }
    }

    private Dictionary<string, T> Load()
    {
        if (_presets is not null) return _presets;
        try
        {
            _presets = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(_path)) ?? new()
                : new Dictionary<string, T>();
        }
        catch
        {
            // Corrupt/unreadable preset file: degrade to empty rather than break the window.
            _presets = new Dictionary<string, T>();
        }
        return _presets;
    }

    private void Persist(Dictionary<string, T> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(presets, JsonOptions));
    }
}
