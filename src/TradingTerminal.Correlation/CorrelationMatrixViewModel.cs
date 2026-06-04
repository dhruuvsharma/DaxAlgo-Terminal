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
/// Drives the Correlation Matrix window: a searchable, category-grouped multi-select instrument
/// checklist (each row tagged with its source broker so two brokers can be correlated side by
/// side), a timeframe + lookback picker, and an on-demand Compute that pulls historical bars per
/// instrument (<see cref="IMarketDataRepository.GetHistoricalBarsAsync"/> — cache-first, then a
/// broker fetch on miss), aligns them by timestamp, and renders the NxN Pearson-on-log-returns
/// matrix via <see cref="CorrelationCalculator"/>.
/// </summary>
public sealed partial class CorrelationMatrixViewModel : ViewModelBase, IDisposable
{
    private const string AllCategories = "All categories";

    // Brokers (IB especially) reject simultaneous historical-data bursts with pacing-violation
    // errors, so a single Compute that fired one reqHistoricalData per instrument at once would
    // see a random subset fail. We cap how many are in flight and retry the ones that hit a
    // rate limit, so the user never has to reclick to "fill in" the misses.
    private const int MaxConcurrentFetches = 3;

    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<CorrelationMatrixViewModel> _logger;

    private CancellationTokenSource? _runCts;

    // Master list — holds the live IsSelected state. The filtered `Instruments` collection is
    // rebuilt from these same instances so ticks survive a search/category change.
    private IReadOnlyList<SelectableInstrument> _allInstruments = Array.Empty<SelectableInstrument>();

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("5 min",  BarSize.FiveMinutes),
        new("15 min", BarSize.FifteenMinutes),
        new("1 hour", BarSize.OneHour),
        new("1 day",  BarSize.OneDay),
    };

    private static readonly IReadOnlyList<LookbackOption> AllLookbacks = new LookbackOption[]
    {
        new("30 days",  TimeSpan.FromDays(30)),
        new("90 days",  TimeSpan.FromDays(90)),
        new("180 days", TimeSpan.FromDays(180)),
        new("1 year",   TimeSpan.FromDays(365)),
    };

    public CorrelationMatrixViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<CorrelationMatrixViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneDay);

        Lookbacks = new ObservableCollection<LookbackOption>(AllLookbacks);
        SelectedLookback = Lookbacks.First(l => l.Duration == TimeSpan.FromDays(90));

        Instruments = new ObservableCollection<SelectableInstrument>();
        Categories = new ObservableCollection<string> { AllCategories };
        Labels = new ObservableCollection<string>();
        MatrixRows = new ObservableCollection<CorrelationRow>();

        // Group the picker by canonical category, ordered category-then-symbol so the headers
        // (Crypto, FX, Commodities, Indices, ETFs, Stocks, …) come out in a predictable order.
        InstrumentsView = CollectionViewSource.GetDefaultView(Instruments);
        InstrumentsView.SortDescriptions.Add(new SortDescription(nameof(SelectableInstrument.CategoryOrder), ListSortDirection.Ascending));
        InstrumentsView.SortDescriptions.Add(new SortDescription(nameof(SelectableInstrument.CanonicalCategory), ListSortDirection.Ascending));
        InstrumentsView.SortDescriptions.Add(new SortDescription(nameof(SelectableInstrument.Symbol), ListSortDirection.Ascending));
        InstrumentsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SelectableInstrument.CanonicalCategory)));

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<LookbackOption> Lookbacks { get; }
    public ObservableCollection<SelectableInstrument> Instruments { get; }
    public ICollectionView InstrumentsView { get; }
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<string> Labels { get; }
    public ObservableCollection<CorrelationRow> MatrixRows { get; }

    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private LookbackOption? _selectedLookback;
    [ObservableProperty] private string _selectedCategory = AllCategories;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Loading instruments…";
    [ObservableProperty] private int _sampleCount;

    public int SelectedCount => _allInstruments.Count(i => i.IsSelected);

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0)
            {
                StatusMessage = "No instruments — connect a broker first.";
                return;
            }

            var wrapped = list
                .Select(i => new SelectableInstrument(i.DisplayName, i.Category, i.Contract, i.Broker))
                .ToList();

            foreach (var w in wrapped)
                w.SelectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));

            _allInstruments = wrapped;

            // Category filter options: "All" + every canonical bucket that's actually present,
            // ordered the same way the groups render.
            var present = wrapped
                .Select(w => w.CanonicalCategory)
                .Distinct()
                .OrderBy(InstrumentCategory.OrderOf)
                .ToList();
            Categories.Clear();
            Categories.Add(AllCategories);
            foreach (var c in present) Categories.Add(c);
            SelectedCategory = AllCategories;

            ApplyFilter();
            StatusMessage = $"{wrapped.Count} instruments across {present.Count} categories — tick at least two, then Compute.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Correlation matrix: instrument load failed");
            StatusMessage = $"Instrument load failed: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        var category = SelectedCategory ?? AllCategories;

        IEnumerable<SelectableInstrument> query = _allInstruments;
        if (category != AllCategories)
            query = query.Where(i => i.CanonicalCategory == category);
        if (term.Length > 0)
            query = query.Where(i =>
                i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.BrokerAbbrev.Contains(term, StringComparison.OrdinalIgnoreCase));

        Instruments.Clear();
        foreach (var inst in query)
            Instruments.Add(inst);
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
        foreach (var inst in _allInstruments) inst.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private async Task ComputeAsync()
    {
        if (IsBusy) return;

        var selected = _allInstruments.Where(i => i.IsSelected).ToList();
        if (selected.Count < 2)
        {
            StatusMessage = "Select at least two instruments.";
            return;
        }
        if (SelectedTimeframe is null || SelectedLookback is null)
        {
            StatusMessage = "Pick a timeframe and lookback.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Fetching {selected.Count} instruments…";
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var barSize = SelectedTimeframe.BarSize;
            var duration = SelectedLookback.Duration;

            var results = await FetchAllAsync(selected, barSize, duration, ct);

            // One automatic retry for transient (rate-limit) misses, run sequentially so the
            // retry itself can't re-trigger the burst that caused them.
            var transient = results.Where(f => f.Transient).Select(f => f.Instrument).ToList();
            if (transient.Count > 0 && !ct.IsCancellationRequested)
            {
                StatusMessage = $"Rate-limited on {transient.Count} — retrying…";
                for (int i = 0; i < results.Count; i++)
                {
                    if (!results[i].Transient) continue;
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    var retry = await FetchBarsAsync(results[i].Instrument, barSize, duration, ct);
                    if (retry.SkipReason is null || !retry.Transient)
                        results[i] = retry;
                }
            }

            var usable = results.Where(f => f.SkipReason is null).ToList();
            var skipped = results.Where(f => f.SkipReason is not null)
                .Select(f => $"{f.Instrument.Symbol} ({f.SkipReason})")
                .ToList();

            if (usable.Count < 2)
            {
                StatusMessage = "Not enough historical data to correlate (need ≥2 instruments with bars)."
                    + (skipped.Count > 0 ? $" Skipped: {string.Join(", ", skipped)}." : string.Empty);
                Labels.Clear();
                MatrixRows.Clear();
                SampleCount = 0;
                return;
            }

            // Disambiguate labels by broker only when the selection spans more than one broker,
            // so a single-broker matrix stays clean ("ES") while a cross-broker one is explicit
            // ("ES·IB" vs "ES·CT").
            bool multiBroker = usable.Select(u => u.Instrument.Broker).Distinct().Count() > 1;

            var result = await Task.Run(() =>
            {
                var series = usable.Select(u => (IReadOnlyList<Bar>)u.Bars).ToList();
                var (timestamps, aligned) = CorrelationCalculator.AlignByTimestamp(series);
                var returns = aligned.Select(a => CorrelationCalculator.LogReturns(a)).ToList();
                var matrix = CorrelationCalculator.PearsonMatrix(returns);
                int samples = timestamps.Count > 0 ? timestamps.Count - 1 : 0;
                var labels = usable.Select(u => multiBroker
                    ? $"{u.Instrument.Symbol}·{u.Instrument.BrokerAbbrev}"
                    : u.Instrument.Symbol).ToList();
                return new CorrelationMatrix(labels, matrix, samples);
            }, ct);

            ct.ThrowIfCancellationRequested();
            BuildMatrix(result);

            StatusMessage = result.SampleCount < 20
                ? $"Computed over {result.SampleCount} bars (low sample — treat with caution)."
                : $"Computed over {result.SampleCount} aligned bars.";
            if (skipped.Count > 0)
                StatusMessage += $" Skipped: {string.Join(", ", skipped)}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Correlation matrix compute failed");
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fetches every selected instrument with a bounded number in flight at once. Firing one
    /// historical request per instrument simultaneously trips broker pacing limits (IB error 162),
    /// so a single Compute would otherwise see a random subset fail. The semaphore caps the burst;
    /// the per-instrument result order is preserved so callers can do an indexed retry pass.
    /// </summary>
    private async Task<List<FetchResult>> FetchAllAsync(
        IReadOnlyList<SelectableInstrument> instruments, BarSize barSize, TimeSpan duration, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentFetches);
        async Task<FetchResult> Gated(SelectableInstrument inst)
        {
            await gate.WaitAsync(ct);
            try { return await FetchBarsAsync(inst, barSize, duration, ct); }
            finally { gate.Release(); }
        }

        var results = await Task.WhenAll(instruments.Select(Gated));
        return results.ToList();
    }

    /// <summary>
    /// Fetches bars for one instrument from <em>its own</em> broker (contracts are broker-specific,
    /// so we never fan a broker-B contract at broker A). The repository is cache-first and fetches
    /// from the broker on a miss, so "data not present locally" resolves itself as long as the
    /// instrument's broker is connected. Returns a non-null <see cref="FetchResult.SkipReason"/>
    /// when the row can't contribute (and flags <see cref="FetchResult.Transient"/> for failures
    /// worth retrying, e.g. a rate limit), so Compute can report exactly why and retry the misses.
    /// </summary>
    private async Task<FetchResult> FetchBarsAsync(
        SelectableInstrument inst, BarSize barSize, TimeSpan duration, CancellationToken ct)
    {
        if (!_selector.IsConnected(inst.Broker))
            return new FetchResult(inst, Array.Empty<Bar>(), $"{inst.BrokerAbbrev} not connected");

        try
        {
            var bars = await _repository.GetHistoricalBarsAsync(inst.Contract, inst.Broker, barSize, duration, ct);
            if (bars is null || bars.Count < 2)
                return new FetchResult(inst, bars ?? Array.Empty<Bar>(), "no data from broker");
            return new FetchResult(inst, bars, SkipReason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A broker fetch can fail transiently (IB pacing, a momentary disconnect). Mark it
            // retryable so Compute can take a second pass instead of the user reclicking.
            _logger.LogWarning(ex, "Correlation matrix: bars unavailable for {Symbol} on {Broker}", inst.Symbol, inst.Broker);
            return new FetchResult(inst, Array.Empty<Bar>(), "fetch failed", Transient: true);
        }
    }

    private void BuildMatrix(CorrelationMatrix result)
    {
        Labels.Clear();
        foreach (var label in result.Labels)
            Labels.Add(label);

        MatrixRows.Clear();
        for (int i = 0; i < result.Size; i++)
        {
            var cells = new List<CorrelationCell>(result.Size);
            for (int j = 0; j < result.Size; j++)
            {
                double v = result.At(i, j);
                cells.Add(new CorrelationCell(
                    Value: v,
                    Display: v.ToString("+0.00;-0.00;0.00"),
                    RowLabel: result.Labels[i],
                    ColLabel: result.Labels[j]));
            }
            MatrixRows.Add(new CorrelationRow(result.Labels[i], cells));
        }

        SampleCount = result.SampleCount;
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
    }
}

/// <summary>Per-instrument fetch outcome. <see cref="SkipReason"/> is null when the bars are
/// usable; otherwise it's a short human-readable reason surfaced in the status line.
/// <see cref="Transient"/> marks failures worth one automatic retry (e.g. a broker rate limit).</summary>
internal sealed record FetchResult(
    SelectableInstrument Instrument, IReadOnlyList<Bar> Bars, string? SkipReason, bool Transient = false);

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

/// <summary>One row of the rendered matrix: a row header plus its cells (one per column).</summary>
public sealed record CorrelationRow(string Header, IReadOnlyList<CorrelationCell> Cells);

/// <summary>A single matrix cell. <see cref="Value"/> feeds the diverging-colour converter;
/// <see cref="Display"/> is the formatted number shown in the cell.</summary>
public sealed record CorrelationCell(double Value, string Display, string RowLabel, string ColLabel);

/// <summary>Bar-size dropdown row.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}

/// <summary>Lookback-window dropdown row.</summary>
public sealed record LookbackOption(string Label, TimeSpan Duration)
{
    public override string ToString() => Label;
}
