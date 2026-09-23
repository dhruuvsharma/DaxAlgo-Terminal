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

    /// <summary>
    /// A strategy authored in the app: an <c>IStrategyKernel</c> from the kernel registry.
    ///
    /// <para>A third backing on purpose. It is a STRATEGY — same card colour, same "Open" — but it is
    /// not an <c>ITradingStrategy</c>, which is the retired contract the plugin factory still holds,
    /// and it is not a visualizer, which has no book. Folding it into either would route Open to
    /// machinery that cannot run it.</para>
    /// </summary>
    public StrategyCatalogItemViewModel(StrategyKernelRegistration kernel)
        : this(kernel, StrategyPresentationStore.Get(kernel.Id)) { }

    public StrategyCatalogItemViewModel(StrategyKernelRegistration kernel, StrategyPresentation presentation)
    {
        Kernel = kernel;
        _name = kernel.Descriptor.DisplayName;
        _description = kernel.Descriptor.Description;
        Apply(presentation);
    }

    /// <summary>
    /// A unit hosted by something above this library — today, a unit written against the Blocks SDK,
    /// with its own web page.
    ///
    /// <para>A fourth backing, for the reason the third was one: it is a strategy or a visualizer by what
    /// it does, but it is neither contract, and Open has to reach the host that runs it. Described
    /// rather than referenced, so this library stays below the runtimes that host such units.</para>
    /// </summary>
    public StrategyCatalogItemViewModel(HostedCatalogUnit hosted)
        : this(hosted, StrategyPresentationStore.Get(hosted.Id)) { }

    public StrategyCatalogItemViewModel(HostedCatalogUnit hosted, StrategyPresentation presentation)
    {
        HostedUnit = hosted;
        Kind = hosted.IsStrategy ? CatalogItemKind.Strategy : CatalogItemKind.Visualizer;
        _name = hosted.DisplayName;
        _description = hosted.Description;
        Apply(presentation);
    }

    public CatalogItemKind Kind { get; } = CatalogItemKind.Strategy;
    public ITradingStrategy? Strategy { get; }
    public VisualizerDescriptor? Visualizer { get; }

    /// <summary>Set when this row is an authored kernel rather than a plugin strategy.</summary>
    public StrategyKernelRegistration? Kernel { get; }

    /// <summary>Set when this row is a unit hosted above this library (a Blocks unit).</summary>
    public HostedCatalogUnit? HostedUnit { get; }

    public string Id => Strategy?.Id ?? Kernel?.Id ?? HostedUnit?.Id ?? Visualizer!.Id;

    /// <summary>The backing unit's own name — what the card shows when there is no override. Defined
    /// once for every backing so the card and its editor cannot disagree about what "default" means,
    /// and a fifth backing is handled in one place rather than found by a crash in another.</summary>
    public string DefaultName => Strategy?.DisplayName ?? Descriptor?.DisplayName ?? HostedUnit?.DisplayName ?? string.Empty;
    public string DefaultDescription => Strategy?.Description ?? Descriptor?.Description ?? HostedUnit?.Description ?? string.Empty;
    public string? DefaultLinkUrl => Strategy?.LinkUrl;

    private VisualizerDescriptor? Descriptor => Visualizer ?? Kernel?.Descriptor;
    public string KindLabel => Kind == CatalogItemKind.Strategy ? "STRATEGY" : "VISUALIZER";
    public string KindForegroundResourceKey => Kind == CatalogItemKind.Strategy ? "Ai.Glow.Brush" : "Accent.Brush";
    public string KindBackgroundResourceKey => Kind == CatalogItemKind.Strategy ? "Ai.Soft" : "Accent.Soft";
    public string PrimaryActionLabel => Kind == CatalogItemKind.Strategy ? "Open" : "Add to chart";
    public string EditActionLabel => Kind == CatalogItemKind.Strategy ? "Edit strategy card…" : "Edit card";
    /// <summary>Quick backtest is a plugin-strategy affordance. An authored kernel has no engine
    /// behind it — the backtester was archived — so offering it would be offering nothing.</summary>
    public bool HasQuickBacktest => Kind == CatalogItemKind.Strategy && Kernel is null && HostedUnit is null;

    public IReadOnlyList<string> DataRequirementTags =>
        Visualizer?.DataRequirementTags ?? Kernel?.Descriptor.DataRequirementTags ?? [];

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
        // Four backings now, so the null-forgiving `Visualizer!` that was safe with two is not. An
        // authored kernel or a hosted unit has neither a Strategy nor a Visualizer, and this threw a
        // NullReferenceException out of the constructor — meaning the catalog crashed the moment a
        // user registered a strategy in Hyperion, which is the one path the card exists for. The card
        // editor then made the same mistake on its own, so the defaults now live in DefaultName and
        // DefaultDescription and both read them.
        Name = string.IsNullOrWhiteSpace(presentation.Name) ? DefaultName : presentation.Name!;
        Description = string.IsNullOrWhiteSpace(presentation.Description) ? DefaultDescription : presentation.Description!;
        LinkUrl = string.IsNullOrWhiteSpace(presentation.LinkUrl) ? DefaultLinkUrl : presentation.LinkUrl.Trim();
        Formula = string.IsNullOrWhiteSpace(presentation.Formula) ? null : presentation.Formula;
        ImagePath = string.IsNullOrWhiteSpace(presentation.ImagePath) ? Descriptor?.ImagePath : presentation.ImagePath;

        CustomTags.Clear();
        foreach (var tag in presentation.Tags ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(tag)) CustomTags.Add(tag.Trim());
        OnPropertyChanged(nameof(HasCustomTags));
    }
}
