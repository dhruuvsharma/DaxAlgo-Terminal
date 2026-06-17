using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.IndexKScore;
using TradingTerminal.Core.IndexRegime;
using TradingTerminal.Core.MarketData.AdvancedRegime;
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

/// <summary>
/// Host VM for the Index Regime Graph strategy. Fans out across every constituent of the chosen
/// index family: for each one it runs the multi-timeframe <see cref="IAdvancedRegimeProvider"/>
/// (the Advanced Market Regime engine), blends its eight timeframe needles for the chosen
/// <see cref="RegimeHorizon"/> into one stock score, then <see cref="IndexRegimeAggregator"/>
/// weights each stock by its index membership and sums to a composite up/down direction.
///
/// <para>The result is presented as a static <b>regime heatmap table</b> — one row per constituent
/// (sorted by contribution), with its blended score/direction up front and one colour-coded cell per
/// timeframe. Analysis runs off the UI thread; every collection mutation marshals through
/// <see cref="UiThread"/>. Signal-mode only.</para>
/// </summary>
public sealed partial class IndexRegimeGraphViewModel : ViewModelBase, IDisposable
{
    private const string StrategyId = "index.regime.graph";
    private const string StrategyName = "Index Regime Graph";

    /// <summary>Bounded fan-out across constituents so we respect IB market-data pacing.</summary>
    private const int MaxConcurrentAnalyses = 5;

    // Coalesce progressive repaints: rebuilding the heatmap on every one of the N constituent
    // completions floods the UI thread. Paint progress at most this often; the final authoritative
    // paint always runs at the end of a cycle.
    private const int ProgressPaintIntervalMs = 200;

    private static readonly string[] TfLabels =
        AdvancedTimeframe.Defaults.Select(t => t.Label).ToArray();

    private readonly LiveStrategyHostServices _services;
    private readonly IAdvancedRegimeProvider _provider;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<IndexRegimeGraphViewModel> _logger;

    private readonly ConcurrentDictionary<string, AdvancedRegimeSnapshot> _snapshots = new();
    // Per-constituent load phase (drives the per-row spinner) and the set of names that fell back to
    // synthetic history this cycle (so the row glyph can flag them amber).
    private readonly ConcurrentDictionary<string, AssetLoadState> _loadStates = new();
    private readonly ConcurrentDictionary<string, byte> _syntheticSymbols = new();
    private readonly List<IDisposable> _liveHandles = new();
    // Symbols the user has expanded to the indicator×timeframe drill-down — preserved across the
    // wholesale row rebuild that each refresh performs.
    private readonly HashSet<string> _expanded = new();

    private IReadOnlyList<IndexComponent> _orderedComponents = Array.Empty<IndexComponent>();
    private CancellationTokenSource? _streamCts;
    private Task? _loopTask;
    private CellSignal _lastBand = CellSignal.Neutral;
    private volatile bool _refreshing;
    private int _syntheticFallbacks;
    private long _lastApplyTicks;

    public IndexRegimeGraphViewModel(
        LiveStrategyHostServices services,
        IAdvancedRegimeProvider provider,
        INotificationPublisher notifications,
        ILogger<IndexRegimeGraphViewModel> logger)
    {
        _services = services;
        _provider = provider;
        _notifications = notifications;
        _logger = logger;

        Families = new ObservableCollection<IndexFamily>(IndexComponentCatalog.All);
        SelectedFamily = Families.FirstOrDefault(f => f.Id == "us30") ?? Families[0];
        Horizons = new ObservableCollection<RegimeHorizon>(Enum.GetValues<RegimeHorizon>());
        SelectedHorizon = RegimeHorizon.Intraday;
        Brokers = new ObservableCollection<BrokerKind>();
        RefreshBrokers();

        TimeframeHeaders = new ObservableCollection<string>(TfLabels);
        ConstituentRows = new ObservableCollection<ConstituentRow>();
    }

    public ObservableCollection<IndexFamily> Families { get; }
    public ObservableCollection<RegimeHorizon> Horizons { get; }
    public ObservableCollection<BrokerKind> Brokers { get; }
    public ObservableCollection<string> TimeframeHeaders { get; }
    public ObservableCollection<ConstituentRow> ConstituentRows { get; }

    [ObservableProperty] private IndexFamily _selectedFamily;
    [ObservableProperty] private RegimeHorizon _selectedHorizon;
    [ObservableProperty] private BrokerKind? _selectedBroker;
    [ObservableProperty] private int _refreshSeconds = 30;
    [ObservableProperty] private bool _autoRefresh = true;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isAlgoRunning;
    [ObservableProperty] private bool _usingSyntheticData;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string _status = "Configure the strategy to begin.";

    [ObservableProperty] private double _compositeScore;
    [ObservableProperty] private double _compositePercent;
    [ObservableProperty] private CellSignal _compositeBand = CellSignal.Neutral;
    [ObservableProperty] private string _compositeDirection = "—";
    [ObservableProperty] private int _bullishCount;
    [ObservableProperty] private int _bearishCount;
    [ObservableProperty] private int _constituentsReady;
    [ObservableProperty] private int _constituentsTotal;

    public string HorizonDescription => TimeframeWeighting.Describe(SelectedHorizon);
    partial void OnSelectedHorizonChanged(RegimeHorizon value) => OnPropertyChanged(nameof(HorizonDescription));

    public string CompositeColorHex => BandColors.Hex(CompositeBand);
    partial void OnCompositeBandChanged(CellSignal value) => OnPropertyChanged(nameof(CompositeColorHex));

    // ── Configure / start / stop ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;
        RefreshBrokers();
        if (SelectedFamily is null) { ValidationError = "Pick an index family."; return; }
        if (_services.Selector.Connected.Count == 0)
        { ValidationError = "No broker is connected. Connect a broker in the login screen first."; return; }
        if (SelectedBroker is null) { ValidationError = "Pick the broker to pull stock data from."; return; }

        IsConfigured = true;
        Start();
    }

    /// <summary>Re-reads the set of connected brokers; keeps the current pick if still connected,
    /// else defaults to the first one.</summary>
    private void RefreshBrokers()
    {
        var connected = _services.Selector.Connected;
        Brokers.Clear();
        foreach (var b in connected) Brokers.Add(b);
        if (SelectedBroker is null || !connected.Contains(SelectedBroker.Value))
            SelectedBroker = connected.Count > 0 ? connected[0] : null;
    }

    [RelayCommand]
    private void Start()
    {
        if (IsStreaming || SelectedFamily is null) return;
        ValidationError = null;

        _orderedComponents = SelectedFamily.Components.OrderByDescending(c => c.IndexWeight).ToList();
        _snapshots.Clear();
        _expanded.Clear();
        ConstituentRows.Clear();
        ConstituentsTotal = _orderedComponents.Count;
        ConstituentsReady = 0;
        _lastBand = CellSignal.Neutral;

        // Seed every constituent as "queued" and paint placeholder rows now, so a loading indicator
        // shows next to each name before the first analysis lands.
        _loadStates.Clear();
        _syntheticSymbols.Clear();
        foreach (var c in _orderedComponents) _loadStates[c.Symbol] = AssetLoadState.Pending;

        // NB: the live broker subscriptions (each writes to the instrument registry / SQLite) are
        // started on a background thread inside the refresh loop — doing them here on the UI thread
        // is what froze the window on Continue.

        IsStreaming = true;
        Status = $"Loading history for {_orderedComponents.Count} constituents on {SelectedBroker}…";
        AddLog("INFO",
            $"Start {SelectedFamily.DisplayName}: {_orderedComponents.Count} constituents, broker={SelectedBroker}, " +
            $"horizon={TimeframeWeighting.Describe(SelectedHorizon)}, refresh={RefreshSeconds}s, auto={AutoRefresh}");

        ApplySnapshot(AggregateCurrent(SelectedFamily.DisplayName, SelectedHorizon));

        _streamCts = new CancellationTokenSource();
        _loopTask = RunRefreshLoopAsync(_streamCts.Token);
    }

    /// <summary>Opens a live broker subscription per constituent so the chosen broker is actively
    /// streaming each name. The bar-based regime is recomputed on the refresh interval (which pulls
    /// the latest bars from the broker, cache-first), while these subscriptions keep the feed warm
    /// and the connection live.</summary>
    private void StartLiveFeed()
    {
        DisposeLiveHandles();
        foreach (var c in _orderedComponents)
        {
            try { _liveHandles.Add(_services.Ingest.Subscribe(c.Contract, ResolveBroker(c))); }
            catch (Exception ex) { _logger.LogDebug(ex, "Live subscribe failed for {Symbol}", c.Symbol); }
        }
        AddLog("WIRE", $"Live feed: subscribed {_liveHandles.Count}/{_orderedComponents.Count} constituents on {SelectedBroker}.");
    }

    private void DisposeLiveHandles()
    {
        foreach (var h in _liveHandles) { try { h.Dispose(); } catch { /* best effort */ } }
        _liveHandles.Clear();
    }

    [RelayCommand]
    private async Task Stop()
    {
        _streamCts?.Cancel();
        if (_loopTask is { } t) { try { await t; } catch { /* cancellation */ } }
        _streamCts?.Dispose();
        _streamCts = null;
        _loopTask = null;
        DisposeLiveHandles();
        IsStreaming = false;
        IsRefreshing = false;
        IsAlgoRunning = false;
        Status = "Stopped";
        AddLog("INFO", "Streaming stopped");
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (!IsStreaming || _refreshing) return;
        _ = RefreshCycleAsync(_streamCts?.Token ?? CancellationToken.None);
    }

    private bool CanRefresh() => IsStreaming && !_refreshing;

    /// <summary>Expands / collapses a constituent's indicator×timeframe drill-down. State is tracked
    /// by symbol so it survives the wholesale row rebuild on the next refresh.</summary>
    [RelayCommand]
    private void ToggleRow(ConstituentRow? row)
    {
        if (row is null) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded) _expanded.Add(row.Symbol);
        else _expanded.Remove(row.Symbol);
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        AddLog("INFO", $"Algo {(IsAlgoRunning ? "ARMED" : "DISARMED")} on {SelectedFamily?.DisplayName}");
    }

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            // Open the per-constituent live subscriptions off the UI thread (registry/SQLite writes).
            await Task.Run(StartLiveFeed, ct);
            await RefreshCycleAsync(ct);
            if (!AutoRefresh)
            {
                await UiThread.RunAsync(() => Status = $"{SelectedFamily?.DisplayName}: snapshot complete (auto-refresh off).");
                return;
            }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, RefreshSeconds)));
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshCycleAsync(ct);
        }
        catch (OperationCanceledException) { /* Stop() */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index regime refresh loop crashed");
            await UiThread.RunAsync(() => AddLog("ERROR", $"Refresh loop error: {ex.Message}"));
        }
    }

    /// <summary>One full analysis pass: every constituent's multi-timeframe regime is computed with
    /// bounded concurrency; as each returns, the composite is re-aggregated over whatever has landed
    /// so far and the table fills in progressively (coalesced).</summary>
    private async Task RefreshCycleAsync(CancellationToken ct)
    {
        if (_refreshing) return;
        _refreshing = true;
        await UiThread.RunAsync(() =>
        {
            IsRefreshing = true;
            RefreshCommand.NotifyCanExecuteChanged();
            Status = $"Analysing {_orderedComponents.Count} constituents…";
        });

        var horizon = SelectedHorizon;
        var familyName = SelectedFamily?.DisplayName ?? "(index)";
        Interlocked.Exchange(ref _syntheticFallbacks, 0);
        Interlocked.Exchange(ref _lastApplyTicks, 0);
        _syntheticSymbols.Clear();
        using var sem = new SemaphoreSlim(MaxConcurrentAnalyses);

        var tasks = _orderedComponents.Select(async component =>
        {
            await sem.WaitAsync(ct);
            try
            {
                // Flip the row's spinner on the moment its analysis actually starts.
                await UiThread.RunAsync(() => SetLoadState(component.Symbol, AssetLoadState.Loading));
                var snap = await AnalyseWithFallbackAsync(component, ct);
                _snapshots[component.Symbol] = snap;
                await UiThread.RunAsync(() => SetLoadState(component.Symbol, StateFor(component.Symbol, snap)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _snapshots[component.Symbol] = AdvancedRegimeSnapshot.Empty with { Symbol = component.Symbol };
                await UiThread.RunAsync(() => SetLoadState(component.Symbol, AssetLoadState.NoData));
                _logger.LogDebug(ex, "Regime analysis failed for {Symbol}", component.Symbol);
            }
            finally { sem.Release(); }

            // Progressive update is HEADLINE-ONLY (composite %, counts) and coalesced. The heavy table
            // rebuild — 30 rows × an 18×8 indicator matrix each — runs once at the end of the cycle,
            // not on every constituent, which is what made the UI hitch every few seconds. Per-row
            // spinners (via SetLoadState above) carry the live progress in the meantime.
            if (ShouldPaintProgress())
            {
                var agg = AggregateCurrent(familyName, horizon);
                await UiThread.RunAsync(() => ApplyHeadline(agg));
            }
        }).ToArray();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        if (!ct.IsCancellationRequested)
        {
            var finalAgg = AggregateCurrent(familyName, horizon);
            await UiThread.RunAsync(() => ApplySnapshot(finalAgg));
        }

        var synthetic = Volatile.Read(ref _syntheticFallbacks);
        if (synthetic > 0)
            await UiThread.RunAsync(() => AddLog("WARN",
                $"{synthetic}/{ConstituentsTotal} constituents returned no data from {SelectedBroker} — that broker " +
                "may not provide these instruments (the index families are US equities: use Alpaca or IB), or markets " +
                "are closed. Showing synthetic history; real data replaces it as soon as the broker supplies it."));

        await UiThread.RunAsync(() =>
        {
            UsingSyntheticData = synthetic > 0;
            _refreshing = false;
            IsRefreshing = false;
            RefreshCommand.NotifyCanExecuteChanged();
            var suffix = synthetic > 0 ? $" — {synthetic} synthetic" : "";
            Status = $"{familyName}: {ConstituentsReady}/{ConstituentsTotal} ready — composite {CompositePercent:+0.0;-0.0;0.0}% ({CompositeDirection}){suffix}.";
        });
        _refreshing = false;
    }

    /// <summary>Analyses one constituent on its resolved broker. When that broker returns no history
    /// — typically markets closed with an empty store — falls back to the always-available Simulated
    /// broker's synthetic history so the row renders a shape instead of going blank. A later refresh
    /// with real data transparently replaces the synthetic snapshot.</summary>
    private async Task<AdvancedRegimeSnapshot> AnalyseWithFallbackAsync(IndexComponent component, CancellationToken ct)
    {
        var broker = ResolveBroker(component);
        var snap = await _provider.AnalyseAsync(
            component.Contract, broker, component.Symbol,
            AdvancedTimeframe.Defaults, AdvancedRegimeSettings.Default, ct);

        if (!IsEmptySnapshot(snap) || broker == BrokerKind.Simulated ||
            !_services.Selector.IsAvailable(BrokerKind.Simulated))
            return snap;

        var fallback = await _provider.AnalyseAsync(
            component.Contract, BrokerKind.Simulated, component.Symbol,
            AdvancedTimeframe.Defaults, AdvancedRegimeSettings.Default, ct);
        if (IsEmptySnapshot(fallback))
            return snap;

        Interlocked.Increment(ref _syntheticFallbacks);
        _syntheticSymbols[component.Symbol] = 1;
        return fallback with { Symbol = component.Symbol };
    }

    /// <summary>Maps an analysed snapshot to the row's terminal load phase: empty → <see cref="AssetLoadState.NoData"/>,
    /// synthetic fallback → <see cref="AssetLoadState.Synthetic"/>, otherwise <see cref="AssetLoadState.Ready"/>.</summary>
    private AssetLoadState StateFor(string symbol, AdvancedRegimeSnapshot snap) =>
        IsEmptySnapshot(snap) ? AssetLoadState.NoData
        : _syntheticSymbols.ContainsKey(symbol) ? AssetLoadState.Synthetic
        : AssetLoadState.Ready;

    /// <summary>Records a constituent's load phase and reflects it on the matching live row immediately
    /// (so the spinner updates without waiting for the next coalesced aggregate paint). UI thread.</summary>
    private void SetLoadState(string symbol, AssetLoadState state)
    {
        _loadStates[symbol] = state;
        foreach (var row in ConstituentRows)
            if (string.Equals(row.Symbol, symbol, StringComparison.Ordinal)) { row.LoadState = state; break; }
    }

    private static bool IsEmptySnapshot(AdvancedRegimeSnapshot snap) =>
        snap.Unavailable || snap.Columns.Count == 0;

    private IndexRegimeSnapshot AggregateCurrent(string familyName, RegimeHorizon horizon)
    {
        var inputs = _orderedComponents
            .Select(c => (Component: c,
                          Snapshot: _snapshots.TryGetValue(c.Symbol, out var s)
                              ? s
                              : AdvancedRegimeSnapshot.Empty with { Symbol = c.Symbol }))
            .ToList();
        return IndexRegimeAggregator.Aggregate(familyName, horizon, inputs, DateTime.UtcNow);
    }

    /// <summary>True at most once per <see cref="ProgressPaintIntervalMs"/> across all the concurrent
    /// constituent completions — gates intermediate repaints so they don't flood the UI thread.</summary>
    private bool ShouldPaintProgress()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastApplyTicks);
        if (now - last < ProgressPaintIntervalMs) return false;
        return Interlocked.CompareExchange(ref _lastApplyTicks, now, last) == last;
    }

    /// <summary>Picks the broker to fetch a constituent from. The user's <see cref="SelectedBroker"/>
    /// is authoritative — when one is picked we use it or fail loudly, and never silently substitute a
    /// different connected broker (that substitution was the "always loads from cTrader" bug). The
    /// first-connected fallback only applies when nothing was picked at all.</summary>
    private BrokerKind ResolveBroker(IndexComponent component)
    {
        if (SelectedBroker is { } picked)
        {
            if (_services.Selector.IsConnected(picked)) return picked;
            throw new InvalidOperationException(
                $"Selected broker {picked} is not connected — reconnect it (or pick another) in the setup form.");
        }
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException($"No broker connected for {component.Symbol}.");
        return connected[0];
    }

    // ── Apply snapshot → headline + heatmap table (UI thread) ─────────────────────────────────

    /// <summary>Full repaint: headline + the heavy heatmap-row rebuild. Used for the initial placeholder
    /// paint and the once-per-cycle authoritative paint.</summary>
    private void ApplySnapshot(IndexRegimeSnapshot snap)
    {
        ApplyHeadline(snap);
        ApplyRows(snap);
    }

    /// <summary>Updates only the composite headline + counts (and band-change logging). Cheap enough to
    /// run on the coalesced progress ticks without churning the UI.</summary>
    private void ApplyHeadline(IndexRegimeSnapshot snap)
    {
        CompositeScore = snap.CompositeScore;
        CompositePercent = snap.CompositeScore * 100;
        CompositeBand = snap.Band;
        CompositeDirection = BandColors.DirectionText(snap.Band);
        BullishCount = snap.BullishCount;
        BearishCount = snap.BearishCount;
        ConstituentsReady = snap.ConstituentsWithData;
        ConstituentsTotal = snap.ConstituentsTotal;

        if (snap.Band != _lastBand)
        {
            if (snap.Band == CellSignal.StrongUp)
            {
                AddLog("ENTRY", $"INDEX STRONG UP: composite {CompositePercent:+0.0}% ({snap.BullishCount}/{snap.ConstituentsWithData} bullish)");
                if (IsAlgoRunning) PublishSignal("LONG", snap);
            }
            else if (snap.Band == CellSignal.StrongDown)
            {
                AddLog("ENTRY", $"INDEX STRONG DOWN: composite {CompositePercent:+0.0}% ({snap.BearishCount}/{snap.ConstituentsWithData} bearish)");
                if (IsAlgoRunning) PublishSignal("SHORT", snap);
            }
            else if (_lastBand is CellSignal.StrongUp or CellSignal.StrongDown)
            {
                AddLog("REGIME", $"Composite eased to {BandColors.DirectionText(snap.Band)} ({CompositePercent:+0.0}%).");
            }
            _lastBand = snap.Band;
        }
    }

    /// <summary>Rebuilds the heatmap rows (the expensive part: each row carries an 18×8 indicator
    /// matrix). Runs once per refresh cycle, not on every constituent completion.</summary>
    private void ApplyRows(IndexRegimeSnapshot snap)
    {
        ConstituentRows.Clear();
        foreach (var c in snap.Constituents.OrderByDescending(c => Math.Abs(c.Contribution)))
        {
            var row = ConstituentRow.From(c, TfLabels);
            row.IsExpanded = _expanded.Contains(c.Symbol);
            row.LoadState = _loadStates.TryGetValue(c.Symbol, out var ls) ? ls : AssetLoadState.Pending;
            ConstituentRows.Add(row);
        }
    }

    // ── Signal publish + logging ──────────────────────────────────────────────────────────────

    private void PublishSignal(string direction, IndexRegimeSnapshot snap)
    {
        var msg = direction == "LONG"
            ? $"Composite {snap.CompositeScore * 100:+0.0}% — {snap.BullishCount}/{snap.ConstituentsWithData} constituents bullish ({TimeframeWeighting.Describe(snap.Horizon)})."
            : $"Composite {snap.CompositeScore * 100:+0.0}% — {snap.BearishCount}/{snap.ConstituentsWithData} constituents bearish ({TimeframeWeighting.Describe(snap.Horizon)}).";
        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: StrategyId,
            StrategyName: StrategyName,
            Symbol: snap.FamilyName,
            Direction: direction,
            Message: msg,
            TimestampUtc: snap.TimestampUtc))
            .FireAndForgetSafe(_logger, $"signal publish {StrategyId}");
    }

    private void AddLog(string level, string message) =>
        _services.ActivityLog.Append(StrategyName, level, message);

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        DisposeLiveHandles();
    }
}
