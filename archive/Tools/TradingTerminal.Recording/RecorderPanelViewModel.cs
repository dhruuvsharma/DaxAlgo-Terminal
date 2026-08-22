using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Recording;

/// <summary>
/// The recorder panel — the small window behind the header button next to the API meter. It is a
/// <b>view onto <see cref="TickRecordingService"/></b>, which owns the recording and outlives this
/// window; closing the panel never stops a recording.
///
/// <para>The only thing this VM owns is a 1 s <see cref="DispatcherTimer"/> that publishes the
/// service's counters to the UI. That's the memory-safety contract: counters are bumped with
/// Interlocked on the feed thread and rendered on a fixed cadence, so a hot tape can't drive the
/// dispatcher (see the memory-safety skill, patterns 2 & 3).</para>
/// </summary>
public sealed partial class RecorderPanelViewModel : ViewModelBase, IDisposable
{
    private const string InstrumentPersistKey = "tool.recorder";

    private readonly DispatcherTimer _refresh;
    private readonly IMarketDataRepository _repository;
    private readonly IInstrumentRegistry _registry;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<RecorderPanelViewModel> _logger;

    public RecorderPanelViewModel(
        TickRecordingService service,
        IMarketDataRepository repository,
        IInstrumentRegistry registry,
        IBrokerSelector selector,
        ILogger<RecorderPanelViewModel> logger)
    {
        Service = service;
        _repository = repository;
        _registry = registry;
        _selector = selector;
        _logger = logger;

        // Seed with the registry rows so the picker is never empty, then swap in the connected
        // brokers' own (broker-tagged) universe — same two-step every other window uses.
        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(
            InstrumentPersistKey, AllInstruments, () => AllInstruments.FirstOrDefault());
        ApplyFilter();

        _ = LoadInstrumentsAsync();
        // The panel is usually opened before a broker has finished connecting; without this the
        // broker-agnostic seed list would be pinned for the window's life.
        _selector.StateChanged += OnBrokerStateChanged;

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refresh.Tick += OnRefreshTick;
        _refresh.Start();
    }

    private async Task LoadInstrumentsAsync()
    {
        var universe = await BrokerInstrumentUniverse.LoadAsync(_repository, _registry, only: null, _logger);
        if (universe.Count == 0) return;

        AllInstruments = universe;
        SelectedInstrument = BrokerInstrumentUniverse.Reselect(universe, SelectedInstrument);
        ApplyFilter();
    }

    private void OnBrokerStateChanged(object? sender, BrokerStateChangedEventArgs e)
    {
        if (e.State != ConnectionState.Connected) return;
        _ = UiThread.RunAsync(() => LoadInstrumentsAsync());
    }

    /// <summary>The recording service the whole panel binds to.</summary>
    public TickRecordingService Service { get; }

    // ── Add-instrument flow ──────────────────────────────────────────────────────────────────────

    /// <summary>Filtered list behind the "+ Add" row's picker.</summary>
    public ObservableCollection<SignalInstrument> Instruments { get; }

    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;

    /// <summary>True while the "+ Add" row is expanded into the instrument picker.</summary>
    [ObservableProperty] private bool _isAdding;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText, SelectedInstrument, 500));

    /// <summary>The "+ Add" row: first click opens the picker, the next confirms the pick.</summary>
    [RelayCommand]
    private void BeginAdd() => IsAdding = true;

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        InstrumentSearchText = string.Empty;
    }

    [RelayCommand]
    private void ConfirmAdd()
    {
        if (SelectedInstrument is null) return;
        Service.Add(SelectedInstrument);
        LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument.Contract.Symbol);
        IsAdding = false;
        InstrumentSearchText = string.Empty;
    }

    [RelayCommand]
    private void RemoveInstrument(RecorderEntry? entry)
    {
        if (entry is not null) Service.Remove(entry);
    }

    [RelayCommand]
    private void ToggleRecording() => Service.ToggleRecording();

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        Service.RefreshElapsed();
        foreach (var entry in Service.Instruments) entry.RaiseCounters();
    }

    public void Dispose()
    {
        // The timer and the broker-state handler are the only things this window owns — the recording
        // itself deliberately keeps running.
        _refresh.Tick -= OnRefreshTick;
        _refresh.Stop();
        _selector.StateChanged -= OnBrokerStateChanged;
    }
}
