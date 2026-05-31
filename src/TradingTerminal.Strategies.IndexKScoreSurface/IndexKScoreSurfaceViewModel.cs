using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.IndexKScore;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;

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
/// <para>On Start, each component is warmed up from <see cref="IMarketDataStore.GetRecentBarsAsync"/>
/// at the chosen timeframe (falling back to 1-minute bars aggregated locally) so the 3D heat
/// surface paints with historical context immediately, instead of waiting one full bar interval
/// per component.</para>
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

        // Warm up each component's calculator + K history from the store so the surface paints
        // with context immediately, not after one full bar interval per component.
        await WarmupAsync(idsBySymbol, streamCt);

        // Initial snapshot.
        var snap = _aggregator.BuildAggregate(DateTime.UtcNow);
        await UiThread.RunAsync(() => ApplySnapshot(snap));
    }

    /// <summary>Loads recent bars per component from the local store and feeds them into each
    /// calculator + K-history queue. First tries the user's selected timeframe; for components
    /// where the store has no bars at that size, falls back to 1-minute bars and aggregates them
    /// locally. Silent on per-component failures — partial warmup is better than none.</summary>
    private async Task WarmupAsync(IReadOnlyDictionary<string, InstrumentId> idsBySymbol, CancellationToken ct)
    {
        if (idsBySymbol.Count == 0) return;
        await UiThread.RunAsync(() =>
        {
            IsWarmingUp = true;
            AddLog("INFO", $"Loading history: {SelectedBarSize.ToDisplayString()} × {KHistoryLength} bars per component (fallback: 1m × aggregate)");
        });

        var targetInterval = SelectedBarSize.ToTimeSpan();
        // Request enough rows that the calculator's longest indicator (MA50, ATR_Reg, etc.)
        // has fully warmed up state before we trim to KHistoryLength for the surface.
        var rawRequest = Math.Max(KHistoryLength + 80, 150);

        var tasks = idsBySymbol.Select(async kv =>
        {
            var (symbol, instrumentId) = kv;
            if (!_runtimes.TryGetValue(symbol, out var runtime)) return (symbol, 0);
            try
            {
                IReadOnlyList<Bar> warmup;
                var direct = await _services.Store.GetRecentBarsAsync(instrumentId, SelectedBarSize, rawRequest, ct);
                if (direct.Count > 0)
                {
                    warmup = direct.Select(b => b.ToBar()).ToList();
                }
                else
                {
                    // Fall back to 1-minute bars and aggregate locally so users with sparse
                    // store history still get *some* context on the surface.
                    var perTarget = Math.Max(1, (int)Math.Round(targetInterval.TotalMinutes));
                    var oneMin = await _services.Store.GetRecentBarsAsync(
                        instrumentId, BarSize.OneMinute, rawRequest * perTarget, ct);
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
                    ? "Warmup: no historical data in store — surface will fill as live bars complete."
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
        lock (runtime.Lock)
        {
            if (runtime.CurrentBarStart == DateTime.MinValue)
            {
                runtime.CurrentBarStart = bucket;
                runtime.Open = runtime.High = runtime.Low = runtime.Close = mid;
                runtime.Volume = 1;
                return;
            }

            if (bucket != runtime.CurrentBarStart)
            {
                completed = new Bar(runtime.CurrentBarStart, runtime.Open, runtime.High, runtime.Low, runtime.Close, runtime.Volume);
                runtime.CurrentBarStart = bucket;
                runtime.Open = runtime.High = runtime.Low = runtime.Close = mid;
                runtime.Volume = 1;
            }
            else
            {
                if (mid > runtime.High) runtime.High = mid;
                if (mid < runtime.Low) runtime.Low = mid;
                runtime.Close = mid;
                runtime.Volume++;
            }
        }

        if (completed is { } bar)
        {
            var snapshot = runtime.Calculator.OnBar(bar);
            if (snapshot is { } s)
            {
                runtime.LastK = s.KFinal;
                runtime.KHistory.Enqueue(s.KFinal);
                while (runtime.KHistory.Count > KHistoryLength) runtime.KHistory.Dequeue();

                var indexSnap = _aggregator.Update(symbol, s);
                _ = UiThread.RunAsync(() =>
                {
                    LogBar(symbol, bar, s);
                    if (indexSnap is { } idx) ApplySnapshot(idx);
                });
            }
        }
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

            var hist = rt.KHistory.ToArray();
            var pad = rows - hist.Length;
            for (var t = 0; t < hist.Length; t++)
                matrix[pad + t, c] = hist[t];
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
        foreach (var h in _hubSubscriptions) h.Dispose();
        _hubSubscriptions.Clear();
        foreach (var h in _ingestHandles) h.Dispose();
        _ingestHandles.Clear();
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
        public Queue<double> KHistory { get; } = new();
    }
}
