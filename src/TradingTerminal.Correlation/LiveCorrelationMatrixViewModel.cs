using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Correlation;

/// <summary>
/// Drives the Live Correlation Matrix window — the real-time sibling of
/// <see cref="CorrelationMatrixViewModel"/>. Instead of fetching historical bars on demand, it
/// subscribes to the live quote stream for each ticked instrument (via the canonical
/// <see cref="IMarketDataIngest"/>/<see cref="IMarketDataHub"/> pipeline, keyed by
/// <see cref="InstrumentId"/>), samples each instrument's mid price onto a shared fixed time grid,
/// keeps a rolling window of those samples, and recomputes the NxN Pearson-on-log-returns matrix on
/// every tick so the grid updates continuously while the market moves.
///
/// Sampling onto a common grid (rather than correlating raw, asynchronously-arriving ticks) is what
/// keeps the series aligned: every instrument contributes exactly one observation per interval, so
/// the returns line up index-for-index without timestamp intersection.
/// </summary>
public sealed partial class LiveCorrelationMatrixViewModel : CorrelationPickerViewModelBase, IDisposable
{
    private readonly IMarketDataIngest _ingest;
    private readonly IMarketDataHub _hub;

    // Latest mid per active instrument. Written from hub callbacks (ingest thread), read on the
    // sampler tick (UI thread) — ConcurrentDictionary makes that hand-off safe without locking.
    private readonly ConcurrentDictionary<InstrumentId, double> _latestMid = new();

    // Active correlation set + its rolling per-instrument sample buffers. Mutated only on the UI
    // thread (Start/Stop commands and the sampler tick), so plain collections are fine here.
    private readonly List<ActiveSeries> _active = new();
    private readonly List<IDisposable> _subscriptions = new();
    private DispatcherTimer? _sampler;

    private static readonly IReadOnlyList<SampleIntervalOption> AllSampleIntervals = new SampleIntervalOption[]
    {
        new("1 sec",  TimeSpan.FromSeconds(1)),
        new("2 sec",  TimeSpan.FromSeconds(2)),
        new("5 sec",  TimeSpan.FromSeconds(5)),
        new("10 sec", TimeSpan.FromSeconds(10)),
    };

    private static readonly IReadOnlyList<WindowOption> AllWindows = new WindowOption[]
    {
        new("60 samples",  60),
        new("120 samples", 120),
        new("300 samples", 300),
        new("600 samples", 600),
    };

    public LiveCorrelationMatrixViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        ILogger<LiveCorrelationMatrixViewModel> logger)
        : base(repository, selector, logger)
    {
        _ingest = ingest;
        _hub = hub;

        SampleIntervals = new ObservableCollection<SampleIntervalOption>(AllSampleIntervals);
        SelectedSampleInterval = SampleIntervals.First(s => s.Interval == TimeSpan.FromSeconds(2));

        Windows = new ObservableCollection<WindowOption>(AllWindows);
        SelectedWindow = Windows.First(w => w.Count == 120);
    }

    public ObservableCollection<SampleIntervalOption> SampleIntervals { get; }
    public ObservableCollection<WindowOption> Windows { get; }

    [ObservableProperty] private SampleIntervalOption? _selectedSampleInterval;
    [ObservableProperty] private WindowOption? _selectedWindow;
    [ObservableProperty] private bool _isStreaming;

    /// <summary>Changing the cadence live just re-paces the running sampler; the rolling window is
    /// kept (the samples are still valid, they'll simply age out at the new rate).</summary>
    partial void OnSelectedSampleIntervalChanged(SampleIntervalOption? value)
    {
        if (_sampler is not null && value is not null)
            _sampler.Interval = value.Interval;
    }

    [RelayCommand]
    private void Start()
    {
        if (IsStreaming) return;

        var selected = SelectedInstruments;
        if (selected.Count < 2)
        {
            StatusMessage = "Select at least two instruments.";
            return;
        }
        if (SelectedSampleInterval is null || SelectedWindow is null)
        {
            StatusMessage = "Pick a sample interval and window.";
            return;
        }

        // Only instruments whose source broker is connected can stream; note the rest so the user
        // knows why a ticked row isn't in the grid.
        var skipped = new List<string>();
        foreach (var inst in selected)
        {
            if (!Selector.IsConnected(inst.Broker))
            {
                skipped.Add($"{inst.Symbol} ({inst.BrokerAbbrev} not connected)");
                continue;
            }

            var id = _ingest.Resolve(inst.Contract, inst.Broker);
            _active.Add(new ActiveSeries(inst, id));

            // Start (or join) the ref-counted L1 pump, then observe its quotes off the hub. The
            // subscription only stashes the latest mid; the sampler does the aligned read.
            _subscriptions.Add(_ingest.Subscribe(inst.Contract, inst.Broker));
            _subscriptions.Add(_hub.Quotes(id).Subscribe(q => _latestMid[id] = q.Mid));
        }

        if (_active.Count < 2)
        {
            StatusMessage = "Need at least two instruments on connected brokers."
                + (skipped.Count > 0 ? $" Skipped: {string.Join(", ", skipped)}." : string.Empty);
            TeardownStreams();
            return;
        }

        _sampler = new DispatcherTimer { Interval = SelectedSampleInterval.Interval };
        _sampler.Tick += OnSampleTick;
        _sampler.Start();

        IsStreaming = true;
        StatusMessage = $"Live — sampling {_active.Count} instruments every {SelectedSampleInterval.Label}, rolling {SelectedWindow.Label}."
            + (skipped.Count > 0 ? $" Skipped: {string.Join(", ", skipped)}." : string.Empty);
        Logger.LogInformation("Live correlation started for {Count} instruments", _active.Count);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsStreaming) return;
        TeardownStreams();
        IsStreaming = false;
        StatusMessage = "Stopped.";
        Logger.LogInformation("Live correlation stopped");
    }

    /// <summary>One sample step: snapshot every active instrument's latest mid onto the shared grid,
    /// age out anything beyond the window, then recompute the matrix from the rolling buffers.</summary>
    private void OnSampleTick(object? sender, EventArgs e)
    {
        int window = SelectedWindow?.Count ?? 120;

        foreach (var series in _active)
        {
            if (_latestMid.TryGetValue(series.Id, out var mid) && mid > 0.0)
            {
                series.Closes.Enqueue(mid);
                while (series.Closes.Count > window)
                    series.Closes.Dequeue();
            }
        }

        // Need ≥3 closes (→ ≥2 returns) before an instrument can correlate.
        var ready = _active.Where(s => s.Closes.Count >= 3).ToList();
        if (ready.Count < 2)
        {
            StatusMessage = $"Live — warming up ({_active.Count} subscribed, waiting for ticks on ≥2)…";
            return;
        }

        var returns = ready
            .Select(s => CorrelationCalculator.LogReturns(s.Closes.ToArray()))
            .ToList();

        // The grid is only meaningful where every series overlaps, so trim each return series to the
        // shortest common length (a late-joining instrument has a shorter buffer until it catches up).
        int common = returns.Min(r => r.Count);
        if (common < 2)
        {
            StatusMessage = $"Live — warming up ({ready.Count} ready, aligning…)";
            return;
        }

        var trimmed = returns.Select(r => (IReadOnlyList<double>)r.Skip(r.Count - common).ToArray()).ToList();
        var matrix = CorrelationCalculator.PearsonMatrix(trimmed);
        var labels = LabelFor(ready.Select(s => s.Instrument).ToList());

        BuildMatrix(new CorrelationMatrix(labels, matrix, common));
        StatusMessage = $"Live — {ready.Count} instruments over last {common + 1} samples "
            + $"(every {SelectedSampleInterval?.Label}). Updated {DateTime.Now:HH:mm:ss}.";
    }

    private void TeardownStreams()
    {
        if (_sampler is not null)
        {
            _sampler.Stop();
            _sampler.Tick -= OnSampleTick;
            _sampler = null;
        }

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch (Exception ex) { Logger.LogDebug(ex, "Live correlation: subscription dispose threw"); }
        }
        _subscriptions.Clear();
        _active.Clear();
        _latestMid.Clear();
    }

    public void Dispose() => TeardownStreams();

    /// <summary>An instrument participating in the live matrix: its picker row, its canonical id,
    /// and the rolling buffer of sampled mid prices (oldest first).</summary>
    private sealed class ActiveSeries(SelectableInstrument instrument, InstrumentId id)
    {
        public SelectableInstrument Instrument { get; } = instrument;
        public InstrumentId Id { get; } = id;
        public Queue<double> Closes { get; } = new();
    }
}

/// <summary>Sampling-cadence dropdown row — how often the live matrix snapshots mids onto the grid.</summary>
public sealed record SampleIntervalOption(string Label, TimeSpan Interval)
{
    public override string ToString() => Label;
}

/// <summary>Rolling-window dropdown row — how many samples the correlation is computed over.</summary>
public sealed record WindowOption(string Label, int Count)
{
    public override string ToString() => Label;
}
