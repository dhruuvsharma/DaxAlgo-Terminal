using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>A plugin the loader auto-disabled after a fault, with the recorded cause.</summary>
public sealed record PluginQuarantine(
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("quarantinedUtc")] DateTime QuarantinedUtc);

/// <summary>
/// Persisted per-plugin lifecycle state (<c>plugins-state.json</c> next to the plugins root):
/// user disables, fault quarantines, and pending uninstalls. Keys are plugin FOLDER names (the
/// loader's folder == dll-name convention), compared case-insensitively (Windows file system).
/// <para>
/// Why pending uninstalls exist: a loaded plugin's assembly file is locked by its
/// AssemblyLoadContext for the host's lifetime (contexts are deliberately rooted — see
/// <see cref="PluginLoader"/>), so removing a live plugin can only happen BEFORE it loads on the
/// next start. The store is read by the loader's <c>LoadWithReport</c> pre-load, where all
/// three states take effect.
/// </para>
/// State mutations save immediately (atomic temp+rename). A corrupt state file never blocks
/// startup: it is replaced with fresh state and the parse error surfaces via <see cref="LoadError"/>.
/// </summary>
public sealed class PluginStateStore
{
    public const string FileName = "plugins-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private StateDto _state;

    public PluginStateStore(string pluginsRoot)
    {
        _path = Path.Combine(pluginsRoot, FileName);
        (_state, LoadError) = Load(_path);
    }

    /// <summary>Non-null when the existing state file was unreadable and fresh state was started —
    /// the host should log it (the file's content is already lost to recovery).</summary>
    public string? LoadError { get; }

    public IReadOnlyList<string> Disabled { get { lock (_gate) return [.. _state.Disabled]; } }
    public IReadOnlyList<PluginQuarantine> Quarantined { get { lock (_gate) return [.. _state.Quarantined]; } }
    public IReadOnlyList<string> PendingUninstalls { get { lock (_gate) return [.. _state.PendingUninstall]; } }

    public bool IsDisabled(string plugin)
    {
        lock (_gate) return _state.Disabled.Contains(plugin, StringComparer.OrdinalIgnoreCase);
    }

    public void SetDisabled(string plugin, bool disabled)
    {
        lock (_gate)
        {
            _state.Disabled.RemoveAll(p => string.Equals(p, plugin, StringComparison.OrdinalIgnoreCase));
            if (disabled) _state.Disabled.Add(plugin);
            Save();
        }
    }

    public PluginQuarantine? QuarantineFor(string plugin)
    {
        lock (_gate)
            return _state.Quarantined.FirstOrDefault(q =>
                string.Equals(q.Plugin, plugin, StringComparison.OrdinalIgnoreCase));
    }

    public void Quarantine(string plugin, string reason)
    {
        lock (_gate)
        {
            _state.Quarantined.RemoveAll(q => string.Equals(q.Plugin, plugin, StringComparison.OrdinalIgnoreCase));
            _state.Quarantined.Add(new PluginQuarantine(plugin, reason, DateTime.UtcNow));
            Save();
        }
    }

    public bool ClearQuarantine(string plugin)
    {
        lock (_gate)
        {
            var removed = _state.Quarantined.RemoveAll(q =>
                string.Equals(q.Plugin, plugin, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void MarkPendingUninstall(string plugin)
    {
        lock (_gate)
        {
            if (!_state.PendingUninstall.Contains(plugin, StringComparer.OrdinalIgnoreCase))
            {
                _state.PendingUninstall.Add(plugin);
                Save();
            }
        }
    }

    public bool ClearPendingUninstall(string plugin)
    {
        lock (_gate)
        {
            var removed = _state.PendingUninstall.RemoveAll(p =>
                string.Equals(p, plugin, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    private static (StateDto State, string? Error) Load(string path)
    {
        if (!File.Exists(path)) return (new StateDto(), null);
        try
        {
            return (JsonSerializer.Deserialize<StateDto>(File.ReadAllText(path), JsonOptions) ?? new StateDto(), null);
        }
        catch (Exception ex)
        {
            return (new StateDto(), $"Plugin state file '{path}' was unreadable and has been reset: {ex.Message}");
        }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("disabled")] public List<string> Disabled { get; set; } = [];
        [JsonPropertyName("quarantined")] public List<PluginQuarantine> Quarantined { get; set; } = [];
        [JsonPropertyName("pendingUninstall")] public List<string> PendingUninstall { get; set; } = [];
    }
}
