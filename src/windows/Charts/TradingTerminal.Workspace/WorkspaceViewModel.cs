using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Charts;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Workspace;

/// <summary>
/// The shell's own state: which canvas is showing, and what it is pointed at.
///
/// <para><b>The instrument universe lives HERE, not in the canvas.</b> That is the inversion this
/// whole thing exists for. Today every surface loads its own instrument list and draws its own
/// picker — the price chart does, an authored unit does it as a parameter buried in an expander, and
/// the order book does it again — so the same choice is made in three places with three different
/// controls and no memory of each other. One header owns it and every canvas is told.</para>
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    /// <summary>Deliberately the SAME key <c>ChartsViewModel</c> uses. The symbol you were last looking
    /// at is the symbol you were last looking at, whichever surface showed it to you — a second key
    /// here would mean opening the workspace forgot what the Charts window remembered, and the two
    /// would drift apart the first time you used both.</summary>
    private const string InstrumentPersistKey = "tool.charts";

    /// <summary>Displayed at once; the picker is hide-until-search past this.</summary>
    private const int MaxInstrumentsDisplayed = 500;

    private readonly IMarketDataRepository _repository;
    private readonly ILogger<WorkspaceViewModel> _logger;
    private IReadOnlyList<TradableInstrument> _allInstruments = [];

    public WorkspaceViewModel(
        IEnumerable<WorkspaceCanvas> canvases,
        IMarketDataRepository repository,
        ILogger<WorkspaceViewModel> logger)
    {
        _repository = repository;
        _logger = logger;

        // Registration order is the picker's order, so a composition root decides what leads simply by
        // registering it first. No priority field to keep in sync with a list nobody re-reads.
        Canvases = new ObservableCollection<WorkspaceCanvas>(canvases);
        CanvasesView = CollectionViewSource.GetDefaultView(Canvases);
        CanvasesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WorkspaceCanvas.Group)));

        Timeframes = new ObservableCollection<ChartTimeframe>(ChartsViewModel.AvailableTimeframes());
        Instruments = [];

        _selectedTimeframe = Timeframes.FirstOrDefault(t => t.BarSize == BarSize.OneHour)
            ?? Timeframes.FirstOrDefault();
        _selectedCanvas = Canvases.FirstOrDefault();

        Subject.Timeframe = _selectedTimeframe;

        _ = LoadInstrumentsAsync();
    }

    /// <summary>What every canvas is handed. One instance for the life of the shell — a canvas that
    /// subscribes to it keeps working across swaps of every other canvas.</summary>
    public WorkspaceSubject Subject { get; } = new();

    public ObservableCollection<WorkspaceCanvas> Canvases { get; }

    /// <summary>The picker's rows, grouped by <see cref="WorkspaceCanvas.Group"/>. A flat list stops
    /// being scannable at about five entries, and this list is meant to grow.</summary>
    public ICollectionView CanvasesView { get; }
    public ObservableCollection<ChartTimeframe> Timeframes { get; }
    public ObservableCollection<TradableInstrument> Instruments { get; }

    [ObservableProperty] private WorkspaceCanvas? _selectedCanvas;
    [ObservableProperty] private TradableInstrument? _selectedInstrument;
    [ObservableProperty] private ChartTimeframe? _selectedTimeframe;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _status = "Loading instruments…";

    /// <summary>True when the active canvas actually reads the shell's instrument. False disables the
    /// picker rather than hiding it, so the header still says what the canvas is showing.</summary>
    public bool CanPickInstrument => SelectedCanvas?.Instrument is not CanvasSubjectMode.Pins;

    public bool CanPickTimeframe => SelectedCanvas?.Timeframe is not CanvasSubjectMode.Pins;

    public bool ShowsInstrument => SelectedCanvas?.Instrument is not CanvasSubjectMode.Ignores;

    public bool ShowsTimeframe => SelectedCanvas?.Timeframe is not CanvasSubjectMode.Ignores;

    /// <summary>Why a disabled control is disabled — the only question a greyed-out control ever
    /// provokes, answered next to it instead of nowhere.</summary>
    public string? PinnedReason =>
        CanPickInstrument && CanPickTimeframe ? null : SelectedCanvas?.PinnedReason;

    partial void OnSelectedCanvasChanged(WorkspaceCanvas? value)
    {
        OnPropertyChanged(nameof(CanPickInstrument));
        OnPropertyChanged(nameof(CanPickTimeframe));
        OnPropertyChanged(nameof(ShowsInstrument));
        OnPropertyChanged(nameof(ShowsTimeframe));
        OnPropertyChanged(nameof(PinnedReason));
    }

    // The subject is pushed rather than bound so a canvas never has to know about this view-model —
    // it is handed a WorkspaceSubject and nothing else.
    partial void OnSelectedInstrumentChanged(TradableInstrument? value)
    {
        Subject.Instrument = value;

        // Persisted on CHANGE, not on close: the shell can be left open for days, and the symbol you
        // were last looking at should survive a crash as well as a clean exit. The same key the Charts
        // window uses, so the two agree rather than each remembering a different last symbol.
        LastInstrumentStore.Save(InstrumentPersistKey, value?.Contract.Symbol);
        ApplyFilter();
    }

    partial void OnSelectedTimeframeChanged(ChartTimeframe? value) => Subject.Timeframe = value;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0)
            {
                Status = "No instruments — connect a broker first.";
                return;
            }

            _allInstruments = list;
            SelectedInstrument =
                InstrumentPickerFilter.Remembered(InstrumentPersistKey, _allInstruments, i => i.Contract.Symbol)
                ?? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                ?? _allInstruments.FirstOrDefault();

            ApplyFilter();
            Status = $"{_allInstruments.Count} instruments.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace: instrument load failed");
            Status = $"Instrument load failed: {ex.Message}";
        }
    }

    /// <summary>Hide-until-search, rebuilt in place so the selection never flickers out — the same
    /// helper and the same behaviour as the Charts toolbar, because it is the same control.</summary>
    private void ApplyFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(
            _allInstruments, InstrumentSearchText, SelectedInstrument,
            MaxInstrumentsDisplayed, i => i.DisplayName));
}
