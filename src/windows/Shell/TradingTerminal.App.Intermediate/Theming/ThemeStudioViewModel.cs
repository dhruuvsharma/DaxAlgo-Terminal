using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TradingTerminal.UI;
using TradingTerminal.UI.Theming;

namespace TradingTerminal.App.Theming;

/// <summary>
/// View-model for the Theme Studio — a live colour playground for UI/UX collaborators. It lists every
/// palette token (grouped, with hex + RGBA editors), applies edits to the running app instantly via
/// <see cref="IThemeManager"/>, and lets the result be saved as a named custom theme that shows up in
/// the View → Theme menu and can be exported/imported as a shareable JSON file.
/// </summary>
public sealed partial class ThemeStudioViewModel : ViewModelBase
{
    private static readonly string[] GroupOrder =
    {
        "Backgrounds", "Surfaces", "Borders", "Text", "Accent",
        "Semantic (P&L / status)", "Gradients", "MahApps accent", "Other",
    };

    private readonly IThemeManager _manager;
    private bool _applyingBase;

    public ThemeStudioViewModel(IThemeManager manager)
    {
        _manager = manager;

        BaseThemes = new ObservableCollection<ThemeDefinition>(
            manager.Themes.Where(t => !t.Id.StartsWith("custom.", StringComparison.Ordinal)));
        _selectedBaseTheme = BaseThemes.FirstOrDefault(t =>
            string.Equals(t.Id, manager.CurrentBaseThemeId, StringComparison.OrdinalIgnoreCase)) ?? BaseThemes.FirstOrDefault();

        Groups = new ObservableCollection<ThemeTokenGroupViewModel>();
        RebuildTokens();
    }

    public ObservableCollection<ThemeDefinition> BaseThemes { get; }
    public ObservableCollection<ThemeTokenGroupViewModel> Groups { get; }

    [ObservableProperty]
    private ThemeDefinition? _selectedBaseTheme;

    /// <summary>Name used when saving/exporting the current edits as a custom theme.</summary>
    [ObservableProperty]
    private string _newThemeName = "My Theme";

    [ObservableProperty]
    private string? _statusMessage;

    partial void OnSelectedBaseThemeChanged(ThemeDefinition? value)
    {
        if (_applyingBase || value is null) return;
        // Switching the base preset starts a fresh edit from that palette.
        _manager.Apply(value.Id);
        RebuildTokens();
        StatusMessage = $"Started from '{value.Name}'.";
    }

    private void RebuildTokens()
    {
        Groups.Clear();
        var byGroup = _manager.EnumerateTokens()
            .GroupBy(t => t.Group)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var name in GroupOrder)
        {
            if (!byGroup.TryGetValue(name, out var tokens)) continue;
            var group = new ThemeTokenGroupViewModel(name);
            foreach (var t in tokens)
                group.Tokens.Add(new ThemeTokenViewModel(_manager, t));
            Groups.Add(group);
        }

        // Any group not in the explicit order (future-proofing) goes last.
        foreach (var kvp in byGroup)
        {
            if (GroupOrder.Contains(kvp.Key)) continue;
            var group = new ThemeTokenGroupViewModel(kvp.Key);
            foreach (var t in kvp.Value)
                group.Tokens.Add(new ThemeTokenViewModel(_manager, t));
            Groups.Add(group);
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        if (SelectedBaseTheme is null) return;
        _manager.Apply(SelectedBaseTheme.Id);
        RebuildTokens();
        StatusMessage = $"Reset to '{SelectedBaseTheme.Name}'.";
    }

    [RelayCommand]
    private void Save()
    {
        var file = BuildFile();
        var def = _manager.RegisterCustomTheme(file);
        ApplyWithoutResetting(def.Id);
        StatusMessage = $"Saved '{file.Name}'. It's now in the View → Theme menu.";
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export theme",
            Filter = "Theme JSON (*.json)|*.json",
            FileName = SanitizeFileName(NewThemeName) + ".json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _manager.ExportThemeFile(BuildFile(), dlg.FileName);
            StatusMessage = $"Exported to {dlg.FileName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import theme",
            Filter = "Theme JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var file = _manager.ImportThemeFile(dlg.FileName);
            var def = _manager.RegisterCustomTheme(file);
            ApplyWithoutResetting(def.Id);
            NewThemeName = file.Name;
            StatusMessage = $"Imported '{file.Name}' and applied it.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Import failed: " + ex.Message;
        }
    }

    /// <summary>Applies a theme + rebuilds the editor without re-triggering the base-theme change
    /// handler (which would reset edits to the bare preset).</summary>
    private void ApplyWithoutResetting(string themeId)
    {
        _manager.Apply(themeId);
        _applyingBase = true;
        SelectedBaseTheme = BaseThemes.FirstOrDefault(t =>
            string.Equals(t.Id, _manager.CurrentBaseThemeId, StringComparison.OrdinalIgnoreCase)) ?? SelectedBaseTheme;
        _applyingBase = false;
        RebuildTokens();
    }

    private CustomThemeFile BuildFile()
    {
        var file = new CustomThemeFile
        {
            Name = string.IsNullOrWhiteSpace(NewThemeName) ? "Custom" : NewThemeName.Trim(),
            BaseThemeId = SelectedBaseTheme?.Id ?? "daxalgo-dark",
        };

        foreach (var group in Groups)
        foreach (var token in group.Tokens)
        {
            if (token.IsGradient)
            {
                file.Gradients[token.PrimaryKey] = token.Stops.Select(s => ThemeColorUtil.ToHex(s.Color)).ToList();
            }
            else
            {
                file.Colors[token.PrimaryKey] = ThemeColorUtil.ToHex(token.Color);
                if (token.LinkedColorKey is not null)
                    file.Colors[token.LinkedColorKey] = ThemeColorUtil.ToHex(token.Color);
            }
        }

        return file;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "theme" : cleaned;
    }
}

/// <summary>A named, collapsible group of token editors (e.g. "Backgrounds", "Gradients").</summary>
public sealed class ThemeTokenGroupViewModel
{
    public ThemeTokenGroupViewModel(string name) => Name = name;

    public string Name { get; }
    public ObservableCollection<ThemeTokenViewModel> Tokens { get; } = new();
}
