using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.AdvancedRegime;
using TradingTerminal.UI;

namespace TradingTerminal.AdvancedMarketRegime;

/// <summary>
/// View-model for the Advanced Live Market Regime dashboard: an 18-row × 8-timeframe indicator
/// table ported from a TradingView multi-timeframe dashboard. Rows and columns toggle
/// independently; each cell shows a direction glyph + compact value on a signal-coloured
/// background, and the Trend row renders the composite −8..+8 needle-angle text gauge.
/// Bars come from the canonical pipeline via <see cref="IAdvancedRegimeProvider"/> — never a
/// broker directly. Snapshots are computed with every row enabled and row toggles are applied
/// at projection time, so hiding/showing a row never refetches.
/// </summary>
public sealed partial class AdvancedMarketRegimeViewModel : ViewModelBase, IDisposable
{
    private readonly IAdvancedRegimeProvider _provider;
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<AdvancedMarketRegimeViewModel> _logger;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _autoCts;
    private AdvancedRegimeSnapshot? _lastSnapshot;

    public const int MaxInstrumentsDisplayed = 500;

    private static readonly (AdvancedIndicatorRow Row, string Label)[] RowCatalog =
    {
        (AdvancedIndicatorRow.Rsi, "RSI"),
        (AdvancedIndicatorRow.Macd, "MACD"),
        (AdvancedIndicatorRow.Cci, "CCI"),
        (AdvancedIndicatorRow.Ma9, "MA 9"),
        (AdvancedIndicatorRow.Ma21, "MA 21"),
        (AdvancedIndicatorRow.Ma50, "MA 50"),
        (AdvancedIndicatorRow.TripleMa, "3MA"),
        (AdvancedIndicatorRow.Vwap, "VWAP"),
        (AdvancedIndicatorRow.SuperTrend, "ST"),
        (AdvancedIndicatorRow.Atr, "ATR"),
        (AdvancedIndicatorRow.AtrRegression, "ATR Reg"),
        (AdvancedIndicatorRow.Std, "STD"),
        (AdvancedIndicatorRow.PocPosition, "POC Pos"),
        (AdvancedIndicatorRow.TrendRange, "TRD"),
        (AdvancedIndicatorRow.Delta, "Delta"),
        (AdvancedIndicatorRow.CumulativeDelta, "Cum Δ"),
        (AdvancedIndicatorRow.VolumeBuySell, "Vol B/S"),
        (AdvancedIndicatorRow.Trend, "Trend"),
    };

    public AdvancedMarketRegimeViewModel(
        IAdvancedRegimeProvider provider,
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<AdvancedMarketRegimeViewModel> logger)
    {
        _provider = provider;
        _repository = repository;
        _selector = selector;
        _logger = logger;

        ColumnOptions = new ObservableCollection<TimeframeColumnOption>(
            AdvancedTimeframe.Defaults.Select(t => new TimeframeColumnOption(t.Label, t.Bucket, t.Enabled)));

        RowOptions = new ObservableCollection<RowToggleOption>(
            RowCatalog.Select(r => new RowToggleOption(r.Row, r.Label)));
        foreach (var row in RowOptions)
            row.PropertyChanged += OnRowToggleChanged;

        AllInstruments = SignalInstrumentCatalog.All;
        // Hide-until-search: empty visible list; ApplyInstrumentFilter (below) collapses it to the selection.
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(InstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? AllInstruments.FirstOrDefault());
        ApplyInstrumentFilter();

        HeaderCells = new ObservableCollection<string>();
        Rows = new ObservableCollection<DashboardRow>();

        // Pull the connected broker's tradable universe in the background; replace the static
        // fallback when the list lands.
        _ = LoadInstrumentsAsync();
    }

    public AdvancedRegimeSettings Settings { get; } = AdvancedRegimeSettings.Default;

    public ObservableCollection<TimeframeColumnOption> ColumnOptions { get; }
    public ObservableCollection<RowToggleOption> RowOptions { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
    public ObservableCollection<string> HeaderCells { get; }
    public ObservableCollection<DashboardRow> Rows { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _showValue = true;
    [ObservableProperty] private bool _showDirection = true;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private int _refreshSeconds = 30;
    [ObservableProperty] private string _lastUpdated = "never";

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "tool.advancedregime";

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    partial void OnShowValueChanged(bool value) => Project();

    partial void OnShowDirectionChanged(bool value) => Project();

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _autoCts?.Cancel();
            _autoCts = new CancellationTokenSource();
            _ = AutoRefreshLoopAsync(_autoCts.Token);
        }
        else
        {
            _autoCts?.Cancel();
        }
    }

    private void OnRowToggleChanged(object? sender, PropertyChangedEventArgs e) => Project();

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await AnalyzeAsync();
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, RefreshSeconds)), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Auto-refresh switched off.
        }
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            AllInstruments = list.Select(i => new SignalInstrument(
                i.DisplayName,
                i.Category,
                i.Contract,
                i.Broker)).ToList();
            SelectedInstrument = (SelectedInstrument?.Contract.Symbol is { } prev
                                     ? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == prev) : null)
                                 ?? InstrumentPickerFilter.Remembered(InstrumentPersistKey, AllInstruments)
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "AAPL")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advanced regime: broker universe load failed, using static catalog");
        }
    }

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="AllInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyInstrumentFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText, SelectedInstrument, MaxInstrumentsDisplayed));

    [RelayCommand]
    public async Task AnalyzeAsync()
    {
        if (IsAnalyzing) return;
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument first."; return; }
        if (!ColumnOptions.Any(c => c.IsEnabled)) { ErrorMessage = "Enable at least one timeframe column."; return; }

        ErrorMessage = null;
        IsAnalyzing = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var broker = ResolveBroker(SelectedInstrument);
            var timeframes = ColumnOptions
                .Select(c => new AdvancedTimeframe(c.Label, c.Bucket, c.IsEnabled))
                .ToList();

            var snapshot = await _provider.AnalyseAsync(
                SelectedInstrument.Contract,
                broker,
                SelectedInstrument.DisplayName,
                timeframes,
                Settings,
                ct);

            _lastSnapshot = snapshot;
            Project();
            LastUpdated = snapshot.GeneratedAtUtc == DateTime.MinValue
                ? "never"
                : snapshot.GeneratedAtUtc.ToString("HH:mm:ss") + " UTC";
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advanced regime analyse failed");
            ErrorMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _runCts?.Cancel();
        AutoRefresh = false;
    }

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    /// <summary>Rebuild the bindable header + row grid from the cached snapshot, applying the
    /// row toggles and display flags. Cheap (no I/O) so it runs on every toggle flip.</summary>
    private void Project()
    {
        var snapshot = _lastSnapshot;
        HeaderCells.Clear();
        Rows.Clear();
        if (snapshot is null || snapshot.Columns.Count == 0) return;

        foreach (var column in snapshot.Columns)
            HeaderCells.Add(column.Timeframe.Label);

        foreach (var option in RowOptions)
        {
            if (!option.IsEnabled) continue;

            var cells = new List<DashboardCell>(snapshot.Columns.Count);
            foreach (var column in snapshot.Columns)
            {
                var cell = column.Cells.FirstOrDefault(c => c.Row == option.Row);
                cells.Add(cell is null
                    ? new DashboardCell("—", "—", NeutralColor, ShowDirection, ShowValue)
                    : new DashboardCell(
                        cell.Glyph,
                        FormatValue(cell.Value, cell.ValueSuffix),
                        ColorFor(cell.Signal),
                        ShowDirection,
                        ShowValue && cell.Glyph != "—"));
            }
            Rows.Add(new DashboardRow(option.Label, cells));
        }
    }

    private const string NeutralColor = "#3A4047";

    private static string ColorFor(CellSignal signal) => signal switch
    {
        CellSignal.StrongUp => "#0F9D44",
        CellSignal.Up => "#2F6B3A",
        CellSignal.Down => "#84393C",
        CellSignal.StrongDown => "#C62828",
        _ => NeutralColor,
    };

    /// <summary>Stops the auto-refresh loop and any in-flight analyze when the tab closes.</summary>
    public void Dispose()
    {
        // Remember the instrument the user was last analyzing so the window reopens on it.
        LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument?.Contract.Symbol);
        _autoCts?.Cancel();
        _autoCts?.Dispose();
        _autoCts = null;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }

    /// <summary>Compact K/M/B value formatting, mirroring the TradingView dashboard's f_fmt.</summary>
    private static string FormatValue(double? value, string? suffix)
    {
        if (value is not double v || double.IsNaN(v)) return "—";
        if (suffix == "/8") return $"{v:0}/8";
        if (suffix == "%") return $"{v:0.##}%";
        var abs = Math.Abs(v);
        return abs switch
        {
            >= 1e9 => $"{v / 1e9:0.##}B",
            >= 1e6 => $"{v / 1e6:0.##}M",
            >= 1e3 => $"{v / 1e3:0.##}K",
            _ => v.ToString("0.##"),
        };
    }
}

/// <summary>One dashboard cell: direction glyph + compact value on a signal-coloured background.
/// Visibility flags are baked in at projection time so the display toggles re-project instead of
/// requiring per-cell ancestor bindings.</summary>
public sealed record DashboardCell(string Glyph, string ValueText, string ColorHex, bool GlyphVisible, bool ValueVisible);

/// <summary>One dashboard row: indicator label + one cell per enabled timeframe column.</summary>
public sealed record DashboardRow(string Label, IReadOnlyList<DashboardCell> Cells);

/// <summary>Toggleable timeframe column. The bucket is a free TimeSpan (20m/30m included), not a
/// broker BarSize — the provider aggregates 1m bars up to it.</summary>
public sealed partial class TimeframeColumnOption : ObservableObject
{
    public TimeframeColumnOption(string label, TimeSpan bucket, bool isEnabled)
    {
        Label = label;
        Bucket = bucket;
        _isEnabled = isEnabled;
    }

    public string Label { get; }
    public TimeSpan Bucket { get; }

    [ObservableProperty] private bool _isEnabled;
}

/// <summary>Toggleable indicator row.</summary>
public sealed partial class RowToggleOption : ObservableObject
{
    public RowToggleOption(AdvancedIndicatorRow row, string label)
    {
        Row = row;
        Label = label;
        _isEnabled = true;
    }

    public AdvancedIndicatorRow Row { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isEnabled;
}
