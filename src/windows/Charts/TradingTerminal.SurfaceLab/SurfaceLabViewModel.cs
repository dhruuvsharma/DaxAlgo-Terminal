using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant.Surfaces;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.SurfaceLab;

/// <summary>
/// View-model for the 3D Surface Lab (Charts menu). The surface-mode selector re-targets the
/// four <see cref="AxisConfigViewModel"/>s (X/Y bucket variables, Z height statistic, W color
/// statistic — each with an optional math formula); Generate pulls historical bars and builds
/// the grid once, while <b>Go Live</b> seeds from history and then streams the canonical hub's
/// quotes/trades into a rolling <see cref="LiveBarSeries"/>, rebuilding the surface off the UI
/// thread on a coalesced 1 s tick (axis edits are picked up live). Memory-safe by construction:
/// bounded DropOldest channels, batch-drained pumps, hard-capped bar retention, and
/// <see cref="Dispose"/> tears down the timer, pumps, and subscriptions.
/// </summary>
public sealed partial class SurfaceLabViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<SurfaceLabViewModel> _logger;

    public const int MaxInstrumentsDisplayed = 500;
    private const int MaxLiveBars = 5000;
    private const int QuoteChannelCapacity = 16_384;
    private const int TradeChannelCapacity = 65_536;
    private const int MaxDrainBatch = 4_096;
    private const string LogSource = "Surface Lab";

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _liveCts;
    private readonly List<IDisposable> _streamHandles = new();
    private readonly IDisposable _rebuildTimer;
    private LiveBarSeries? _liveBars;
    private bool _dirty;
    private bool _rebuilding;
    private bool _disposed;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute,      TimeSpan.FromMinutes(1)),
        new("5m",  BarSize.FiveMinutes,    TimeSpan.FromMinutes(5)),
        new("15m", BarSize.FifteenMinutes, TimeSpan.FromMinutes(15)),
        new("1h",  BarSize.OneHour,        TimeSpan.FromHours(1)),
        new("1d",  BarSize.OneDay,         TimeSpan.FromDays(1)),
    };

    public SurfaceLabViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        InMemoryLogSink log,
        ILogger<SurfaceLabViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _log = log;
        _logger = logger;

        Modes = new ObservableCollection<SurfaceModeOption>
        {
            new("Temporal Aggregation", SurfaceMode.TemporalAggregation,
                "Seasonality surface: bucket realized returns by two calendar dimensions (hour × weekday, …)."),
            new("Statistical / Cross-Sectional", SurfaceMode.CrossSectional,
                "Conditional returns: bucket by prior return / volatility / volume / lag; height = next-period statistic."),
        };

        XAxis = new AxisConfigViewModel(SurfaceAxisRole.X, "X axis");
        YAxis = new AxisConfigViewModel(SurfaceAxisRole.Y, "Y axis");
        ZAxis = new AxisConfigViewModel(SurfaceAxisRole.Z, "Z axis (height)");
        WAxis = new AxisConfigViewModel(SurfaceAxisRole.Color, "Color (W)");

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneMinute);

        AllInstruments = SignalInstrumentCatalog.All;
        // Hide-until-search: empty visible list; ApplyInstrumentFilter (below) collapses it to the selection.
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(InstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "BTCUSDT")
                  ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                  ?? AllInstruments.FirstOrDefault());
        ApplyInstrumentFilter();

        SelectedMode = Modes[0];

        // Coalesced live rebuild: pumps only mark dirty; this tick snapshots the rolling bars
        // and rebuilds the grid off-thread, so tick rate never becomes rebuild rate.
        _rebuildTimer = UiThread.CreateRenderTimer(TimeSpan.FromSeconds(1), OnRebuildTick);

        _ = LoadInstrumentsAsync();
    }

    // ── Configuration surface ─────────────────────────────────────────────────────────────────

    public ObservableCollection<SurfaceModeOption> Modes { get; }
    public AxisConfigViewModel XAxis { get; }
    public AxisConfigViewModel YAxis { get; }
    public AxisConfigViewModel ZAxis { get; }
    public AxisConfigViewModel WAxis { get; }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    [ObservableProperty] private SurfaceModeOption? _selectedMode;
    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private int _barCount = 2000;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _runStatus = "Configure the axes, then Generate (one-shot) or Go Live.";

    // ── Live streaming ────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _liveStatus = "";

    public bool CanGenerate => !IsRunning && !IsLive;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));
    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));

    // ── Quant display toggles ─────────────────────────────────────────────────────────────────

    /// <summary>Places a red pin + value label on the surface's absolute maximum.</summary>
    [ObservableProperty] private bool _showPeakMarker = true;

    /// <summary>Colors the surface by neighbor-gradient robustness (green plateau = stable
    /// effect, red spike = noise/outlier) instead of by the W statistic.</summary>
    [ObservableProperty] private bool _robustnessColorMode;

    /// <summary>Vertical exaggeration of the Z axis (render-only).</summary>
    [ObservableProperty] private double _heightScale = 1.0;

    // ── Result + analytics ────────────────────────────────────────────────────────────────────

    /// <summary>The computed surface. Null until the first successful build.</summary>
    public SurfaceGridResult? Result { get; private set; }

    public bool HasResult => Result is not null;

    [ObservableProperty] private string _globalMaxText = "—";
    [ObservableProperty] private string _globalMinText = "—";
    [ObservableProperty] private string _peakLocationText = "—";
    [ObservableProperty] private string _cellCountText = "—";

    // ── Interactive slicing ───────────────────────────────────────────────────────────────────

    [ObservableProperty] private int _sliceXIndex;
    [ObservableProperty] private int _sliceYIndex;
    [ObservableProperty] private int _sliceXMax;
    [ObservableProperty] private int _sliceYMax;
    [ObservableProperty] private string _sliceXLabel = "—";
    [ObservableProperty] private string _sliceYLabel = "—";

    /// <summary>Raised after a successful build — full 3D + analytics redraw.</summary>
    public event EventHandler? SurfaceUpdated;

    /// <summary>Raised when a slice slider moves — the view redraws only the cutting planes
    /// and the 2D slice charts, never the mesh.</summary>
    public event EventHandler? SliceChanged;

    partial void OnSelectedModeChanged(SurfaceModeOption? value)
    {
        if (value is null) return;
        // Different default picks for X vs Y so the initial grid is never degenerate
        // (hour × weekday, return-bin × vol-bucket). Z defaults to average return,
        // W to frequency — the classic seasonality read.
        XAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.X), preferredIndex: 0);
        YAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.Y), preferredIndex: 1);
        ZAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.Z), preferredIndex: 0);
        WAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.Color), preferredIndex: 3 /* count */);
        if (IsLive) _dirty = true; // live surface re-targets on the next tick
    }

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "tool.surfacelab";

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();
    partial void OnShowPeakMarkerChanged(bool value) => SurfaceUpdated?.Invoke(this, EventArgs.Empty);
    partial void OnRobustnessColorModeChanged(bool value) => SurfaceUpdated?.Invoke(this, EventArgs.Empty);
    partial void OnHeightScaleChanged(double value) => SurfaceUpdated?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedInstrumentChanged(SignalInstrument? value)
    {
        if (IsLive) StopLive("Instrument changed — live stream stopped.");
    }

    partial void OnSelectedTimeframeChanged(TimeframeOption? value)
    {
        if (IsLive) StopLive("Timeframe changed — live stream stopped.");
    }

    partial void OnSliceXIndexChanged(int value)
    {
        SliceXLabel = Result is { } r && value >= 0 && value < r.XLabels.Length ? r.XLabels[value] : "—";
        SliceChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSliceYIndexChanged(int value)
    {
        SliceYLabel = Result is { } r && value >= 0 && value < r.YLabels.Length ? r.YLabels[value] : "—";
        SliceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── One-shot Generate ─────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task GenerateAsync()
    {
        if (IsRunning || IsLive) return;
        if (Validate() is not { } config) return;

        ErrorMessage = null;
        IsRunning = true;
        RunStatus = "Loading bars…";
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var bars = await FetchHistoryAsync(config.Broker, ct);
            if (bars.Count < 200)
            {
                ErrorMessage = $"Only {bars.Count} bars returned; need at least 200.";
                return;
            }

            RunStatus = "Computing surface…";
            var request = MakeRequest(config, SurfaceGridBuilder.EstimatePeriodsPerYear(bars));
            var result = await Task.Run(() => SurfaceGridBuilder.Build(bars, request, ct), ct);
            Apply(result, bars.Count, resetSlices: true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Surface Lab generation failed");
            ErrorMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    // ── Live mode ─────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task ToggleLiveAsync()
    {
        if (IsLive)
        {
            StopLive("Live stream stopped.");
            return;
        }

        if (IsRunning) return;
        if (Validate() is not { } config) return;

        ErrorMessage = null;
        IsRunning = true;
        RunStatus = "Seeding from history…";
        try
        {
            // Seed the rolling window from history so the surface is dense from second one;
            // live ticks then extend it. A short/empty history is fine — the window fills live.
            IReadOnlyList<Bar> history = Array.Empty<Bar>();
            try
            {
                history = await FetchHistoryAsync(config.Broker, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Surface Lab: history seed failed, starting live from empty");
            }

            var series = new LiveBarSeries(config.Timeframe.Interval, Math.Clamp(BarCount, 200, MaxLiveBars));
            series.Seed(history);
            _liveBars = series;

            var id = _ingest.Resolve(config.Instrument.Contract, config.Broker);
            _liveCts = new CancellationTokenSource();
            var ct = _liveCts.Token;

            // Ingest.Subscribe starts the broker's L1 feed; SubscribeTrades adds the tape where
            // the broker has one (volume for the decile/Amihud axes; harmless no-op otherwise).
            _streamHandles.Add(_ingest.Subscribe(config.Instrument.Contract, config.Broker));
            _streamHandles.Add(_ingest.SubscribeTrades(config.Instrument.Contract, config.Broker));
            _ = RunPumpAsync(_hub.Quotes(id), QuoteChannelCapacity, ct, OnQuoteBatch);
            _ = RunPumpAsync(_hub.Trades(id), TradeChannelCapacity, ct, OnTradeBatch);

            IsLive = true;
            _dirty = true; // draw the seeded surface immediately
            LiveStatus = $"LIVE · {config.Instrument.DisplayName} · seeded {history.Count} bars";
            RunStatus = "Streaming — the surface rebuilds as ticks arrive.";
            _log.Append(LogSource, "INFO", $"Live surface on {config.Instrument.DisplayName} ({config.Timeframe.Label})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surface Lab: live subscribe failed");
            ErrorMessage = $"Live subscribe failed: {ex.Message}";
            StopLive(null);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void StopLive(string? status)
    {
        _liveCts?.Cancel();
        _liveCts?.Dispose();
        _liveCts = null;
        foreach (var h in _streamHandles)
        {
            try { h.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Surface Lab: stream handle dispose threw"); }
        }
        _streamHandles.Clear();
        _liveBars = null;
        _dirty = false;
        if (IsLive) _log.Append(LogSource, "INFO", "Live surface stopped");
        IsLive = false;
        LiveStatus = "";
        if (status is not null) RunStatus = status;
    }

    /// <summary>Bounded + DropOldest channel + batch-drain: a fast feed the UI can't keep up
    /// with is capped in memory (oldest shed) and marshalled to the UI once per batch.</summary>
    private async Task RunPumpAsync<T>(IObservable<T> source, int capacity, CancellationToken ct, Action<List<T>> onUiBatch)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
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
                var snapshot = batch; // reused list is safe: onUiBatch runs before the next drain
                await UiThread.RunAsync(() =>
                {
                    onUiBatch(snapshot);
                    _dirty = true;
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Surface Lab pump ended"); }
        finally { channel.Writer.TryComplete(); }
    }

    private void OnQuoteBatch(List<Quote> quotes)
    {
        if (_liveBars is not { } series) return;
        foreach (var q in quotes)
        {
            if (q.Bid <= 0 || q.Ask <= 0) continue;
            series.PushPrice(q.EventTimeUtc, (q.Bid + q.Ask) * 0.5);
        }
    }

    private void OnTradeBatch(List<TradePrint> trades)
    {
        if (_liveBars is not { } series) return;
        foreach (var t in trades)
        {
            series.PushPrice(t.EventTimeUtc, t.Price);
            series.PushVolume(t.Size);
        }
    }

    /// <summary>The coalesced live tick: snapshot the rolling bars on the UI thread, rebuild the
    /// grid off it. Skipped while a rebuild is in flight, so a slow build can never stack.</summary>
    private async void OnRebuildTick()
    {
        if (!IsLive || !_dirty || _rebuilding) return;
        if (_liveBars is not { } series || SelectedMode is null) return;

        var xSpec = XAxis.ToSpec(out var err);
        var ySpec = err is null ? YAxis.ToSpec(out err) : null;
        var zSpec = err is null ? ZAxis.ToSpec(out err) : null;
        var wSpec = err is null ? WAxis.ToSpec(out err) : null;
        if (err is not null)
        {
            // Mid-edit axis config (e.g. a half-typed formula): show it, keep streaming, retry
            // on the next tick once the config parses again.
            ErrorMessage = err;
            return;
        }

        var bars = series.Snapshot();
        if (bars.Length < 30) { LiveStatus = $"LIVE · warming up ({bars.Length} bars)"; return; }

        _dirty = false;
        _rebuilding = true;
        var request = new SurfaceRequest(
            SelectedMode.Mode, xSpec!, ySpec!, zSpec!, wSpec!,
            SurfaceGridBuilder.EstimatePeriodsPerYear(bars));
        try
        {
            var result = await Task.Run(() => SurfaceGridBuilder.Build(bars, request));
            if (!IsLive) return; // stopped while building
            ErrorMessage = null;
            Apply(result, bars.Length, resetSlices: false);
            LiveStatus = $"LIVE · {bars.Length} bars · updated {DateTime.Now:HH:mm:ss}";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Surface Lab live rebuild failed");
            ErrorMessage = $"Live rebuild failed: {ex.Message}";
            StopLive("Live stopped after a rebuild failure.");
        }
        finally
        {
            _rebuilding = false;
        }
    }

    // ── Shared build plumbing ─────────────────────────────────────────────────────────────────

    private sealed record BuildConfig(
        SignalInstrument Instrument,
        TimeframeOption Timeframe,
        BrokerKind Broker,
        SurfaceAxisSpec X, SurfaceAxisSpec Y, SurfaceAxisSpec Z, SurfaceAxisSpec W);

    private BuildConfig? Validate()
    {
        if (SelectedMode is null) { ErrorMessage = "Pick a surface mode."; return null; }
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument."; return null; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return null; }
        if (BarCount < 200) { ErrorMessage = "Need at least 200 bars for meaningful statistics."; return null; }

        var xSpec = XAxis.ToSpec(out var err);
        if (xSpec is null) { ErrorMessage = err; return null; }
        var ySpec = YAxis.ToSpec(out err);
        if (ySpec is null) { ErrorMessage = err; return null; }
        var zSpec = ZAxis.ToSpec(out err);
        if (zSpec is null) { ErrorMessage = err; return null; }
        var wSpec = WAxis.ToSpec(out err);
        if (wSpec is null) { ErrorMessage = err; return null; }

        BrokerKind broker;
        try { broker = ResolveBroker(SelectedInstrument); }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; return null; }

        return new BuildConfig(SelectedInstrument, SelectedTimeframe, broker, xSpec, ySpec, zSpec, wSpec);
    }

    private SurfaceRequest MakeRequest(BuildConfig c, double periodsPerYear) =>
        new(SelectedMode!.Mode, c.X, c.Y, c.Z, c.W, periodsPerYear);

    private async Task<IReadOnlyList<Bar>> FetchHistoryAsync(BrokerKind broker, CancellationToken ct)
    {
        var duration = EstimateDuration(SelectedTimeframe!.BarSize, BarCount);
        return await _repository.GetHistoricalBarsAsync(
            SelectedInstrument!.Contract, broker, SelectedTimeframe.BarSize, duration, ct);
    }

    private void Apply(SurfaceGridResult result, int barCount, bool resetSlices)
    {
        Result = result;
        OnPropertyChanged(nameof(HasResult));

        var max = SurfaceGridAnalysis.FindMax(result.Z);
        var min = SurfaceGridAnalysis.FindMin(result.Z);
        GlobalMaxText = max.IsValid
            ? $"{SurfaceAxisFormats.Format(max.Value, result.ZFormat)}  @ {result.XName} {result.XLabels[max.Col]} · {result.YName} {result.YLabels[max.Row]}"
            : "—";
        GlobalMinText = min.IsValid
            ? $"{SurfaceAxisFormats.Format(min.Value, result.ZFormat)}  @ {result.XName} {result.XLabels[min.Col]} · {result.YName} {result.YLabels[min.Row]}"
            : "—";
        PeakLocationText = max.IsValid
            ? $"{result.XName} = {result.XLabels[max.Col]},  {result.YName} = {result.YLabels[max.Row]},  {result.ZName} = {SurfaceAxisFormats.Format(max.Value, result.ZFormat)}"
            : "—";
        CellCountText = $"{result.Columns} × {result.Rows} cells · {barCount} bars";
        if (!IsLive)
            RunStatus = $"{result.ZName} over {result.XName} × {result.YName} · {CellCountText}";

        SliceXMax = result.Columns - 1;
        SliceYMax = result.Rows - 1;
        int newX, newY;
        if (resetSlices)
        {
            // Park the slice cursors on the peak so the 2D viewer opens at the interesting spot.
            newX = Math.Clamp(max.IsValid ? max.Col : result.Columns / 2, 0, SliceXMax);
            newY = Math.Clamp(max.IsValid ? max.Row : result.Rows / 2, 0, SliceYMax);
        }
        else
        {
            // Live: keep the analyst's cursor where they put it (clamped to the new grid).
            newX = Math.Clamp(SliceXIndex, 0, SliceXMax);
            newY = Math.Clamp(SliceYIndex, 0, SliceYMax);
        }
        SliceXIndex = newX;
        SliceYIndex = newY;
        // Refresh labels + slice charts even when the indices didn't move (the data did).
        SliceXLabel = result.XLabels[newX];
        SliceYLabel = result.YLabels[newY];
        SliceChanged?.Invoke(this, EventArgs.Empty);

        SurfaceUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Remember the instrument the user was last on so this window reopens on it.
        LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument?.Contract.Symbol);
        _rebuildTimer.Dispose();
        StopLive(null);
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }

    // ── Shared picker plumbing (each tool owns its copy — independent projects) ─────────────

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    private static TimeSpan EstimateDuration(BarSize size, int barCount) => size switch
    {
        BarSize.OneMinute      => TimeSpan.FromMinutes(barCount * 1.5),
        BarSize.FiveMinutes    => TimeSpan.FromMinutes(barCount * 5 * 1.5),
        BarSize.FifteenMinutes => TimeSpan.FromMinutes(barCount * 15 * 1.5),
        BarSize.OneHour        => TimeSpan.FromHours(barCount * 1.5),
        BarSize.OneDay         => TimeSpan.FromDays(barCount * 1.5),
        _                      => TimeSpan.FromDays(7),
    };

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            AllInstruments = list.Select(i => new SignalInstrument(
                i.DisplayName, i.Category, i.Contract, i.Broker)).ToList();
            SelectedInstrument = (SelectedInstrument?.Contract.Symbol is { } prev
                                     ? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == prev) : null)
                                 ?? InstrumentPickerFilter.Remembered(InstrumentPersistKey, AllInstruments)
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "BTCUSDT")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surface Lab: broker universe load failed, using static catalog");
        }
    }

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="AllInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyInstrumentFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText, SelectedInstrument, MaxInstrumentsDisplayed));
}

/// <summary>Surface-mode dropdown row.</summary>
public sealed record SurfaceModeOption(string Label, SurfaceMode Mode, string Description)
{
    public override string ToString() => Label;
}

/// <summary>Bar-size dropdown row (with the wall-clock interval the live aggregator buckets by).
/// Each tool owns its own copy so the panels stay independent projects.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize, TimeSpan Interval)
{
    public override string ToString() => Label;
}
