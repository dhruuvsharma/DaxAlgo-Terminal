using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.Rsi;

public sealed partial class RsiStrategyViewModel : ViewModelBase, IDisposable
{
    public const int MaxBarsRetained = 300;
    public const int RsiPeriod = RsiCalculator.DefaultPeriod;

    /// <summary>Cap on instruments shown in the picker; the search box narrows the full universe.</summary>
    public const int MaxInstrumentsDisplayed = 500;

    private readonly LiveStrategyHostServices _services;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<RsiStrategyViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _ingestHandle;

    private enum RsiZone { Neutral, Overbought, Oversold }
    private RsiZone _lastZone = RsiZone.Neutral;

    public RsiStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        ILogger<RsiStrategyViewModel> logger)
    {
        _services = services;
        _notifications = notifications;
        _logger = logger;

        AllInstruments = InstrumentCatalog.All;
        Instruments = new ObservableCollection<TradeableInstrument>(
            AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        Bars = new ObservableCollection<Bar>();
        RsiSeries = Array.Empty<double>();

        // Swap the static catalog for the connected broker's tradable universe.
        _ = LoadInstrumentsAsync();
    }

    /// <summary>Full tradable universe from the connected broker (or the static fallback);
    /// <see cref="InstrumentSearchText"/> filters this into <see cref="Instruments"/>.</summary>
    public IReadOnlyList<TradeableInstrument> AllInstruments { get; private set; }

    /// <summary>Instruments shown in the picker — a capped, search-filtered view of <see cref="AllInstruments"/>.</summary>
    [ObservableProperty]
    private ObservableCollection<TradeableInstrument> _instruments = new();

    /// <summary>Free-text filter applied over <see cref="AllInstruments"/>.</summary>
    [ObservableProperty]
    private string _instrumentSearchText = string.Empty;

    public ObservableCollection<Bar> Bars { get; }

    public double[] RsiSeries { get; private set; }

    [ObservableProperty]
    private TradeableInstrument? _selectedInstrument;

    [ObservableProperty]
    private double _overbought = RsiCalculator.DefaultOverbought;

    [ObservableProperty]
    private double _oversold = RsiCalculator.DefaultOversold;

    /// <summary>True once the user clicks Continue on the setup form.</summary>
    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private string _status = "Configure the strategy to begin.";

    [ObservableProperty]
    private double? _lastPrice;

    [ObservableProperty]
    private double? _lastRsi;

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>True only while the user has armed the algo via the Run button. Display-only otherwise.</summary>
    [ObservableProperty]
    private bool _isAlgoRunning;

    [ObservableProperty]
    private string? _validationError;

    public event EventHandler? BarsChanged;

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _services.Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;

            AllInstruments = list
                .Select(i => new TradeableInstrument(
                    $"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}",
                    i.Category,
                    i.Contract,
                    i.Broker))
                .ToList();

            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "AAPL")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RSI instrument list load failed; using static catalog");
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
        SelectedInstrument = keep is not null && Instruments.Contains(keep)
            ? keep
            : Instruments.FirstOrDefault();
    }

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;

        if (SelectedInstrument is null)
        {
            ValidationError = "Pick an instrument before continuing.";
            return;
        }

        if (Overbought <= Oversold)
        {
            ValidationError = "Overbought must be greater than oversold.";
            return;
        }

        if (Overbought is < 0 or > 100 || Oversold is < 0 or > 100)
        {
            ValidationError = "Thresholds must be between 0 and 100.";
            return;
        }

        IsConfigured = true;
        _ = StartStreamAsync(CancellationToken.None);
    }

    /// <summary>Toggle the algo. While armed, signals are acted upon; while not, this is display-only.</summary>
    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        _logger.LogInformation("RSI algo {State} for {Symbol}", IsAlgoRunning ? "ARMED" : "STOPPED", label);
        Status = IsAlgoRunning
            ? $"Algo running on {label} (OB {Overbought:F0} / OS {Oversold:F0})"
            : $"Streaming {label} — algo idle";
    }

    public async Task StartStreamAsync(CancellationToken ct)
    {
        if (IsStreaming) return;
        if (SelectedInstrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(SelectedInstrument); }
        catch (InvalidOperationException ex) { ValidationError = ex.Message; Status = ex.Message; return; }

        var contract = SelectedInstrument.Contract;
        var size = BarSize.OneMinute;

        Status = $"Loading {SelectedInstrument.DisplayName} history…";
        Bars.Clear();
        RsiSeries = Array.Empty<double>();
        BarsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var historical = await _services.Repository.GetHistoricalBarsAsync(contract, broker, size,
                TimeSpan.FromDays(1), ct);
            foreach (var b in historical.TakeLast(MaxBarsRetained))
                Bars.Add(b);

            RecalculateRsi();
            BarsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSI historical backfill failed");
            Status = $"History fetch failed: {ex.Message}";
            return;
        }

        _streamCts = new CancellationTokenSource();
        var streamCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _streamCts.Token).Token;

        IsStreaming = true;
        Status = $"Streaming {SelectedInstrument.DisplayName} — algo idle";

        _ = RunStreamAsync(contract, broker, size, streamCt);
    }

    private async Task RunStreamAsync(Contract contract, BrokerKind broker, BarSize size, CancellationToken ct)
    {
        // Canonical pipeline: resolve id, observe hub.Bars(id), and start (or join) the ref-counted
        // ingest pump that feeds it. A bounded channel decouples the hub publish thread from the
        // consumer; each bar is marshalled to the UI thread before mutating observable state.
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        var channel = Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var subscription = _services.Hub.Bars(instrumentId, size).Subscribe(b =>
            channel.Writer.TryWrite(b.ToBar()));
        _ingestHandle = _services.Ingest.SubscribeBars(contract, broker, size);

        try
        {
            await foreach (var bar in channel.Reader.ReadAllAsync(ct))
            {
                await UiThread.RunAsync(() =>
                {
                    AppendBar(bar);
                    EvaluateSignal();
                    BarsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RSI stream ended");
            await UiThread.RunAsync(() => Status = $"Stream stopped: {ex.Message}");
        }
        finally
        {
            channel.Writer.TryComplete();
            await UiThread.RunAsync(() => IsStreaming = false);
        }
    }

    public void AppendBar(Bar bar)
    {
        Bars.Add(bar);
        while (Bars.Count > MaxBarsRetained) Bars.RemoveAt(0);
        LastPrice = bar.Close;
        RecalculateRsi();
    }

    private void RecalculateRsi()
    {
        RsiSeries = RsiCalculator.Compute(Bars, RsiPeriod);
        var last = RsiSeries.LastOrDefault();
        LastRsi = double.IsNaN(last) ? null : last;
    }

    private void EvaluateSignal()
    {
        if (LastRsi is not { } rsi) return;
        if (!IsAlgoRunning) return;

        var zone = rsi >= Overbought ? RsiZone.Overbought
                 : rsi <= Oversold   ? RsiZone.Oversold
                                     : RsiZone.Neutral;

        if (zone == _lastZone) return;
        _lastZone = zone;
        if (zone == RsiZone.Neutral) return;

        var label = SelectedInstrument?.DisplayName ?? "(none)";
        var direction = zone == RsiZone.Overbought ? "SHORT" : "LONG";
        var phrase = zone == RsiZone.Overbought ? "OVERBOUGHT — would short" : "OVERSOLD — would long";

        _logger.LogInformation("[RSI ARMED] {Symbol} {Phrase} — RSI={Rsi:F2}", label, phrase, rsi);

        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: "rsi.overbought.oversold",
            StrategyName: "RSI",
            Symbol: label,
            Direction: direction,
            Message: $"{phrase}  (RSI={rsi:F2}, OB={Overbought:F0}, OS={Oversold:F0})",
            TimestampUtc: DateTime.UtcNow))
            .FireAndForgetSafe(_logger, "RSI signal publish");
    }

    public async Task StopStreamAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _ingestHandle?.Dispose();
        _ingestHandle = null;
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
        _ingestHandle?.Dispose();
        _ingestHandle = null;
    }
}
