using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Edits a strategy card's presentation overrides — name, tags, description, alpha-generating formula,
/// and a UI screenshot. A blank name/description means "use the strategy's own"; "Reset to default"
/// clears every override so later code changes to the strategy show through again. <see cref="Build"/>
/// produces the minimal override set to persist.
/// </summary>
public sealed partial class StrategyPresentationEditorViewModel : ViewModelBase
{
    private readonly StrategyCatalogItemViewModel _item;

    public StrategyPresentationEditorViewModel(StrategyCatalogItemViewModel item)
    {
        _item = item;
        DefaultName = item.Strategy.DisplayName;
        DefaultDescription = item.Strategy.Description;

        _name = item.Name;
        _description = item.Description;
        _tagsText = string.Join(", ", item.CustomTags);
        _formula = item.Formula ?? string.Empty;
        _imagePath = item.ImagePath;
    }

    public string StrategyId => _item.Id;
    public string DefaultName { get; }
    public string DefaultDescription { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _tagsText;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _formula;
    [ObservableProperty] private string? _imagePath;

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);
    partial void OnImagePathChanged(string? value) => OnPropertyChanged(nameof(HasImage));

    [RelayCommand]
    private void BrowseImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a screenshot of the strategy UI",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) ImagePath = dialog.FileName;
    }

    [RelayCommand]
    private void ClearImage() => ImagePath = null;

    /// <summary>Clears every field back to the strategy's own compiled metadata (persisted as no override).</summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        Name = DefaultName;
        Description = DefaultDescription;
        TagsText = string.Empty;
        Formula = string.Empty;
        ImagePath = null;
    }

    /// <summary>The overrides to persist. A name/description equal to the strategy's own value (or blank)
    /// is stored as null so the strategy's metadata shows through — and a later code change still lands.</summary>
    public StrategyPresentation Build()
    {
        var tags = TagsText.Split(
            new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new StrategyPresentation(
            Name: string.IsNullOrWhiteSpace(Name) || string.Equals(Name.Trim(), DefaultName) ? null : Name.Trim(),
            Description: string.IsNullOrWhiteSpace(Description) || string.Equals(Description.Trim(), DefaultDescription) ? null : Description.Trim(),
            Tags: tags.Length == 0 ? null : tags,
            Formula: string.IsNullOrWhiteSpace(Formula) ? null : Formula.Trim(),
            ImagePath: string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath);
    }
}
