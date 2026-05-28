using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

namespace TradingTerminal.Strategies.OrderFlowCube;

public sealed partial class OrderFlowCubeViewModel : ViewModelBase, IDisposable
{
    public const int MaxTrailPoints = 60;
    public const int MaxInstrumentsDisplayed = 500;
    public const int MaxLogEntries = 200;
    private const int TradeRateSummaryEveryN = 25;

    /// <summary>
    /// Static capability table — which brokers actually wire SubscribeTradesAsync today. The
    /// trade ingest layer swallows NotSupportedException at LogDebug level, so without this
    /// the UI sits at "Subscribing... trade tape" forever with Trades = 0. cTrader's protocol
    /// genuinely has no trade-tape channel; NT/Alpaca are unwired stubs (tracked in
    /// ideas.md / project memory).
    /// </summary>
    private static bool BrokerSupportsTradeTape(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => true,
        _ => false,
    };

    public sealed record LogEntry(DateTime At, string Level, string Message)
    {
        public string Display => $"{At:HH:mm:ss.fff}  {Level,-5}  {Message}";
    }

    private readonly LiveStrategyHostServices _services;
    private readonly INotificationPublisher _notifications;
    private readonly InMemoryLogSink _appLogSink;
    private readonly ILogger<OrderFlowCubeViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _quoteHandle;
    private IDisposable? _tradeHandle;
    private OrderFlowCubeCalculator? _calc;
    private QuoteDerivedTradeSynthesizer? _synthesizer;
    private bool _useSynthetic;
    private DateTime _streamStartUtc;
    private string? _symbolFilterToken;
    private bool _logSinkAttached;

    private enum CubeRegime { Neutral, Accumulation, Distribution }
    private CubeRegime _lastRegime = CubeRegime.Neutral;

    public OrderFlowCubeViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        InMemoryLogSink appLogSink,
        ILogger<OrderFlowCubeViewModel> logger)
    {
        _services = services;
        _notifications = notifications;
        _appLogSink = appLogSink;
        _logger = logger;

        AllInstruments = InstrumentCatalog.All;
        Instruments = new ObservableCollection<TradeableInstrument>(
            AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        TrailPoints = new ObservableCollection<CubePoint>();
        LogEntries = new ObservableCollection<LogEntry>();
        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<LogEntry> LogEntries { get; }

    private bool _firstTradeSeen;

    private void AddLog(string level, string message)
    {
        LogEntries.Add(new LogEntry(DateTime.Now, level, message));
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(0);
    }

    public IReadOnlyList<TradeableInstrument> AllInstruments { get; private set; }

    [ObservableProperty] private ObservableCollection<TradeableInstrument> _instruments = new();
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private TradeableInstrument? _selectedInstrument;

    // Calculator parameters (editable on the setup form).
    [ObservableProperty] private int _recentWindow = 50;
    [ObservableProperty] private int _trendWindow = 500;
    [ObservableProperty] private int _baselineWindow = 2000;
    [ObservableProperty] private double _cvdThreshold = 0.40;
    [ObservableProperty] private double _aggressorBuyThreshold = 0.60;
    [ObservableProperty] private double _sizeRatioThreshold = 1.20;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private string _status = "Configure the strategy to begin.";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isAlgoRunning;
    [ObservableProperty] private string? _validationError;

    // Live signals.
    [ObservableProperty] private double _currentCvd;
    [ObservableProperty] private double _currentAggressor = 0.5;
    [ObservableProperty] private double _currentSizeRatio = 1.0;
    [ObservableProperty] private long _tradesSeen;
    [ObservableProperty] private string _regimeLabel = "—";

    /// <summary>A trail point in cube phase space. Older points fade in the visualization.</summary>
    public sealed record CubePoint(double Aggressor, double Cvd, double SizeRatio, DateTime At);

    public ObservableCollection<CubePoint> TrailPoints { get; }

    public event EventHandler? TrailChanged;

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _services.Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            AllInstruments = list
                .Select(i => new TradeableInstrument(
                    $"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}",
                    i.Category, i.Contract, i.Broker))
                .ToList();
            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "ES")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order-flow cube instrument list load failed; using static catalog");
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

    private BrokerKind ResolveBroker(TradeableInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _services.Selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    private void ApplyInstrumentFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<TradeableInstrument> query = AllInstruments;
        if (term.Length > 0)
            query = AllInstruments.Where(i =>
                i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));
        var shown = query.Take(MaxInstrumentsDisplayed).ToList();
        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);
        Instruments = new ObservableCollection<TradeableInstrument>(shown);
        SelectedInstrument = keep is not null && Instruments.Contains(keep) ? keep : Instruments.FirstOrDefault();
    }

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;
        if (SelectedInstrument is null) { ValidationError = "Pick an instrument before continuing."; return; }
        if (RecentWindow < 5) { ValidationError = "Recent window must be at least 5 trades."; return; }
        if (TrendWindow < RecentWindow) { ValidationError = "Trend window must be ≥ recent window."; return; }
        if (BaselineWindow < TrendWindow) { ValidationError = "Baseline window must be ≥ trend window."; return; }
        if (CvdThreshold <= 0 || CvdThreshold >= 1) { ValidationError = "CVD threshold must be in (0, 1)."; return; }
        if (AggressorBuyThreshold <= 0.5 || AggressorBuyThreshold >= 1) { ValidationError = "Aggressor buy threshold must be in (0.5, 1)."; return; }
        if (SizeRatioThreshold <= 0) { ValidationError = "Size ratio threshold must be > 0."; return; }

        IsConfigured = true;
        _ = StartStreamAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        _logger.LogInformation("Order-flow cube {State} for {Symbol}", IsAlgoRunning ? "ARMED" : "STOPPED", label);
        Status = IsAlgoRunning ? $"Armed on {label}" : $"Streaming {label} — algo idle";
    }

    public async Task StartStreamAsync(CancellationToken ct)
    {
        if (IsStreaming || SelectedInstrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(SelectedInstrument); }
        catch (InvalidOperationException ex) { ValidationError = ex.Message; Status = ex.Message; AddLog("ERROR", ex.Message); return; }

        // Soft capability check — no native trade tape ⇒ auto-fallback to L1 quote-derived
        // synthetic trades. Same shape of signal at degraded fidelity (size axis approximated
        // by top-of-book size rather than real fill volume).
        _useSynthetic = !BrokerSupportsTradeTape(broker);
        if (_useSynthetic)
            AddLog("WARN", $"{BrokerLabel(broker)} has no native trade tape. Falling back to SYNTHETIC L1-derived trades (mid-up=Buy, mid-down=Sell, size=top-of-book). Signal degraded vs real tape.");

        var contract = SelectedInstrument.Contract;
        _calc = new OrderFlowCubeCalculator(RecentWindow, TrendWindow, BaselineWindow);
        _synthesizer = _useSynthetic ? new QuoteDerivedTradeSynthesizer() : null;
        TrailPoints.Clear();
        TradesSeen = 0;
        _firstTradeSeen = false;
        RegimeLabel = "—";
        var tapeLabel = _useSynthetic ? "SYNTHETIC L1-derived" : "real trade tape";
        Status = $"Subscribing {SelectedInstrument.DisplayName} ({BrokerLabel(broker)}) — {tapeLabel}…";
        AddLog("INFO", $"Subscribing {SelectedInstrument.DisplayName} on {BrokerLabel(broker)} [{tapeLabel}] (recent={RecentWindow}, trend={TrendWindow}, baseline={BaselineWindow})");

        // Mirror Serilog pipeline events for THIS symbol into the strategy log panel so the user
        // sees broker-side failures (IB error 354/10090, ingest pump exceptions, delayed-data
        // warnings, etc.) without leaving the strategy window.
        _streamStartUtc = DateTime.UtcNow;
        _symbolFilterToken = contract.Symbol;
        if (!_logSinkAttached)
        {
            ((INotifyCollectionChanged)_appLogSink.Entries).CollectionChanged += OnAppLogChanged;
            _logSinkAttached = true;
        }

        _streamCts = new CancellationTokenSource();
        var streamCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _streamCts.Token).Token;
        IsStreaming = true;
        _ = RunStreamAsync(contract, broker, streamCt);
        _ = WatchNoTradesAsync(streamCt);
        await Task.CompletedTask;
    }

    private void OnAppLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
        foreach (TradingTerminal.UI.Logging.LogEntry item in e.NewItems)
        {
            if (item.TimestampUtc < _streamStartUtc) continue;
            var msg = item.Message ?? string.Empty;
            var level = item.Level ?? "Info";
            var isErrorish = level.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                          || level.Contains("Error", StringComparison.OrdinalIgnoreCase)
                          || level.Contains("Fatal", StringComparison.OrdinalIgnoreCase);
            var matchesSymbol = !string.IsNullOrEmpty(_symbolFilterToken)
                             && msg.Contains(_symbolFilterToken, StringComparison.OrdinalIgnoreCase);
            var matchesKeyword =
                msg.Contains("IB market-data", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("Trade ingest", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("tick by tick", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("tickByTick", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("IB error", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("reqTickByTickData", StringComparison.OrdinalIgnoreCase);
            if (!isErrorish && !matchesSymbol && !matchesKeyword) continue;
            AddLog($"SYS:{level}", msg);

            if (msg.Contains("Delayed", StringComparison.OrdinalIgnoreCase) && msg.Contains("market-data", StringComparison.OrdinalIgnoreCase))
                AddLog("WARN", "IB is in DELAYED data mode — reqTickByTickData needs LIVE data. Trade tape will be empty until InteractiveBrokers:MarketDataType is 1 and the account has live data.");
        }
    }

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
                    AddLog("WARN", "  • Mid price isn't moving — flat quotes produce no synthetic events");
                    AddLog("WARN", "  • Broker quote stream isn't flowing — check SYS log entries above");
                }
                else
                {
                    AddLog("WARN", "  • Market closed (US equities/futures regular session: 09:30–16:00 ET)");
                    AddLog("WARN", "  • IB on DELAYED data — reqTickByTickData needs LIVE; set InteractiveBrokers:MarketDataType=1");
                    AddLog("WARN", "  • No market-data permissions for this instrument's exchange (IB error 10189) — subscribe in IB Account Management");
                    AddLog("WARN", "  • Instrument illiquid right now — try SPY/ES/AAPL during US hours");
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunStreamAsync(Contract contract, BrokerKind broker, CancellationToken ct)
    {
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        await UiThread.RunAsync(() => AddLog("WIRE", $"Resolved {contract.Symbol} → InstrumentId={instrumentId.Value}"));

        // Quote pump always runs — needed for Lee-Ready bid/ask context in real mode, and is
        // the primary source for the synthesizer in synthetic mode.
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

        var channel = Channel.CreateUnbounded<TradePrint>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
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

        try
        {
            await foreach (var trade in channel.Reader.ReadAllAsync(ct))
            {
                await UiThread.RunAsync(() => OnTrade(trade));
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order-flow cube stream ended");
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

    private void OnTrade(TradePrint trade)
    {
        if (_calc is null) return;
        _calc.Add(trade);
        TradesSeen++;

        if (!_firstTradeSeen)
        {
            _firstTradeSeen = true;
            AddLog("INFO", $"First trade received: {trade.Aggressor} {trade.Size} @ {trade.Price} ({trade.Source})");
        }
        else if (TradesSeen % TradeRateSummaryEveryN == 0)
        {
            var side = trade.Aggressor.ToString().ToUpperInvariant();
            AddLog("DATA", $"#{TradesSeen,5}: {side,-7} {trade.Size,6} @ {trade.Price,10:F4}");
        }

        if (!_calc.IsWarm)
        {
            Status = $"Warming up… {TradesSeen} trades seen";
            return;
        }

        CurrentCvd = _calc.CvdImbalance;
        CurrentAggressor = _calc.AggressorRatio;
        CurrentSizeRatio = _calc.SizeRatio;

        TrailPoints.Add(new CubePoint(CurrentAggressor, CurrentCvd, CurrentSizeRatio, trade.EventTimeUtc));
        while (TrailPoints.Count > MaxTrailPoints) TrailPoints.RemoveAt(0);

        EvaluateRegime();
        TrailChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EvaluateRegime()
    {
        var regime = (CurrentCvd, CurrentAggressor, CurrentSizeRatio) switch
        {
            var (c, a, s) when c >= CvdThreshold && a >= AggressorBuyThreshold && s >= SizeRatioThreshold
                => CubeRegime.Accumulation,
            var (c, a, s) when c <= -CvdThreshold && a <= 1 - AggressorBuyThreshold && s >= SizeRatioThreshold
                => CubeRegime.Distribution,
            _ => CubeRegime.Neutral,
        };

        RegimeLabel = regime switch
        {
            CubeRegime.Accumulation => "ACCUMULATION",
            CubeRegime.Distribution => "DISTRIBUTION",
            _ => "neutral",
        };
        Status = IsAlgoRunning
            ? $"Armed on {SelectedInstrument?.DisplayName} — {RegimeLabel}"
            : $"Streaming — {RegimeLabel}";

        if (regime == _lastRegime) return;
        _lastRegime = regime;
        AddLog("REGIME", $"{regime}  CVD={CurrentCvd:F2}  Agg={CurrentAggressor:F2}  Size×={CurrentSizeRatio:F2}");
        if (regime == CubeRegime.Neutral) return;
        if (!IsAlgoRunning) return;

        var label = SelectedInstrument?.DisplayName ?? "(none)";
        var direction = regime == CubeRegime.Accumulation ? "LONG" : "SHORT";
        _logger.LogInformation("[CUBE ARMED] {Symbol} {Regime} CVD={Cvd:F2} Agg={Agg:F2} Size={Size:F2}",
            label, regime, CurrentCvd, CurrentAggressor, CurrentSizeRatio);
        AddLog("SIGNAL", $"{direction} — {regime}  on {label}");

        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: "orderflow.cube",
            StrategyName: "Order Flow Cube",
            Symbol: label,
            Direction: direction,
            Message: $"{regime} — CVD={CurrentCvd:F2}, Aggressor={CurrentAggressor:F2}, Size={CurrentSizeRatio:F2}x",
            TimestampUtc: DateTime.UtcNow))
            .FireAndForgetSafe(_logger, "Order-flow cube signal publish");
    }

    public async Task StopStreamAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _quoteHandle?.Dispose(); _quoteHandle = null;
        _tradeHandle?.Dispose(); _tradeHandle = null;
        DetachAppLogSink();
        IsStreaming = false;
        IsAlgoRunning = false;
        Status = "Stopped";
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _quoteHandle?.Dispose(); _quoteHandle = null;
        _tradeHandle?.Dispose(); _tradeHandle = null;
        DetachAppLogSink();
    }

    private void DetachAppLogSink()
    {
        if (!_logSinkAttached) return;
        ((INotifyCollectionChanged)_appLogSink.Entries).CollectionChanged -= OnAppLogChanged;
        _logSinkAttached = false;
    }
}
