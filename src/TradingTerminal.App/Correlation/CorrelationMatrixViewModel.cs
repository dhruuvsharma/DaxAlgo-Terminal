using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.App.Correlation;

/// <summary>
/// Drives the Correlation Matrix window: a searchable multi-select instrument checklist, a
/// timeframe + lookback picker, and an on-demand Compute that pulls historical bars per instrument
/// (<see cref="IMarketDataRepository.GetHistoricalBarsAsync"/>), aligns them by timestamp, and
/// renders the NxN Pearson-on-log-returns matrix via <see cref="CorrelationCalculator"/>.
/// </summary>
public sealed partial class CorrelationMatrixViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<CorrelationMatrixViewModel> _logger;

    private CancellationTokenSource? _runCts;

    // Master list — holds the live IsSelected state. The filtered `Instruments` collection is
    // rebuilt from these same instances so ticks survive a search.
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
        Labels = new ObservableCollection<string>();
        MatrixRows = new ObservableCollection<CorrelationRow>();

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<LookbackOption> Lookbacks { get; }
    public ObservableCollection<SelectableInstrument> Instruments { get; }
    public ObservableCollection<string> Labels { get; }
    public ObservableCollection<CorrelationRow> MatrixRows { get; }

    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private LookbackOption? _selectedLookback;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Loading instruments…";
    [ObservableProperty] private int _sampleCount;

    public int SelectedCount => _allInstruments.Count(i => i.IsSelected);

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();

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
            ApplyFilter();
            StatusMessage = $"{wrapped.Count} instruments — tick at least two, then Compute.";
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
        IEnumerable<SelectableInstrument> query = _allInstruments;
        if (term.Length > 0)
            query = _allInstruments.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

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

            var fetched = await Task.WhenAll(selected.Select(inst => FetchBarsAsync(inst, barSize, duration, ct)));

            var usable = fetched.Where(f => f.Bars.Count >= 2).ToList();
            var skipped = fetched.Where(f => f.Bars.Count < 2).Select(f => f.Instrument.Symbol).ToList();

            if (usable.Count < 2)
            {
                StatusMessage = "Not enough historical data to correlate (need ≥2 instruments with bars).";
                Labels.Clear();
                MatrixRows.Clear();
                SampleCount = 0;
                return;
            }

            var result = await Task.Run(() =>
            {
                var series = usable.Select(u => (IReadOnlyList<Bar>)u.Bars).ToList();
                var (timestamps, aligned) = CorrelationCalculator.AlignByTimestamp(series);
                var returns = aligned.Select(a => CorrelationCalculator.LogReturns(a)).ToList();
                var matrix = CorrelationCalculator.PearsonMatrix(returns);
                int samples = timestamps.Count > 0 ? timestamps.Count - 1 : 0;
                var labels = usable.Select(u => u.Instrument.Symbol).ToList();
                return new CorrelationMatrix(labels, matrix, samples);
            }, ct);

            ct.ThrowIfCancellationRequested();
            BuildMatrix(result);

            StatusMessage = result.SampleCount < 20
                ? $"Computed over {result.SampleCount} bars (low sample — treat with caution)."
                : $"Computed over {result.SampleCount} aligned bars.";
            if (skipped.Count > 0)
                StatusMessage += $" Skipped (no data): {string.Join(", ", skipped)}.";
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

    private async Task<(SelectableInstrument Instrument, IReadOnlyList<Bar> Bars)> FetchBarsAsync(
        SelectableInstrument inst, BarSize barSize, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var broker = ResolveBroker(inst);
            var bars = await _repository.GetHistoricalBarsAsync(inst.Contract, broker, barSize, duration, ct);
            return (inst, bars ?? Array.Empty<Bar>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Correlation matrix: bars unavailable for {Symbol}", inst.Symbol);
            return (inst, Array.Empty<Bar>());
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

    /// <summary>Prefer the instrument's own broker when connected; otherwise fall back to the first
    /// connected broker. Throws when nothing is connected so the user gets a clear message.</summary>
    private BrokerKind ResolveBroker(SelectableInstrument instrument)
    {
        if (_selector.IsConnected(instrument.Broker))
            return instrument.Broker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker first.");
        return connected[0];
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
    }
}

/// <summary>Checklist row wrapping a broker instrument with a bindable selection flag.</summary>
public sealed partial class SelectableInstrument : ObservableObject
{
    public SelectableInstrument(string displayName, string category, Contract contract, BrokerKind broker)
    {
        DisplayName = displayName;
        Category = category;
        Contract = contract;
        Broker = broker;
    }

    public string DisplayName { get; }
    public string Category { get; }
    public Contract Contract { get; }
    public BrokerKind Broker { get; }

    public string Symbol => Contract.Symbol;

    [ObservableProperty] private bool _isSelected;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
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
