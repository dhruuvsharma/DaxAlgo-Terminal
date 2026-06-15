using System.IO;
using System.Windows;

namespace TradingTerminal.UI.Theming;

/// <summary>A selectable app theme — an id, a display name, and the palette dictionary it swaps in.</summary>
public sealed record ThemeDefinition(string Id, string Name, string PaletteUri);

/// <summary>
/// Swaps the active colour palette at runtime. A theme is just a palette <c>ResourceDictionary</c>
/// (Themes/Brushes.xaml, Themes/Monochrome.xaml, …) defining the same key set; the structural style
/// layer references those keys via <c>DynamicResource</c>, so replacing the palette dictionary in
/// <see cref="Application.Resources"/> re-skins every open window live. The choice is persisted.
/// </summary>
public interface IThemeManager
{
    IReadOnlyList<ThemeDefinition> Themes { get; }
    string CurrentThemeId { get; }

    /// <summary>Swaps to the given theme (falls back to the default for an unknown id) and persists it.</summary>
    void Apply(string themeId);

    /// <summary>Applies the persisted theme (or the default on first run). Call once at startup.</summary>
    void ApplySaved();
}

/// <inheritdoc cref="IThemeManager"/>
public sealed class ThemeManager : IThemeManager
{
    private const string BaseUri = "pack://application:,,,/TradingTerminal.UI;component/Themes/";

    private static readonly ThemeDefinition[] _themes =
    {
        new("amber", "Bloomberg Amber", BaseUri + "Brushes.xaml"),
        new("mono",  "Monochrome (B&W)", BaseUri + "Monochrome.xaml"),
    };

    private static readonly string PrefFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal", "theme.txt");

    public IReadOnlyList<ThemeDefinition> Themes => _themes;

    public string CurrentThemeId { get; private set; } = _themes[0].Id;

    public void ApplySaved() => Apply(LoadSavedId());

    public void Apply(string themeId)
    {
        var def = _themes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase))
                  ?? _themes[0];

        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        var newPalette = new ResourceDictionary { Source = new Uri(def.PaletteUri, UriKind.Absolute) };

        // Replace whichever registered palette is currently merged (matched by file name), preserving
        // its slot so the palette stays before the structural style dictionaries. Insert if absent.
        var paletteFiles = _themes.Select(t => FileName(t.PaletteUri))
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = -1;
        for (var i = 0; i < dicts.Count; i++)
        {
            var fn = dicts[i].Source is { } src ? FileName(src.OriginalString) : null;
            if (fn is not null && paletteFiles.Contains(fn)) { index = i; break; }
        }

        if (index >= 0) dicts[index] = newPalette;
        else dicts.Insert(0, newPalette);

        CurrentThemeId = def.Id;
        SaveId(def.Id);
    }

    private static string FileName(string uri)
    {
        var slash = uri.LastIndexOf('/');
        return slash >= 0 ? uri[(slash + 1)..] : uri;
    }

    private static string LoadSavedId()
    {
        try
        {
            if (File.Exists(PrefFile))
            {
                var id = File.ReadAllText(PrefFile).Trim();
                if (_themes.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
                    return id;
            }
        }
        catch { /* fall through to default */ }
        return _themes[0].Id;
    }

    private static void SaveId(string id)
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefFile);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(PrefFile, id);
        }
        catch { /* persistence is best-effort */ }
    }
}
