using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Presets;

namespace TradingTerminal.BubbleChart;

/// <summary>One executed-trade bubble: time, price, volume, side (+1 buy / −1 sell / 0 neutral), and
/// whether it dwarfs the rolling mean print size (a "large lot").</summary>
public readonly record struct HeatTrade(DateTime Time, double Price, long Size, int Side, bool Large);

/// <summary>A time-axis column-width preset (label + seconds of wall-clock per heatmap column).</summary>
public sealed record HeatTimeframe(string Label, double Seconds)
{
    public override string ToString() => Label;
}

/// <summary>
/// Drives the experimental <b>Bookmap-style</b> chart: a time × price <b>liquidity heatmap</b> (resting
/// L2 size, brighter = heavier) with <b>trade volume bubbles</b> overlaid (green = net buying, red = net
/// selling, size ∝ executed volume). This is the look of the Bookmap / VolBook applications.
///
/// <para>Self-contained (no reference to the production Bookmap window): it mirrors that window's
/// memory-safe pipeline — bounded depth + trade channels with batch-drain, time-uniform columns, bounded
/// retained history, a coalesced render timer (the feed only marks dirty), and full <see cref="Dispose"/>
/// teardown.</para>
/// </summary>
public sealed partial class BubbleChartViewModel : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;
    /// <summary>Columns kept for the scrolling view; older ones are trimmed.</summary>
    public const int MaxRetained = 360;
    private const int MaxTrades = 4000;
    private const int DepthChannelCapacity = 16_384;
    private const int TradeChannelCapacity = 65_536;
    private const int MaxDrainBatch = 4_096;
    private const string LogSource = "Bubble Heatmap";

    // Rolling large-lot detection (mirrors the Bookmap VM): a print dwarfing the recent mean size.
    private const int SizeWindow = 200;
    private const double LargeMultiple = 5.0;

    private static bool BrokerHasDepth(BrokerKind b) => b is
        BrokerKind.CTrader or BrokerKind.Binance or BrokerKind.IronBeam or BrokerKind.Upstox or
        BrokerKind.Coinbase or BrokerKind.Bybit or BrokerKind.Kraken or BrokerKind.Okx or BrokerKind.Simulated;

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<BubbleChartViewModel> _logger;

    private IReadOnlyList<SignalInstrument> _allInstruments;
    private CancellationTokenSource? _streamCts;
    private readonly List<IDisposable> _streamHandles = new();
    private readonly IDisposable _renderTimer;
    private bool _dirty;
    private bool _ready;
    private bool _disposed;

    // ── Rolling buffers (read by the surface on the UI thread) ──────────────────────────────────
    private readonly List<DepthSnapshot> _columns = new();   // time-uniform liquidity columns
    private readonly Queue<HeatTrade> _trades = new();        // recent prints → bubbles
    private DateTime _lastColumnTime;

    private readonly Queue<long> _sizeWindow = new();
    private long _sizeSum;

    private TimeSpan ColumnInterval => TimeSpan.FromSeconds(SelectedTimeframe?.Seconds ?? 1.0);

    public BubbleChartViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        InMemoryLogSink log,
        ILogger<BubbleChartViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _log = log;
        _logger = logger;

        _allInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        Timeframes = new ObservableCollection<HeatTimeframe>(AllTimeframes);

        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "BTCUSDT")
                             ?? Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();
        SelectedTimeframe = Timeframes.First(t => t.Label == "1 s");
        RetainedOptions = new ObservableCollection<int> { 180, 360, 720 };
        _retainedColumns = MaxRetained;
        PresetNames = new ObservableCollection<string>(_presetStore.Names);

        // Coalesced render tick (~12 fps). Feed only marks dirty; this raises SurfaceChanged.
        _renderTimer = UiThread.CreateRenderTimer(TimeSpan.FromMilliseconds(80), OnRenderTick);

        _ready = true;
        _ = LoadInstrumentsAsync();
        Restart();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<HeatTimeframe> Timeframes { get; }

    /// <summary>Retained-column window options for the toolbar (visible history length).</summary>
    public ObservableCollection<int> RetainedOptions { get; }

    /// <summary>Saved preset names for the toolbar picker.</summary>
    public ObservableCollection<string> PresetNames { get; }

    private static readonly IReadOnlyList<HeatTimeframe> AllTimeframes = new[]
    {
        new HeatTimeframe("250 ms", 0.25),
        new HeatTimeframe("500 ms", 0.5),
        new HeatTimeframe("1 s", 1),
        new HeatTimeframe("2 s", 2),
        new HeatTimeframe("5 s", 5),
        new HeatTimeframe("10 s", 10),
    };

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private HeatTimeframe? _selectedTimeframe;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _status = "Pick an instrument to stream the liquidity heatmap (needs L2 depth).";

    // ── Read-outs (top-right panel) ──────────────────────────────────────────────────────────────
    [ObservableProperty] private double? _bestBid;
    [ObservableProperty] private double? _bestAsk;
    [ObservableProperty] private double? _mid;
    [ObservableProperty] private double? _lastPrice;
    [ObservableProperty] private long _tradeCount;
    [ObservableProperty] private int _columnsFilled;
    [ObservableProperty] private int _priceDecimals = 2;
    [ObservableProperty] private bool _noDepth;

    /// <summary>Freezes redraws (columns/trades keep rolling underneath; resume is instant).</summary>
    [ObservableProperty] private bool _isPaused;

    // ── View toggles (presentation only; applied in RecentTrades / the retained trim) ───────────
    [ObservableProperty] private bool _showBubbles = true;
    /// <summary>Show only the large-lot prints (≥5× the rolling mean size).</summary>
    [ObservableProperty] private bool _largeOnly;
    /// <summary>Columns of history kept and drawn (one column per timeframe bucket).</summary>
    [ObservableProperty] private int _retainedColumns;

    // ── Presets (named view-option snapshots; see ToolPresetStore) ──────────────────────────────
    /// <summary>Editable preset-picker text: type a name and Save, or pick an existing preset to apply.</summary>
    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string? _selectedPreset;

    /// <summary>Raised on the UI thread (timer-coalesced) when the buffers change; the surface redraws.</summary>
    public event EventHandler? SurfaceChanged;

    // ── Buffers exposed to the surface (read on the UI thread) ─────────────────────────────────
    public IReadOnlyList<DepthSnapshot> Columns => _columns;

    /// <summary>Prints for the bubble overlay, filtered by the view toggles (all / large-only / off) —
    /// the surface stays a dumb renderer while the VM owns the view state.</summary>
    public HeatTrade[] RecentTrades()
    {
        if (!ShowBubbles) return Array.Empty<HeatTrade>();
        if (!LargeOnly) return _trades.ToArray();
        return _trades.Where(t => t.Large).ToArray();
    }

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) { if (_ready) Restart(); }

    partial void OnShowBubblesChanged(bool value) => _dirty = true;
    partial void OnLargeOnlyChanged(bool value) => _dirty = true;

    partial void OnRetainedColumnsChanged(int value)
    {
        if (value < 10) return;
        while (_columns.Count > value) _columns.RemoveAt(0);
        ColumnsFilled = _columns.Count;
        _dirty = true;
    }

    partial void OnIsPausedChanged(bool value) =>
        Status = value
            ? $"⏸ Paused — the book and tape keep rolling in the background ({SelectedInstrument?.DisplayName})."
            : $"Resumed — {SelectedInstrument?.DisplayName}.";

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null) return;
        PresetName = value;
        if (_presetStore.Get(value) is { } preset) ApplyPreset(preset);
    }

    partial void OnSelectedTimeframeChanged(HeatTimeframe? value)
    {
        // Rebuild columns at the new width going forward; trades carry over.
        if (!_ready) return;
        _columns.Clear();
        _lastColumnTime = default;
        ColumnsFilled = 0;
        _dirty = true;
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            _allInstruments = list
                .Select(i => new SignalInstrument($"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}", i.Category, i.Contract, i.Broker))
                .ToList();
            var keep = SelectedInstrument;
            ApplyFilter();
            SelectedInstrument = keep is not null && Instruments.Contains(keep)
                ? keep
                : _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "BTCUSDT")
                  ?? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                  ?? _allInstruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bubble heatmap: instrument list load failed; using static catalog");
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
        SurfaceChanged?.Invoke(this, EventArgs.Empty);

        var instrument = SelectedInstrument;
        if (instrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }

        NoDepth = !BrokerHasDepth(broker);

        try
        {
            var id = _ingest.Resolve(instrument.Contract, broker);
            _streamCts = new CancellationTokenSource();
            var ct = _streamCts.Token;

            // Ingest.Subscribe starts the broker's L1+L2 feed; SubscribeTrades adds the tape (no-op
            // handle if the broker has none). The two pumps tap the canonical hub.
            _streamHandles.Add(_ingest.Subscribe(instrument.Contract, broker));
            _streamHandles.Add(_ingest.SubscribeTrades(instrument.Contract, broker));
            _ = RunPumpAsync(_hub.Depth(id), ct, OnSnapshot);
            _ = RunPumpAsync(_hub.Trades(id), ct, OnTrade);

            var note = NoDepth ? " — this broker serves no L2 depth, so the heatmap stays empty (use Binance / a depth-capable broker)" : string.Empty;
            Status = $"Streaming {instrument.DisplayName} ({BrokerLabel(broker)}){note}";
            _log.Append(LogSource, NoDepth ? "WARN" : "INFO", $"Bubble heatmap on {instrument.DisplayName} [{BrokerLabel(broker)}]");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bubble heatmap: subscribe failed for {Symbol}", instrument.Contract.Symbol);
            Status = $"Subscribe failed: {ex.Message}";
        }
    }

    // Bounded + DropOldest channel + batch-drain: a fast book/tape the UI can't keep up with is capped
    // in memory (oldest shed) instead of piling an unbounded backlog; the frame is marked dirty once per
    // drained batch and the render is timer-coalesced.
    private async Task RunPumpAsync<T>(IObservable<T> source, CancellationToken ct, Action<T> onUi)
    {
        var cap = typeof(T) == typeof(DepthSnapshot) ? DepthChannelCapacity : TradeChannelCapacity;
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(cap)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        using var sub = source.Subscribe(x => channel.Writer.TryWrite(x));
        var batch = new List<T>(256);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxDrainBatch && channel.Reader.TryRead(out var item)) batch.Add(item);
                if (batch.Count == 0) continue;
                await UiThread.RunAsync(() =>
                {
                    foreach (var item in batch) onUi(item);
                    _dirty = true;
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Bubble heatmap pump ended"); }
        finally { channel.Writer.TryComplete(); }
    }

    /// <summary>A depth snapshot updates the current time column, or opens a new one once the column's
    /// wall-clock width has elapsed (so a burst of L2 updates can't shred the time axis).</summary>
    private void OnSnapshot(DepthSnapshot snapshot)
    {
        bool newColumn = _columns.Count == 0 || snapshot.TimestampUtc - _lastColumnTime >= ColumnInterval;
        if (newColumn)
        {
            _columns.Add(snapshot);
            _lastColumnTime = snapshot.TimestampUtc;
            while (_columns.Count > Math.Max(10, RetainedColumns)) _columns.RemoveAt(0);
        }
        else
        {
            _columns[^1] = snapshot; // refresh the resting book within the same time bucket
        }

        ColumnsFilled = _columns.Count;
        BestBid = snapshot.BestBid > 0 ? snapshot.BestBid : null;
        BestAsk = snapshot.BestAsk > 0 ? snapshot.BestAsk : null;
        Mid = BestBid is { } b && BestAsk is { } a ? (a + b) * 0.5 : null;
        var anchor = Mid ?? BestAsk ?? BestBid ?? 0;
        if (anchor > 0) PriceDecimals = DecimalsFor(anchor);
    }

    private void OnTrade(TradePrint trade)
    {
        var size = trade.Size;
        var side = trade.Aggressor == AggressorSide.Buy ? 1 : trade.Aggressor == AggressorSide.Sell ? -1 : 0;

        _sizeWindow.Enqueue(size);
        _sizeSum += size;
        while (_sizeWindow.Count > SizeWindow) _sizeSum -= _sizeWindow.Dequeue();
        var mean = _sizeWindow.Count > 0 ? (double)_sizeSum / _sizeWindow.Count : 0;
        var large = _sizeWindow.Count >= 20 && size >= Math.Max(2, mean * LargeMultiple);

        _trades.Enqueue(new HeatTrade(trade.EventTimeUtc, trade.Price, size, side, large));
        while (_trades.Count > MaxTrades) _trades.Dequeue();

        LastPrice = trade.Price;
        TradeCount++;
    }

    private void OnRenderTick()
    {
        if (IsPaused || !_dirty) return;
        _dirty = false;
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetBuffers()
    {
        _columns.Clear();
        _trades.Clear();
        _sizeWindow.Clear();
        _sizeSum = 0;
        _lastColumnTime = default;
        _dirty = false;
        ColumnsFilled = 0;
        TradeCount = 0;
        BestBid = BestAsk = Mid = LastPrice = null;
    }

    // ── Presets ──────────────────────────────────────────────────────────────────────────────────

    private readonly ToolPresetStore<BubbleHeatmapPreset> _presetStore = new("bubble-heatmap");

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0) return;
        _presetStore.Save(name, new BubbleHeatmapPreset(
            SelectedTimeframe?.Label ?? "1 s", ShowBubbles, LargeOnly, RetainedColumns));
        RefreshPresetNames(selected: name);
        _log.Append(LogSource, "INFO", $"Preset '{name}' saved");
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var name = SelectedPreset ?? PresetName.Trim();
        if (string.IsNullOrEmpty(name) || !_presetStore.Delete(name)) return;
        RefreshPresetNames(selected: null);
        _log.Append(LogSource, "INFO", $"Preset '{name}' deleted");
    }

    private void ApplyPreset(BubbleHeatmapPreset preset)
    {
        if (Timeframes.FirstOrDefault(t => t.Label == preset.TimeframeLabel) is { } tf)
            SelectedTimeframe = tf;
        ShowBubbles = preset.ShowBubbles;
        LargeOnly = preset.LargeOnly;
        if (RetainedOptions.Contains(preset.RetainedColumns)) RetainedColumns = preset.RetainedColumns;
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG snapshots stay view-side) ─────────

    [RelayCommand]
    private async Task ExportTradesCsvAsync()
    {
        if (_trades.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,price,size,side,large");
        foreach (var t in _trades)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{t.Time:O},{t.Price},{t.Size},{t.Side},{t.Large}"));
        await SaveCsvAsync("bubble-trades", sb.ToString());
    }

    [RelayCommand]
    private async Task ExportBookCsvAsync()
    {
        if (_columns.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,best_bid,best_ask");
        foreach (var c in _columns)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{c.TimestampUtc:O},{c.BestBid},{c.BestAsk}"));
        await SaveCsvAsync("bubble-book", sb.ToString());
    }

    private async Task SaveCsvAsync(string baseName, string content)
    {
        try
        {
            var symbol = (SelectedInstrument?.Contract.Symbol ?? "book").Replace('/', '-').Replace(':', '-');
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"{baseName}-{symbol}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, content);
            _log.Append(LogSource, "INFO", $"Exported → {path}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bubble heatmap: CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker (e.g. the keyless Binance tile) in the login screen.");
        return connected[0];
    }

    private static string BrokerLabel(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    private static int DecimalsFor(double price)
    {
        var p = Math.Abs(price);
        if (p >= 100) return 2;
        if (p >= 1) return 3;
        if (p >= 0.01) return 5;
        return 8;
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        foreach (var h in _streamHandles)
        {
            try { h.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bubble heatmap: stream handle dispose threw"); }
        }
        _streamHandles.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderTimer.Dispose();
        StopStream();
    }
}

/// <summary>A named snapshot of the Bubble Heatmap window's view options, persisted per user by
/// <see cref="ToolPresetStore{T}"/> (LocalAppData\DaxAlgo Terminal\tool-presets\bubble-heatmap.json).</summary>
public sealed record BubbleHeatmapPreset(
    string TimeframeLabel,
    bool ShowBubbles,
    bool LargeOnly,
    int RetainedColumns);
