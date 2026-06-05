using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Shared base for the single-instrument, price × time heatmaps (depth, order-book imbalance,
/// volume-at-price). Owns everything those have in common: the instrument picker (broker universe,
/// search, source-broker resolution), the live subscription lifecycle, and a fixed-cadence render
/// timer that rebuilds the frame off the UI thread's beat so a fast feed can't drive the redraw rate.
///
/// Subclasses provide only the data path: <see cref="StartPumps"/> (which feed to subscribe — depth
/// or trades — using the <see cref="PumpDepth"/>/<see cref="PumpTrades"/> helpers), what to reset in
/// <see cref="ResetBuffers"/>, and how to turn the buffered data into a <see cref="HeatmapFrame"/> in
/// <see cref="BuildFrame"/>. Selecting an instrument auto-(re)starts the stream.
/// </summary>
public abstract partial class SingleInstrumentHeatmapViewModelBase : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;

    /// <summary>Redraw cadence — decoupled from the data feed so a fast book/tape can't thrash the UI.</summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(200);

    protected IMarketDataRepository Repository { get; }
    protected IMarketDataHub Hub { get; }
    protected IMarketDataIngest Ingest { get; }
    protected IBrokerSelector Selector { get; }
    protected ILogger Logger { get; }

    private IReadOnlyList<SignalInstrument> _allInstruments;
    private CancellationTokenSource? _streamCts;
    private readonly List<IDisposable> _streamHandles = new();
    private readonly DispatcherTimer _renderTimer;
    private bool _dirty;

    protected SingleInstrumentHeatmapViewModelBase(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger logger)
    {
        Repository = repository;
        Hub = hub;
        Ingest = ingest;
        Selector = selector;
        Logger = logger;

        _allInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        _renderTimer = new DispatcherTimer { Interval = RenderInterval };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _status = "Pick an instrument to stream.";

    /// <summary>The latest computed frame, or null before any data has arrived. Read by the code-behind.</summary>
    public IHeatmapFrame? CurrentFrame { get; private set; }

    /// <summary>Raised on the UI thread after the frame is rebuilt (or cleared). The window redraws.</summary>
    public event EventHandler? HeatmapUpdated;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) => Restart();

    // ---- subclass contract ----

    /// <summary>Subscribe the broker feed(s) this heatmap needs and start the pump(s) via
    /// <see cref="PumpDepth"/> / <see cref="PumpTrades"/>. Add any ingest handles to keep alive with
    /// <see cref="AddStreamHandle"/>.</summary>
    protected abstract void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct);

    /// <summary>Clear the subclass's rolling buffers (called on every (re)start).</summary>
    protected abstract void ResetBuffers();

    /// <summary>Project the current buffers into a frame (called on the render tick, UI thread).</summary>
    protected abstract IHeatmapFrame? BuildFrame();

    // ---- helpers for subclasses ----

    /// <summary>Flag that the buffers changed; the render timer will rebuild the frame next tick.</summary>
    protected void MarkDirty() => _dirty = true;

    /// <summary>Keep an ingest subscription handle alive for the duration of the stream.</summary>
    protected void AddStreamHandle(IDisposable handle) => _streamHandles.Add(handle);

    /// <summary>Pump depth snapshots off the hub through a bounded channel and invoke <paramref name="onUi"/>
    /// on the UI thread for each, marking the frame dirty.</summary>
    protected void PumpDepth(InstrumentId id, CancellationToken ct, Action<DepthSnapshot> onUi)
        => _ = RunPumpAsync(Hub.Depth(id), ct, onUi);

    /// <summary>Pump trade prints off the hub through a bounded channel and invoke <paramref name="onUi"/>
    /// on the UI thread for each, marking the frame dirty.</summary>
    protected void PumpTrades(InstrumentId id, CancellationToken ct, Action<TradePrint> onUi)
        => _ = RunPumpAsync(Hub.Trades(id), ct, onUi);

    private async Task RunPumpAsync<T>(IObservable<T> source, CancellationToken ct, Action<T> onUi)
    {
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var subscription = source.Subscribe(x => channel.Writer.TryWrite(x));
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct))
                await UiThread.RunAsync(() => { onUi(item); MarkDirty(); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogDebug(ex, "Heatmap pump ended"); }
        finally { channel.Writer.TryComplete(); }
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;

            _allInstruments = list
                .Select(i => new SignalInstrument($"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}", i.Category, i.Contract, i.Broker))
                .ToList();

            var keep = SelectedInstrument;
            ApplyFilter();
            SelectedInstrument = keep is not null && Instruments.Contains(keep)
                ? keep
                : _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? _allInstruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Heatmap: instrument list load failed; using static catalog");
        }
    }

    private void ApplyFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<SignalInstrument> query = _allInstruments;
        if (term.Length > 0)
            query = _allInstruments.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

        var shown = query.Take(MaxInstrumentsDisplayed).ToList();
        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);

        Instruments.Clear();
        foreach (var inst in shown) Instruments.Add(inst);
    }

    private void Restart()
    {
        StopStream();
        ResetBuffers();
        RaiseFrame(null);

        var instrument = SelectedInstrument;
        if (instrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }

        try
        {
            var id = Ingest.Resolve(instrument.Contract, broker);
            _streamCts = new CancellationTokenSource();
            StartPumps(instrument, broker, id, _streamCts.Token);
            Status = $"Streaming {instrument.DisplayName} ({BrokerLabel(broker)})…";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Heatmap: subscribe failed for {Symbol}", instrument.Contract.Symbol);
            Status = $"Subscribe failed: {ex.Message}";
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!_dirty) return;
        _dirty = false;
        RaiseFrame(BuildFrame());
    }

    /// <summary>Set the current frame and notify the view. Pass null to clear.</summary>
    protected void RaiseFrame(IHeatmapFrame? frame)
    {
        CurrentFrame = frame;
        HeatmapUpdated?.Invoke(this, EventArgs.Empty);
    }

    protected BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && Selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    protected static string BrokerLabel(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        foreach (var h in _streamHandles)
        {
            try { h.Dispose(); }
            catch (Exception ex) { Logger.LogDebug(ex, "Heatmap: stream handle dispose threw"); }
        }
        _streamHandles.Clear();
    }

    public virtual void Dispose()
    {
        _renderTimer.Stop();
        _renderTimer.Tick -= OnRenderTick;
        StopStream();
    }
}
