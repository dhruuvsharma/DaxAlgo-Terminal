using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public RecorderPanelViewModel(TickRecordingService service)
    {
        Service = service;
        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(
            InstrumentPersistKey, AllInstruments, () => AllInstruments.FirstOrDefault());
        ApplyFilter();

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refresh.Tick += OnRefreshTick;
        _refresh.Start();
    }

    /// <summary>The recording service the whole panel binds to.</summary>
    public TickRecordingService Service { get; }

    // ── Add-instrument flow ──────────────────────────────────────────────────────────────────────

    /// <summary>Filtered list behind the "+ Add" row's picker.</summary>
    public ObservableCollection<SignalInstrument> Instruments { get; }

    public IReadOnlyList<SignalInstrument> AllInstruments { get; }

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
        // The timer is the only thing this window owns — the recording deliberately keeps running.
        _refresh.Tick -= OnRefreshTick;
        _refresh.Stop();
    }
}
