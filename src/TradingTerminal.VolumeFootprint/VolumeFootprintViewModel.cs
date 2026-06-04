using System.Collections.ObjectModel;
using System.Threading.Channels;
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
/// <see cref="FootprintBar"/>s with per-price buy/sell volume, delta and point-of-control.
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

    private FootprintBar? _currentBar;
    private DateTime _currentBucketStart = DateTime.MinValue;
    private long _cumulativeDelta;

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
        Bars = new ObservableCollection<FootprintBar>();
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        Intervals = new ObservableCollection<FootprintInterval>(AllIntervals);

        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();
        SelectedInterval = Intervals.First(i => i.Label == "1m");

        _ready = true;
        _ = LoadInstrumentsAsync();
        Restart();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<FootprintInterval> Intervals { get; }

    /// <summary>Most recent footprint bars, oldest first (rendered left → right).</summary>
    public ObservableCollection<FootprintBar> Bars { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private FootprintInterval? _selectedInterval;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _tickSizeText = "0.25";
    [ObservableProperty] private int _maxBars = 14;
    [ObservableProperty] private string _status = "Pick an instrument to stream its footprint.";
    [ObservableProperty] private long _tradesSeen;
    [ObservableProperty] private long _sessionDelta;

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
        _currentBar = null;
        _currentBucketStart = DateTime.MinValue;
        _cumulativeDelta = 0;
        TradesSeen = 0;
        SessionDelta = 0;
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

        if (_currentBar is null || bucket != _currentBucketStart)
        {
            // Finalize the prior bar's cumulative delta before rolling.
            if (_currentBar is not null)
            {
                _cumulativeDelta += _currentBar.Delta;
                _currentBar.CumulativeDelta = _cumulativeDelta;
            }
            _currentBucketStart = bucket;
            _currentBar = new FootprintBar(bucket, TickSize);
            Bars.Add(_currentBar);
            while (Bars.Count > Math.Max(2, MaxBars)) Bars.RemoveAt(0);
        }

        var isBuy = trade.Aggressor switch
        {
            AggressorSide.Buy => true,
            AggressorSide.Sell => false,
            // Unknown aggressor (illiquid first prints) — fall back to nothing rather than mislabel.
            _ => (bool?)null,
        } ?? (trade.Price >= (_currentBar?.Close is { } c && !double.IsNaN(c) ? c : trade.Price));

        _currentBar!.Add(trade.Price, trade.Size, isBuy);

        TradesSeen++;
        // Live forming-bar delta folds into the running session delta for the header read-out.
        SessionDelta = _cumulativeDelta + _currentBar.Delta;
        // Keep the forming bar's running CVD current so the footer reads right before it rolls.
        _currentBar.CumulativeDelta = SessionDelta;

        Status = $"{SelectedInstrument?.DisplayName} · {Bars.Count} bars · {TradesSeen} trades · CVD {SessionDelta:+#;-#;0}";
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearBars()
    {
        Bars.Clear();
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

    public void Dispose() => StopStream();
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
