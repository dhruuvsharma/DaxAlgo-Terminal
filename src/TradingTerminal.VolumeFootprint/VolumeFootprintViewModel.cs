using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// Drives the Volume Footprint window — a bid/ask cluster chart. Mirrors the order-flow strategy VMs:
/// it resolves the source broker, pumps the trade tape (with a synthetic L1-derived fallback for
/// brokers that don't wire <c>SubscribeTradesAsync</c>, so the chart still works against the fake
/// client / cTrader / Alpaca), and aggregates each <see cref="TradePrint"/> into time-bucketed
/// <see cref="RenderBar"/>s via <see cref="FootprintFeatures.BuildBar"/> (the shared Core extractor).
///
/// The window renders the cluster grid onto a Canvas in code-behind off the <see cref="FootprintChanged"/>
/// event — same convention the Order Flow Cube window uses (presentation, not business logic). This VM
/// holds no view code.
/// </summary>
public sealed partial class VolumeFootprintViewModel : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;
    private const string LogSource = "Volume Footprint";

    /// <summary>Which brokers actually wire a native trade tape today (see the cube VM's note). The
    /// rest fall back to synthetic L1-derived prints so the chart isn't permanently empty.</summary>
    private static bool BrokerSupportsTradeTape(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => true,
        _ => false,
    };

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<VolumeFootprintViewModel> _logger;

    private IReadOnlyList<SignalInstrument> _allInstruments;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _quoteHandle;
    private IDisposable? _tradeHandle;
    private readonly QuoteTradeSynthesizer _synth = new();
    private bool _useSynthetic;

    // ── Per-bar accumulator ────────────────────────────────────────────────────────────────
    // Instead of a mutable FootprintBar mutated by Add(), we keep the raw prints for the
    // forming bar and call FootprintFeatures.BuildBar (Core) on every trade to rebuild the
    // immutable Core bar. This ensures the chart and Apex v2 engine score from the same math.
    private readonly List<FootprintPrint> _currentPrints = new();
    private DateTime _currentBucketStart = DateTime.MinValue;
    private long _cumulativeDelta;

    // Cached render bar for the forming bar so we can replace it in Bars in-place.
    private RenderBar? _currentRenderBar;

    /// <summary>Wall-clock arrival times of recent trades, pruned to <see cref="TicksWindow"/>, so the
    /// stats panel can show a live ticks-per-second throughput that decays when flow stops.</summary>
    private readonly Queue<DateTime> _tradeArrivals = new();
    private static readonly TimeSpan TicksWindow = TimeSpan.FromSeconds(2);

    /// <summary>Refreshes the ticks/sec read-out (which must decay between trades) off the UI thread's
    /// dispatcher. The chart canvas is only redrawn on actual trades, not on this tick.</summary>
    private readonly DispatcherTimer _statsTimer;

    /// <summary>False until the constructor has built every collection. Suppresses the
    /// <c>[ObservableProperty]</c> setters' On*Changed callbacks from running <see cref="Restart"/>
    /// against half-initialized state.</summary>
    private bool _ready;

    /// <summary>Selectable bar intervals — label + the time bucket each footprint bar spans.</summary>
    public sealed record FootprintInterval(string Label, TimeSpan Span);

    private static readonly IReadOnlyList<FootprintInterval> AllIntervals = new[]
    {
        new FootprintInterval("15s", TimeSpan.FromSeconds(15)),
        new FootprintInterval("30s", TimeSpan.FromSeconds(30)),
        new FootprintInterval("1m",  TimeSpan.FromMinutes(1)),
        new FootprintInterval("5m",  TimeSpan.FromMinutes(5)),
    };

    public VolumeFootprintViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        InMemoryLogSink log,
        ILogger<VolumeFootprintViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _log = log;
        _logger = logger;

        // Build every collection BEFORE assigning the selected values: the generated property
        // setters call the On*Changed handlers, which would otherwise fire Restart() (and touch
        // Bars) before it exists. The _ready guard suppresses those mid-construction callbacks; we
        // kick off a single Restart() explicitly once everything is wired.
        _allInstruments = SignalInstrumentCatalog.All;
        Bars = new ObservableCollection<RenderBar>();
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        Intervals = new ObservableCollection<FootprintInterval>(AllIntervals);

        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();
        SelectedInterval = Intervals.First(i => i.Label == "1m");

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statsTimer.Tick += (_, _) => UpdateTicksPerSecond();
        _statsTimer.Start();

        _ready = true;
        _ = LoadInstrumentsAsync();
        Restart();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<FootprintInterval> Intervals { get; }

    /// <summary>Most recent footprint bars, oldest first (rendered left → right).</summary>
    public ObservableCollection<RenderBar> Bars { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private FootprintInterval? _selectedInterval;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _tickSizeText = "0.25";
    [ObservableProperty] private int _maxBars = 14;
    [ObservableProperty] private string _status = "Pick an instrument to stream its footprint.";
    [ObservableProperty] private long _tradesSeen;
    [ObservableProperty] private long _sessionDelta;

    // ── Top-right stats panel ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _pocSlopeText = "—";
    /// <summary>Sign of the POC-regression slope: +1 rising, -1 falling, 0 flat (drives the colour).</summary>
    [ObservableProperty] private int _pocSlopeDirection;
    [ObservableProperty] private string _buyPocSlopeText = "—";
    [ObservableProperty] private int _buyPocSlopeDirection;
    [ObservableProperty] private string _sellPocSlopeText = "—";
    [ObservableProperty] private int _sellPocSlopeDirection;
    [ObservableProperty] private int _cvdDirection;
    [ObservableProperty] private string _ticksPerSecondText = "0.0";
    [ObservableProperty] private string _visibleVolumeText = "0";
    [ObservableProperty] private string _buySellText = "—";
    [ObservableProperty] private string _currentPocText = "—";

    /// <summary>Least-squares slope of POC price against bar (column) index across the visible bars,
    /// in price units per bar. Read by the window code-behind to draw the regression line.</summary>
    public double PocSlope { get; private set; }

    /// <summary>Intercept of the POC regression at column 0, in price units.</summary>
    public double PocIntercept { get; private set; }

    /// <summary>True once ≥2 bars carry a valid POC, so a regression line can be drawn.</summary>
    public bool HasRegression { get; private set; }

    /// <summary>Buy-POC regression (slope/intercept/validity), same convention as the total POC.</summary>
    public double BuyPocSlope { get; private set; }
    public double BuyPocIntercept { get; private set; }
    public bool HasBuyRegression { get; private set; }

    /// <summary>Sell-POC regression (slope/intercept/validity), same convention as the total POC.</summary>
    public double SellPocSlope { get; private set; }
    public double SellPocIntercept { get; private set; }
    public bool HasSellRegression { get; private set; }

    /// <summary>Raised on the UI thread whenever the bar set changes and the canvas should redraw.</summary>
    public event EventHandler? FootprintChanged;

    private double TickSize => double.TryParse(TickSizeText, out var t) && t > 0 ? t : 0.25;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) { if (_ready) Restart(); }
    partial void OnSelectedIntervalChanged(FootprintInterval? value) { if (_ready) Restart(); }
    partial void OnTickSizeTextChanged(string value) { if (_ready) Restart(); }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            _allInstruments = list
                .Select(i => new SignalInstrument(i.DisplayName, i.Category, i.Contract, i.Broker))
                .ToList();
            var keep = SelectedInstrument;
            ApplyFilter();
            SelectedInstrument = keep is not null && Instruments.Contains(keep)
                ? keep
                : _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? _allInstruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Footprint: instrument list load failed; using static catalog");
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
        var instrument = SelectedInstrument;
        var interval = SelectedInterval;
        if (instrument is null || interval is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; ClearBars(); return; }

        _useSynthetic = !BrokerSupportsTradeTape(broker);
        _synth.Reset();
        _currentPrints.Clear();
        _currentRenderBar = null;
        _currentBucketStart = DateTime.MinValue;
        _cumulativeDelta = 0;
        TradesSeen = 0;
        SessionDelta = 0;
        ResetStats();
        ClearBars();

        var tape = _useSynthetic ? "synthetic L1-derived" : "real trade tape";
        Status = $"Streaming {instrument.DisplayName} ({BrokerLabel(broker)}) — {tape}, {interval.Label} bars @ {TickSize} tick…";
        _log.Append(LogSource, _useSynthetic ? "WARN" : "INFO",
            $"Footprint on {instrument.DisplayName} [{BrokerLabel(broker)}] — {tape}, {interval.Label} bars, tick {TickSize}");

        _streamCts = new CancellationTokenSource();
        _ = RunStreamAsync(instrument.Contract, broker, _streamCts.Token);
    }

    private async Task RunStreamAsync(Contract contract, BrokerKind broker, CancellationToken ct)
    {
        InstrumentId instrumentId;
        try
        {
            instrumentId = _ingest.Resolve(contract, broker);
            // Quote pump always runs: it carries bid/ask context for the real tape's Lee-Ready
            // classification and is the source for the synthesizer in fallback mode.
            _quoteHandle = _ingest.Subscribe(contract, broker);
            if (!_useSynthetic) _tradeHandle = _ingest.SubscribeTrades(contract, broker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Footprint: subscribe failed for {Symbol}", contract.Symbol);
            await UiThread.RunAsync(() => Status = $"Subscribe failed: {ex.Message}");
            return;
        }

        var channel = Channel.CreateUnbounded<TradePrint>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var sub = _useSynthetic
            ? _hub.Quotes(instrumentId).Subscribe(q =>
              {
                  var t = _synth.Synthesize(q);
                  if (t is not null) channel.Writer.TryWrite(t);
              })
            : _hub.Trades(instrumentId).Subscribe(t => channel.Writer.TryWrite(t));

        try
        {
            await foreach (var trade in channel.Reader.ReadAllAsync(ct))
                await UiThread.RunAsync(() => OnTrade(trade));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Footprint: trade stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private void OnTrade(TradePrint trade)
    {
        var interval = SelectedInterval;
        if (interval is null) return;

        var ts = trade.EventTimeUtc;
        var span = interval.Span;
        var bucket = new DateTime(ts.Ticks - (ts.Ticks % span.Ticks), DateTimeKind.Utc);
        var tickSize = TickSize;
        var quality = _useSynthetic ? FeedQuality.SyntheticL1 : FeedQuality.RealTape;

        if (bucket != _currentBucketStart)
        {
            // Finalize the completed bar: seal the prior prints into the Bars collection.
            if (_currentBucketStart != DateTime.MinValue && _currentRenderBar is not null)
            {
                // The forming bar was already added; rebuild it as a finalized bar and
                // update the cumulative-delta thread before rolling.
                var finalized = BuildRenderBar(_currentPrints, tickSize, _currentBucketStart, bucket, quality, _cumulativeDelta);
                _cumulativeDelta += finalized.Core.Delta;
                // Replace the last entry (the forming bar) with the finalized version.
                if (Bars.Count > 0) Bars[^1] = finalized;
            }

            _currentBucketStart = bucket;
            _currentPrints.Clear();
            _currentRenderBar = null;
        }

        // Accumulate this print using Core's FootprintPrint projection.
        _currentPrints.Add(FootprintPrint.From(trade));

        // Rebuild the forming bar from all accumulated prints. The Core extractor is stateless,
        // deterministic and cheap (O(prints) per call); rebuilding on every trade is correct and
        // gives the same result as the old incremental Add() path.
        var formingBar = BuildRenderBar(_currentPrints, tickSize, bucket,
            bucket + span, quality, _cumulativeDelta);
        _currentRenderBar = formingBar;

        if (_currentBucketStart == bucket && (Bars.Count == 0 || Bars[^1].StartUtc != bucket))
        {
            Bars.Add(formingBar);
            while (Bars.Count > Math.Max(2, MaxBars)) Bars.RemoveAt(0);
        }
        else if (Bars.Count > 0 && Bars[^1].StartUtc == bucket)
        {
            // Replace the forming bar with the freshly rebuilt version.
            Bars[^1] = formingBar;
        }

        TradesSeen++;
        _tradeArrivals.Enqueue(DateTime.UtcNow);
        // Live forming-bar delta folds into the running session delta for the header read-out.
        SessionDelta = _cumulativeDelta + formingBar.Core.Delta;

        UpdateStats();
        Status = $"{SelectedInstrument?.DisplayName} · {Bars.Count} bars · {TradesSeen} trades · CVD {SessionDelta:+#;-#;0}";
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Delegates bar construction to <see cref="FootprintFeatures.BuildBar"/> (Core) and
    /// wraps the result in a <see cref="RenderBar"/> that adds the argmax buy/sell POC fields the
    /// overlay connector lines need.</summary>
    private static RenderBar BuildRenderBar(
        IReadOnlyList<FootprintPrint> prints,
        double tickSize,
        DateTime startUtc,
        DateTime endUtc,
        FeedQuality quality,
        long cumulativeDeltaBefore)
    {
        var core = FootprintFeatures.BuildBar(prints, tickSize, startUtc, endUtc,
            quality, cumulativeDeltaBefore);
        return new RenderBar(core);
    }

    /// <summary>Recomputes the POC regression and the stats-panel read-outs from the visible bars.
    /// Called on each trade (UI thread) before the canvas redraws. Cheap — O(visible bars).</summary>
    private void UpdateStats()
    {
        var bars = Bars;
        int n = bars.Count;

        // Least-squares fit of each POC flavour (price vs column index) over bars with a valid POC.
        (PocSlope, PocIntercept, HasRegression)         = FitPoc(b => b.PointOfControl);
        (BuyPocSlope, BuyPocIntercept, HasBuyRegression) = FitPoc(b => b.BuyPointOfControl);
        (SellPocSlope, SellPocIntercept, HasSellRegression) = FitPoc(b => b.SellPointOfControl);

        (PocSlopeText, PocSlopeDirection)     = FormatSlope(HasRegression, PocSlope);
        (BuyPocSlopeText, BuyPocSlopeDirection)   = FormatSlope(HasBuyRegression, BuyPocSlope);
        (SellPocSlopeText, SellPocSlopeDirection) = FormatSlope(HasSellRegression, SellPocSlope);

        // Buy/sell split from Core bar fields.
        long buy = 0, sell = 0;
        foreach (var b in bars) { buy += b.Core.BuyVolume; sell += b.Core.SellVolume; }
        var vol = buy + sell;
        VisibleVolumeText = vol.ToString("N0", CultureInfo.InvariantCulture);
        BuySellText = vol > 0
            ? $"{100.0 * buy / vol:0}% / {100.0 * sell / vol:0}%"
            : "—";

        var lastPoc = n > 0 ? bars[n - 1].PointOfControl : double.NaN;
        CurrentPocText = double.IsNaN(lastPoc)
            ? "—"
            : lastPoc.ToString("N" + DecimalsFor(TickSize), CultureInfo.InvariantCulture);

        CvdDirection = Math.Sign(SessionDelta);
    }

    /// <summary>Least-squares fit of a chosen POC price (y) against column index (x) over the visible
    /// bars, skipping bars whose POC is NaN. Returns (slope price/bar, intercept at column 0, valid).</summary>
    private (double slope, double intercept, bool has) FitPoc(Func<RenderBar, double> selector)
    {
        var bars = Bars;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int m = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            var p = selector(bars[i]);
            if (double.IsNaN(p)) continue;
            sx += i; sy += p; sxx += (double)i * i; sxy += i * p; m++;
        }
        var denom = m * sxx - sx * sx;
        if (m < 2 || Math.Abs(denom) < 1e-9) return (0, 0, false);
        var slope = (m * sxy - sx * sy) / denom;
        return (slope, (sy - slope * sx) / m, true);
    }

    private static (string text, int direction) FormatSlope(bool has, double slope) =>
        has
            ? ($"{slope:+0.####;-0.####;0} /bar", slope > 1e-9 ? 1 : slope < -1e-9 ? -1 : 0)
            : ("—", 0);

    /// <summary>Prunes the arrival window and republishes the live ticks/sec rate. Runs on the stats
    /// timer so the figure decays toward zero when the tape goes quiet.</summary>
    private void UpdateTicksPerSecond()
    {
        var cutoff = DateTime.UtcNow - TicksWindow;
        while (_tradeArrivals.Count > 0 && _tradeArrivals.Peek() < cutoff) _tradeArrivals.Dequeue();
        var rate = _tradeArrivals.Count / TicksWindow.TotalSeconds;
        TicksPerSecondText = rate.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void ResetStats()
    {
        _tradeArrivals.Clear();
        PocSlope = 0; PocIntercept = 0; HasRegression = false;
        BuyPocSlope = 0; BuyPocIntercept = 0; HasBuyRegression = false;
        SellPocSlope = 0; SellPocIntercept = 0; HasSellRegression = false;
        PocSlopeText = "—"; PocSlopeDirection = 0;
        BuyPocSlopeText = "—"; BuyPocSlopeDirection = 0;
        SellPocSlopeText = "—"; SellPocSlopeDirection = 0;
        CvdDirection = 0;
        TicksPerSecondText = "0.0"; VisibleVolumeText = "0"; BuySellText = "—"; CurrentPocText = "—";
    }

    private static int DecimalsFor(double tick)
    {
        if (tick >= 1) return 0;
        var decimals = 0;
        while (tick < 1 && decimals < 8) { tick *= 10; decimals++; }
        return decimals;
    }

    private void ClearBars()
    {
        Bars.Clear();
        _currentPrints.Clear();
        _currentRenderBar = null;
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
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

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _quoteHandle?.Dispose(); _quoteHandle = null;
        _tradeHandle?.Dispose(); _tradeHandle = null;
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        StopStream();
    }
}

/// <summary>
/// L1 tick-rule synthesizer: derives <see cref="TradePrint"/>s from a quote stream when the broker
/// has no native trade tape. Mid ticks up ⇒ buy print at the ask (size = ask size); mid ticks down ⇒
/// sell print at the bid (size = bid size); unchanged mid ⇒ no event. Mirrors the strategy projects'
/// internal synthesizer; kept local so the App doesn't reach into a strategy assembly.
/// </summary>
internal sealed class QuoteTradeSynthesizer
{
    private Quote? _prev;

    public TradePrint? Synthesize(Quote q)
    {
        var prev = _prev;
        _prev = q;
        if (prev is null || q.Mid == prev.Mid) return null;
        var isBuy = q.Mid > prev.Mid;
        var price = isBuy ? q.Ask : q.Bid;
        var size = Math.Max(1L, isBuy ? q.AskSize : q.BidSize);
        return new TradePrint(q.InstrumentId, q.EventTimeUtc, q.IngestTimeUtc, price, size,
            isBuy ? AggressorSide.Buy : AggressorSide.Sell, q.Source, q.Sequence, q.EventTimeApproximate);
    }

    public void Reset() => _prev = null;
}
