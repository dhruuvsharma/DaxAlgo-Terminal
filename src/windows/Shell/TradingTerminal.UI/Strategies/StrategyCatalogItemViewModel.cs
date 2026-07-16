using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// One row in the strategy catalog: the compiled <see cref="ITradingStrategy"/> plus the user's
/// presentation overrides (name / description / tags / alpha formula / UI image). Effective values fall
/// back to the strategy's own metadata, and the image falls back to the app logo in XAML. Observable, so
/// an in-place edit refreshes the card live without rebuilding the catalog.
/// </summary>
public sealed partial class StrategyCatalogItemViewModel : ViewModelBase
{
    public StrategyCatalogItemViewModel(ITradingStrategy strategy)
        : this(strategy, StrategyPresentationStore.Get(strategy.Id)) { }

    public StrategyCatalogItemViewModel(ITradingStrategy strategy, StrategyPresentation presentation)
    {
        Strategy = strategy;
        _name = strategy.DisplayName;
        _description = strategy.Description;
        Apply(presentation);
    }

    /// <summary>The underlying strategy — the catalog's pill converters, Open and Quick-backtest all key
    /// off this, so it stays exposed even as the display fields are overridden.</summary>
    public ITradingStrategy Strategy { get; }

    public string Id => Strategy.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string? _formula;
    [ObservableProperty] private string? _imagePath;

    /// <summary>Extra free-text tags the user added — rendered alongside the auto data/asset pills.</summary>
    public ObservableCollection<string> CustomTags { get; } = [];

    public bool HasFormula => !string.IsNullOrWhiteSpace(Formula);
    public bool HasCustomTags => CustomTags.Count > 0;

    partial void OnFormulaChanged(string? value) => OnPropertyChanged(nameof(HasFormula));

    /// <summary>Overlay the strategy's compiled metadata with a set of overrides (blank ⇒ fall back).</summary>
    public void Apply(StrategyPresentation presentation)
    {
        Name = string.IsNullOrWhiteSpace(presentation.Name) ? Strategy.DisplayName : presentation.Name!;
        Description = string.IsNullOrWhiteSpace(presentation.Description) ? Strategy.Description : presentation.Description!;
        Formula = string.IsNullOrWhiteSpace(presentation.Formula) ? null : presentation.Formula;
        ImagePath = string.IsNullOrWhiteSpace(presentation.ImagePath) ? null : presentation.ImagePath;

        CustomTags.Clear();
        foreach (var tag in presentation.Tags ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(tag)) CustomTags.Add(tag.Trim());
        OnPropertyChanged(nameof(HasCustomTags));
    }
}
