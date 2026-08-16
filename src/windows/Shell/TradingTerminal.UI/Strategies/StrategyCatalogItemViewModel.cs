using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// One catalog row backed by either a compiled strategy or a lightweight visualizer descriptor,
/// plus the user's presentation overrides.
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

    public StrategyCatalogItemViewModel(VisualizerDescriptor visualizer)
        : this(visualizer, StrategyPresentationStore.Get(visualizer.Id)) { }

    public StrategyCatalogItemViewModel(VisualizerDescriptor visualizer, StrategyPresentation presentation)
    {
        Visualizer = visualizer;
        Kind = CatalogItemKind.Visualizer;
        _name = visualizer.DisplayName;
        _description = visualizer.Description;
        Apply(presentation);
    }

    public CatalogItemKind Kind { get; } = CatalogItemKind.Strategy;
    public ITradingStrategy? Strategy { get; }
    public VisualizerDescriptor? Visualizer { get; }

    public string Id => Strategy?.Id ?? Visualizer!.Id;
    public string KindLabel => Kind == CatalogItemKind.Strategy ? "STRATEGY" : "VISUALIZER";
    public string KindForegroundResourceKey => Kind == CatalogItemKind.Strategy ? "Ai.Glow.Brush" : "Accent.Brush";
    public string KindBackgroundResourceKey => Kind == CatalogItemKind.Strategy ? "Ai.Soft" : "Accent.Soft";
    public string PrimaryActionLabel => Kind == CatalogItemKind.Strategy ? "Open" : "Add to chart (Basic)";
    public string EditActionLabel => Kind == CatalogItemKind.Strategy ? "Edit strategy card…" : "Edit card";

    /// <summary>Engine id for Quick Backtest — explicit mapping, else the live strategy id
    /// (authored strategies register under their script id).</summary>
    public string? ResolvedBacktestStrategyId =>
        Strategy is null ? null
        : !string.IsNullOrWhiteSpace(Strategy.BacktestStrategyId) ? Strategy.BacktestStrategyId
        : Strategy.Id;

    /// <summary>Set by the shell from <c>IBacktestStrategyRegistry</c> so the Quick Backtest
    /// menu only appears when a runnable engine option exists.</summary>
    [ObservableProperty] private bool _hasQuickBacktest;

    public IReadOnlyList<string> DataRequirementTags => Visualizer?.DataRequirementTags ?? [];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string? _linkUrl;
    [ObservableProperty] private string? _formula;
    [ObservableProperty] private string? _imagePath;

    public ObservableCollection<string> CustomTags { get; } = [];

    public bool HasFormula => !string.IsNullOrWhiteSpace(Formula);
    public bool HasCustomTags => CustomTags.Count > 0;
    public Uri? LinkUri => Uri.TryCreate(LinkUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
    public bool HasLink => LinkUri is not null;

    partial void OnFormulaChanged(string? value) => OnPropertyChanged(nameof(HasFormula));
    partial void OnLinkUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(LinkUri));
        OnPropertyChanged(nameof(HasLink));
    }

    public void Apply(StrategyPresentation presentation)
    {
        var defaultName = Strategy?.DisplayName ?? Visualizer!.DisplayName;
        var defaultDescription = Strategy?.Description ?? Visualizer!.Description;
        var defaultImagePath = Visualizer?.ImagePath;

        Name = string.IsNullOrWhiteSpace(presentation.Name) ? defaultName : presentation.Name!;
        Description = string.IsNullOrWhiteSpace(presentation.Description) ? defaultDescription : presentation.Description!;
        LinkUrl = string.IsNullOrWhiteSpace(presentation.LinkUrl) ? Strategy?.LinkUrl : presentation.LinkUrl.Trim();
        Formula = string.IsNullOrWhiteSpace(presentation.Formula) ? null : presentation.Formula;
        ImagePath = string.IsNullOrWhiteSpace(presentation.ImagePath) ? defaultImagePath : presentation.ImagePath;

        CustomTags.Clear();
        foreach (var tag in presentation.Tags ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(tag)) CustomTags.Add(tag.Trim());
        OnPropertyChanged(nameof(HasCustomTags));
    }
}
