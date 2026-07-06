using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;
using static TradingTerminal.Core.MarketData.Indicators;

namespace TradingTerminal.Charts;

/// <summary>
/// Drives the TradingView-style Charts window. Mirrors <c>CorrelationMatrixViewModel</c>: pulls the
/// broker instrument universe + historical bars from <see cref="IMarketDataRepository"/>, computes
/// indicators in C# (so chart / backtest / live numbers agree), and streams the forming candle from
/// <see cref="IMarketDataHub"/>. The window renders everything via Lightweight Charts in a WebView2 —
/// this VM holds no view code, it just raises <see cref="SnapshotReady"/> / <see cref="CandleUpdated"/>.
/// </summary>
public sealed partial class ChartsViewModel : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<ChartsViewModel> _logger;

    private IReadOnlyList<TradableInstrument> _allInstruments = Array.Empty<TradableInstrument>();
    private bool _chartReady;
    private CancellationTokenSource? _loadCts;
    private IDisposable? _liveSub;
    private IDisposable? _ingestHandle;

    private static readonly IReadOnlyList<ChartTimeframe> AllTimeframes = new[]
    {
        new ChartTimeframe("1m",  BarSize.OneMinute,      TimeSpan.FromDays(2)),
        new ChartTimeframe("5m",  BarSize.FiveMinutes,    TimeSpan.FromDays(5)),
        new ChartTimeframe("15m", BarSize.FifteenMinutes, TimeSpan.FromDays(15)),
        new ChartTimeframe("1h",  BarSize.OneHour,        TimeSpan.FromDays(60)),
        new ChartTimeframe("1D",  BarSize.OneDay,         TimeSpan.FromDays(365)),
    };

    public ChartsViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<ChartsViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _logger = logger;

        Timeframes = new ObservableCollection<ChartTimeframe>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);
        Instruments = new ObservableCollection<TradableInstrument>();

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<ChartTimeframe> Timeframes { get; }
    public ObservableCollection<TradableInstrument> Instruments { get; }

    [ObservableProperty] private TradableInstrument? _selectedInstrument;
    [ObservableProperty] private ChartTimeframe? _selectedTimeframe;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _showSma = true;
    [ObservableProperty] private bool _showEma = true;
    [ObservableProperty] private bool _showRsi;
    [ObservableProperty] private bool _showMacd;
    [ObservableProperty] private string _status = "Loading instruments…";

    /// <summary>Raised after a history load with the full chart payload (candles + volume + indicators).</summary>
    public event EventHandler<ChartSnapshot>? SnapshotReady;

    /// <summary>Raised on each live forming/closed candle for the active instrument.</summary>
    public event EventHandler<ChartCandle>? CandleUpdated;

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "tool.charts";

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(TradableInstrument? value) => QueueReload();
    partial void OnSelectedTimeframeChanged(ChartTimeframe? value) => QueueReload();
    partial void OnShowSmaChanged(bool value) => QueueReload();
    partial void OnShowEmaChanged(bool value) => QueueReload();
    partial void OnShowRsiChanged(bool value) => QueueReload();
    partial void OnShowMacdChanged(bool value) => QueueReload();

    /// <summary>Called by the window once the WebView2 page has loaded and can receive data.</summary>
    public Task NotifyChartReadyAsync()
    {
        _chartReady = true;
        return ReloadAsync();
    }

    private void QueueReload()
    {
        if (_chartReady) _ = ReloadAsync();
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0)
            {
                Status = "No instruments — connect a broker first.";
                return;
            }
            _allInstruments = list;
            SelectedInstrument =
                (SelectedInstrument?.Contract.Symbol is { } prev
                    ? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == prev) : null)
                ?? InstrumentPickerFilter.Remembered(InstrumentPersistKey, _allInstruments, i => i.Contract.Symbol)
                ?? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                ?? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "AAPL")
                ?? _allInstruments.FirstOrDefault();
            ApplyFilter();
            Status = $"{_allInstruments.Count} instruments.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Charts: instrument load failed");
            Status = $"Instrument load failed: {ex.Message}";
        }
    }

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="_allInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(_allInstruments, InstrumentSearchText, SelectedInstrument,
            MaxInstrumentsDisplayed, i => i.DisplayName));

    private async Task ReloadAsync()
    {
        var instrument = SelectedInstrument;
        var tf = SelectedTimeframe;
        if (instrument is null || tf is null) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        StopLive();

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }

        Status = $"Loading {instrument.DisplayName} ({tf.Label})…";
        try
        {
            var bars = await _repository.GetHistoricalBarsAsync(instrument.Contract, broker, tf.BarSize, tf.Lookback, ct)
                       ?? Array.Empty<Bar>();

            var candles = new ChartCandle[bars.Count];
            var volume = new ChartVolume[bars.Count];
            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                var t = ToEpoch(b.TimestampUtc);
                candles[i] = new ChartCandle(t, b.Open, b.High, b.Low, b.Close);
                volume[i] = new ChartVolume(t, b.Volume, b.Close >= b.Open ? "#26a69a80" : "#ef535080");
            }

            var snapshot = new ChartSnapshot(
                Symbol: instrument.DisplayName,
                Timeframe: tf.Label,
                Candles: candles,
                Volume: volume,
                Sma: ShowSma ? Sma(bars, 20) : null,
                Ema: ShowEma ? Ema(bars, 50) : null,
                Rsi: ShowRsi ? Rsi(bars, 14) : null,
                Macd: ShowMacd ? Macd(bars, 12, 26, 9) : null);

            if (ct.IsCancellationRequested) return;
            SnapshotReady?.Invoke(this, snapshot);
            Status = bars.Count == 0
                ? $"No history for {instrument.DisplayName} — is the broker connected and streaming?"
                : $"{instrument.DisplayName} · {tf.Label} · {bars.Count} bars";

            StartLive(instrument, broker, tf.BarSize);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Charts: load failed for {Symbol}", instrument.Contract.Symbol);
            Status = $"Load failed: {ex.Message}";
        }
    }

    private void StartLive(TradableInstrument instrument, BrokerKind broker, BarSize size)
    {
        try
        {
            var id = _ingest.Resolve(instrument.Contract, broker);
            _ingestHandle = _ingest.SubscribeBars(instrument.Contract, broker, size);
            _liveSub = _hub.Bars(id, size).Subscribe(bar =>
                _ = UiThread.RunAsync(() => CandleUpdated?.Invoke(this,
                    new ChartCandle(ToEpoch(bar.OpenTimeUtc), bar.Open, bar.High, bar.Low, bar.Close))));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Charts: live subscription failed (continuing with history only)");
        }
    }

    private void StopLive()
    {
        _liveSub?.Dispose(); _liveSub = null;
        _ingestHandle?.Dispose(); _ingestHandle = null;
    }

    private BrokerKind ResolveBroker(TradableInstrument instrument)
    {
        if (_selector.IsConnected(instrument.Broker)) return instrument.Broker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker first.");
        return connected[0];
    }

    private static long ToEpoch(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // ── Indicators (computed in C# over closes; reuse Core primitives) ──────────────────────────

    private static ChartLinePoint[] Sma(IReadOnlyList<Bar> bars, int period)
    {
        var ind = new SimpleMovingAverage(period);
        var pts = new List<ChartLinePoint>(bars.Count);
        foreach (var b in bars) { ind.Push(b.Close); if (ind.IsReady) pts.Add(new ChartLinePoint(ToEpoch(b.TimestampUtc), Round(ind.Value))); }
        return pts.ToArray();
    }

    private static ChartLinePoint[] Ema(IReadOnlyList<Bar> bars, int period)
    {
        var ind = new ExponentialMovingAverage(period);
        var pts = new List<ChartLinePoint>(bars.Count);
        foreach (var b in bars) { ind.Push(b.Close); if (ind.IsReady) pts.Add(new ChartLinePoint(ToEpoch(b.TimestampUtc), Round(ind.Value))); }
        return pts.ToArray();
    }

    private static ChartLinePoint[] Rsi(IReadOnlyList<Bar> bars, int period)
    {
        var ind = new RelativeStrengthIndex(period);
        var pts = new List<ChartLinePoint>(bars.Count);
        foreach (var b in bars) { ind.Push(b.Close); if (ind.IsReady) pts.Add(new ChartLinePoint(ToEpoch(b.TimestampUtc), Round(ind.Value))); }
        return pts.ToArray();
    }

    private static MacdPoint[] Macd(IReadOnlyList<Bar> bars, int fast, int slow, int signal)
    {
        var emaFast = new ExponentialMovingAverage(fast);
        var emaSlow = new ExponentialMovingAverage(slow);
        var emaSig = new ExponentialMovingAverage(signal);
        var pts = new List<MacdPoint>(bars.Count);
        foreach (var b in bars)
        {
            emaFast.Push(b.Close);
            emaSlow.Push(b.Close);
            if (!emaSlow.IsReady) continue;
            var macd = emaFast.Value - emaSlow.Value;
            emaSig.Push(macd);
            var sig = emaSig.Value;
            pts.Add(new MacdPoint(ToEpoch(b.TimestampUtc), Round(macd), Round(sig), Round(macd - sig)));
        }
        return pts.ToArray();
    }

    private static double Round(double v) => Math.Round(v, 6);

    public void Dispose()
    {
        // Remember the instrument the user was last charting so the window reopens on it.
        LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument?.Contract.Symbol);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        StopLive();
    }
}

/// <summary>A selectable timeframe — label, the canonical <see cref="BarSize"/>, and how much history to pull.</summary>
public sealed record ChartTimeframe(string Label, BarSize BarSize, TimeSpan Lookback);

// ── JSON bridge DTOs (camelCase via the window's serializer) → Lightweight Charts shapes ─────────
public sealed record ChartCandle(long Time, double Open, double High, double Low, double Close);
public sealed record ChartVolume(long Time, double Value, string Color);
public sealed record ChartLinePoint(long Time, double Value);
public sealed record MacdPoint(long Time, double Macd, double Signal, double Hist);
public sealed record ChartSnapshot(
    string Symbol,
    string Timeframe,
    ChartCandle[] Candles,
    ChartVolume[] Volume,
    ChartLinePoint[]? Sma,
    ChartLinePoint[]? Ema,
    ChartLinePoint[]? Rsi,
    MacdPoint[]? Macd);
