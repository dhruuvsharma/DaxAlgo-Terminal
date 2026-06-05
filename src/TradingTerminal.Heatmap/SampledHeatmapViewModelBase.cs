using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Correlation;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Shared base for the multi-instrument heatmaps (cross-asset volatility and rolling correlation).
/// Builds on <see cref="CorrelationPickerViewModelBase"/> for the category-grouped multi-select
/// instrument checklist, and adds the live sampler the two share: on Start it subscribes the live
/// quote stream for every ticked instrument, stashes each one's latest mid, and a <see cref="DispatcherTimer"/>
/// samples them onto a shared time grid. Each tick calls <see cref="OnSample"/> (subclass appends to its
/// rolling buffers) then <see cref="BuildFrame"/> (subclass projects them into a heatmap).
///
/// Sampling onto a common grid keeps the per-instrument series aligned index-for-index without
/// timestamp intersection — the same approach as the Live Correlation Matrix.
/// </summary>
public abstract partial class SampledHeatmapViewModelBase : CorrelationPickerViewModelBase, IDisposable
{
    private readonly IMarketDataIngest _ingest;
    private readonly IMarketDataHub _hub;

    // Latest mid per active instrument — written from hub callbacks (ingest thread), read on the
    // sampler tick (UI thread). ConcurrentDictionary makes that hand-off safe without locking.
    protected ConcurrentDictionary<InstrumentId, double> LatestMid { get; } = new();

    // The active set, mutated only on the UI thread (Start/Stop) and read on the sampler tick.
    protected List<ActiveInstrument> Active { get; } = new();

    private readonly List<IDisposable> _subscriptions = new();
    private DispatcherTimer? _sampler;

    private static readonly IReadOnlyList<SampleStepOption> AllSteps = new SampleStepOption[]
    {
        new("1 sec",  TimeSpan.FromSeconds(1)),
        new("2 sec",  TimeSpan.FromSeconds(2)),
        new("5 sec",  TimeSpan.FromSeconds(5)),
        new("10 sec", TimeSpan.FromSeconds(10)),
    };

    private static readonly IReadOnlyList<SampleWindowOption> AllWindows = new SampleWindowOption[]
    {
        new("60 samples",  60),
        new("120 samples", 120),
        new("240 samples", 240),
    };

    protected SampledHeatmapViewModelBase(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        ILogger logger)
        : base(repository, selector, logger)
    {
        _ingest = ingest;
        _hub = hub;

        Steps = new ObservableCollection<SampleStepOption>(AllSteps);
        SelectedStep = Steps.First(s => s.Interval == TimeSpan.FromSeconds(2));

        Windows = new ObservableCollection<SampleWindowOption>(AllWindows);
        SelectedWindow = Windows.First(w => w.Count == 120);
    }

    public ObservableCollection<SampleStepOption> Steps { get; }
    public ObservableCollection<SampleWindowOption> Windows { get; }

    [ObservableProperty] private SampleStepOption? _selectedStep;
    [ObservableProperty] private SampleWindowOption? _selectedWindow;
    [ObservableProperty] private bool _isStreaming;

    public HeatmapFrame? CurrentFrame { get; private set; }
    public event EventHandler? HeatmapUpdated;

    /// <summary>How many samples the subclass's rolling buffers should retain.</summary>
    protected int WindowSize => SelectedWindow?.Count ?? 120;

    partial void OnSelectedStepChanged(SampleStepOption? value)
    {
        if (_sampler is not null && value is not null)
            _sampler.Interval = value.Interval;
    }

    [RelayCommand]
    private void Start()
    {
        if (IsStreaming) return;

        var selected = SelectedInstruments;
        if (selected.Count < 2) { StatusMessage = "Select at least two instruments."; return; }
        if (SelectedStep is null || SelectedWindow is null) { StatusMessage = "Pick a sample step and window."; return; }

        var skipped = new List<string>();
        foreach (var inst in selected)
        {
            if (!Selector.IsConnected(inst.Broker))
            {
                skipped.Add($"{inst.Symbol} ({inst.BrokerAbbrev} not connected)");
                continue;
            }

            var id = _ingest.Resolve(inst.Contract, inst.Broker);
            Active.Add(new ActiveInstrument(inst, id));
            _subscriptions.Add(_ingest.Subscribe(inst.Contract, inst.Broker));
            _subscriptions.Add(_hub.Quotes(id).Subscribe(q => LatestMid[id] = q.Mid));
        }

        if (Active.Count < 2)
        {
            StatusMessage = "Need at least two instruments on connected brokers."
                + (skipped.Count > 0 ? $" Skipped: {string.Join(", ", skipped)}." : string.Empty);
            Teardown();
            return;
        }

        OnStart();

        _sampler = new DispatcherTimer { Interval = SelectedStep.Interval };
        _sampler.Tick += OnTick;
        _sampler.Start();

        IsStreaming = true;
        StatusMessage = $"Live — sampling {Active.Count} instruments every {SelectedStep.Label}, rolling {SelectedWindow.Label}."
            + (skipped.Count > 0 ? $" Skipped: {string.Join(", ", skipped)}." : string.Empty);
        Logger.LogInformation("Sampled heatmap started for {Count} instruments", Active.Count);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsStreaming) return;
        Teardown();
        IsStreaming = false;
        StatusMessage = "Stopped.";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        OnSample();
        RaiseFrame(BuildFrame());
    }

    /// <summary>Reset the subclass's rolling buffers for the freshly-built <see cref="Active"/> set.</summary>
    protected abstract void OnStart();

    /// <summary>Append one aligned sample (read <see cref="LatestMid"/> for each <see cref="Active"/> row).</summary>
    protected abstract void OnSample();

    /// <summary>Project the current buffers into a heatmap frame (UI thread).</summary>
    protected abstract HeatmapFrame? BuildFrame();

    protected void RaiseFrame(HeatmapFrame? frame)
    {
        CurrentFrame = frame;
        HeatmapUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void Teardown()
    {
        if (_sampler is not null)
        {
            _sampler.Stop();
            _sampler.Tick -= OnTick;
            _sampler = null;
        }

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch (Exception ex) { Logger.LogDebug(ex, "Sampled heatmap: subscription dispose threw"); }
        }
        _subscriptions.Clear();
        Active.Clear();
        LatestMid.Clear();
        RaiseFrame(null);
    }

    public void Dispose() => Teardown();
}

/// <summary>An instrument participating in a sampled heatmap: its picker row + canonical id.</summary>
public sealed record ActiveInstrument(SelectableInstrument Instrument, InstrumentId Id);

/// <summary>Sampling-cadence dropdown row.</summary>
public sealed record SampleStepOption(string Label, TimeSpan Interval)
{
    public override string ToString() => Label;
}

/// <summary>Rolling-window dropdown row.</summary>
public sealed record SampleWindowOption(string Label, int Count)
{
    public override string ToString() => Label;
}
