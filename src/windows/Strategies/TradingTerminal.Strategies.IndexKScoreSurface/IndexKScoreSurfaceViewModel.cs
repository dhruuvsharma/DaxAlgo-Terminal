using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.IndexKScore;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;
using TradingTerminal.UI.Presets;

namespace TradingTerminal.Strategies.IndexKScoreSurface;

/// <summary>
/// Multi-instrument host VM for the Index K-Score Surface strategy. Unlike the single-symbol
/// <see cref="LiveSignalStrategyViewModelBase"/>, this VM fans out to every component of the
/// chosen index family in parallel — for each one it starts a quote pump, aggregates ticks into
/// configurable-interval bars locally, feeds finalized bars into a dedicated
/// <see cref="IndexKScoreCalculator"/>, then folds the snapshot into the
/// <see cref="IndexKScoreAggregator"/>. The aggregator's per-stock thresholds and
/// piercing-aggregation logic produce the index-level LONG / SHORT signals.
///
/// <para>On Start, each component is warmed up cache-first via
/// <see cref="IMarketDataRepository.GetHistoricalBarsAsync"/> at the chosen timeframe — the local
/// store is served when present, otherwise bars are fetched from the component's broker and
/// persisted (falling back to local 1-minute bars aggregated to the target interval when neither
/// has data) — so the 3D heat surface paints with historical context immediately, instead of
/// waiting one full bar interval per component.</para>
///
/// <para>The surface itself is a [time-slice × component] matrix of <c>K_final</c> values,
/// keyed by sorting components by ascending index weight (lightest on the left, heaviest on
/// the right). The per-column threshold drops monotonically across X — heavier names need less
/// |K| to count as piercing — and the renderer uses it as the texture-mapping pivot: cells
/// below the threshold paint cool/blue, cells crossing paint with the heat gradient.</para>
///
/// <para>Signal-mode only — no order placement, no broker writes. The window publishes
/// <see cref="INotificationPublisher"/> events when an index-level entry fires.</para>
/// </summary>
public sealed partial class IndexKScoreSurfaceViewModel : ViewModelBase, IDisposable
{
    public const int KHistoryLength = 30;
    private const string StrategyId = "index.kscore.surface";
    private const string StrategyName = "Index K-Score Surface";

    private readonly LiveStrategyHostServices _services;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<IndexKScoreSurfaceViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private readonly List<IDisposable> _ingestHandles = new();
    private readonly List<IDisposable> _hubSubscriptions = new();
    private readonly Dictionary<string, ComponentRuntime> _runtimes = new();
    private IReadOnlyList<IndexComponent> _orderedComponents = Array.Empty<IndexComponent>();
    private IndexKScoreAggregator? _aggregator;
    private bool _lastLong;
    private bool _lastShort;

    /// <summary>Coalesces intra-bar surface repaints across all N components down to a bounded
    /// render rate (~8 Hz) so a burst of quotes across the index family doesn't trigger a redraw
    /// storm. Set <see cref="_surfaceDirty"/> on each quote; the timer flushes one repaint.</summary>
    private IDisposable? _renderTimer;
    private volatile bool _surfaceDirty;
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(125);

    public IndexKScoreSurfaceViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        ILogger<IndexKScoreSurfaceViewModel> logger)
    {
        _services = services;
        _notifications = notifications;
        _logger = logger;

        Families = new ObservableCollection<IndexFamily>(IndexComponentCatalog.All);
        SelectedFamily = Families.FirstOrDefault(f => f.Id == "us30") ?? Families[0];
        RefreshPresetNames(selected: null);

        BarSizes = new ObservableCollection<BarSize>
        {
            BarSize.OneMinute, BarSize.ThreeMinutes, BarSize.FiveMinutes, BarSize.FifteenMinutes, BarSize.OneHour,
        };
        SelectedBarSize = BarSize.FiveMinutes;

        Components = new ObservableCollection<ComponentSnapshot>();
    }

    public ObservableCollection<IndexFamily> Families { get; }
    public ObservableCollection<BarSize> BarSizes { get; }
    public ObservableCollection<ComponentSnapshot> Components { get; }

    [ObservableProperty] private IndexFamily _selectedFamily;
    [ObservableProperty] private BarSize _selectedBarSize;

    [ObservableProperty] private double _tMin = 0.25;
    [ObservableProperty] private double _tMax = 0.80;
    [ObservableProperty] private int _minPierceCount = 4;
    [ObservableProperty] private double _cumKThreshold = 10.0;

    [ObservableProperty] private int _rsiLength = 14;
    [ObservableProperty] private int _macdFast = 12;
    [ObservableProperty] private int _macdSlow = 26;
    [ObservableProperty] private int _macdSignal = 9;
    [ObservableProperty] private int _atrLength = 14;
    [ObservableProperty] private int _atrRegLength = 50;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isAlgoRunning;
    [ObservableProperty] private bool _isWarmingUp;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string _status = "Configure the strategy to begin.";

    /// <summary>Vertical exaggeration of the K (Z) axis on the 3D surface — render-only, so it's
    /// live while streaming. The Window reads it when building the mesh and re-renders on change.</summary>
    [ObservableProperty] private double _surfaceHeightScale = 1.6;

    [ObservableProperty] private int _piercingUpCount;
    [ObservableProperty] private int _piercingDownCount;
    [ObservableProperty] private double _cumulativeKUp;
    [ObservableProperty] private double _cumulativeKDown;
    [ObservableProperty] private bool _longSignalActive;
    [ObservableProperty] private bool _shortSignalActive;
    [ObservableProperty] private int _componentsReady;
    [ObservableProperty] private int _componentsTotal;

    /// <summary>Latest index snapshot — consumed by the right-side data grid.</summary>
    public IndexSnapshot? LatestSnapshot { get; private set; }

    /// <summary>
    /// [time-slice × component] matrix of <c>K_final</c> values. Rows: 0 = oldest, last = newest.
    /// Columns: ordered by ascending index weight. Consumed by the 3D viewport's heightmap
    /// rendering. Null until <see cref="ApplySnapshot"/> has run at least once.
    /// </summary>
    public double[,]? Surface { get; private set; }

    /// <summary>Per-column threshold (same length as the surface's column dimension). Used by
    /// the renderer to pivot the texture coordinate between cool (below threshold) and heat
    /// (crossed). Null until <see cref="ApplySnapshot"/> has run at least once.</summary>
    public double[]? Thresholds { get; private set; }

    /// <summary>Symbol per column for the renderer's axis tick labels.</summary>
    public string[]? ColumnSymbols { get; private set; }

    public event EventHandler? SurfaceChanged;

    // ── Display pause (render-only; components, calculators and gates keep running) ──

    /// <summary>Gates the coalesced surface repaint; every component stream, K calculator and
    /// the aggregator keep running underneath. Resume repaints on the next render tick.</summary>
    [ObservableProperty] private bool _isPaused;

    partial void OnIsPausedChanged(bool value)
    {
        if (value)
        {
            Status = "⏸ Display paused — components keep streaming underneath.";
            return;
        }
        Status = "Resumed.";
        _surfaceDirty = true;
    }

    // ── Named presets (K-score tuning + render options; never the index family) ──

    private readonly ToolPresetStore<KScorePreset> _presetStore = new("strategy-index-kscore");

    public ObservableCollection<string> PresetNames { get; } = new();

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
        _presetStore.Save(name, new KScorePreset(
            TMin, TMax, MinPierceCount, CumKThreshold,
            RsiLength, MacdFast, MacdSlow, MacdSignal, AtrLength, AtrRegLength,
            SurfaceHeightScale));
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
    private void ApplyPreset(KScorePreset preset)
    {
        if (preset.TMax > preset.TMin && preset.TMin > 0) { TMin = preset.TMin; TMax = preset.TMax; }
        if (preset.MinPierceCount > 0) MinPierceCount = preset.MinPierceCount;
        if (preset.CumKThreshold > 0) CumKThreshold = preset.CumKThreshold;
        if (preset.RsiLength > 1) RsiLength = preset.RsiLength;
        if (preset.MacdFast > 0 && preset.MacdSlow > preset.MacdFast)
        {
            MacdFast = preset.MacdFast;
            MacdSlow = preset.MacdSlow;
        }
        if (preset.MacdSignal > 0) MacdSignal = preset.MacdSignal;
        if (preset.AtrLength > 1) AtrLength = preset.AtrLength;
        if (preset.AtrRegLength > 1) AtrRegLength = preset.AtrRegLength;
        if (preset.SurfaceHeightScale > 0) SurfaceHeightScale = preset.SurfaceHeightScale;
        _surfaceDirty = true;
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG stays view-side) ──

    /// <summary>Exports the K-score surface: one row per (time-slice, component) with the
    /// component symbol and its per-column piercing threshold.</summary>
    [RelayCommand]
    private async Task ExportSurfaceCsvAsync()
    {
        if (Surface is not { } grid || ColumnSymbols is not { } symbols) return;
        var rows = grid.GetLength(0);
        var cols = grid.GetLength(1);
        var sb = new StringBuilder();
        sb.AppendLine("slice,component,k_final,threshold");
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r},{(c < symbols.Length ? symbols[c] : c.ToString())},{grid[r, c]},{(Thresholds is { } t && c < t.Length ? t[c] : double.NaN)}"));
        try
        {
            var family = SelectedFamily?.Id ?? "index";
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"kscore-{family}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, sb.ToString());
            AddLog("EXPORT", $"Exported → {path}");
            Status = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "K-score surface CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }

    public IndexKScoreParameters BuildParameters() => new()
    {
        RsiLength = RsiLength,
        MacdFast = MacdFast,
        MacdSlow = MacdSlow,
        MacdSignal = MacdSignal,
        AtrLength = AtrLength,
        AtrRegLength = AtrRegLength,
    };

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;
        if (SelectedFamily is null) { ValidationError = "Pick an index family."; return; }
        if (TMin <= 0 || TMax <= 0 || TMin >= TMax)
        { ValidationError = "Thresholds require 0 < T_min < T_max."; return; }
        if (MinPierceCount < 1) { ValidationError = "Min pierce count must be >= 1."; return; }
        if (CumKThreshold <= 0) { ValidationError = "Cumulative K threshold must be > 0."; return; }

        IndexKScoreParameters parameters;
        try { parameters = BuildParameters(); parameters.Validate(); }
        catch (Exception ex) { ValidationError = ex.Message; return; }

        if (_services.Selector.Connected.Count == 0)
        { ValidationError = "No broker is connected. Connect a broker in the login screen first."; return; }

        try
        {
            _aggregator = new IndexKScoreAggregator(
                SelectedFamily.Components, TMin, TMax, MinPierceCount, CumKThreshold);
        }
        catch (Exception ex) { ValidationError = ex.Message; return; }

        // Order components by ascending index weight — lightest left, heaviest right. The
        // threshold drops monotonically across X so the renderer can draw a single threshold
        // curtain that ramps down across the component axis.
        _orderedComponents = SelectedFamily.Components.OrderBy(c => c.IndexWeight).ToList();

        IsConfigured = true;
        ComponentsTotal = _orderedComponents.Count;
        ComponentsReady = 0;
        _ = StartStreamAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        AddLog("INFO", $"Algo {(IsAlgoRunning ? "ARMED" : "DISARMED")} on {SelectedFamily?.DisplayName}");
        Status = IsAlgoRunning
            ? $"Armed on {SelectedFamily?.DisplayName} ({ComponentsReady}/{ComponentsTotal} ready)"
            : $"Streaming {SelectedFamily?.DisplayName} — algo idle";
    }

    /// <summary>Strip "▶ Start" — rebuilds the aggregator with the current (possibly edited)
    /// thresholds and component ordering, then restarts the multi-component stream. This is how
    /// the locked params get re-applied after a Stop.</summary>
    [RelayCommand]
    private void Start()
    {
        if (IsStreaming || SelectedFamily is null) return;
        ValidationError = null;
        if (TMin <= 0 || TMax <= 0 || TMin >= TMax)
        { ValidationError = "Thresholds require 0 < T_min < T_max."; return; }
        if (MinPierceCount < 1) { ValidationError = "Min pierce count must be >= 1."; return; }
        if (CumKThreshold <= 0) { ValidationError = "Cumulative K threshold must be > 0."; return; }
        try
        {
            _aggregator = new IndexKScoreAggregator(
                SelectedFamily.Components, TMin, TMax, MinPierceCount, CumKThreshold);
        }
        catch (Exception ex) { ValidationError = ex.Message; return; }

        _orderedComponents = SelectedFamily.Components.OrderBy(c => c.IndexWeight).ToList();
        ComponentsTotal = _orderedComponents.Count;
        ComponentsReady = 0;
        _ = StartStreamAsync(CancellationToken.None);
    }

    /// <summary>Strip "■ Stop" — stops the stream so the locked threshold/indicator params can be edited.</summary>
    [RelayCommand]
    private Task Stop() => StopStreamAsync();

    private async Task StartStreamAsync(CancellationToken ct)
    {
        if (IsStreaming || _aggregator is null || SelectedFamily is null) return;

        _streamCts = new CancellationTokenSource();
        var streamCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _streamCts.Token).Token;
        var parameters = BuildParameters();

        IsStreaming = true;
        Status = $"Subscribing {_orderedComponents.Count} components on {SelectedFamily.DisplayName}…";
        AddLog("INFO",
            $"Streaming {SelectedFamily.DisplayName}: {_orderedComponents.Count} components, " +
            $"bar={SelectedBarSize.ToDisplayString()}, T=[{TMin:F2},{TMax:F2}], " +
            $"minPierce={MinPierceCount}, cumK≥{CumKThreshold:F2}");

        var subscribed = 0;
        var failedSymbols = new List<string>();
        var idsBySymbol = new Dictionary<string, InstrumentId>();

        foreach (var c in _orderedComponents)
        {
            try
            {
                BrokerKind broker = ResolveBroker(c);
                var instrumentId = _services.Ingest.Resolve(c.Contract, broker);
                idsBySymbol[c.Symbol] = instrumentId;
                var handle = _services.Ingest.Subscribe(c.Contract, broker);
                _ingestHandles.Add(handle);

                var runtime = new ComponentRuntime(
                    c, new IndexKScoreCalculator(parameters), SelectedBarSize.ToTimeSpan());
                _runtimes[c.Symbol] = runtime;

                var hubSub = _services.Hub.Quotes(instrumentId).Subscribe(q => OnQuote(c.Symbol, q));
                _hubSubscriptions.Add(hubSub);

                subscribed++;
            }
            catch (Exception ex)
            {
                failedSymbols.Add(c.Symbol);
                _logger.LogWarning(ex, "Failed to subscribe {Symbol}", c.Symbol);
            }
        }

        AddLog("WIRE", $"Subscribed {subscribed}/{_orderedComponents.Count} components");
        if (failedSymbols.Count > 0)
            AddLog("ERROR", $"Failed: {string.Join(", ", failedSymbols)}");

        // Warm up each component's calculator + K history from the store/broker so the surface
        // paints with context immediately, not after one full bar interval per component.
        await WarmupAsync(idsBySymbol, streamCt);

        // Initial snapshot — paint immediately even with zero warmup bars so the window shows the
        // threshold curtain + axes (BuildSurface always pads to a full 30×N matrix). Also start
        // the coalesced intra-bar render timer so the surface moves between bar closes.
        var snap = _aggregator.BuildAggregate(DateTime.UtcNow);
        await UiThread.RunAsync(() =>
        {
            ApplySnapshot(snap);
            StartRenderTimer();
        });
    }

    /// <summary>Starts (or restarts) the ~8 Hz coalescing render timer. Each tick checks the dirty
    /// flag and, if set, repaints the surface with the current provisional (intra-bar) column. Runs
    /// on the UI dispatcher; must be created on the UI thread.</summary>
    private void StartRenderTimer()
    {
        // Guard against double-start (a rapid Stop→Start could otherwise orphan a live timer).
        // Portable coalescing render timer — ticks are marshalled to the UI thread (see UiThread).
        if (_renderTimer is not null) return;
        _renderTimer = UiThread.CreateRenderTimer(RenderInterval, OnRenderTick);
    }

    /// <summary>Coalesced repaint. Render-only: rebuilds the surface matrix (overlaying each
    /// component's provisional intra-bar K on the newest column) and fires <see cref="SurfaceChanged"/>.
    /// Does NOT touch the calculator, K-history, or aggregator — those stay bar-close-driven, so
    /// signal/entry semantics are unchanged.</summary>
    private void OnRenderTick()
    {
        if (IsPaused) return;   // _surfaceDirty keeps accumulating; resume repaints next tick
        if (!_surfaceDirty) return;
        _surfaceDirty = false;
        if (_aggregator is null || _orderedComponents.Count == 0) return;
        BuildSurface();
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads recent bars per component and feeds them into each calculator + K-history
    /// queue so the surface paints with context immediately, not after one full bar interval per
    /// component. History comes from <see cref="IMarketDataRepository.GetHistoricalBarsAsync"/> —
    /// cache-first: it serves the local store when present, otherwise fetches from the component's
    /// broker and persists, so an empty store still populates the surface within seconds. As a
    /// last resort (broker returns nothing too) we fall back to local 1-minute bars aggregated to
    /// the target interval. Silent on per-component failures — partial warmup is better than none.</summary>
    private async Task WarmupAsync(IReadOnlyDictionary<string, InstrumentId> idsBySymbol, CancellationToken ct)
    {
        if (idsBySymbol.Count == 0) return;
        await UiThread.RunAsync(() =>
        {
            IsWarmingUp = true;
            AddLog("INFO", $"Loading history: {SelectedBarSize.ToDisplayString()} × {KHistoryLength} bars per component (broker-backed, store cache-first)");
        });

        var targetInterval = SelectedBarSize.ToTimeSpan();
        // Request enough rows that the calculator's longest indicator (MA50, ATR_Reg, etc.)
        // has fully warmed up state before we trim to KHistoryLength for the surface.
        var rawRequest = Math.Max(KHistoryLength + 80, 150);
        var duration = targetInterval * rawRequest;

        var tasks = idsBySymbol.Select(async kv =>
        {
            var (symbol, instrumentId) = kv;
            if (!_runtimes.TryGetValue(symbol, out var runtime)) return (symbol, 0);
            try
            {
                BrokerKind broker = ResolveBroker(runtime.Component);

                // Cache-first via the repository: local store hit, else broker fetch + persist.
                // Returns Bar directly — no OhlcvBar.ToBar() projection.
                IReadOnlyList<Bar> warmup = await _services.Repository.GetHistoricalBarsAsync(
                    runtime.Component.Contract, broker, SelectedBarSize, duration, ct);

                if (warmup.Count == 0)
                {
                    // Last resort — neither store nor broker had bars at the target size. Pull
                    // local 1-minute bars and aggregate locally so users with only sparse minute
                    // history still get *some* context on the surface.
                    var perTarget = Math.Max(1, (int)Math.Round(targetInterval.TotalMinutes));
                    var oneMin = await _services.Store.GetRecentBarsAsync(
                        instrumentId, BarSize.OneMinute, rawRequest * perTarget, broker, ct);
                    warmup = AggregateBars(oneMin, targetInterval);
                }
                if (warmup.Count == 0) return (symbol, 0);

                foreach (var bar in warmup)
                {
                    var snap = runtime.Calculator.OnBar(bar);
                    if (snap is { } s)
                    {
                        runtime.LastK = s.KFinal;
                        runtime.KHistory.Enqueue(s.KFinal);
                        while (runtime.KHistory.Count > KHistoryLength) runtime.KHistory.Dequeue();
                        _aggregator?.Update(symbol, s);
                    }
                }
                return (symbol, warmup.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Warmup failed for {Symbol}", symbol);
                return (symbol, 0);
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var total = results.Sum(r => r.Item2);
        var nonEmpty = results.Count(r => r.Item2 > 0);
        await UiThread.RunAsync(() =>
        {
            IsWarmingUp = false;
            AddLog("WIRE",
                nonEmpty == 0
                    ? "Warmup: no historical data from store or broker — surface will fill as live bars complete."
                    : $"Warmup: loaded {total} bars across {nonEmpty}/{results.Length} components.");
        });
    }

    private static IReadOnlyList<Bar> AggregateBars(IReadOnlyList<OhlcvBar> source, TimeSpan target)
    {
        if (source.Count == 0 || target.Ticks <= 0) return Array.Empty<Bar>();
        var result = new List<Bar>();
        var currentStart = DateTime.MinValue;
        double open = 0, high = 0, low = 0, close = 0;
        long volume = 0;
        foreach (var b in source)
        {
            var bucket = new DateTime(b.OpenTimeUtc.Ticks - (b.OpenTimeUtc.Ticks % target.Ticks), DateTimeKind.Utc);
            if (currentStart == DateTime.MinValue)
            {
                currentStart = bucket;
                open = b.Open; high = b.High; low = b.Low; close = b.Close; volume = b.Volume;
                continue;
            }
            if (bucket != currentStart)
            {
                result.Add(new Bar(currentStart, open, high, low, close, volume));
                currentStart = bucket;
                open = b.Open; high = b.High; low = b.Low; close = b.Close; volume = b.Volume;
            }
            else
            {
                if (b.High > high) high = b.High;
                if (b.Low < low) low = b.Low;
                close = b.Close;
                volume += b.Volume;
            }
        }
        result.Add(new Bar(currentStart, open, high, low, close, volume));
        return result;
    }

    private BrokerKind ResolveBroker(IndexComponent component)
    {
        if (component.Broker is { } explicitBroker && _services.Selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException($"No broker connected for {component.Symbol}.");
        return connected[0];
    }

    private void OnQuote(string symbol, Quote q)
    {
        if (_aggregator is null) return;
        if (!_runtimes.TryGetValue(symbol, out var runtime)) return;

        var mid = q.Mid;
        var ts = q.EventTimeUtc;
        var bucketTicks = ts.Ticks - (ts.Ticks % runtime.BarInterval.Ticks);
        var bucket = new DateTime(bucketTicks, DateTimeKind.Utc);

        Bar? completed = null;
        IndexKScoreCalculator.Snapshot? barSnapshot = null;
        IndexSnapshot? indexSnap = null;
        lock (runtime.Lock)
        {
            if (runtime.CurrentBarStart == DateTime.MinValue)
            {
                runtime.CurrentBarStart = bucket;
                runtime.Open = runtime.High = runtime.Low = runtime.Close = mid;
                runtime.Volume = 1;
                runtime.ProvisionalK = double.NaN;
                MarkSurfaceDirty();
                return;
            }

            if (bucket != runtime.CurrentBarStart)
            {
                completed = new Bar(runtime.CurrentBarStart, runtime.Open, runtime.High, runtime.Low, runtime.Close, runtime.Volume);
                runtime.CurrentBarStart = bucket;
                runtime.Open = runtime.High = runtime.Low = runtime.Close = mid;
                runtime.Volume = 1;
                runtime.ProvisionalK = double.NaN;
            }
            else
            {
                if (mid > runtime.High) runtime.High = mid;
                if (mid < runtime.Low) runtime.Low = mid;
                runtime.Close = mid;
                runtime.Volume++;
            }

            // Render-only provisional K for the in-progress bar: take the last finalized K and
            // nudge it by the in-progress bar's return from its open, clamped to the K range. This
            // makes the newest surface column drift intra-bar with price without invoking the
            // (stateful) calculator. Replaced by the real K on bar close.
            runtime.ProvisionalK = ComputeProvisionalK(runtime);

            // On bar close, finalize K and mutate the per-component K-history INSIDE the lock so the
            // UI-thread render tick (BuildSurface, which copies KHistory under the same lock) never
            // races a concurrent enumerate + Enqueue/Dequeue on the non-thread-safe Queue<T>.
            if (completed is { } bar)
            {
                barSnapshot = runtime.Calculator.OnBar(bar);
                if (barSnapshot is { } s)
                {
                    runtime.LastK = s.KFinal;
                    runtime.KHistory.Enqueue(s.KFinal);
                    while (runtime.KHistory.Count > KHistoryLength) runtime.KHistory.Dequeue();
                    indexSnap = _aggregator.Update(symbol, s);
                }
            }
        }

        // Every quote dirties the surface; the coalescing render timer flushes one repaint at the
        // bounded render rate. Cheap + thread-safe (volatile flag) — safe to call off the UI thread.
        MarkSurfaceDirty();

        if (completed is { } closedBar && barSnapshot is { } closedSnap)
        {
            _ = UiThread.RunAsync(() =>
            {
                LogBar(symbol, closedBar, closedSnap);
                if (indexSnap is { } idx) ApplySnapshot(idx);
            });
        }
    }

    /// <summary>Flags the surface for the next coalesced repaint. Thread-safe — volatile write,
    /// no UI marshalling. The <see cref="_renderTimer"/> tick (on the UI thread) consumes it.</summary>
    private void MarkSurfaceDirty() => _surfaceDirty = true;

    /// <summary>Render-only running K for an in-progress bar. Caller holds <c>runtime.Lock</c>.
    /// Blends the last finalized K toward the sign of the in-progress bar's return-from-open,
    /// scaled by an intra-bar gain and clamped to the K range (±1.5). Purely a visual hint so the
    /// newest surface column drifts with price between bar closes; never feeds signals.</summary>
    private static double ComputeProvisionalK(ComponentRuntime runtime)
    {
        const double kClamp = 1.5;
        // Intra-bar fractional return from the bar's open. Normalised by a small reference move so
        // a typical bar's drift maps to a meaningful K nudge without dominating the finalized K.
        if (runtime.Open <= 0) return runtime.LastK;
        var ret = (runtime.Close - runtime.Open) / runtime.Open;
        const double refMove = 0.002;   // ~0.2% reference intra-bar move → full-unit nudge
        const double gain = 0.6;        // how much the provisional can deviate from LastK
        var nudge = Math.Clamp(ret / refMove, -1.0, 1.0) * gain;
        return Math.Clamp(runtime.LastK + nudge, -kClamp, kClamp);
    }

    /// <summary>One log line per component per bar close — OHLC + K + breakdown of top signals
    /// that contributed to the K direction. Caller already on UI thread.</summary>
    private void LogBar(string symbol, Bar bar, IndexKScoreCalculator.Snapshot snap)
    {
        var b = snap.Breakdown;
        var arrow = snap.KFinal > 0 ? "▲" : snap.KFinal < 0 ? "▼" : "·";
        var flags = (snap.Overbought ? " OB" : string.Empty) + (snap.Oversold ? " OS" : string.Empty);
        AddLog("BAR",
            $"{symbol,-6} {bar.TimestampUtc:HH:mm} " +
            $"O={bar.Open:F2} H={bar.High:F2} L={bar.Low:F2} C={bar.Close:F2}  " +
            $"K={snap.KFinal:+0.00;-0.00;0.00}{arrow} (raw={snap.KRaw:+0.00;-0.00;0.00}, conf={snap.Confidence:F2})  " +
            $"st={b.SuperTrend:+0;-0;0} ma={b.ThreeMa:+0;-0;0} rsi={b.Rsi:+0.0;-0.0;0.0} " +
            $"macd={b.Macd:+0;-0;0} vwap={b.Vwap:+0;-0;0} cvd={b.CumDelta:+0.0;-0.0;0.0}{flags}");
    }

    private void ApplySnapshot(IndexSnapshot snap)
    {
        LatestSnapshot = snap;
        Components.Clear();
        foreach (var row in snap.Components) Components.Add(row);

        PiercingUpCount = snap.PiercingUpCount;
        PiercingDownCount = snap.PiercingDownCount;
        CumulativeKUp = snap.CumulativeKUp;
        CumulativeKDown = snap.CumulativeKDown;
        LongSignalActive = snap.LongSignalActive;
        ShortSignalActive = snap.ShortSignalActive;
        ComponentsReady = snap.Components.Count(c => c.HasData);

        BuildSurface();

        if (snap.LongSignalActive && !_lastLong)
        {
            AddLog("ENTRY", $"LONG INDEX: {snap.PiercingUpCount} stocks pierced UP, ΣK={snap.CumulativeKUp:F2}");
            if (IsAlgoRunning) PublishSignal("LONG", snap);
        }
        else if (snap.ShortSignalActive && !_lastShort)
        {
            AddLog("ENTRY", $"SHORT INDEX: {snap.PiercingDownCount} stocks pierced DOWN, ΣK={snap.CumulativeKDown:F2}");
            if (IsAlgoRunning) PublishSignal("SHORT", snap);
        }
        else if (!snap.LongSignalActive && _lastLong)
            AddLog("REGIME", "Long signal cleared.");
        else if (!snap.ShortSignalActive && _lastShort)
            AddLog("REGIME", "Short signal cleared.");

        _lastLong = snap.LongSignalActive;
        _lastShort = snap.ShortSignalActive;

        if (IsPaused) { _surfaceDirty = true; return; }
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds the [time × component] matrix from each runtime's K-history queue and
    /// the matching per-column thresholds. Rows: 0 = oldest. The runtime queue is shorter than
    /// <see cref="KHistoryLength"/> until enough bars have completed; we pad the start with
    /// zeros so the surface keeps a constant shape.</summary>
    private void BuildSurface()
    {
        if (_aggregator is null || _orderedComponents.Count == 0) return;

        var cols = _orderedComponents.Count;
        var rows = KHistoryLength;
        var matrix = new double[rows, cols];
        var thresholds = new double[cols];
        var symbols = new string[cols];

        for (var c = 0; c < cols; c++)
        {
            var comp = _orderedComponents[c];
            thresholds[c] = _aggregator.ComputeThreshold(comp.IndexWeight);
            symbols[c] = comp.Symbol;
            if (!_runtimes.TryGetValue(comp.Symbol, out var rt)) continue;

            // Snapshot the per-component shared state (K-history + provisional K) under the runtime
            // lock — OnQuote mutates these on the hub/broker thread, and Queue<T> is not thread-safe
            // for concurrent enumerate + Enqueue/Dequeue. Copy under the lock, render after.
            double[] hist;
            double prov;
            lock (rt.Lock)
            {
                hist = rt.KHistory.ToArray();
                prov = rt.ProvisionalK;
            }

            var pad = rows - hist.Length;
            for (var t = 0; t < hist.Length; t++)
                matrix[pad + t, c] = hist[t];

            // Overlay the in-progress bar's provisional (render-only) K on the newest time row so
            // the surface drifts intra-bar. Leaves the finalized K-history untouched.
            if (!double.IsNaN(prov)) matrix[rows - 1, c] = prov;
        }

        Surface = matrix;
        Thresholds = thresholds;
        ColumnSymbols = symbols;
    }

    private void PublishSignal(string direction, IndexSnapshot snap)
    {
        var msg = direction == "LONG"
            ? $"{snap.PiercingUpCount} components pierced UP, cumulative K={snap.CumulativeKUp:F2}"
            : $"{snap.PiercingDownCount} components pierced DOWN, cumulative K={snap.CumulativeKDown:F2}";
        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: StrategyId,
            StrategyName: StrategyName,
            Symbol: SelectedFamily?.DisplayName ?? "(index)",
            Direction: direction,
            Message: msg,
            TimestampUtc: snap.TimestampUtc))
            .FireAndForgetSafe(_logger, "Index K-Score signal publish");
    }

    /// <summary>Placeholder per spec — exit logic deferred to the next iteration.</summary>
    private void OnExitSignal() { }

    private void AddLog(string level, string message) =>
        _services.ActivityLog.Append(StrategyName, level, message);

    public async Task StopStreamAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        StopRenderTimer();
        foreach (var h in _hubSubscriptions) h.Dispose();
        _hubSubscriptions.Clear();
        foreach (var h in _ingestHandles) h.Dispose();
        _ingestHandles.Clear();
        _runtimes.Clear();
        IsStreaming = false;
        IsAlgoRunning = false;
        Status = "Stopped";
        AddLog("INFO", "Streaming stopped");
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        StopRenderTimer();
        foreach (var h in _hubSubscriptions) h.Dispose();
        _hubSubscriptions.Clear();
        foreach (var h in _ingestHandles) h.Dispose();
        _ingestHandles.Clear();
    }

    /// <summary>Stops + tears down the coalescing render timer. Safe to call from any thread — the
    /// null-out, <see cref="DispatcherTimer.Stop"/> and unhook all happen inside one UI-thread
    /// action so the ordering is deterministic: a subsequent <see cref="StartRenderTimer"/> queued
    /// on the UI thread observes <c>_renderTimer == null</c> only after the old timer has stopped,
    /// so a rapid Stop→Start cannot leave two live timers.</summary>
    private void StopRenderTimer()
    {
        // Dispose stops the timer; a late in-flight tick is a harmless no-op (the dirty-flag guard
        // in OnRenderTick short-circuits it).
        var timer = _renderTimer;
        _renderTimer = null;
        timer?.Dispose();
    }

    private sealed class ComponentRuntime(IndexComponent component, IndexKScoreCalculator calc, TimeSpan barInterval)
    {
        public IndexComponent Component { get; } = component;
        public IndexKScoreCalculator Calculator { get; } = calc;
        public TimeSpan BarInterval { get; } = barInterval;
        public object Lock { get; } = new();
        public DateTime CurrentBarStart { get; set; } = DateTime.MinValue;
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        public double LastK { get; set; }

        /// <summary>Running, render-only K for the in-progress (incomplete) bar. Derived from the
        /// last finalized K nudged by the in-progress bar's return from its open — purely a visual
        /// hint so the surface's newest column moves intra-bar. Never feeds the calculator or the
        /// aggregator (those stay bar-close-driven). NaN until the first in-progress tick.</summary>
        public double ProvisionalK { get; set; } = double.NaN;

        public Queue<double> KHistory { get; } = new();
    }
}

/// <summary>A named snapshot of the K-score tuning + render options, persisted per user by
/// <see cref="ToolPresetStore{T}"/> (tool-presets/strategy-index-kscore.json). Engine params
/// apply on the next Start; the height scale is live. Never the index family.</summary>
public sealed record KScorePreset(
    double TMin,
    double TMax,
    int MinPierceCount,
    double CumKThreshold,
    int RsiLength,
    int MacdFast,
    int MacdSlow,
    int MacdSignal,
    int AtrLength,
    int AtrRegLength,
    double SurfaceHeightScale);
