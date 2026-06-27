using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// Drives the 1-Minute Order Flow Pressure Map — a single heatmap matrix (ticker × time) over the
/// S&amp;P 100/500 universe. Mirrors the per-tool VM convention (Volume Footprint): it resolves a
/// connected broker, subscribes the universe to the hub's 1m bars + quotes (+ optional depth), primes
/// a 20-day same-time-of-day relative-volume baseline via the cache-first repository, evaluates each
/// completed candle through <see cref="PressureMapCalculator"/>, and republishes an immutable render
/// snapshot once per second. The window draws the matrix on a Canvas off <see cref="PressureMapChanged"/>
/// (presentation only). Data/signals only — never places orders, never writes to the store.
/// </summary>
public sealed partial class OrderFlowPressureMapViewModel : ViewModelBase, IDisposable
{
    private const string LogSource = "Pressure Map";

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<OrderFlowPressureMapViewModel> _logger;
    private readonly OrderFlowPressureMapOptions _opt;

    private readonly object _sync = new();
    private readonly List<PressureRow> _rows = new();
    private readonly List<IDisposable> _hubHandles = new();
    private readonly List<IDisposable> _ingestHandles = new();
    private CancellationTokenSource? _cts;
    private IDisposable? _renderTimer;
    private volatile bool _dirty;
    private bool _ready;
    private bool _disposed;

    private readonly int _columns;

    public OrderFlowPressureMapViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        InMemoryLogSink log,
        ILogger<OrderFlowPressureMapViewModel> logger,
        IOptions<OrderFlowPressureMapOptions> options)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _log = log;
        _logger = logger;
        _opt = options.Value;
        _columns = Math.Max(10, _opt.DisplayWindowColumns);

        _selectedUniverse = _opt.Universe;
        _minRelVol = _opt.MinRelativeVolume;
        _selectedSignalFilter = _opt.SignalFilter;
        _showOnlyActive = _opt.ShowOnlyActive;

        Snapshot = Array.Empty<PressureRowSnapshot>();
        _ready = true;
        Restart();
    }

    // ── Filter bar (bound from the window) ──────────────────────────────────────────────────────
    [ObservableProperty] private PressureUniverse _selectedUniverse;
    [ObservableProperty] private double _minRelVol;
    [ObservableProperty] private SignalTypeFilter _selectedSignalFilter;
    [ObservableProperty] private bool _showOnlyActive;
    [ObservableProperty] private string _status = "Starting…";

    // ── Pinned-row inline detail ────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _pinnedTitle = string.Empty;
    [ObservableProperty] private string _pinnedPrice = "—";
    [ObservableProperty] private string _pinnedCandle = "—";
    [ObservableProperty] private string _pinnedRelVol = "—";
    [ObservableProperty] private string _pinnedImbalance = "—";
    [ObservableProperty] private string _pinnedSignal = "—";
    [ObservableProperty] private string _pinnedSignalTime = "—";

    private string? _pinnedSymbol;

    /// <summary>The currently pinned ticker symbol, or null. Read by the window to highlight the row.</summary>
    public string? PinnedSymbol => _pinnedSymbol;

    /// <summary>The most recent immutable render snapshot, filtered + sorted. Read by the window on the
    /// <see cref="PressureMapChanged"/> event (UI thread).</summary>
    public IReadOnlyList<PressureRowSnapshot> Snapshot { get; private set; }

    /// <summary>UTC open-time of the newest (right-most) matrix column, for the X-axis labels.</summary>
    public DateTime LatestColumnTime { get; private set; }

    public int Columns => _columns;

    public IReadOnlyList<PressureUniverse> Universes { get; } =
        new[] { PressureUniverse.Sp100, PressureUniverse.Sp500 };

    public IReadOnlyList<SignalTypeFilter> SignalFilters { get; } =
        new[] { SignalTypeFilter.All, SignalTypeFilter.Absorption, SignalTypeFilter.Breakthrough, SignalTypeFilter.Breakdown };

    /// <summary>Raised on the UI thread once per second when the snapshot changes; the window redraws.</summary>
    public event EventHandler? PressureMapChanged;

    partial void OnSelectedUniverseChanged(PressureUniverse value) { if (_ready) Restart(); }
    partial void OnMinRelVolChanged(double value) { _dirty = true; }
    partial void OnSelectedSignalFilterChanged(SignalTypeFilter value) { _dirty = true; }
    partial void OnShowOnlyActiveChanged(bool value) { _dirty = true; }

    /// <summary>Pin/unpin a ticker row (toggles when the same symbol is clicked again).</summary>
    public void Pin(string symbol)
    {
        _pinnedSymbol = string.Equals(_pinnedSymbol, symbol, StringComparison.Ordinal) ? null : symbol;
        IsPinned = _pinnedSymbol is not null;
        UpdatePinnedLive();
    }

    [RelayCommand]
    private void Unpin()
    {
        _pinnedSymbol = null;
        IsPinned = false;
    }

    private void Restart()
    {
        StopAll();

        BrokerKind broker;
        try
        {
            broker = ResolveBroker();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            Snapshot = Array.Empty<PressureRowSnapshot>();
            PressureMapChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var symbols = SelectedUniverse == PressureUniverse.Sp500
            ? Sp100Sp500Catalog.Sp500
            : Sp100Sp500Catalog.Sp100;

        var cap = Math.Max(1, _opt.MaxConcurrentSubscriptions);
        var take = Math.Min(symbols.Count, cap);
        if (symbols.Count > take)
            _log.Append(LogSource, "WARN",
                $"{SelectedUniverse} has {symbols.Count} names; capping live subscriptions to {take} (MaxConcurrentSubscriptions). Raise the cap or stay on S&P 100 for full coverage.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        lock (_sync)
        {
            _rows.Clear();
            for (var i = 0; i < take; i++)
            {
                var s = symbols[i];
                var contract = Sp100Sp500Catalog.ToContract(s);
                InstrumentId id;
                try { id = _ingest.Resolve(contract, broker); }
                catch (Exception ex) { _logger.LogWarning(ex, "PressureMap: resolve failed for {Symbol}", s.Symbol); continue; }

                var row = new PressureRow(s.Symbol, s.Name, contract, broker, id, _columns);
                _rows.Add(row);

                // Start broker feeds (ref-counted). Quotes (+depth) and 1m streaming bars.
                _ingestHandles.Add(_ingest.Subscribe(contract, broker));
                _ingestHandles.Add(_ingest.SubscribeBars(contract, broker, BarSize.OneMinute));

                // Subscribe the canonical hub fan-out. Callbacks land on ingest threads; we mutate the
                // row under _sync and let the 1s UI timer snapshot it — no per-event UI marshalling.
                _hubHandles.Add(_hub.Bars(id, BarSize.OneMinute).Subscribe(b => OnBar(row, b)));
                _hubHandles.Add(_hub.Quotes(id).Subscribe(q => OnQuote(row, q)));
                if (_opt.EnableDepth)
                    _hubHandles.Add(_hub.Depth(id).Subscribe(d => OnDepth(row, d)));
            }
        }

        Status = $"{SelectedUniverse} · {take} tickers · {BrokerLabel(broker)} · priming 20-day baseline…";
        _log.Append(LogSource, "INFO",
            $"Pressure Map on {SelectedUniverse} ({take} tickers) via {BrokerLabel(broker)}; depth {( _opt.EnableDepth ? "on" : "off (top-of-book)")}.");

        StartRenderTimer();
        _ = PrimeBaselinesAsync(broker, ct);
        _dirty = true;
    }

    /// <summary>Fetches the 20-day, same-time-of-day average 1m volume per ticker (cache-first, bounded
    /// parallelism) so relative volume is meaningful from the first live candle. Falls back to the
    /// rolling 30-minute intraday baseline per row when no history is available.</summary>
    private async Task PrimeBaselinesAsync(BrokerKind broker, CancellationToken ct)
    {
        PressureRow[] rows;
        lock (_sync) rows = _rows.ToArray();

        using var gate = new SemaphoreSlim(8);
        var duration = TimeSpan.FromDays(Math.Max(1, _opt.BaselineDays));
        var primed = 0;
        var failed = 0;

        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var bars = await _repository
                    .GetHistoricalBarsAsync(row.Contract, broker, BarSize.OneMinute, duration, ct);
                if (bars.Count == 0) { Interlocked.Increment(ref failed); return; }

                // Average volume per minute-of-day across the lookback window.
                var sum = new Dictionary<int, (double Total, int Count)>();
                foreach (var b in bars)
                {
                    var key = (int)b.TimestampUtc.TimeOfDay.TotalMinutes;
                    var cur = sum.TryGetValue(key, out var v) ? v : default;
                    sum[key] = (cur.Total + b.Volume, cur.Count + 1);
                }

                var baseline = new Dictionary<int, double>(sum.Count);
                foreach (var (k, v) in sum) baseline[k] = v.Count > 0 ? v.Total / v.Count : 0;

                lock (_sync) row.TodBaseline = baseline;
                Interlocked.Increment(ref primed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _logger.LogDebug(ex, "PressureMap: baseline fetch failed for {Symbol}", row.Symbol);
            }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { return; }

        _dirty = true;
        await UiThread.RunAsync(() =>
        {
            Status = $"{SelectedUniverse} · {rows.Length} tickers · baseline primed for {primed}" +
                     (failed > 0 ? $" ({failed} via intraday fallback)" : "") + ".";
        });
        _log.Append(LogSource, failed > 0 ? "WARN" : "INFO",
            $"Baseline primed: {primed} from 20-day history, {failed} on the 30-minute intraday fallback.");
    }

    private void OnBar(PressureRow row, OhlcvBar b)
    {
        lock (_sync)
        {
            if (!b.IsFinal) { row.Forming = b; _dirty = true; return; }

            row.RecentBars.Add(b.ToBar());
            while (row.RecentBars.Count > Math.Max(_opt.Atr14Period + 2, 16)) row.RecentBars.RemoveAt(0);
            var atr = PressureMapCalculator.Atr(row.RecentBars, _opt.Atr14Period);

            var key = (int)b.OpenTimeUtc.TimeOfDay.TotalMinutes;
            double baseline = 0;
            if (row.TodBaseline is { } tod && tod.TryGetValue(key, out var todAvg) && todAvg > 0)
                baseline = todAvg;
            else if (row.ShortVol.Count > 0)
                baseline = row.ShortVol.Average();

            var (bidDepth, askDepth) = row.CurrentDepth();
            var cell = PressureMapCalculator.Evaluate(b, atr, baseline, bidDepth, askDepth, MinRelVol, _opt);

            row.Cells.Add(cell);
            while (row.Cells.Count > _columns) row.Cells.RemoveAt(0);

            row.ShortVol.Enqueue(b.Volume);
            while (row.ShortVol.Count > Math.Max(1, _opt.ShortBaselineMinutes)) row.ShortVol.Dequeue();

            if (cell.Signal != PressureSignal.Neutral)
            {
                row.LastSignal = cell.Signal;
                row.LastSignalTime = b.OpenTimeUtc;
            }
            row.Forming = null;
            _dirty = true;
        }
    }

    private void OnQuote(PressureRow row, Quote q)
    {
        lock (_sync)
        {
            row.LastBid = q.Bid;
            row.LastAsk = q.Ask;
            row.LastBidSize = q.BidSize;
            row.LastAskSize = q.AskSize;
            row.LastPrice = q.Mid;
            _dirty = true;
        }
    }

    private void OnDepth(PressureRow row, DepthSnapshot d)
    {
        long bid = 0, ask = 0;
        for (var i = 0; i < d.Bids.Count && i < 5; i++) bid += d.Bids[i].Size;
        for (var i = 0; i < d.Asks.Count && i < 5; i++) ask += d.Asks[i].Size;
        lock (_sync)
        {
            row.BidDepth5 = bid;
            row.AskDepth5 = ask;
            row.HasDepth = bid > 0 || ask > 0;
            _dirty = true;
        }
    }

    private void StartRenderTimer()
    {
        // Portable coalescing render timer — ticks are marshalled to the UI thread (see UiThread).
        if (_renderTimer is not null) return;
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, _opt.GuiRefreshMs));
        _renderTimer = UiThread.CreateRenderTimer(interval, OnRenderTick);
    }

    private void StopRenderTimer()
    {
        // Dispose stops the timer; a late in-flight tick is a harmless no-op (the _dirty guard
        // in OnRenderTick short-circuits it).
        var timer = _renderTimer;
        _renderTimer = null;
        timer?.Dispose();
    }

    private void OnRenderTick()
    {
        UpdatePinnedLive();
        if (!_dirty) return;
        _dirty = false;

        List<PressureRowSnapshot> snap;
        DateTime latest;
        lock (_sync)
        {
            latest = DateTime.MinValue;
            foreach (var r in _rows)
                if (r.Cells.Count > 0 && r.Cells[^1].OpenTimeUtc > latest) latest = r.Cells[^1].OpenTimeUtc;

            snap = new List<PressureRowSnapshot>(_rows.Count);
            foreach (var r in _rows)
            {
                var cells = new PressureCell?[_columns];
                if (latest > DateTime.MinValue)
                    foreach (var c in r.Cells)
                    {
                        var col = _columns - 1 - (int)Math.Round((latest - c.OpenTimeUtc).TotalMinutes);
                        if (col >= 0 && col < _columns) cells[col] = c;
                    }

                var (bd, ad) = r.CurrentDepth();
                var lastCell = r.Cells.Count > 0 ? r.Cells[^1] : null;
                snap.Add(new PressureRowSnapshot
                {
                    Symbol = r.Symbol,
                    Name = r.Name,
                    Cells = cells,
                    LastPrice = r.LastPrice,
                    BookImbalance = PressureMapCalculator.BookImbalance(bd, ad),
                    RelativeVolume = lastCell?.RelativeVolume ?? 0,
                    LastSignal = r.LastSignal,
                    LastSignalTime = r.LastSignalTime,
                    HasActiveSignal = lastCell is { Signal: not PressureSignal.Neutral },
                });
            }
        }

        LatestColumnTime = latest;
        Snapshot = ApplyFilterSort(snap);
        PressureMapChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<PressureRowSnapshot> ApplyFilterSort(List<PressureRowSnapshot> rows)
    {
        IEnumerable<PressureRowSnapshot> q = rows;

        if (ShowOnlyActive)
            q = q.Where(r => r.HasActiveSignal);

        q = SelectedSignalFilter switch
        {
            SignalTypeFilter.Absorption => q.Where(r =>
                r.LastSignal is PressureSignal.BullishAbsorption or PressureSignal.BearishAbsorption),
            SignalTypeFilter.Breakthrough => q.Where(r => r.LastSignal == PressureSignal.BullishBreakthrough),
            SignalTypeFilter.Breakdown => q.Where(r => r.LastSignal == PressureSignal.BearishBreakdown),
            _ => q,
        };

        return q
            .OrderByDescending(r => r.HasActiveSignal)
            .ThenByDescending(r => r.RelativeVolume)
            .ThenByDescending(r => Math.Abs(r.BookImbalance))
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    private void UpdatePinnedLive()
    {
        var symbol = _pinnedSymbol;
        if (symbol is null) { IsPinned = false; return; }

        // Resolve the row AND read its state in one lock scope so a concurrent StopAll() (e.g. a
        // universe switch) can't clear _rows between the lookup and the read. Format into locals
        // under the lock; publish the observable properties after releasing it.
        string title, price, candle, relVol, imbalance, signal, signalTime;
        lock (_sync)
        {
            var row = _rows.FirstOrDefault(r => string.Equals(r.Symbol, symbol, StringComparison.Ordinal));
            if (row is null) { IsPinned = false; return; }

            var (bd, ad) = row.CurrentDepth();
            var imb = PressureMapCalculator.BookImbalance(bd, ad);
            var lastCell = row.Cells.Count > 0 ? row.Cells[^1] : null;
            var forming = row.Forming;

            title = $"{row.Symbol} — {row.Name}";
            price = row.LastPrice > 0 ? row.LastPrice.ToString("N2") : "—";
            candle = forming is { } f
                ? $"O {f.Open:N2}  H {f.High:N2}  L {f.Low:N2}  C {f.Close:N2}  V {f.Volume:N0}"
                : lastCell is { } c0 ? $"O {c0.Open:N2}  H {c0.High:N2}  L {c0.Low:N2}  C {c0.Close:N2}  V {c0.Volume:N0}" : "—";
            relVol = lastCell is { } c1 ? c1.RelativeVolume.ToString("N2") + "×" : "—";
            imbalance = imb.ToString("+0.00;-0.00;0.00");
            signal = row.LastSignal == PressureSignal.Neutral ? "—" : Describe(row.LastSignal);
            signalTime = row.LastSignalTime is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "—";
        }

        IsPinned = true;
        PinnedTitle = title;
        PinnedPrice = price;
        PinnedCandle = candle;
        PinnedRelVol = relVol;
        PinnedImbalance = imbalance;
        PinnedSignal = signal;
        PinnedSignalTime = signalTime;
    }

    private BrokerKind ResolveBroker()
    {
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect a broker that serves US equities (IB / Alpaca) to populate the map.");
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

    internal static string Describe(PressureSignal s) => s switch
    {
        PressureSignal.BullishAbsorption => "Bullish Absorption",
        PressureSignal.BearishAbsorption => "Bearish Absorption",
        PressureSignal.BullishBreakthrough => "Bullish Breakthrough",
        PressureSignal.BearishBreakdown => "Bearish Breakdown",
        _ => "Neutral",
    };

    private void StopAll()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        StopRenderTimer();
        lock (_sync)
        {
            foreach (var h in _hubHandles) h.Dispose();
            foreach (var h in _ingestHandles) h.Dispose();
            _hubHandles.Clear();
            _ingestHandles.Clear();
            _rows.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }
}

/// <summary>Mutable per-ticker runtime state. Guarded by the VM's <c>_sync</c> lock; never crosses a
/// thread boundary itself — only the immutable <see cref="PressureRowSnapshot"/> does.</summary>
internal sealed class PressureRow
{
    public PressureRow(string symbol, string name, Contract contract, BrokerKind broker, InstrumentId id, int columns)
    {
        Symbol = symbol;
        Name = name;
        Contract = contract;
        Broker = broker;
        Id = id;
        Cells = new List<PressureCell>(columns);
    }

    public string Symbol { get; }
    public string Name { get; }
    public Contract Contract { get; }
    public BrokerKind Broker { get; }
    public InstrumentId Id { get; }

    public List<PressureCell> Cells { get; }
    public List<Bar> RecentBars { get; } = new();
    public Queue<long> ShortVol { get; } = new();
    public Dictionary<int, double>? TodBaseline { get; set; }

    public double LastBid { get; set; }
    public double LastAsk { get; set; }
    public long LastBidSize { get; set; }
    public long LastAskSize { get; set; }
    public double LastPrice { get; set; }

    public long BidDepth5 { get; set; }
    public long AskDepth5 { get; set; }
    public bool HasDepth { get; set; }

    public OhlcvBar? Forming { get; set; }

    public PressureSignal LastSignal { get; set; } = PressureSignal.Neutral;
    public DateTime? LastSignalTime { get; set; }

    /// <summary>Top-5 depth sums when L2 is available, else top-of-book quote sizes (the spec fallback).</summary>
    public (double bid, double ask) CurrentDepth() =>
        HasDepth ? (BidDepth5, AskDepth5) : (LastBidSize, LastAskSize);
}
