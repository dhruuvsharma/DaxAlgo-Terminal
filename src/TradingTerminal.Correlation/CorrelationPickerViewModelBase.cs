using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Correlation;

/// <summary>
/// Shared base for the two correlation tools — the on-demand historical
/// <see cref="CorrelationMatrixViewModel"/> and the live, real-time
/// <see cref="LiveCorrelationMatrixViewModel"/>. It owns everything the two have in common: the
/// searchable, category-grouped, multi-select instrument checklist (each row tagged with its
/// source broker so two brokers can be correlated side by side), and the rendered NxN grid
/// (<see cref="Labels"/> + <see cref="MatrixRows"/>) with its status/sample readouts. Subclasses
/// supply the data path — a historical bar fetch vs. a live rolling sampler — and hand the computed
/// <see cref="CorrelationMatrix"/> to <see cref="BuildMatrix"/> on the UI thread.
/// </summary>
public abstract partial class CorrelationPickerViewModelBase : ViewModelBase
{
    protected const string AllCategories = "All categories";

    /// <summary>Hard cap on how many rows the checklist shows at once. A multi-broker universe is
    /// thousands of symbols (Alpaca alone ≈10k); dumping all of them into a sorted+grouped view froze
    /// the window on open. We show the first N (selected rows always pinned) and let search narrow —
    /// the same bounded-picker pattern the other tool windows use.</summary>
    private const int MaxVisible = 500;

    protected IMarketDataRepository Repository { get; }
    protected IBrokerSelector Selector { get; }
    protected ILogger Logger { get; }

    // Master list — holds the live IsSelected state. The filtered `Instruments` collection is
    // rebuilt from these same instances so selection survives a search/category change.
    protected IReadOnlyList<SelectableInstrument> AllInstruments { get; private set; } = Array.Empty<SelectableInstrument>();

    protected CorrelationPickerViewModelBase(
        IMarketDataRepository repository, IBrokerSelector selector, ILogger logger)
    {
        Repository = repository;
        Selector = selector;
        Logger = logger;

        Instruments = new ObservableCollection<SelectableInstrument>();
        Categories = new ObservableCollection<string> { AllCategories };

        // Group the picker by canonical category, ordered category-then-symbol so the headers
        // (Crypto, FX, Commodities, Indices, ETFs, Stocks, …) come out in a predictable order.
        InstrumentsView = CollectionViewSource.GetDefaultView(Instruments);
        InstrumentsView.SortDescriptions.Add(new SortDescription(nameof(SelectableInstrument.CategoryOrder), ListSortDirection.Ascending));
        InstrumentsView.SortDescriptions.Add(new SortDescription(nameof(SelectableInstrument.CanonicalCategory), ListSortDirection.Ascending));
        InstrumentsView.SortDescriptions.Add(new SortDescription(nameof(SelectableInstrument.Symbol), ListSortDirection.Ascending));
        InstrumentsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SelectableInstrument.CanonicalCategory)));

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<SelectableInstrument> Instruments { get; }
    public ICollectionView InstrumentsView { get; }
    public ObservableCollection<string> Categories { get; }

    [ObservableProperty] private string _selectedCategory = AllCategories;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Loading instruments…";
    [ObservableProperty] private int _sampleCount;

    /// <summary>How many rows the checklist is currently showing (after the cap/filter) and how many
    /// exist in total — drives the "showing X of N" chip.</summary>
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _totalCount;

    /// <summary>The latest computed matrix, rendered as one immutable snapshot by
    /// <see cref="CorrelationMatrixControl"/> — a single property change per update instead of
    /// rebuilding N² cell elements (which made the live tool stutter at its sample cadence).</summary>
    [ObservableProperty] private CorrelationMatrix? _matrixResult;

    public int SelectedCount => AllInstruments.Count(i => i.IsSelected);

    /// <summary>The currently ticked instruments, in picker order. Subclasses correlate these.</summary>
    protected IReadOnlyList<SelectableInstrument> SelectedInstruments =>
        AllInstruments.Where(i => i.IsSelected).ToList();

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0)
            {
                StatusMessage = "No instruments — connect a broker first.";
                return;
            }

            // Wrap + classify + dedupe OFF the UI thread. A multi-broker universe is thousands of
            // rows and the category classification is pure CPU; running it on the dispatcher (as the
            // old await-continuation did) is what froze the window the moment it opened.
            var (wrapped, present) = await Task.Run(() =>
            {
                var seen = new HashSet<(BrokerKind, string, string)>();
                var items = new List<SelectableInstrument>(list.Count);
                foreach (var i in list)
                {
                    var key = (i.Broker, i.Contract.Symbol ?? string.Empty, i.Contract.SecType ?? string.Empty);
                    if (!seen.Add(key)) continue; // drop exact (broker, symbol, type) duplicates
                    items.Add(new SelectableInstrument(i.DisplayName, i.Category, i.Contract, i.Broker));
                }
                var cats = items.Select(w => w.CanonicalCategory).Distinct().OrderBy(InstrumentCategory.OrderOf).ToList();
                return (items, cats);
            });

            foreach (var w in wrapped)
                w.SelectionChanged += OnInstrumentSelectionChanged;

            AllInstruments = wrapped;
            TotalCount = wrapped.Count;

            Categories.Clear();
            Categories.Add(AllCategories);
            foreach (var c in present) Categories.Add(c);
            SelectedCategory = AllCategories;

            ApplyFilter();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Correlation picker: instrument load failed");
            StatusMessage = $"Instrument load failed: {ex.Message}";
        }
    }

    private void OnInstrumentSelectionChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(SelectedCount));

    private void ApplyFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        var category = SelectedCategory ?? AllCategories;

        IEnumerable<SelectableInstrument> query = AllInstruments;
        if (category != AllCategories)
            query = query.Where(i => i.CanonicalCategory == category);
        if (term.Length > 0)
            query = query.Where(i =>
                i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.BrokerAbbrev.Contains(term, StringComparison.OrdinalIgnoreCase));

        var shown = query.Take(MaxVisible).ToList();
        // Ticked rows must stay visible even if they fall outside the cap/filter, so selection survives.
        foreach (var sel in AllInstruments.Where(i => i.IsSelected))
            if (!shown.Contains(sel)) shown.Add(sel);

        // Repopulate the (now capped) visible set. We deliberately do NOT wrap this in
        // InstrumentsView.DeferRefresh(): that defers the *view's* refresh while the source
        // ObservableCollection still raises CollectionChanged, which ListCollectionView then tries to
        // process — and it throws "Cannot change … while Refresh is being deferred". With the 500-row
        // cap the per-item sorted/grouped inserts are cheap, so a plain Clear + Add is correct here.
        Instruments.Clear();
        foreach (var inst in shown) Instruments.Add(inst);

        VisibleCount = shown.Count;
        StatusMessage = TotalCount == 0
            ? "Loading instruments…"
            : VisibleCount < TotalCount
                ? $"Showing {VisibleCount} of {TotalCount} — search or pick a category to narrow. Tick ≥2 to begin."
                : $"{TotalCount} instruments — tick at least two to begin.";
    }

    /// <summary>Releases everything the picker base holds so the window+VM can be collected: detaches
    /// the per-row selection handlers (which capture this VM), drops the instrument lists and the last
    /// matrix snapshot. Subclasses call this from their own Dispose after tearing down data streams.</summary>
    protected void CleanupInstruments()
    {
        foreach (var i in AllInstruments) i.SelectionChanged -= OnInstrumentSelectionChanged;
        AllInstruments = Array.Empty<SelectableInstrument>();
        Instruments.Clear();
        MatrixResult = null;
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var inst in Instruments) inst.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var inst in AllInstruments) inst.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>Publishes a computed matrix to the grid (one snapshot swap — the rendering control
    /// redraws in a single pass). Must be called on the UI thread.</summary>
    protected void BuildMatrix(CorrelationMatrix result)
    {
        MatrixResult = result;
        SampleCount = result.SampleCount;
    }

    /// <summary>Disambiguates matrix labels by broker only when the set spans more than one broker,
    /// so a single-broker matrix stays clean ("ES") while a cross-broker one is explicit
    /// ("ES·IB" vs "ES·CT"). Shared by both correlation tools.</summary>
    protected static IReadOnlyList<string> LabelFor(IReadOnlyList<SelectableInstrument> instruments)
    {
        bool multiBroker = instruments.Select(i => i.Broker).Distinct().Count() > 1;
        return instruments
            .Select(i => multiBroker ? $"{i.Symbol}·{i.BrokerAbbrev}" : i.Symbol)
            .ToList();
    }
}

/// <summary>Checklist row wrapping a broker instrument with a bindable selection flag. Carries the
/// normalized <see cref="CanonicalCategory"/> (for grouping/filtering) and the source broker (so
/// the picker can show "AAPL [IB]" vs "AAPL [AL]" and a cross-broker matrix can disambiguate).</summary>
public sealed partial class SelectableInstrument : ObservableObject
{
    public SelectableInstrument(string displayName, string category, Contract contract, BrokerKind broker)
    {
        DisplayName = displayName;
        RawCategory = category;
        Contract = contract;
        Broker = broker;

        CanonicalCategory = InstrumentCategory.Classify(category, contract);
        CategoryOrder = InstrumentCategory.OrderOf(CanonicalCategory);
        BrokerAbbrev = InstrumentCategory.BrokerAbbrev(broker);
    }

    public string DisplayName { get; }
    public string RawCategory { get; }
    public Contract Contract { get; }
    public BrokerKind Broker { get; }

    public string CanonicalCategory { get; }
    public int CategoryOrder { get; }
    public string BrokerAbbrev { get; }

    public string Symbol => Contract.Symbol;

    [ObservableProperty] private bool _isSelected;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Folds each broker's free-text category (IB "Index ETFs"/"Spot Forex", Alpaca "Crypto",
/// cTrader "FX"/"Metals"/"Energy / Commodities"/"Indices", …) into one canonical set of buckets so
/// the picker groups consistently regardless of which broker supplied the row. Continuous-futures
/// rows are routed to Indices or Commodities by their root symbol.
/// </summary>
internal static class InstrumentCategory
{
    public const string Crypto = "Crypto";
    public const string Fx = "FX";
    public const string Commodities = "Commodities";
    public const string Indices = "Indices";
    public const string Etfs = "ETFs";
    public const string Stocks = "Stocks";
    public const string Futures = "Futures";
    public const string Other = "Other";

    private static readonly string[] Order = { Crypto, Fx, Commodities, Indices, Etfs, Stocks, Futures, Other };

    private static readonly HashSet<string> IndexFutures = new(StringComparer.OrdinalIgnoreCase)
        { "ES", "NQ", "YM", "RTY", "MES", "MNQ", "MYM", "M2K", "NKD", "VX", "DAX", "FDAX", "FESX" };

    private static readonly HashSet<string> CommodityFutures = new(StringComparer.OrdinalIgnoreCase)
        { "CL", "GC", "SI", "NG", "HG", "PL", "PA", "RB", "HO", "BZ", "MCL", "MGC", "SIL",
          "ZC", "ZS", "ZW", "ZL", "ZM", "HE", "LE", "GF", "KC", "SB", "CC", "CT", "OJ" };

    public static int OrderOf(string category)
    {
        int idx = Array.IndexOf(Order, category);
        return idx < 0 ? Order.Length : idx;
    }

    public static string Classify(string? rawCategory, Contract? contract)
    {
        var raw = (rawCategory ?? string.Empty).ToLowerInvariant();
        var sec = (contract?.SecType ?? string.Empty).ToUpperInvariant();
        var sym = contract?.Symbol ?? string.Empty;

        if (raw.Contains("crypto")) return Crypto;
        if (sec == "CASH" || raw.Contains("forex") || raw == "fx" || raw.StartsWith("fx ")) return Fx;
        if (raw.Contains("etf")) return Etfs;
        if (raw.Contains("metal") || raw.Contains("energy") || raw.Contains("commodit")) return Commodities;
        if (raw.Contains("indic")) return Indices;

        if (sec is "CONTFUT" or "FUT" || raw.Contains("future"))
        {
            var root = RootSymbol(sym);
            if (IndexFutures.Contains(root)) return Indices;
            if (CommodityFutures.Contains(root)) return Commodities;
            return Futures;
        }

        if (sec == "STK" || raw.Contains("stock") || raw.Contains("equit")) return Stocks;
        return Other;
    }

    /// <summary>Short broker tag shown on each picker row and in cross-broker matrix labels.</summary>
    public static string BrokerAbbrev(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NT",
        BrokerKind.CTrader => "CT",
        BrokerKind.Alpaca => "AL",
        _ => broker.ToString().ToUpperInvariant(),
    };

    private static string RootSymbol(string symbol) =>
        new string(symbol.TakeWhile(char.IsLetter).ToArray());
}

