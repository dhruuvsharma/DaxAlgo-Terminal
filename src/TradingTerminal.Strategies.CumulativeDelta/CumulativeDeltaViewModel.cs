using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.CumulativeDelta;

/// <summary>
/// View-model for the Cumulative Delta Scalper. Mirrors the MT5 EA's signal logic:
/// tick-by-tick bid moves classify each tick as up/down/ignore; bar-deltas (= upticks − downticks
/// over the lifetime of a bar) feed a circular window of <see cref="WindowSize"/> entries; the
/// summed cumulative delta crosses ±<see cref="DeltaThreshold"/> to trigger a signal. ATR(14)
/// on the chart timeframe gates volatility; EMA(50) on the 15m timeframe gates trend direction.
///
/// This is display-only — no orders are placed. Signals appear in the dashboard and the log.
/// </summary>
public sealed partial class CumulativeDeltaViewModel : ViewModelBase, IDisposable
{
    public const int MaxBarsRetained = 300;

    private readonly IMarketDataRepository _repository;
    private readonly ILogger<CumulativeDeltaViewModel> _logger;

    private CancellationTokenSource? _cts;
    private double _prevBid;
    private bool _prevBidInitialised;
    private DateTime _currentBarOpenUtc;

    private readonly int[] _circularBuffer = new int[MaxWindowSize];
    private int _bufferIndex;
    private int _bufferFilled;
    private int _previousCumDelta;

    private DateTime _lastSignalDayUtc;
    // Reserved for future loss-driven cooldown (no orders today, so never assigned).
    private readonly DateTime _cooldownUntilUtc = DateTime.MinValue;

    public const int MaxWindowSize = 50;

    public CumulativeDeltaViewModel(IMarketDataRepository repository, ILogger<CumulativeDeltaViewModel> logger)
    {
        _repository = repository;
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
    [ObservableProperty] private double _minAtr = 0.00030;
    [ObservableProperty] private double _maxAtr = 0.00200;
    [ObservableProperty] private bool _useHtfFilter = true;
    [ObservableProperty] private bool _useSessionFilter = true;
    [ObservableProperty] private int _maxDailyTrades = 8;
    [ObservableProperty] private int _cooldownMinutes = 15;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private string? _validationError;

    // ---------- Runtime bindings ----------

    /// <summary>Bars on the chart timeframe (history + live). Used for ATR and price chart.</summary>
    public ObservableCollection<Bar> Bars { get; }

    /// <summary>Most recent N bar-deltas (oldest → newest), where N = <see cref="WindowSize"/>.</summary>
    public ObservableCollection<int> BarDeltas { get; }

    /// <summary>Last 12 signal lines for the dashboard.</summary>
    public ObservableCollection<string> RecentSignals { get; }

    [ObservableProperty] private string _status = "Configure the strategy to begin.";
    [ObservableProperty] private string _guardReason = "";

    [ObservableProperty] private double? _lastBid;
    [ObservableProperty] private double? _lastAsk;
    [ObservableProperty] private double? _lastSpreadPoints;
    [ObservableProperty] private int _liveDelta;          // running delta on the in-progress bar
    [ObservableProperty] private int _cumulativeDelta;    // sum of completed bar-deltas in window
    [ObservableProperty] private double? _lastAtr;
    [ObservableProperty] private double? _lastEmaHtf;

    [ObservableProperty] private int _todaySignalCount;
    [ObservableProperty] private bool _isStreaming;

    /// <summary>True only while the user has armed the algo. Display-only otherwise.</summary>
    [ObservableProperty] private bool _isAlgoRunning;

    public event EventHandler? BarsChanged;
    public event EventHandler? DeltasChanged;

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
        _logger.LogInformation("CumulativeDelta algo {State} for {Symbol}",
            IsAlgoRunning ? "ARMED" : "STOPPED", label);
        Status = IsAlgoRunning
            ? $"Algo running on {label} (window {WindowSize}, threshold ±{DeltaThreshold})"
            : $"Streaming {label} — algo idle";
    }

    public async Task StartStreamAsync()
    {
        if (IsStreaming || SelectedInstrument is null) return;

        var contract = SelectedInstrument.Contract;
        var chartTf = SelectedTimeframe;

        Bars.Clear();
        BarDeltas.Clear();
        ResetDeltaState();
        BarsChanged?.Invoke(this, EventArgs.Empty);
        DeltasChanged?.Invoke(this, EventArgs.Empty);

        Status = $"Loading {SelectedInstrument.DisplayName} history…";

        try
        {
            // Backfill chart-timeframe bars for ATR seed and chart context.
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
        Status = $"Streaming {SelectedInstrument.DisplayName} — algo idle";
        _currentBarOpenUtc = AlignToTimeframeUtc(DateTime.UtcNow, chartTf);

        // Three concurrent feeds:
        //   1. Tick-by-tick bid/ask  →  uptick/downtick accounting
        //   2. Chart-TF bars         →  drives bar-close finalisation, ATR, chart
        //   3. 15m bars              →  HTF EMA(50) filter
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

    // ---------- Tick processing (bid-tick rule from MT5 Market.mqh:74-88) ----------

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
        LastSpreadPoints = (tick.Ask - tick.Bid);

        if (!_prevBidInitialised)
        {
            _prevBid = tick.Bid;
            _prevBidInitialised = true;
            return;
        }

        if (tick.Bid > _prevBid)      LiveDelta++;
        else if (tick.Bid < _prevBid) LiveDelta--;
        _prevBid = tick.Bid;
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

    /// <summary>
    /// Push the just-closed bar's tick-delta into the circular buffer, recompute cumDelta,
    /// detect threshold crosses, and (if armed) act on signals.
    /// </summary>
    private void OnBarClosed()
    {
        var w = Math.Clamp(WindowSize, 2, MaxWindowSize);
        var candleDelta = LiveDelta;

        _circularBuffer[_bufferIndex] = candleDelta;
        _bufferIndex = (_bufferIndex + 1) % w;
        if (_bufferFilled < w) _bufferFilled++;

        // Snapshot ordered deltas for the UI (oldest → newest).
        BarDeltas.Clear();
        var start = (_bufferFilled >= w) ? _bufferIndex : 0;
        for (var i = 0; i < _bufferFilled; i++)
            BarDeltas.Add(_circularBuffer[(start + i) % w]);
        DeltasChanged?.Invoke(this, EventArgs.Empty);

        var cumDelta = SumWindow(w);
        CumulativeDelta = cumDelta;
        LiveDelta = 0;

        // Reset daily counter at UTC midnight.
        var todayUtc = DateTime.UtcNow.Date;
        if (todayUtc != _lastSignalDayUtc)
        {
            _lastSignalDayUtc = todayUtc;
            TodaySignalCount = 0;
        }

        if (_bufferFilled < w)
        {
            _previousCumDelta = cumDelta;
            return;
        }

        var signal = ClassifySignal(cumDelta, _previousCumDelta);
        _previousCumDelta = cumDelta;

        if (signal == 0) return;

        if (!CheckGuards(signal, out var reason))
        {
            GuardReason = reason;
            return;
        }
        GuardReason = "ACTIVE";

        if (!IsAlgoRunning)
        {
            // Surface the would-be signal in the dashboard but don't count or log as a real trade.
            PushSignalLine($"{DateTime.Now:HH:mm:ss}  (idle)  {DirectionLabel(signal)} cumΔ={cumDelta}");
            return;
        }

        TodaySignalCount++;
        var line = $"{DateTime.Now:HH:mm:ss}  ARMED  {DirectionLabel(signal)} cumΔ={cumDelta} bid={LastBid:F5}";
        PushSignalLine(line);
        _logger.LogInformation("[CumulativeDelta] {Direction} signal cumDelta={CumDelta} bid={Bid} (would {OrderType})",
            DirectionLabel(signal), cumDelta, LastBid, signal > 0 ? "BUY" : "SELL");
    }

    private int SumWindow(int w)
    {
        var sum = 0;
        var n = Math.Min(_bufferFilled, w);
        for (var i = 0; i < n; i++) sum += _circularBuffer[i];
        return sum;
    }

    /// <summary>1 = BUY cross, -1 = SELL cross, 0 = none. Mirrors Signal.mqh:25-54.</summary>
    private int ClassifySignal(int cumDelta, int previousCumDelta)
    {
        if (previousCumDelta <= DeltaThreshold && cumDelta > DeltaThreshold) return 1;
        if (previousCumDelta >= -DeltaThreshold && cumDelta < -DeltaThreshold) return -1;
        return 0;
    }

    // ---------- HTF (15m) bar stream: EMA(50) trend filter ----------

    private async Task RunHtfBarsAsync(Contract contract, CancellationToken ct)
    {
        var htfBars = new List<Bar>();
        try
        {
            // Backfill enough 15m bars to seed EMA(50).
            var hist = await _repository.GetHistoricalBarsAsync(contract, BarSize.FifteenMinutes,
                TimeSpan.FromDays(2), ct);
            htfBars.AddRange(hist);
            RecalculateHtfEma(htfBars);

            await foreach (var bar in _repository.SubscribeBarsAsync(contract, BarSize.FifteenMinutes, ct))
            {
                htfBars.Add(bar);
                if (htfBars.Count > 200) htfBars.RemoveAt(0);
                RecalculateHtfEma(htfBars);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTF bar stream ended");
        }
    }

    private void RecalculateHtfEma(IReadOnlyList<Bar> htfBars)
    {
        var ema = Indicators.Ema(htfBars, 50);
        LastEmaHtf = double.IsNaN(ema) ? null : ema;
    }

    private void RecalculateAtr()
    {
        var atr = Indicators.Atr(Bars, 14);
        LastAtr = double.IsNaN(atr) ? null : atr;
    }

    // ---------- Guards (Risk.mqh:30-92) ----------

    private bool CheckGuards(int signal, out string reason)
    {
        if (UseSessionFilter && !IsInSession(DateTime.UtcNow))
            { reason = "SESSION CLOSED"; return false; }

        if (TodaySignalCount >= MaxDailyTrades)
            { reason = $"DAILY LIMIT ({TodaySignalCount}/{MaxDailyTrades})"; return false; }

        if (DateTime.UtcNow < _cooldownUntilUtc)
            { reason = $"COOLDOWN ({(_cooldownUntilUtc - DateTime.UtcNow).TotalMinutes:F0}m left)"; return false; }

        if (LastAtr is { } atr)
        {
            if (atr < MinAtr) { reason = $"ATR TOO LOW ({atr:F5})"; return false; }
            if (atr > MaxAtr) { reason = $"ATR TOO HIGH ({atr:F5})"; return false; }
        }
        else
        {
            reason = "ATR UNAVAILABLE"; return false;
        }

        if (UseHtfFilter)
        {
            if (LastEmaHtf is not { } ema || LastBid is not { } bid)
                { reason = "HTF EMA UNAVAILABLE"; return false; }
            if (signal > 0 && bid <= ema) { reason = "HTF FILTER (long: bid≤EMA15m)"; return false; }
            if (signal < 0 && bid >= ema) { reason = "HTF FILTER (short: bid≥EMA15m)"; return false; }
        }

        reason = "ACTIVE";
        return true;
    }

    private static bool IsInSession(DateTime utc)
    {
        var h = utc.Hour;
        return (h >= 8 && h < 12) || (h >= 13 && h < 17);
    }

    // ---------- Helpers ----------

    private static string DirectionLabel(int signal) => signal > 0 ? "LONG" : "SHORT";

    private void PushSignalLine(string line)
    {
        RecentSignals.Insert(0, line);
        while (RecentSignals.Count > 12) RecentSignals.RemoveAt(RecentSignals.Count - 1);
    }

    private void ResetDeltaState()
    {
        Array.Clear(_circularBuffer);
        _bufferIndex = 0;
        _bufferFilled = 0;
        _previousCumDelta = 0;
        _prevBidInitialised = false;
        LiveDelta = 0;
        CumulativeDelta = 0;
    }

    private static DateTime AlignToTimeframeUtc(DateTime utc, BarSize size)
    {
        var ticks = size.ToTimeSpan().Ticks;
        return new DateTime(utc.Ticks - (utc.Ticks % ticks), DateTimeKind.Utc);
    }
}
