using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace TradingTerminal.UI.Theming;

/// <summary>A selectable app theme — an id, a display name, and the palette dictionary it swaps in.</summary>
public sealed record ThemeDefinition(string Id, string Name, string PaletteUri);

/// <summary>
/// Swaps the active colour palette at runtime. A theme is just a palette <c>ResourceDictionary</c>
/// (Themes/Brushes.xaml, Themes/Monochrome.xaml, …) defining the same key set; the structural style
/// layer references those keys via <c>DynamicResource</c>, so replacing the palette dictionary in
/// <see cref="Application.Resources"/> re-skins every open window live. The choice is persisted.
///
/// On top of the built-in presets, the Theme Studio can edit individual tokens live and save the
/// result as a named <b>custom theme</b> (a base palette + a full colour/gradient override snapshot),
/// which then appears alongside the presets and can be exported/imported as a shareable JSON file.
/// </summary>
public interface IThemeManager
{
    /// <summary>Built-in presets plus any discovered custom themes.</summary>
    IReadOnlyList<ThemeDefinition> Themes { get; }

    string CurrentThemeId { get; }

    /// <summary>The built-in palette the current theme is based on (itself, if a preset).</summary>
    string CurrentBaseThemeId { get; }

    /// <summary>Raised when the <see cref="Themes"/> list changes (custom theme saved/imported).</summary>
    event EventHandler? ThemesChanged;

    /// <summary>Swaps to the given theme (built-in or custom; falls back to the default for an unknown id) and persists it.</summary>
    void Apply(string themeId);

    /// <summary>Loads custom themes from disk and applies the persisted theme (or the default on first run). Call once at startup.</summary>
    void ApplySaved();

    // ── Theme Studio (live editing) ─────────────────────────────────────────────────────────────

    /// <summary>Discovers every editable token in the active palette (solids + gradients), grouped and
    /// pair-linked, with current effective values. The basis for the Theme Studio editor.</summary>
    IReadOnlyList<ThemeToken> EnumerateTokens();

    /// <summary>The current effective colour for a brush/colour resource key, or null if absent.</summary>
    Color? ReadColor(string key);

    /// <summary>The current effective gradient for a resource key, or null if it isn't a gradient.</summary>
    LinearGradientBrush? ReadGradient(string key);

    /// <summary>Live-applies a colour to a brush or colour resource key (type auto-detected). Re-skins
    /// every open window immediately via <c>DynamicResource</c>.</summary>
    void SetColorOverride(string key, Color value);

    /// <summary>Live-applies new stop colours to a gradient resource key, preserving its geometry/offsets.</summary>
    void SetGradientOverride(string key, IReadOnlyList<Color> stops);

    // ── Custom theme persistence / sharing ──────────────────────────────────────────────────────

    /// <summary>Installs a custom theme (writes it under %LOCALAPPDATA%, registers it, raises
    /// <see cref="ThemesChanged"/>) and returns its definition.</summary>
    ThemeDefinition RegisterCustomTheme(CustomThemeFile file);

    /// <summary>Serialises a theme file to an arbitrary path (for sharing).</summary>
    void ExportThemeFile(CustomThemeFile file, string path);

    /// <summary>Reads + parses a theme file from an arbitrary path (does not install it).</summary>
    CustomThemeFile ImportThemeFile(string path);

    /// <summary>Looks up the override snapshot behind a custom theme id.</summary>
    bool TryGetCustomTheme(string id, out CustomThemeFile file);
}

/// <inheritdoc cref="IThemeManager"/>
public sealed class ThemeManager : IThemeManager
{
    private const string BaseUri = "pack://application:,,,/TradingTerminal.UI;component/Themes/";

    private static readonly ThemeDefinition[] _builtins =
    {
        new("amber", "Bloomberg Amber", BaseUri + "Brushes.xaml"),
        new("mono",  "Monochrome (B&W)", BaseUri + "Monochrome.xaml"),
        new("greek-light", "Greek — Marble (light)", BaseUri + "GreekLight.xaml"),
        new("greek-dark",  "Greek — Obsidian (dark)", BaseUri + "GreekDark.xaml"),
    };

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgoTerminal");
    private static readonly string PrefFile = Path.Combine(AppDataDir, "theme.txt");
    private static readonly string ThemesDir = Path.Combine(AppDataDir, "themes");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Custom theme id → its override snapshot.</summary>
    private readonly Dictionary<string, CustomThemeFile> _customs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Built-ins + discovered customs, rebuilt whenever the custom set changes.</summary>
    private List<ThemeDefinition> _all = new(_builtins);

    /// <summary>Top-level resource keys we've overridden — cleared on every theme swap so stale edits
    /// from a previous custom theme don't shadow the new palette.</summary>
    private readonly HashSet<string> _overrideKeys = new(StringComparer.Ordinal);

    public event EventHandler? ThemesChanged;

    public IReadOnlyList<ThemeDefinition> Themes => _all;

    public string CurrentThemeId { get; private set; } = _builtins[0].Id;

    public string CurrentBaseThemeId =>
        _customs.TryGetValue(CurrentThemeId, out var c) ? ResolveBuiltin(c.BaseThemeId).Id : CurrentThemeId;

    public void ApplySaved()
    {
        LoadCustomThemes();
        Apply(LoadSavedId());
    }

    public void Apply(string themeId)
    {
        var app = Application.Current;
        if (app is null) return;

        if (_customs.TryGetValue(themeId, out var custom))
        {
            SwapPalette(custom.BaseThemeId);
            foreach (var (key, hex) in custom.Colors)
                SetColorOverride(key, ParseColor(hex));
            foreach (var (key, stops) in custom.Gradients)
                SetGradientOverride(key, stops.Select(ParseColor).ToList());

            CurrentThemeId = themeId;
            SaveId(themeId);
            return;
        }

        var def = ResolveBuiltin(themeId);
        SwapPalette(def.Id);
        CurrentThemeId = def.Id;
        SaveId(def.Id);
    }

    /// <summary>Replaces the merged palette dictionary with the named built-in's, clearing any live
    /// overrides first so they don't shadow the incoming palette.</summary>
    private void SwapPalette(string builtinId)
    {
        var def = ResolveBuiltin(builtinId);
        var app = Application.Current;
        if (app is null) return;

        ClearOverrides();

        var dicts = app.Resources.MergedDictionaries;
        var newPalette = new ResourceDictionary { Source = new Uri(def.PaletteUri, UriKind.Absolute) };

        // Replace whichever registered palette is currently merged (matched by file name), preserving
        // its slot so the palette stays before the structural style dictionaries. Insert if absent.
        var paletteFiles = _builtins.Select(t => FileName(t.PaletteUri)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = -1;
        for (var i = 0; i < dicts.Count; i++)
        {
            var fn = dicts[i].Source is { } src ? FileName(src.OriginalString) : null;
            if (fn is not null && paletteFiles.Contains(fn)) { index = i; break; }
        }

        if (index >= 0) dicts[index] = newPalette;
        else dicts.Insert(0, newPalette);
    }

    private static ThemeDefinition ResolveBuiltin(string id) =>
        _builtins.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? _builtins[0];

    // ── Live editing ────────────────────────────────────────────────────────────────────────────

    public Color? ReadColor(string key)
    {
        var res = Application.Current?.Resources[key];
        return res switch
        {
            SolidColorBrush b => b.Color,
            Color c => c,
            _ => null,
        };
    }

    public LinearGradientBrush? ReadGradient(string key) =>
        Application.Current?.Resources[key] as LinearGradientBrush;

    public void SetColorOverride(string key, Color value)
    {
        var app = Application.Current;
        if (app is null) return;

        // Type-match the existing resource so brush keys stay brushes and raw colour keys stay colours.
        if (app.Resources[key] is Color)
            app.Resources[key] = value;
        else
            app.Resources[key] = new SolidColorBrush(value);

        _overrideKeys.Add(key);
    }

    public void SetGradientOverride(string key, IReadOnlyList<Color> stops)
    {
        var app = Application.Current;
        if (app is null || stops.Count == 0) return;

        var existing = app.Resources[key] as LinearGradientBrush;
        var brush = new LinearGradientBrush
        {
            StartPoint = existing?.StartPoint ?? new Point(0, 0),
            EndPoint = existing?.EndPoint ?? new Point(0, 1),
        };
        for (var i = 0; i < stops.Count; i++)
        {
            var offset = existing is not null && i < existing.GradientStops.Count
                ? existing.GradientStops[i].Offset
                : (stops.Count <= 1 ? 0d : (double)i / (stops.Count - 1));
            brush.GradientStops.Add(new GradientStop(stops[i], offset));
        }

        app.Resources[key] = brush;
        _overrideKeys.Add(key);
    }

    private void ClearOverrides()
    {
        var app = Application.Current;
        if (app is not null)
            foreach (var key in _overrideKeys)
                app.Resources.Remove(key);
        _overrideKeys.Clear();
    }

    public IReadOnlyList<ThemeToken> EnumerateTokens()
    {
        var tokens = new List<ThemeToken>();
        var palette = FindActivePaletteDictionary();
        if (palette is null) return tokens;

        var keys = palette.Keys.OfType<string>().ToList();
        var keySet = new HashSet<string>(keys, StringComparer.Ordinal);

        // Colour keys that are the sibling of a brush — shown via the brush row, not on their own.
        var linkedColorKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (palette[key] is SolidColorBrush && LinkedColorKeyFor(key, keySet) is { } ck)
                linkedColorKeys.Add(ck);

        foreach (var key in keys)
        {
            switch (palette[key])
            {
                case SolidColorBrush brush:
                {
                    var ck = LinkedColorKeyFor(key, keySet);
                    var color = ReadColor(key) ?? brush.Color;
                    tokens.Add(new ThemeToken(Humanize(key), GroupOf(key), ThemeTokenKind.Solid, key, ck, color, Array.Empty<Color>()));
                    break;
                }
                case Color c when !linkedColorKeys.Contains(key):
                {
                    var color = ReadColor(key) ?? c;
                    tokens.Add(new ThemeToken(Humanize(key), GroupOf(key), ThemeTokenKind.Solid, key, null, color, Array.Empty<Color>()));
                    break;
                }
                case LinearGradientBrush g:
                {
                    var live = ReadGradient(key) ?? g;
                    var stops = live.GradientStops.Select(s => s.Color).ToList();
                    tokens.Add(new ThemeToken(Humanize(key), GroupOf(key), ThemeTokenKind.Gradient, key, null, default, stops));
                    break;
                }
            }
        }

        return tokens;
    }

    /// <summary>The brush's sibling <c>.Color</c> key, if one exists. Handles both the
    /// <c>Foo</c>/<c>Foo.Color</c> and the <c>Foo.Brush</c>/<c>Foo.Color</c> naming conventions.</summary>
    private static string? LinkedColorKeyFor(string brushKey, HashSet<string> keys)
    {
        var direct = brushKey + ".Color";
        if (keys.Contains(direct)) return direct;
        if (brushKey.EndsWith(".Brush", StringComparison.Ordinal))
        {
            var stripped = brushKey[..^".Brush".Length] + ".Color";
            if (keys.Contains(stripped)) return stripped;
        }
        return null;
    }

    private ResourceDictionary? FindActivePaletteDictionary()
    {
        var app = Application.Current;
        if (app is null) return null;
        var paletteFiles = _builtins.Select(t => FileName(t.PaletteUri)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dict in app.Resources.MergedDictionaries)
            if (dict.Source is { } src && paletteFiles.Contains(FileName(src.OriginalString)))
                return dict;
        return null;
    }

    private static string GroupOf(string key)
    {
        if (key.StartsWith("MahApps", StringComparison.Ordinal)) return "MahApps accent";
        if (key.StartsWith("Gradient", StringComparison.Ordinal)) return "Gradients";
        if (key.StartsWith("Background", StringComparison.Ordinal)) return "Backgrounds";
        if (key.StartsWith("Border", StringComparison.Ordinal)) return "Borders";
        if (key.StartsWith("Text", StringComparison.Ordinal)) return "Text";
        if (key.StartsWith("Surface", StringComparison.Ordinal)) return "Surfaces";
        if (key.StartsWith("Accent", StringComparison.Ordinal)) return "Accent";
        if (key.StartsWith("Bullish", StringComparison.Ordinal) || key.StartsWith("Bearish", StringComparison.Ordinal)
            || key.StartsWith("Danger", StringComparison.Ordinal) || key.StartsWith("Warning", StringComparison.Ordinal)
            || key.StartsWith("Highlight", StringComparison.Ordinal))
            return "Semantic (P&L / status)";
        return "Other";
    }

    private static string Humanize(string key)
    {
        var trimmed = key;
        if (trimmed.EndsWith(".Brush", StringComparison.Ordinal)) trimmed = trimmed[..^".Brush".Length];
        else if (trimmed.EndsWith(".Color", StringComparison.Ordinal)) trimmed = trimmed[..^".Color".Length];
        return trimmed.Replace('.', ' ');
    }

    // ── Custom theme persistence / sharing ──────────────────────────────────────────────────────

    public ThemeDefinition RegisterCustomTheme(CustomThemeFile file)
    {
        var slug = Slug(file.Name);
        var id = "custom." + slug;
        try
        {
            Directory.CreateDirectory(ThemesDir);
            File.WriteAllText(Path.Combine(ThemesDir, slug + ".json"), JsonSerializer.Serialize(file, JsonOpts));
        }
        catch { /* persistence is best-effort; the in-memory registration still works this session */ }

        _customs[id] = file;
        RebuildThemeList();
        ThemesChanged?.Invoke(this, EventArgs.Empty);
        return new ThemeDefinition(id, file.Name + " (custom)", ResolveBuiltin(file.BaseThemeId).PaletteUri);
    }

    public void ExportThemeFile(CustomThemeFile file, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));

    public CustomThemeFile ImportThemeFile(string path) =>
        JsonSerializer.Deserialize<CustomThemeFile>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Theme file is empty or malformed.");

    public bool TryGetCustomTheme(string id, out CustomThemeFile file) => _customs.TryGetValue(id, out file!);

    private void LoadCustomThemes()
    {
        _customs.Clear();
        try
        {
            if (Directory.Exists(ThemesDir))
            {
                foreach (var path in Directory.EnumerateFiles(ThemesDir, "*.json"))
                {
                    try
                    {
                        var file = JsonSerializer.Deserialize<CustomThemeFile>(File.ReadAllText(path));
                        if (file is null) continue;
                        var id = "custom." + Path.GetFileNameWithoutExtension(path);
                        _customs[id] = file;
                    }
                    catch { /* skip a malformed theme file, keep loading the rest */ }
                }
            }
        }
        catch { /* themes dir unreadable — just expose the built-ins */ }
        RebuildThemeList();
    }

    private void RebuildThemeList()
    {
        var list = new List<ThemeDefinition>(_builtins);
        foreach (var (id, file) in _customs)
            list.Add(new ThemeDefinition(id, file.Name + " (custom)", ResolveBuiltin(file.BaseThemeId).PaletteUri));
        _all = list;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    internal static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return Colors.Magenta; } // visibly wrong on a typo, never throws mid-apply
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "theme" : slug;
    }

    private static string FileName(string uri)
    {
        var slash = uri.LastIndexOf('/');
        return slash >= 0 ? uri[(slash + 1)..] : uri;
    }

    private string LoadSavedId()
    {
        try
        {
            if (File.Exists(PrefFile))
            {
                var id = File.ReadAllText(PrefFile).Trim();
                if (_all.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
                    return id;
            }
        }
        catch { /* fall through to default */ }
        return _builtins[0].Id;
    }

    private static void SaveId(string id)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(PrefFile, id);
        }
        catch { /* persistence is best-effort */ }
    }
}
