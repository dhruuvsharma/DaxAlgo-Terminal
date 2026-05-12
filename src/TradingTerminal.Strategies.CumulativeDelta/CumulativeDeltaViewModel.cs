using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.CumulativeDelta;

/// <summary>
/// View-model for the Cumulative Delta Scalper — sniper-mode port of the cTrader cBot at
/// platforms/cTrader/CumulativeDeltaScalper/src/CumulativeDeltaScalper.cs.
///
/// Tick-level uptick/downtick delta is summed across a sliding window of N candles.
/// Entry on cumulative-delta crossover of ±DeltaThreshold, gated by up to five confirmations:
///   1. Momentum alignment — last 3 bar-deltas share direction.
///   2. HTF EMA(50) on 15m — bid above EMA for longs, below for shorts.
///   3. EMA slope over <see cref="EmaSlopeBars"/> 15m bars matches signal direction.
///   4. ADX(14) on 15m ≥ <see cref="AdxThreshold"/> (trending regime).
///   5. Spread inside both rolling-avg multiplier and a hard cap.
/// Multi-window session filter (Asia / London / NewYork / Overlap, GMT). Per-session and
/// daily caps with an inter-signal cooldown.
///
/// Display-only — no orders, no SL/TP, no lot sizing. Signals appear in the dashboard, the
/// log, and the notifier.
/// </summary>
public sealed partial class CumulativeDeltaViewModel : ViewModelBase, IDisposable
{
    public const int MaxBarsRetained = 300;
    public const int MaxWindowSize = 50;
    public const int MaxSpreadHistorySize = 200;

    private readonly IMarketDataRepository _repository;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<CumulativeDeltaViewModel> _logger;

    private CancellationTokenSource? _cts;
    private double _prevBid;
    private bool _prevBidInitialised;

    // Tick-level uptick/downtick accounting for the in-progress bar.
    private int _uptickCount;
    private int _downtickCount;

    // Closed-bar circular buffer.
    private readonly int[] _circularBuffer = new int[MaxWindowSize];
    private int _bufferIndex;
    private int _bufferFilled;
    private int _previousCumDelta;

    // Spread-history circular buffer (per-bar samples in price units).
    private readonly double[] _spreadHistory = new double[MaxSpreadHistorySize];
    private int _spreadHistoryIdx;
    private int _spreadHistoryFilled;

    // Per-session bookkeeping.
    private SessionId _currentSession = SessionId.None;
    private int _sessionSignalCount;

    // Daily / cooldown bookkeeping.
    private DateTime _lastSignalDayUtc;
    private DateTime _lastSignalTimeUtc = DateTime.MinValue;

    public CumulativeDeltaViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        ILogger<CumulativeDeltaViewModel> logger)
    {
        _repository = repository;
        _notifications = notifications;
        _logger = logger;

        Instruments = InstrumentCatalog.All;
        SelectedInstrument = Instruments.FirstOrDefault(i => i.DisplayName.StartsWith("EUR.USD"))
                             ?? Instruments[0];

        TimeframeOptions = new[]
        {
            BarSize.OneMinute,
            BarSize.ThreeMinutes,
            BarSize.FiveMinutes,
            BarSize.FifteenMinutes,
        };
        SelectedTimeframe = BarSize.ThreeMinutes;

        Bars = new ObservableCollection<Bar>();
        BarDeltas = new ObservableCollection<int>();
        RecentSignals = new ObservableCollection<string>();
    }

    // ---------- Setup form bindings ----------

    public IReadOnlyList<TradeableInstrument> Instruments { get; }
    public IReadOnlyList<BarSize> TimeframeOptions { get; }

    [ObservableProperty] private TradeableInstrument? _selectedInstrument;
    [ObservableProperty] private BarSize _selectedTimeframe;

    [ObservableProperty] private int _windowSize = 10;
    [ObservableProperty] private int _deltaThreshold = 300;

    // Sniper filters
    [ObservableProperty] private int _minConfirmations = 5;
    [ObservableProperty] private int _emaSlopeBars = 3;
    [ObservableProperty] private double _adxThreshold = 18.0;
    [ObservableProperty] private double _spreadAvgMultiplier = 1.5;
    [ObservableProperty] private int _spreadHistorySize = 30;
    [ObservableProperty] private double _maxSpread = 0.00015; // price units; ~15 points on EURUSD

    // Volatility gate
    [ObservableProperty] private double _minAtr = 0.00030;
    [ObservableProperty] private double _maxAtr = 0.00200;

    // Session windows (GMT minute-of-day boundaries; users edit hours via the form)
    [ObservableProperty] private bool _useSessionFilter = true;
    [ObservableProperty] private bool _overlapOnly = true;

    [ObservableProperty] private int _overlapStartHour = 12;
    [ObservableProperty] private int _overlapStartMin  = 30;
    [ObservableProperty] private int _overlapEndHour   = 16;
    [ObservableProperty] private int _overlapEndMin    = 0;

    [ObservableProperty] private bool _useAsiaSession   = false;
    [ObservableProperty] private int _asiaStartHour = 0;
    [ObservableProperty] private int _asiaEndHour   = 7;

    [ObservableProperty] private bool _useLondonSession = true;
    [ObservableProperty] private int _londonStartHour = 7;
    [ObservableProperty] private int _londonEndHour   = 12;

    [ObservableProperty] private bool _useNewYorkSession = true;
    [ObservableProperty] private int _newYorkStartHour = 12;
    [ObservableProperty] private int _newYorkStartMin  = 30;
    [ObservableProperty] private int _newYorkEndHour   = 17;

    [ObservableProperty] private bool _useHtfFilter = true;
    [ObservableProperty] private int _maxSignalsPerSession = 2;
    [ObservableProperty] private int _maxDailySignals = 3;
    [ObservableProperty] private int _minSecondsBetweenSignals = 900;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private string? _validationError;

    // ---------- Runtime bindings ----------

    public ObservableCollection<Bar> Bars { get; }
    public ObservableCollection<int> BarDeltas { get; }
    public ObservableCollection<string> RecentSignals { get; }

    [ObservableProperty] private string _status = "Configure the strategy to begin.";
    [ObservableProperty] private string _guardReason = "";

    [ObservableProperty] private double? _lastBid;
    [ObservableProperty] private double? _lastAsk;
    [ObservableProperty] private double? _lastSpread;
    [ObservableProperty] private double? _avgSpread;
    [ObservableProperty] private int _liveDelta;
    [ObservableProperty] private int _cumulativeDelta;
    [ObservableProperty] private double? _lastAtr;
    [ObservableProperty] private double? _lastEmaHtf;
    [ObservableProperty] private double? _lastAdx;
    [ObservableProperty] private SessionId _activeSession = SessionId.None;
    [ObservableProperty] private int _lastConfirmationScore;

    [ObservableProperty] private int _todaySignalCount;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isAlgoRunning;

    public event EventHandler? BarsChanged;
    public event EventHandler? DeltasChanged;

    // 15m bar cache used by EMA + slope + ADX.
    private readonly List<Bar> _htfBars = new();

    // ---------- Setup → start ----------

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;

        if (SelectedInstrument is null)
            { ValidationError = "Pick an instrument before continuing."; return; }
        if (WindowSize is < 2 or > MaxWindowSize)
            { ValidationError = $"Window size must be between 2 and {MaxWindowSize}."; return; }
        if (DeltaThreshold <= 0)
            { ValidationError = "Delta threshold must be positive."; return; }
        if (MinConfirmations is < 0 or > 5)
            { ValidationError = "Min confirmations must be between 0 and 5."; return; }
        if (SpreadHistorySize is < 1 or > MaxSpreadHistorySize)
            { ValidationError = $"Spread history size must be between 1 and {MaxSpreadHistorySize}."; return; }
        if (MinAtr < 0 || MaxAtr <= MinAtr)
            { ValidationError = "Max ATR must be greater than Min ATR (and both ≥ 0)."; return; }

        IsConfigured = true;
        _ = StartStreamAsync();
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        _logger.LogInformation("CumulativeDelta sniper {State} for {Symbol}",
            IsAlgoRunning ? "ARMED" : "STOPPED", label);
        Status = IsAlgoRunning
            ? $"Sniper armed on {label} — minConf {MinConfirmations}/5"
            : $"Streaming {label} — sniper idle";
    }

    public async Task StartStreamAsync()
    {
        if (IsStreaming || SelectedInstrument is null) return;

        var contract = SelectedInstrument.Contract;
        var chartTf = SelectedTimeframe;

        Bars.Clear();
        BarDeltas.Clear();
        ResetState();
        BarsChanged?.Invoke(this, EventArgs.Empty);
        DeltasChanged?.Invoke(this, EventArgs.Empty);

        Status = $"Loading {SelectedInstrument.DisplayName} history…";

        try
        {
            var history = await _repository.GetHistoricalBarsAsync(contract, chartTf, TimeSpan.FromDays(1));
            foreach (var b in history.TakeLast(MaxBarsRetained))
                Bars.Add(b);
            RecalculateAtr();
            BarsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CumulativeDelta history backfill failed");
            Status = $"History fetch failed: {ex.Message}";
            return;
        }

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        Status = $"Streaming {SelectedInstrument.DisplayName} — sniper idle";

        _ = RunTicksAsync(contract, _cts.Token);
        _ = RunChartBarsAsync(contract, chartTf, _cts.Token);
        _ = RunHtfBarsAsync(contract, _cts.Token);
    }

    public async Task StopStreamAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsStreaming = false;
        IsAlgoRunning = false;
        Status = "Stopped";
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ---------- Tick stream (bid-tick rule) ----------

    private async Task RunTicksAsync(Contract contract, CancellationToken ct)
    {
        try
        {
            await foreach (var tick in _repository.SubscribeTicksAsync(contract, ct))
                ProcessTick(tick);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tick stream ended");
            Status = $"Tick stream stopped: {ex.Message}";
        }
    }

    private void ProcessTick(Tick tick)
    {
        LastBid = tick.Bid;
        LastAsk = tick.Ask;
        LastSpread = tick.Ask - tick.Bid;

        if (!_prevBidInitialised)
        {
            _prevBid = tick.Bid;
            _prevBidInitialised = true;
            return;
        }

        if (tick.Bid > _prevBid)      _uptickCount++;
        else if (tick.Bid < _prevBid) _downtickCount++;
        _prevBid = tick.Bid;
        LiveDelta = _uptickCount - _downtickCount;
    }

    // ---------- Chart-TF bar stream: finalise bars, drive signals ----------

    private async Task RunChartBarsAsync(Contract contract, BarSize chartTf, CancellationToken ct)
    {
        try
        {
            await foreach (var bar in _repository.SubscribeBarsAsync(contract, chartTf, ct))
            {
                AppendBar(bar);
                OnBarClosed();
                BarsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chart bar stream ended");
            Status = $"Bar stream stopped: {ex.Message}";
        }
        finally { IsStreaming = false; }
    }

    private void AppendBar(Bar bar)
    {
        Bars.Add(bar);
        while (Bars.Count > MaxBarsRetained) Bars.RemoveAt(0);
        RecalculateAtr();
    }

    private void OnBarClosed()
    {
        var w = Math.Clamp(WindowSize, 2, MaxWindowSize);
        var candleDelta = _uptickCount - _downtickCount;

        _circularBuffer[_bufferIndex] = candleDelta;
        _bufferIndex = (_bufferIndex + 1) % w;
        if (_bufferFilled < w) _bufferFilled++;

        BarDeltas.Clear();
        var start = (_bufferFilled >= w) ? _bufferIndex : 0;
        for (var i = 0; i < _bufferFilled; i++)
            BarDeltas.Add(_circularBuffer[(start + i) % w]);
        DeltasChanged?.Invoke(this, EventArgs.Empty);

        var cumDelta = SumWindow(w);
        CumulativeDelta = cumDelta;

        // Sample once per bar.
        if (LastSpread is { } s)
        {
            _spreadHistory[_spreadHistoryIdx] = s;
            _spreadHistoryIdx = (_spreadHistoryIdx + 1) % Math.Max(1, SpreadHistorySize);
            if (_spreadHistoryFilled < SpreadHistorySize) _spreadHistoryFilled++;
            AvgSpread = AverageSpread();
        }

        _uptickCount = 0;
        _downtickCount = 0;
        LiveDelta = 0;

        UpdateSessionState();
        ResetDailyCountersIfNeeded();

        if (_bufferFilled < w)
        {
            _previousCumDelta = cumDelta;
            return;
        }

        var crossover = ClassifyCrossover(cumDelta, _previousCumDelta);
        _previousCumDelta = cumDelta;

        if (crossover == 0) return;

        EvaluateSignal(crossover, cumDelta);
    }

    private void EvaluateSignal(int crossover, int cumDelta)
    {
        var symbol = SelectedInstrument?.DisplayName ?? "(none)";
        var direction = DirectionLabel(crossover);

        if (!CheckPreSignalGuards(out var reason))
        {
            GuardReason = reason;
            return;
        }

        var conf = CountConfirmations(crossover);
        LastConfirmationScore = conf;

        if (conf < MinConfirmations)
        {
            GuardReason = $"CONF {conf}/5 (need {MinConfirmations})";
            // Record a low-conf attempt as an idle signal so the user can tune the bar.
            PushSignalLine($"{DateTime.Now:HH:mm:ss}  (idle)  {direction} cumΔ={cumDelta} conf={conf}/5");
            _notifications.PublishAsync(new StrategyNotification(
                Kind: NotificationKind.IdleSignal,
                StrategyId: "cumulative.delta.scalper",
                StrategyName: "Cumulative Delta",
                Symbol: symbol,
                Direction: direction,
                Message: $"(idle) {direction} cumΔ={cumDelta} conf={conf}/5",
                TimestampUtc: DateTime.UtcNow))
                .FireAndForgetSafe(_logger, "cumdelta low-conf idle");
            return;
        }

        GuardReason = $"ACTIVE {_currentSession}";

        if (!IsAlgoRunning)
        {
            PushSignalLine($"{DateTime.Now:HH:mm:ss}  (idle)  {direction} cumΔ={cumDelta} conf={conf}/5");
            _notifications.PublishAsync(new StrategyNotification(
                Kind: NotificationKind.IdleSignal,
                StrategyId: "cumulative.delta.scalper",
                StrategyName: "Cumulative Delta",
                Symbol: symbol,
                Direction: direction,
                Message: $"(idle) {direction} cumΔ={cumDelta} conf={conf}/5",
                TimestampUtc: DateTime.UtcNow))
                .FireAndForgetSafe(_logger, "cumdelta unarmed idle");
            return;
        }

        TodaySignalCount++;
        _sessionSignalCount++;
        _lastSignalTimeUtc = DateTime.UtcNow;

        var line = $"{DateTime.Now:HH:mm:ss}  ARMED  {direction} cumΔ={cumDelta} conf={conf}/5 sess={_currentSession}";
        PushSignalLine(line);
        _logger.LogInformation("[CumulativeDelta] SNIPER {Direction} cumDelta={CumDelta} conf={Conf}/5 session={Session}",
            direction, cumDelta, conf, _currentSession);

        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: "cumulative.delta.scalper",
            StrategyName: "Cumulative Delta",
            Symbol: symbol,
            Direction: direction,
            Message: $"SNIPER {direction}  cumΔ={cumDelta}  conf={conf}/5  session={_currentSession}",
            TimestampUtc: DateTime.UtcNow))
            .FireAndForgetSafe(_logger, "cumdelta sniper signal");
    }

    // ---------- Confirmations ----------

    private int CountConfirmations(int signal)
    {
        var c = 0;
        if (CheckMomentumAlignment(signal)) c++;
        if (CheckHtfEma(signal))            c++;
        if (CheckEmaSlope(signal))          c++;
        if (CheckAdxTrending())             c++;
        if (CheckSpread())                  c++;
        return c;
    }

    private bool CheckMomentumAlignment(int signal)
    {
        if (BarDeltas.Count < 3) return false;
        for (var i = BarDeltas.Count - 3; i < BarDeltas.Count; i++)
        {
            if (signal > 0 && BarDeltas[i] <= 0) return false;
            if (signal < 0 && BarDeltas[i] >= 0) return false;
        }
        return true;
    }

    private bool CheckHtfEma(int signal)
    {
        if (!UseHtfFilter) return true;
        if (LastEmaHtf is not { } ema || LastBid is not { } bid || ema == 0) return false;
        return signal > 0 ? bid > ema : bid < ema;
    }

    private bool CheckEmaSlope(int signal)
    {
        var slopeBars = Math.Max(EmaSlopeBars, 1);
        if (_htfBars.Count < slopeBars + 50) return false;

        var emaNow    = Indicators.Ema(_htfBars, 50);
        var emaBefore = Indicators.Ema(_htfBars.Take(_htfBars.Count - slopeBars).ToList(), 50);
        if (double.IsNaN(emaNow) || double.IsNaN(emaBefore)) return false;

        var slope = emaNow > emaBefore ? 1 : (emaNow < emaBefore ? -1 : 0);
        return signal > 0 ? slope > 0 : (signal < 0 && slope < 0);
    }

    private bool CheckAdxTrending() =>
        LastAdx is { } adx && adx >= AdxThreshold;

    private bool CheckSpread()
    {
        if (LastSpread is not { } cur) return false;
        if (cur > MaxSpread) return false;
        var avg = AvgSpread ?? 0;
        if (avg <= 0) return true; // bootstrap
        return cur <= avg * SpreadAvgMultiplier;
    }

    // ---------- Pre-signal guards (cheap, run before confirmation count) ----------

    private bool CheckPreSignalGuards(out string reason)
    {
        if (UseSessionFilter && _currentSession == SessionId.None)
            { reason = "SESSION CLOSED"; return false; }

        if (LastSpread is { } s && s > MaxSpread)
            { reason = $"SPREAD HIGH ({s:F5}>{MaxSpread:F5})"; return false; }

        if (TodaySignalCount >= MaxDailySignals)
            { reason = $"DAILY LIMIT ({TodaySignalCount}/{MaxDailySignals})"; return false; }

        if (_sessionSignalCount >= MaxSignalsPerSession)
            { reason = $"SESSION LIMIT ({_sessionSignalCount}/{MaxSignalsPerSession})"; return false; }

        var now = DateTime.UtcNow;
        if (_lastSignalTimeUtc != DateTime.MinValue
            && now < _lastSignalTimeUtc.AddSeconds(MinSecondsBetweenSignals))
        {
            var left = (_lastSignalTimeUtc.AddSeconds(MinSecondsBetweenSignals) - now).TotalSeconds;
            reason = $"COOLDOWN ({left:F0}s left)"; return false;
        }

        if (LastAtr is { } atr)
        {
            if (atr < MinAtr) { reason = $"ATR LOW ({atr:F5})"; return false; }
            if (atr > MaxAtr) { reason = $"ATR HIGH ({atr:F5})"; return false; }
        }
        else
        {
            reason = "ATR UNAVAILABLE"; return false;
        }

        reason = $"ACTIVE {_currentSession}";
        return true;
    }

    private int ClassifyCrossover(int cumDelta, int previousCumDelta)
    {
        if (previousCumDelta <=  DeltaThreshold && cumDelta >  DeltaThreshold) return  1;
        if (previousCumDelta >= -DeltaThreshold && cumDelta < -DeltaThreshold) return -1;
        return 0;
    }

    // ---------- HTF (15m) bar stream: EMA(50) + ADX(14) ----------

    private async Task RunHtfBarsAsync(Contract contract, CancellationToken ct)
    {
        try
        {
            var hist = await _repository.GetHistoricalBarsAsync(contract, BarSize.FifteenMinutes,
                TimeSpan.FromDays(2), ct);
            _htfBars.Clear();
            _htfBars.AddRange(hist);
            RecalculateHtfIndicators();

            await foreach (var bar in _repository.SubscribeBarsAsync(contract, BarSize.FifteenMinutes, ct))
            {
                _htfBars.Add(bar);
                if (_htfBars.Count > 400) _htfBars.RemoveAt(0);
                RecalculateHtfIndicators();
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTF bar stream ended");
        }
    }

    private void RecalculateHtfIndicators()
    {
        var ema = Indicators.Ema(_htfBars, 50);
        LastEmaHtf = double.IsNaN(ema) ? null : ema;

        var adx = Indicators.Adx(_htfBars, 14);
        LastAdx = double.IsNaN(adx) ? null : adx;
    }

    private void RecalculateAtr()
    {
        var atr = Indicators.Atr(Bars, 14);
        LastAtr = double.IsNaN(atr) ? null : atr;
    }

    // ---------- Sessions ----------

    private void UpdateSessionState()
    {
        var s = GetCurrentSession();
        if (s != _currentSession)
        {
            _currentSession = s;
            ActiveSession = s;
            _sessionSignalCount = 0;
            _logger.LogInformation("[CumulativeDelta] Session → {Session}", s);
        }
    }

    private SessionId GetCurrentSession()
    {
        var t = MinutesOfDayUtc(DateTime.UtcNow);
        if (OverlapOnly)
        {
            return InRange(t, OverlapStartHour, OverlapStartMin, OverlapEndHour, OverlapEndMin)
                ? SessionId.Overlap : SessionId.None;
        }
        if (UseAsiaSession    && InRange(t, AsiaStartHour,     0, AsiaEndHour,    0)) return SessionId.Asia;
        if (UseLondonSession  && InRange(t, LondonStartHour,   0, LondonEndHour,  0)) return SessionId.London;
        if (UseNewYorkSession && InRange(t, NewYorkStartHour, NewYorkStartMin, NewYorkEndHour, 0)) return SessionId.NewYork;
        return SessionId.None;
    }

    private static int MinutesOfDayUtc(DateTime utc) => utc.Hour * 60 + utc.Minute;

    private static bool InRange(int totalMinutes, int sH, int sM, int eH, int eM)
    {
        var start = sH * 60 + sM;
        var end   = eH * 60 + eM;
        return totalMinutes >= start && totalMinutes < end;
    }

    private void ResetDailyCountersIfNeeded()
    {
        var todayUtc = DateTime.UtcNow.Date;
        if (todayUtc != _lastSignalDayUtc)
        {
            _lastSignalDayUtc = todayUtc;
            TodaySignalCount = 0;
        }
    }

    // ---------- Helpers ----------

    private double AverageSpread()
    {
        if (_spreadHistoryFilled <= 0) return 0;
        var sum = 0.0;
        for (var i = 0; i < _spreadHistoryFilled; i++) sum += _spreadHistory[i];
        return sum / _spreadHistoryFilled;
    }

    private int SumWindow(int w)
    {
        var sum = 0;
        var n = Math.Min(_bufferFilled, w);
        for (var i = 0; i < n; i++) sum += _circularBuffer[i];
        return sum;
    }

    private static string DirectionLabel(int signal) => signal > 0 ? "LONG" : "SHORT";

    private void PushSignalLine(string line)
    {
        RecentSignals.Insert(0, line);
        while (RecentSignals.Count > 12) RecentSignals.RemoveAt(RecentSignals.Count - 1);
    }

    private void ResetState()
    {
        Array.Clear(_circularBuffer);
        Array.Clear(_spreadHistory);
        _bufferIndex = 0;
        _bufferFilled = 0;
        _previousCumDelta = 0;
        _spreadHistoryIdx = 0;
        _spreadHistoryFilled = 0;
        _prevBidInitialised = false;
        _uptickCount = 0;
        _downtickCount = 0;
        _sessionSignalCount = 0;
        _lastSignalTimeUtc = DateTime.MinValue;
        _currentSession = SessionId.None;
        ActiveSession = SessionId.None;
        LiveDelta = 0;
        CumulativeDelta = 0;
        AvgSpread = null;
        LastConfirmationScore = 0;
    }
}

public enum SessionId
{
    None = 0,
    Asia = 1,
    London = 2,
    NewYork = 3,
    Overlap = 4,
}
