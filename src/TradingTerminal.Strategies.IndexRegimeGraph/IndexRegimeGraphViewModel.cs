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
/// <para>The result is drawn as a feed-forward neural net — <c>companies → timeframes →
/// indicators → output → ×weight → signal</c> — plus a right-hand panel listing the same scores.
/// Clicking a company "lights up" its pathway and drives the hidden layers to that company's
/// values; with nothing selected the hidden layers show the index aggregate. Analysis runs off the
/// UI thread; every collection mutation marshals through <see cref="UiThread"/>. Signal-mode only.</para>
/// </summary>
public sealed partial class IndexRegimeGraphViewModel : ViewModelBase, IDisposable
{
    private const string StrategyId = "index.regime.graph";
    private const string StrategyName = "Index Regime Graph";

    // Layer geometry (logical canvas units; the view's pan/zoom transform scales them).
    // Layer order, left → right: companies → indicators → timeframes → output → signal.
    private const double MidY = 540;
    private const double CompanyX = 60, CompanySpacing = 36, CompanySize = 32;
    private const double IndicatorX = 440, IndicatorSpacing = 42, IndicatorSize = 36;
    private const double TimeframeX = 820, TimeframeSpacing = 80, TimeframeSize = 54;
    private const double OutputX = 1180, OutputSize = 86;
    private const double SignalX = 1400, SignalSize = 132;

    /// <summary>Bounded fan-out across constituents so we respect IB market-data pacing.</summary>
    private const int MaxConcurrentAnalyses = 5;

    private static readonly AdvancedIndicatorRow[] IndicatorRows = Enum.GetValues<AdvancedIndicatorRow>();

    private readonly LiveStrategyHostServices _services;
    private readonly IAdvancedRegimeProvider _provider;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<IndexRegimeGraphViewModel> _logger;

    private readonly ConcurrentDictionary<string, AdvancedRegimeSnapshot> _snapshots = new();
    private readonly List<IDisposable> _liveHandles = new();
    private readonly Dictionary<string, GraphNode> _companyNodes = new();
    private readonly Dictionary<string, GraphNode> _tfNodes = new();
    private readonly Dictionary<AdvancedIndicatorRow, GraphNode> _indNodes = new();
    // company → first hidden layer (indicators) fan, keyed by company symbol.
    private readonly Dictionary<string, List<GraphEdge>> _companyFanEdges = new();
    // last hidden layer (timeframes) → output.
    private readonly List<GraphEdge> _toOutputEdges = new();
    private GraphNode? _outputNode;
    private GraphNode? _signalNode;
    private GraphEdge? _outSigEdge;

    private IReadOnlyList<IndexComponent> _orderedComponents = Array.Empty<IndexComponent>();
    private IReadOnlyDictionary<string, ConstituentRegimeScore> _latestByStock =
        new Dictionary<string, ConstituentRegimeScore>();
    private IndexRegimeSnapshot _lastSnapshot = IndexRegimeSnapshot.Empty;
    private double _maxWeight = 1;
    private string? _focusSymbol;
    private CancellationTokenSource? _streamCts;
    private Task? _loopTask;
    private CellSignal _lastBand = CellSignal.Neutral;
    private volatile bool _refreshing;
    private int _syntheticFallbacks;

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

        GraphNodes = new ObservableCollection<GraphNode>();
        GraphEdges = new ObservableCollection<GraphEdge>();
        ConstituentScores = new ObservableCollection<ConstituentRegimeScore>();
    }

    public ObservableCollection<IndexFamily> Families { get; }
    public ObservableCollection<RegimeHorizon> Horizons { get; }
    public ObservableCollection<BrokerKind> Brokers { get; }
    public ObservableCollection<GraphNode> GraphNodes { get; }
    public ObservableCollection<GraphEdge> GraphEdges { get; }
    public ObservableCollection<ConstituentRegimeScore> ConstituentScores { get; }

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

    [ObservableProperty] private GraphNode? _selectedNode;
    [ObservableProperty] private string _focusLabel = "Index aggregate (click a company to focus)";

    /// <summary>Raised when the graph topology is rebuilt — the view fits it to the viewport.</summary>
    public event EventHandler? GraphRebuilt;

    /// <summary>Raised when the user asks to recentre the view (Home / Fit).</summary>
    public event EventHandler? ResetViewRequested;

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
        _maxWeight = Math.Max(1e-9, _orderedComponents.Max(c => c.IndexWeight));
        _snapshots.Clear();
        _focusSymbol = null;
        FocusLabel = "Index aggregate (click a company to focus)";
        ConstituentsTotal = _orderedComponents.Count;
        ConstituentsReady = 0;
        _lastBand = CellSignal.Neutral;

        BuildNeuralGraph();
        StartLiveFeed();

        IsStreaming = true;
        Status = $"Loading history for {_orderedComponents.Count} constituents on {SelectedBroker}…";
        AddLog("INFO",
            $"Start {SelectedFamily.DisplayName}: {_orderedComponents.Count} constituents, broker={SelectedBroker}, " +
            $"horizon={TimeframeWeighting.Describe(SelectedHorizon)}, refresh={RefreshSeconds}s, auto={AutoRefresh}");

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

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        AddLog("INFO", $"Algo {(IsAlgoRunning ? "ARMED" : "DISARMED")} on {SelectedFamily?.DisplayName}");
    }

    [RelayCommand]
    private void ResetView() => ResetViewRequested?.Invoke(this, EventArgs.Empty);

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
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
    /// so far and the views fill in progressively.</summary>
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
        using var sem = new SemaphoreSlim(MaxConcurrentAnalyses);

        var tasks = _orderedComponents.Select(async component =>
        {
            await sem.WaitAsync(ct);
            try
            {
                _snapshots[component.Symbol] = await AnalyseWithFallbackAsync(component, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _snapshots[component.Symbol] = AdvancedRegimeSnapshot.Empty with { Symbol = component.Symbol };
                _logger.LogDebug(ex, "Regime analysis failed for {Symbol}", component.Symbol);
            }
            finally { sem.Release(); }

            var agg = AggregateCurrent(familyName, horizon);
            await UiThread.RunAsync(() => ApplySnapshot(agg));
        }).ToArray();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        var synthetic = Volatile.Read(ref _syntheticFallbacks);
        if (synthetic > 0)
            await UiThread.RunAsync(() => AddLog("WARN",
                $"{synthetic}/{ConstituentsTotal} constituents had no live/stored history on {SelectedBroker} — " +
                "showing synthetic history (markets likely closed). Real data replaces it on the next refresh."));

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
    /// broker's synthetic history so the node renders a shape instead of going blank. A later refresh
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
        return fallback with { Symbol = component.Symbol };
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

    /// <summary>Picks the broker to fetch a constituent from. A component may pin its own broker;
    /// otherwise the user's chosen <see cref="SelectedBroker"/> wins (when still connected), falling
    /// back to the first connected broker so a stale pick never strands the whole index.</summary>
    private BrokerKind ResolveBroker(IndexComponent component)
    {
        if (component.Broker is { } explicitBroker && _services.Selector.IsConnected(explicitBroker))
            return explicitBroker;
        if (SelectedBroker is { } picked && _services.Selector.IsConnected(picked))
            return picked;
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException($"No broker connected for {component.Symbol}.");
        return connected[0];
    }

    // ── Apply snapshot → headline + panel + graph (UI thread) ─────────────────────────────────

    private void ApplySnapshot(IndexRegimeSnapshot snap)
    {
        _lastSnapshot = snap;
        _latestByStock = snap.Constituents.ToDictionary(c => c.Symbol);

        CompositeScore = snap.CompositeScore;
        CompositePercent = snap.CompositeScore * 100;
        CompositeBand = snap.Band;
        CompositeDirection = DirectionText(snap.Band);
        BullishCount = snap.BullishCount;
        BearishCount = snap.BearishCount;
        ConstituentsReady = snap.ConstituentsWithData;
        ConstituentsTotal = snap.ConstituentsTotal;

        ConstituentScores.Clear();
        foreach (var c in snap.Constituents.OrderByDescending(c => Math.Abs(c.Contribution)))
            ConstituentScores.Add(c);

        UpdateGraph(snap);

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
                AddLog("REGIME", $"Composite eased to {DirectionText(snap.Band)} ({CompositePercent:+0.0}%).");
            }
            _lastBand = snap.Band;
        }
    }

    // ── Build the feed-forward net (once per Start) ───────────────────────────────────────────

    private void BuildNeuralGraph()
    {
        GraphNodes.Clear();
        GraphEdges.Clear();
        _companyNodes.Clear();
        _tfNodes.Clear();
        _indNodes.Clear();
        _companyFanEdges.Clear();
        _toOutputEdges.Clear();
        SelectedNode = null;

        // Hidden layer 1 — indicators (now the first hidden layer, nearest the companies).
        var indTop = LayerTop(IndicatorRows.Length, IndicatorSpacing);
        for (var k = 0; k < IndicatorRows.Length; k++)
        {
            var node = new GraphNode
            {
                Id = $"ind:{IndicatorRows[k]}", Kind = GraphNodeKind.Indicator, Label = RowLabel(IndicatorRows[k]),
                X = IndicatorX, Y = indTop + k * IndicatorSpacing, Size = IndicatorSize,
            };
            _indNodes[IndicatorRows[k]] = node;
            GraphNodes.Add(node);
        }

        // Hidden layer 2 — timeframes (now the second hidden layer, feeding the output).
        var labels = AdvancedTimeframe.Defaults.Select(t => t.Label).ToList();
        var tfTop = LayerTop(labels.Count, TimeframeSpacing);
        for (var j = 0; j < labels.Count; j++)
        {
            var node = new GraphNode
            {
                Id = $"tf:{labels[j]}", Kind = GraphNodeKind.Timeframe, Label = labels[j],
                X = TimeframeX, Y = tfTop + j * TimeframeSpacing, Size = TimeframeSize,
            };
            _tfNodes[labels[j]] = node;
            GraphNodes.Add(node);
        }

        // Output + signal.
        _outputNode = new GraphNode { Id = "output", Kind = GraphNodeKind.Output, Label = "OUTPUT", X = OutputX, Y = MidY - OutputSize / 2, Size = OutputSize };
        _signalNode = new GraphNode { Id = "signal", Kind = GraphNodeKind.Signal, Label = SelectedFamily?.DisplayName ?? "Index", SubLabel = "—", X = SignalX, Y = MidY - SignalSize / 2, Size = SignalSize };
        GraphNodes.Add(_outputNode);
        GraphNodes.Add(_signalNode);

        // Layer 0 — companies. Added last so they paint on top of the company→tf fan.
        var compTop = LayerTop(_orderedComponents.Count, CompanySpacing);
        for (var i = 0; i < _orderedComponents.Count; i++)
        {
            var c = _orderedComponents[i];
            var node = new GraphNode
            {
                Id = $"co:{c.Symbol}", Kind = GraphNodeKind.Company, Label = c.Symbol,
                SubLabel = $"w {c.IndexWeight:P1}", HasData = false,
                X = CompanyX, Y = compTop + i * CompanySpacing, Size = CompanySize,
            };
            _companyNodes[c.Symbol] = node;
            GraphNodes.Add(node);
        }

        // ── Synapses ──────────────────────────────────────────────────────────────────────
        // companies → every indicator (the input fan).
        foreach (var (sym, co) in _companyNodes)
        {
            var list = new List<GraphEdge>(_indNodes.Count);
            foreach (var ind in _indNodes.Values)
            {
                var e = new GraphEdge { From = co, To = ind, Thickness = 1.0, Opacity = 0.10 };
                list.Add(e);
                GraphEdges.Add(e);
            }
            _companyFanEdges[sym] = list;
        }
        // indicators → timeframes (the static hidden mesh).
        foreach (var ind in _indNodes.Values)
            foreach (var tf in _tfNodes.Values)
                GraphEdges.Add(new GraphEdge { From = ind, To = tf, Thickness = 0.8, Opacity = 0.07 });
        // timeframes → output.
        foreach (var tf in _tfNodes.Values)
        {
            var e = new GraphEdge { From = tf, To = _outputNode, Thickness = 1.2, Opacity = 0.4 };
            _toOutputEdges.Add(e);
            GraphEdges.Add(e);
        }
        // output → signal (×weight).
        _outSigEdge = new GraphEdge { From = _outputNode, To = _signalNode, Thickness = 6, Opacity = 0.8 };
        GraphEdges.Add(_outSigEdge);

        GraphRebuilt?.Invoke(this, EventArgs.Empty);
    }

    // ── Update node/edge activations each refresh (and on focus change) ───────────────────────

    private void UpdateGraph(IndexRegimeSnapshot snap)
    {
        // Companies (input layer).
        foreach (var c in snap.Constituents)
        {
            if (!_companyNodes.TryGetValue(c.Symbol, out var node)) continue;
            node.Score = c.StockScore;
            node.Band = c.Band;
            node.HasData = c.HasData;
            node.Size = CompanySize * (0.7 + 0.6 * (c.IndexWeight / _maxWeight));
            node.SubLabel = c.HasData ? $"{c.StockScore * 100:+0.0;-0.0;0.0}" : "…";
        }

        var focus = _focusSymbol is not null && _latestByStock.TryGetValue(_focusSymbol, out var f) ? f : null;

        // Hidden layer 2 — timeframes (focused company, else index aggregate).
        foreach (var (label, node) in _tfNodes)
        {
            var v = focus is not null ? FocusTimeframeScore(focus, label) : AggregateTimeframeScore(label);
            node.Score = v;
            node.Band = IndexRegimeAggregator.BandFor(v);
            node.SubLabel = $"{v * 100:+0;-0;0}";
        }

        // Hidden layer 1 — indicators.
        foreach (var (row, node) in _indNodes)
        {
            var v = focus is not null ? FocusIndicatorValue(focus, row) : AggregateIndicatorValue(row);
            node.Score = v;
            node.Band = IndexRegimeAggregator.BandFor(v);
        }

        // Output.
        if (_outputNode is not null)
        {
            var outVal = focus?.StockScore ?? snap.CompositeScore;
            _outputNode.Score = outVal;
            _outputNode.Band = IndexRegimeAggregator.BandFor(outVal);
            _outputNode.Label = focus is not null ? focus.Symbol : "OUTPUT";
            _outputNode.SubLabel = $"{outVal * 100:+0.0;-0.0;0.0}";
        }

        // Signal (always the composite).
        if (_signalNode is not null)
        {
            _signalNode.Score = snap.CompositeScore;
            _signalNode.Band = snap.Band;
            _signalNode.Label = $"{snap.CompositeScore * 100:+0.0;-0.0;0.0}%";
            _signalNode.SubLabel = DirectionText(snap.Band);
        }

        if (_outSigEdge is not null)
        {
            _outSigEdge.Band = snap.Band;
            // weight of the focused name (its leverage on the index), else the whole index.
            _outSigEdge.From!.SubLabel = focus is not null ? $"×{focus.IndexWeight:P1}" : _outputNode?.SubLabel ?? "";
        }

        // Edge activations: company→indicator fan lit by source company; the focused company is bright.
        foreach (var (sym, edges) in _companyFanEdges)
        {
            _latestByStock.TryGetValue(sym, out var cs);
            var band = cs?.Band ?? CellSignal.Neutral;
            var mag = cs is null ? 0 : Math.Abs(cs.StockScore);
            var isFocus = sym == _focusSymbol;
            foreach (var e in edges)
            {
                e.Band = band;
                e.Thickness = 0.8 + 2.5 * mag;
                e.Opacity = _focusSymbol is null ? 0.14 : (isFocus ? 0.85 : 0.03);
            }
        }

        // timeframes→output lit by the timeframe activation.
        foreach (var e in _toOutputEdges)
        {
            e.Band = e.From!.Band;
            e.Opacity = 0.25 + 0.45 * Math.Min(1, Math.Abs(e.From.Score));
        }
    }

    [RelayCommand]
    private void SelectNode(GraphNode? node)
    {
        if (SelectedNode is { } prev) prev.IsSelected = false;
        SelectedNode = node;
        if (node is not null) node.IsSelected = true;

        // Only company nodes change the focus; clicking elsewhere keeps the current focus.
        if (node is { Kind: GraphNodeKind.Company })
        {
            var sym = node.Id["co:".Length..];
            _focusSymbol = _focusSymbol == sym ? null : sym; // toggle focus off if re-clicked
            FocusLabel = _focusSymbol is null
                ? "Index aggregate (click a company to focus)"
                : $"Focus: {sym} — its pathway through the net";
            UpdateGraph(_lastSnapshot);
        }
    }

    [RelayCommand]
    private void ClearFocus()
    {
        _focusSymbol = null;
        FocusLabel = "Index aggregate (click a company to focus)";
        if (SelectedNode is { } prev) { prev.IsSelected = false; SelectedNode = null; }
        UpdateGraph(_lastSnapshot);
    }

    // ── Aggregate / focus helpers ─────────────────────────────────────────────────────────────

    private static double FocusTimeframeScore(ConstituentRegimeScore c, string label)
    {
        foreach (var tf in c.TimeframeScores)
            if (tf.Label == label) return tf.Score;
        return 0;
    }

    private double AggregateTimeframeScore(string label)
    {
        double sum = 0, w = 0;
        foreach (var c in _latestByStock.Values)
        {
            if (!c.HasData) continue;
            sum += FocusTimeframeScore(c, label) * c.IndexWeight;
            w += c.IndexWeight;
        }
        return w > 1e-9 ? sum / w : 0;
    }

    private static double FocusIndicatorValue(ConstituentRegimeScore c, AdvancedIndicatorRow row)
    {
        double sum = 0; int n = 0;
        foreach (var col in c.Columns)
        {
            foreach (var cell in col.Cells)
                if (cell.Row == row) { sum += SignalValue(cell.Signal); n++; break; }
        }
        return n > 0 ? sum / n : 0;
    }

    private double AggregateIndicatorValue(AdvancedIndicatorRow row)
    {
        double sum = 0, w = 0;
        foreach (var c in _latestByStock.Values)
        {
            if (!c.HasData) continue;
            sum += FocusIndicatorValue(c, row) * c.IndexWeight;
            w += c.IndexWeight;
        }
        return w > 1e-9 ? sum / w : 0;
    }

    private static double SignalValue(CellSignal s) => s switch
    {
        CellSignal.StrongUp => 1.0,
        CellSignal.Up => 0.5,
        CellSignal.Down => -0.5,
        CellSignal.StrongDown => -1.0,
        _ => 0.0,
    };

    private static double LayerTop(int count, double spacing) => MidY - (count - 1) * spacing / 2.0;

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

    private static string DirectionText(CellSignal band) => band switch
    {
        CellSignal.StrongUp => "STRONG UP",
        CellSignal.Up => "Up",
        CellSignal.Down => "Down",
        CellSignal.StrongDown => "STRONG DOWN",
        _ => "Neutral",
    };

    private static string RowLabel(AdvancedIndicatorRow row) => row switch
    {
        AdvancedIndicatorRow.Rsi => "RSI",
        AdvancedIndicatorRow.Macd => "MACD",
        AdvancedIndicatorRow.Cci => "CCI",
        AdvancedIndicatorRow.Ma9 => "MA9",
        AdvancedIndicatorRow.Ma21 => "MA21",
        AdvancedIndicatorRow.Ma50 => "MA50",
        AdvancedIndicatorRow.TripleMa => "3MA",
        AdvancedIndicatorRow.Vwap => "VWAP",
        AdvancedIndicatorRow.SuperTrend => "ST",
        AdvancedIndicatorRow.Atr => "ATR",
        AdvancedIndicatorRow.AtrRegression => "ATRr",
        AdvancedIndicatorRow.Std => "STD",
        AdvancedIndicatorRow.PocPosition => "POC",
        AdvancedIndicatorRow.TrendRange => "TRD",
        AdvancedIndicatorRow.Delta => "Δ",
        AdvancedIndicatorRow.CumulativeDelta => "CVD",
        AdvancedIndicatorRow.VolumeBuySell => "V B/S",
        AdvancedIndicatorRow.Trend => "TREND",
        _ => row.ToString(),
    };

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }
}
