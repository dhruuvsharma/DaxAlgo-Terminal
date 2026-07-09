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
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Presets;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public sealed partial class OrderFlowSurfaceSpikeViewModel : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;
    private const int TradeSummaryEveryN = 50;

    private static bool BrokerSupportsTradeTape(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => true,
        _ => false,
    };

    /// <summary>Source tag for this strategy's rows in the universal Activity Log.</summary>
    private const string LogSource = "Order Flow Surface Spike";

    private readonly LiveStrategyHostServices _services;
    private readonly INotificationPublisher _notifications;
    private readonly InMemoryLogSink _appLogSink;
    private readonly ILogger<OrderFlowSurfaceSpikeViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _quoteHandle;
    private IDisposable? _tradeHandle;
    private string? _symbolFilterToken;

    private OrderFlowSurfaceCalculator? _calc;
    private QuoteDerivedTradeSynthesizer? _synthesizer;
    private bool _useSynthetic;
    private int _candidateDirection;
    private int _candidateConsecutive;
    private long _position;
    private double _entryPrice;
    private int _orderSeq;

    public OrderFlowSurfaceSpikeViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        InMemoryLogSink appLogSink,
        ILogger<OrderFlowSurfaceSpikeViewModel> logger)
    {
        _services = services;
        _notifications = notifications;
        _appLogSink = appLogSink;
        _logger = logger;

        AllInstruments = SignalInstrumentCatalog.All;
        // Hide-until-search + restore the last instrument used here (see InstrumentPickerFilter).
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(InstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? AllInstruments.FirstOrDefault());
        ApplyInstrumentFilter();

        PresetNames = new ObservableCollection<string>(_presetStore.Names);
        _ = LoadInstrumentsAsync();
    }

    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "orderflow.surface.spike";

    [ObservableProperty] private ObservableCollection<SignalInstrument> _instruments = new();
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private SignalInstrument? _selectedInstrument;

    // Calculator / strategy parameters.
    [ObservableProperty] private int _ticksPerSlice = 100;
    [ObservableProperty] private int _numSlices = 30;
    [ObservableProperty] private double _priceBinSize = 0.05;
    [ObservableProperty] private int _windowBins = 41;
    [ObservableProperty] private double _spikeThreshold = 2.5;
    [ObservableProperty] private long _quantity = 1;
    [ObservableProperty] private double _stopLossPips = 20;
    [ObservableProperty] private double _takeProfitPips = 40;
    [ObservableProperty] private int _confirmationTicks = 2;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private string _status = "Configure the strategy to begin.";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isAlgoRunning;
    [ObservableProperty] private string? _validationError;

    /// <summary>Vertical exaggeration of the Z-score (Z) axis on the 3D surface — render-only, so
    /// it's live while streaming. The Window reads it when building the mesh and re-renders on change.</summary>
    [ObservableProperty] private double _surfaceHeightScale = 1.6;

    // Live readouts.
    [ObservableProperty] private double _currentMean;
    [ObservableProperty] private double _currentStd;
    [ObservableProperty] private double _currentSpikeZ;
    [ObservableProperty] private long _currentSpikeBin;
    [ObservableProperty] private long _tradesSeen;
    [ObservableProperty] private long _positionDisplay;
    [ObservableProperty] private string _signalLabel = "—";

    /// <summary>Latest Z-score surface for the renderer. <c>[slice, binOffset]</c>.</summary>
    public double[,]? Surface { get; private set; }
    public long LatestBin { get; private set; }

    public event EventHandler? SurfaceChanged;

    // ── Display pause (render-only; the tape + spike detector keep running) ────────────────────

    /// <summary>Gates the surface redraw; the tape pump and spike detector keep running
    /// underneath, so resume replays one redraw with everything that happened while paused.</summary>
    [ObservableProperty] private bool _isPaused;
    private bool _surfaceDirty;

    partial void OnIsPausedChanged(bool value)
    {
        if (value)
        {
            Status = "⏸ Display paused — the tape keeps streaming underneath.";
            return;
        }
        Status = IsStreaming ? $"Streaming — {SignalLabel}" : "Resumed.";
        if (!_surfaceDirty) return;
        _surfaceDirty = false;
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSurfaceChanged()
    {
        if (IsPaused) { _surfaceDirty = true; return; }
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Named presets (spike tuning + render options; never the instrument) ────────────────────

    private readonly ToolPresetStore<SurfaceSpikePreset> _presetStore = new("strategy-orderflow-surface-spike");

    public ObservableCollection<string> PresetNames { get; }

    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string? _selectedPreset;

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null) return;
        PresetName = value;
        if (_presetStore.Get(value) is { } preset) ApplyPreset(preset);
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0) return;
        _presetStore.Save(name, new SurfaceSpikePreset(
            TicksPerSlice, NumSlices, PriceBinSize, WindowBins, SpikeThreshold,
            ConfirmationTicks, Quantity, StopLossPips, TakeProfitPips, SurfaceHeightScale));
        RefreshPresetNames(selected: name);
        AddLog("PRESET", $"Preset '{name}' saved");
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var name = SelectedPreset ?? PresetName.Trim();
        if (string.IsNullOrEmpty(name) || !_presetStore.Delete(name)) return;
        RefreshPresetNames(selected: null);
        AddLog("PRESET", $"Preset '{name}' deleted");
    }

    /// <summary>Engine params apply on the next Start (locked while streaming); the surface
    /// height scale is render-only and applies immediately.</summary>
    private void ApplyPreset(SurfaceSpikePreset preset)
    {
        if (preset.TicksPerSlice > 0) TicksPerSlice = preset.TicksPerSlice;
        if (preset.NumSlices > 1) NumSlices = preset.NumSlices;
        if (preset.PriceBinSize > 0) PriceBinSize = preset.PriceBinSize;
        if (preset.WindowBins > 2) WindowBins = preset.WindowBins;
        if (preset.SpikeThreshold > 0) SpikeThreshold = preset.SpikeThreshold;
        if (preset.ConfirmationTicks > 0) ConfirmationTicks = preset.ConfirmationTicks;
        if (preset.Quantity > 0) Quantity = preset.Quantity;
        if (preset.StopLossPips > 0) StopLossPips = preset.StopLossPips;
        if (preset.TakeProfitPips > 0) TakeProfitPips = preset.TakeProfitPips;
        if (preset.SurfaceHeightScale > 0) SurfaceHeightScale = preset.SurfaceHeightScale;
        RaiseSurfaceChanged();
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG stays view-side) ─────────────────

    /// <summary>Exports the current Z-score surface, one row per (slice, bin) cell.</summary>
    [RelayCommand]
    private async Task ExportSurfaceCsvAsync()
    {
        if (Surface is not { } grid) return;
        var slices = grid.GetLength(0);
        var bins = grid.GetLength(1);
        var half = bins / 2;
        var sb = new StringBuilder();
        sb.AppendLine("slice,bin_offset,zscore");
        for (var sl = 0; sl < slices; sl++)
            for (var b = 0; b < bins; b++)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{sl},{b - half},{grid[sl, b]}"));
        try
        {
            var symbol = (SelectedInstrument?.Contract.Symbol ?? "spike").Replace('/', '-').Replace(':', '-');
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"surface-spike-{symbol}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, sb.ToString());
            AddLog("EXPORT", $"Exported → {path}");
            Status = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surface-spike CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }

    // Bounded, DropOldest channel + batch draining caps memory under a fast tape (the old unbounded
    // channel + per-trade marshal could pile a backlog into GBs over a long session).
    private const int TradeChannelCapacity = 65_536;
    private const int MaxStreamDrainBatch = 4_096;

    private void AddLog(string level, string message) =>
        _appLogSink.Append(LogSource, level, message);

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _services.Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            // Broker is shown as a coloured pill by the dropdown — keep DisplayName clean.
            AllInstruments = list
                .Select(i => new SignalInstrument(i.DisplayName, i.Category, i.Contract, i.Broker))
                .ToList();
            SelectedInstrument = (SelectedInstrument?.Contract.Symbol is { } prev
                                     ? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == prev) : null)
                                 ?? InstrumentPickerFilter.Remembered(InstrumentPersistKey, AllInstruments)
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "ES")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surface-spike instrument list load failed; using static catalog");
        }
    }

    private static string BrokerLabel(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _services.Selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="AllInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyInstrumentFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText, SelectedInstrument, MaxInstrumentsDisplayed));

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;
        if (SelectedInstrument is null) { ValidationError = "Pick an instrument before continuing."; return; }
        if (TicksPerSlice < 1) { ValidationError = "Ticks per slice must be >= 1."; return; }
        if (NumSlices < 2) { ValidationError = "Number of slices must be >= 2."; return; }
        if (PriceBinSize <= 0) { ValidationError = "Price bin size must be > 0."; return; }
        if (WindowBins < 5 || WindowBins % 2 == 0) { ValidationError = "Window bins must be odd and >= 5."; return; }
        if (SpikeThreshold <= 0) { ValidationError = "Spike threshold must be > 0."; return; }
        if (ConfirmationTicks < 1) { ValidationError = "Confirmation ticks must be >= 1."; return; }
        if (Quantity <= 0) { ValidationError = "Quantity must be > 0."; return; }
        if (StopLossPips <= 0 || TakeProfitPips <= 0) { ValidationError = "SL/TP must be > 0."; return; }

        IsConfigured = true;
        _ = StartStreamAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        AddLog("INFO", $"Algo {(IsAlgoRunning ? "ARMED" : "DISARMED")} for {label}");
        Status = IsAlgoRunning ? $"Armed on {label}" : $"Streaming {label} — algo idle";
    }

    public async Task StartStreamAsync(CancellationToken ct)
    {
        if (IsStreaming || SelectedInstrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(SelectedInstrument); }
        catch (InvalidOperationException ex) { ValidationError = ex.Message; Status = ex.Message; AddLog("ERROR", ex.Message); return; }

        // Capability check is now a soft branch — no native trade tape ⇒ auto-fallback to
        // L1 quote-derived synthetic trades. The directional-flow signal still works; the size
        // axis is degraded (top-of-book size, not real filled volume). Clear log marker.
        _useSynthetic = !BrokerSupportsTradeTape(broker);
        if (_useSynthetic)
        {
            AddLog("WARN", $"{BrokerLabel(broker)} has no native trade tape. Falling back to SYNTHETIC L1-derived trades (mid-up=Buy, mid-down=Sell, size=top-of-book). Signal degraded vs real tape.");
        }

        var contract = SelectedInstrument.Contract;
        _calc = new OrderFlowSurfaceCalculator(TicksPerSlice, NumSlices, PriceBinSize, WindowBins);
        _synthesizer = _useSynthetic ? new QuoteDerivedTradeSynthesizer() : null;
        TradesSeen = 0;
        CurrentMean = CurrentStd = CurrentSpikeZ = 0;
        CurrentSpikeBin = 0;
        SignalLabel = "—";
        PositionDisplay = 0;
        _candidateDirection = 0; _candidateConsecutive = 0;
        _position = 0; _entryPrice = 0;
        Surface = null;

        var tapeLabel = _useSynthetic ? "SYNTHETIC L1-derived" : "real trade tape";
        Status = $"Subscribing {SelectedInstrument.DisplayName} ({BrokerLabel(broker)}) — {tapeLabel}…";
        AddLog("INFO", $"Subscribing {SelectedInstrument.DisplayName} on {BrokerLabel(broker)} [{tapeLabel}]  (slice={TicksPerSlice}t × {NumSlices} slices, bin={PriceBinSize}, window={WindowBins}, Z*={SpikeThreshold:F2}, confirm={ConfirmationTicks}t)");

        _symbolFilterToken = contract.Symbol;

        _streamCts = new CancellationTokenSource();
        var streamCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _streamCts.Token).Token;
        IsStreaming = true;
        _firstTradeSeen = false;
        _ = RunStreamAsync(contract, broker, streamCt);
        _ = WatchNoTradesAsync(streamCt);
        await Task.CompletedTask;
    }

    /// <summary>Watchdog: if no trade has arrived 20 seconds after the stream started, emit a
    /// diagnostic checklist so the user knows the wiring is fine but the data isn't flowing.</summary>
    private async Task WatchNoTradesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (_firstTradeSeen) return;
            await UiThread.RunAsync(() =>
            {
                if (_firstTradeSeen) return;
                var modeNote = _useSynthetic ? " (synthetic mode)" : " (real trade tape)";
                AddLog("WARN", $"No trades received for {_symbolFilterToken} after 20s{modeNote}. Likely causes:");
                if (_useSynthetic)
                {
                    AddLog("WARN", "  • Mid price isn't moving — flat quotes produce no synthetic events; market may be closed or instrument inactive");
                    AddLog("WARN", "  • Broker quote stream itself isn't flowing — check the SYS log entries above");
                }
                else
                {
                    AddLog("WARN", "  • Market is closed (US equities/futures regular session: 09:30–16:00 ET)");
                    AddLog("WARN", "  • IB account on DELAYED data — reqTickByTickData needs LIVE; set InteractiveBrokers:MarketDataType=1");
                    AddLog("WARN", "  • No market-data permissions for this instrument's exchange (IB error 10189) — subscribe in IB Account Management");
                    AddLog("WARN", "  • Instrument illiquid right now — try SPY/ES/AAPL during US hours");
                }
            });
        }
        catch (OperationCanceledException) { /* stream stopped, ignore */ }
    }

    private async Task RunStreamAsync(Contract contract, BrokerKind broker, CancellationToken ct)
    {
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        await UiThread.RunAsync(() => AddLog("WIRE", $"Resolved {contract.Symbol} → InstrumentId={instrumentId.Value}"));

        // Quote pump always runs — needed for Lee-Ready bid/ask context in real mode, and is the
        // primary source for the synthesizer in synthetic mode.
        _quoteHandle = _services.Ingest.Subscribe(contract, broker);
        await UiThread.RunAsync(() => AddLog("WIRE", "Quote+depth pump started"));

        if (!_useSynthetic)
        {
            _tradeHandle = _services.Ingest.SubscribeTrades(contract, broker);
            await UiThread.RunAsync(() => AddLog("WIRE", "Real trade pump started — awaiting first trade…"));
        }
        else
        {
            await UiThread.RunAsync(() => AddLog("WIRE", "Synthetic mode — deriving trades from quote stream"));
        }

        // Bounded + DropOldest + batch draining: a fast tape the UI can't keep up with is capped in
        // memory (oldest prints shed) instead of piling an unbounded backlog into GBs, and the
        // surface is rebuilt once per drained batch rather than once per trade.
        var channel = Channel.CreateBounded<TradePrint>(new BoundedChannelOptions(TradeChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var sub = _useSynthetic
            ? _services.Hub.Quotes(instrumentId).Subscribe(q =>
              {
                  var synth = _synthesizer?.Synthesize(q);
                  if (synth is not null) channel.Writer.TryWrite(synth);
              })
            : _services.Hub.Trades(instrumentId).Subscribe(t => channel.Writer.TryWrite(t));
        await UiThread.RunAsync(() => AddLog("WIRE",
            _useSynthetic
                ? $"Hub.Quotes({instrumentId.Value}) observer connected (synthesizing)"
                : $"Hub.Trades({instrumentId.Value}) observer connected"));

        var batch = new List<TradePrint>(256);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxStreamDrainBatch && channel.Reader.TryRead(out var t)) batch.Add(t);
                if (batch.Count == 0) continue;
                await UiThread.RunAsync(() =>
                {
                    foreach (var trade in batch) OnTrade(trade);
                    RaiseSurfaceChanged();
                });
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surface-spike stream ended");
            await UiThread.RunAsync(() =>
            {
                Status = $"Stream stopped: {ex.Message}";
                AddLog("ERROR", $"Stream ended: {ex.GetType().Name}: {ex.Message}");
            });
        }
        finally
        {
            channel.Writer.TryComplete();
            await UiThread.RunAsync(() => IsStreaming = false);
        }
    }

    private bool _firstTradeSeen;

    private void OnTrade(TradePrint trade)
    {
        if (_calc is null) return;
        TradesSeen++;

        if (!_firstTradeSeen)
        {
            _firstTradeSeen = true;
            AddLog("INFO", $"First trade received: {trade.Aggressor} {trade.Size} @ {trade.Price} ({trade.Source})");
        }
        else if (TradesSeen % TradeSummaryEveryN == 0)
        {
            AddLog("DATA", $"#{TradesSeen,5}  {trade.Aggressor,-7} {trade.Size,6} @ {trade.Price,10:F4}");
        }

        var add = _calc.Add(trade, SpikeThreshold);
        CurrentMean = add.Mean;
        CurrentStd = add.Std;
        CurrentSpikeZ = add.SpikeZ;
        CurrentSpikeBin = add.SpikeBin ?? 0;
        LatestBin = add.LatestBin;
        Surface = _calc.GetZScoreSurface();

        EvaluateSignal(trade, add);
        // Redraw is raised once per drained batch by the pump, not per trade — see RunStreamAsync.
    }

    private void EvaluateSignal(TradePrint trade, OrderFlowSurfaceCalculator.AddResult add)
    {
        // Update label first so the UI shows current state regardless of algo armed/disarmed.
        SignalLabel = add.SpikeBin is null
            ? "no spike"
            : $"spike Z={add.SpikeZ:+0.00;-0.00} @ bin {add.SpikeBin}";

        // TP/SL (always active when in position, regardless of arm state — once entered, manage it).
        if (_position != 0)
        {
            var diff = trade.Price - _entryPrice;
            var hitSl = _position > 0 ? diff <= -StopLossPips : diff >= StopLossPips;
            var hitTp = _position > 0 ? diff >= TakeProfitPips : diff <= -TakeProfitPips;
            if (hitSl || hitTp)
            {
                AddLog("EXIT", $"{(hitTp ? "TP" : "SL")} hit at {trade.Price:F4} (entry {_entryPrice:F4}, pnl {diff:+0.00;-0.00})");
                FlattenPosition();
                return;
            }
        }

        if (add.SpikeBin is not null)
        {
            var dir = Math.Sign(add.SpikeZ);
            if (_candidateDirection == dir) _candidateConsecutive++;
            else { _candidateDirection = dir; _candidateConsecutive = 1; }

            if (_position == 0 && _candidateConsecutive >= ConfirmationTicks)
            {
                if (IsAlgoRunning)
                {
                    _position = dir > 0 ? Quantity : -Quantity;
                    _entryPrice = trade.Price;
                    PositionDisplay = _position;
                    var side = dir > 0 ? "LONG" : "SHORT";
                    AddLog("ENTRY", $"{side} {Quantity} @ {trade.Price:F4}  (Z={add.SpikeZ:+0.00;-0.00} confirmed {_candidateConsecutive}t)");
                    PublishSignal(side, $"Spike confirmed: Z={add.SpikeZ:F2}, entry {trade.Price:F4}");
                    _orderSeq++;
                }
                else
                {
                    AddLog("CONFIRM", $"Confirmed {(_candidateDirection > 0 ? "LONG" : "SHORT")} candidate (Z={add.SpikeZ:+0.00;-0.00}) — algo idle, no order");
                }
                _candidateDirection = 0; _candidateConsecutive = 0;
            }
            else if (_position != 0 && Math.Sign(_position) != dir)
            {
                AddLog("EXIT", $"Z flipped against position (Z={add.SpikeZ:+0.00;-0.00}) — flatten");
                FlattenPosition();
            }
            return;
        }

        // No spike — reset candidate. If in a position, Z has reverted; exit.
        _candidateDirection = 0; _candidateConsecutive = 0;
        if (_position != 0)
        {
            AddLog("EXIT", "Z reverted below threshold — flatten");
            FlattenPosition();
        }
    }

    private void FlattenPosition()
    {
        _position = 0;
        PositionDisplay = 0;
    }

    private void PublishSignal(string direction, string message)
    {
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: "orderflow.surface.spike",
            StrategyName: "Order Flow Surface Spike",
            Symbol: label,
            Direction: direction,
            Message: message,
            TimestampUtc: DateTime.UtcNow))
            .FireAndForgetSafe(_logger, "Surface-spike signal publish");
    }

    /// <summary>Strip "▶ Start" — (re)builds the calculator with the current slice/bin params and
    /// starts the stream. Lets the user Stop, edit the locked params, then Start to re-apply.</summary>
    [RelayCommand]
    private Task Start() => StartStreamAsync(CancellationToken.None);

    /// <summary>Strip "■ Stop" — stops the stream so the locked slice/bin params can be edited.</summary>
    [RelayCommand]
    private Task Stop() => StopStreamAsync();

    public async Task StopStreamAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _quoteHandle?.Dispose(); _quoteHandle = null;
        _tradeHandle?.Dispose(); _tradeHandle = null;
        IsStreaming = false;
        IsAlgoRunning = false;
        Status = "Stopped";
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        // Remember the instrument the user was last on so this window reopens on it.
        LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument?.Contract.Symbol);
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _quoteHandle?.Dispose(); _quoteHandle = null;
        _tradeHandle?.Dispose(); _tradeHandle = null;
    }
}

/// <summary>A named snapshot of the spike detector's tuning + render options, persisted per user
/// by <see cref="ToolPresetStore{T}"/> (tool-presets\strategy-orderflow-surface-spike.json).
/// Engine params apply on the next Start; the height scale is live. Never the instrument.</summary>
public sealed record SurfaceSpikePreset(
    int TicksPerSlice,
    int NumSlices,
    double PriceBinSize,
    int WindowBins,
    double SpikeThreshold,
    int ConfirmationTicks,
    long Quantity,
    double StopLossPips,
    double TakeProfitPips,
    double SurfaceHeightScale);
