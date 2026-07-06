using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant.TimeSeries;
using TradingTerminal.UI;

namespace TradingTerminal.Ml.KalmanFilter;

/// <summary>Which state-space model the tool runs.</summary>
public enum KalmanMode
{
    LocalLevel,
    LocalLinearTrend,
    DynamicHedgeRatio,
}

/// <summary>
/// View-model for the Kalman Filter tool (Machine Learning menu). Three classic state-space
/// models over historical bars, filtered off the UI thread (Core math): local level (adaptive
/// smoothing), local linear trend (level + slope), and the dynamic pairs hedge ratio
/// (yₜ = αₜ + βₜ·xₜ on log prices of two instruments — the time-varying β every pairs desk
/// uses). The single knob is the process/observation noise ratio Q/R: bigger tracks faster but
/// noisier. The view draws price-vs-filtered-state (or β over time) plus the standardized
/// innovation z-score chart on the <see cref="Updated"/> event; innovations hugging ±2 is the
/// "filter is honest" diagnostic, and in pairs mode the z-score IS the mean-reversion signal.
/// </summary>
public sealed partial class KalmanFilterViewModel : ViewModelBase
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<KalmanFilterViewModel> _logger;
    private CancellationTokenSource? _runCts;

    public const int MaxInstrumentsDisplayed = 500;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute),
        new("5m",  BarSize.FiveMinutes),
        new("15m", BarSize.FifteenMinutes),
        new("1h",  BarSize.OneHour),
        new("1d",  BarSize.OneDay),
    };

    private static readonly IReadOnlyList<ModeOption> AllModes = new ModeOption[]
    {
        new("Local level (smoothing)", KalmanMode.LocalLevel),
        new("Local linear trend", KalmanMode.LocalLinearTrend),
        new("Dynamic hedge ratio (pairs)", KalmanMode.DynamicHedgeRatio),
    };

    /// <summary>Q/R presets, smallest (heaviest smoothing) to largest (fastest tracking).</summary>
    private static readonly IReadOnlyList<double> QrPresets = new[] { 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1, 1.0 };

    public KalmanFilterViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<KalmanFilterViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);
        Modes = new ObservableCollection<ModeOption>(AllModes);
        SelectedMode = Modes[0];
        QrRatios = new ObservableCollection<double>(QrPresets);
        QrRatio = 1e-3;

        AllInstruments = SignalInstrumentCatalog.All;
        // Hide-until-search: empty visible list; ApplyInstrumentFilter (below) collapses it to the selections.
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(InstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? AllInstruments.FirstOrDefault());
        SecondInstrument = InstrumentPickerFilter.InitialSelection(SecondInstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "QQQ") ?? AllInstruments.Skip(1).FirstOrDefault());
        ApplyInstrumentFilter();

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<ModeOption> Modes { get; }
    public ObservableCollection<double> QrRatios { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    /// <summary>Keys under which this window remembers its two selected instruments (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "ml.kalman";
    private const string SecondInstrumentPersistKey = "ml.kalman.second";

    /// <summary>Chart payload for the view renderer. Null until the first successful run.</summary>
    public KalmanChartData? ChartData { get; private set; }

    /// <summary>Raised after a successful run so the code-behind can redraw the plots.</summary>
    public event EventHandler? Updated;

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private SignalInstrument? _secondInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private ModeOption? _selectedMode;
    [ObservableProperty] private double _qrRatio;
    [ObservableProperty] private int _barCount = 500;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _headlineLabel = "FILTERED STATE";
    [ObservableProperty] private string _headlineValue = "—";
    [ObservableProperty] private string _headlineDetail = "";
    [ObservableProperty] private string _modelSummary = "";
    [ObservableProperty] private string _innovationSummary = "";
    [ObservableProperty] private string _runStatus = "Not run.";

    public bool IsPairsMode => SelectedMode?.Mode == KalmanMode.DynamicHedgeRatio;

    partial void OnSelectedModeChanged(ModeOption? value) => OnPropertyChanged(nameof(IsPairsMode));
    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument first."; return; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return; }
        if (SelectedMode is null) { ErrorMessage = "Pick a model."; return; }
        if (QrRatio <= 0) { ErrorMessage = "Q/R must be positive."; return; }
        if (BarCount < 60) { ErrorMessage = "Need at least 60 bars."; return; }
        var pairs = SelectedMode.Mode == KalmanMode.DynamicHedgeRatio;
        if (pairs && SecondInstrument is null) { ErrorMessage = "Pick the second (hedge) instrument."; return; }
        if (pairs && ReferenceEquals(SecondInstrument, SelectedInstrument))
        {
            ErrorMessage = "Pairs mode needs two different instruments.";
            return;
        }

        ErrorMessage = null;
        IsRunning = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var duration = EstimateDuration(SelectedTimeframe.BarSize, BarCount);
            var bars = await _repository.GetHistoricalBarsAsync(
                SelectedInstrument.Contract, ResolveBroker(SelectedInstrument), SelectedTimeframe.BarSize, duration, ct);
            if (bars.Count < 60)
            {
                ErrorMessage = $"Only {bars.Count} bars returned for {SelectedInstrument.DisplayName}; need at least 60.";
                return;
            }

            IReadOnlyList<Bar> hedgeBars = Array.Empty<Bar>();
            if (pairs)
            {
                hedgeBars = await _repository.GetHistoricalBarsAsync(
                    SecondInstrument!.Contract, ResolveBroker(SecondInstrument!), SelectedTimeframe.BarSize, duration, ct);
                if (hedgeBars.Count < 60)
                {
                    ErrorMessage = $"Only {hedgeBars.Count} bars returned for {SecondInstrument!.DisplayName}; need at least 60.";
                    return;
                }
            }

            var mode = SelectedMode.Mode;
            var qr = QrRatio;
            var result = await Task.Run(() => Analyze(bars, hedgeBars, mode, qr), ct);
            if (result is null)
            {
                ErrorMessage = pairs
                    ? "Filter failed — too few overlapping bars between the two instruments."
                    : "Filter failed — series too short or degenerate.";
                return;
            }

            Apply(result, mode);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kalman filter run failed");
            ErrorMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    // ── Analysis (CPU-bound, runs off the UI thread) ────────────────────────────────────────────

    private static Analysis? Analyze(
        IReadOnlyList<Bar> bars, IReadOnlyList<Bar> hedgeBars, KalmanMode mode, double qOverR)
    {
        if (mode == KalmanMode.DynamicHedgeRatio)
        {
            // Join the two series on bar timestamps — brokers can return slightly different windows.
            var hedgeByTime = new Dictionary<DateTime, double>(hedgeBars.Count);
            foreach (var b in hedgeBars) hedgeByTime[b.TimestampUtc] = b.Close;

            var times = new List<DateTime>();
            var y = new List<double>();
            var x = new List<double>();
            foreach (var b in bars)
            {
                if (!hedgeByTime.TryGetValue(b.TimestampUtc, out var hx)) continue;
                times.Add(b.TimestampUtc);
                y.Add(Math.Log(Math.Max(b.Close, 1e-12)));
                x.Add(Math.Log(Math.Max(hx, 1e-12)));
            }
            if (y.Count < 60) return null;

            var result = KalmanFilters.DynamicRegression(y, x, qOverR);
            if (result is null) return null;

            return new Analysis(
                new KalmanChartData(
                    times.ToArray(),
                    Primary: result.Slope,           // β over time
                    Secondary: y.ToArray(),          // unused in pairs primary chart, kept for tooltips
                    Observed: Array.Empty<double>(),
                    Z: result.StandardizedInnovations,
                    IsPairs: true),
                result, y.Count);
        }

        var n = bars.Count;
        var closes = new double[n];
        var barTimes = new DateTime[n];
        for (var i = 0; i < n; i++)
        {
            closes[i] = bars[i].Close;
            barTimes[i] = bars[i].TimestampUtc;
        }

        var r = mode == KalmanMode.LocalLevel
            ? KalmanFilters.LocalLevel(closes, qOverR)
            : KalmanFilters.LocalLinearTrend(closes, qOverR);
        if (r is null) return null;

        return new Analysis(
            new KalmanChartData(barTimes, r.Level, r.Slope, closes, r.StandardizedInnovations, IsPairs: false),
            r, n);
    }

    private void Apply(Analysis a, KalmanMode mode)
    {
        ChartData = a.Chart;
        HasResult = true;

        var r = a.Result;
        if (mode == KalmanMode.DynamicHedgeRatio)
        {
            var beta = r.Slope.Length > 0 ? r.Slope[^1] : 0;
            var alpha = r.Level.Length > 0 ? r.Level[^1] : 0;
            var z = r.StandardizedInnovations.Length > 0 ? r.StandardizedInnovations[^1] : 0;
            HeadlineLabel = "CURRENT HEDGE β";
            HeadlineValue = beta.ToString("0.000");
            HeadlineDetail = $"α {alpha:0.000} · latest spread z {z:+0.00;-0.00} " +
                             (Math.Abs(z) >= 2 ? "(stretched — classic pairs entry zone)" : "(inside the band)");
        }
        else
        {
            var level = r.Level.Length > 0 ? r.Level[^1] : 0;
            HeadlineLabel = "FILTERED LEVEL";
            HeadlineValue = level.ToString("0.####");
            HeadlineDetail = mode == KalmanMode.LocalLinearTrend && r.Slope.Length > 0
                ? $"slope {r.Slope[^1]:+0.####;-0.####;0} per bar"
                : "adaptive smoothing — lag follows Q/R";
        }

        ModelSummary = $"Q/R {QrRatio:0.######} · R {r.ObservationNoise:0.###e+0} · Q {r.ProcessNoise:0.###e+0} · logL {r.LogLikelihood:0.0}";

        // Innovation whiteness: a well-specified filter has z ~ N(0,1).
        var zs = r.StandardizedInnovations;
        if (zs.Length > 2)
        {
            double mean = 0;
            for (var i = 1; i < zs.Length; i++) mean += zs[i];
            mean /= zs.Length - 1;
            double var0 = 0;
            var outliers = 0;
            for (var i = 1; i < zs.Length; i++)
            {
                var dv = zs[i] - mean;
                var0 += dv * dv;
                if (Math.Abs(zs[i]) > 2) outliers++;
            }
            var std = Math.Sqrt(var0 / (zs.Length - 2));
            InnovationSummary =
                $"standardized innovations: mean {mean:+0.00;-0.00;0.00}, std {std:0.00} (target 0 / 1) · " +
                $"|z| > 2 on {(double)outliers / (zs.Length - 1):0.0%} of bars (≈4.6% if Gaussian)";
        }

        RunStatus = $"{a.PointCount} points · {SelectedTimeframe?.Label} · {SelectedInstrument?.DisplayName}" +
                    (a.Chart.IsPairs ? $" vs {SecondInstrument?.DisplayName} (log prices, timestamp-joined)" : string.Empty);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    // ── Shared picker plumbing (each tool owns its copy — independent projects) ─────────────────

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    private static TimeSpan EstimateDuration(BarSize size, int barCount) => size switch
    {
        BarSize.OneMinute      => TimeSpan.FromMinutes(barCount * 1.5),
        BarSize.FiveMinutes    => TimeSpan.FromMinutes(barCount * 5 * 1.5),
        BarSize.FifteenMinutes => TimeSpan.FromMinutes(barCount * 15 * 1.5),
        BarSize.OneHour        => TimeSpan.FromHours(barCount * 1.5),
        BarSize.OneDay         => TimeSpan.FromDays(barCount * 1.5),
        _                      => TimeSpan.FromDays(7),
    };

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            AllInstruments = list.Select(i => new SignalInstrument(
                i.DisplayName, i.Category, i.Contract, i.Broker)).ToList();
            SelectedInstrument = (SelectedInstrument?.Contract.Symbol is { } prev
                                     ? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == prev) : null)
                                 ?? InstrumentPickerFilter.Remembered(InstrumentPersistKey, AllInstruments)
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault();
            SecondInstrument = (SecondInstrument?.Contract.Symbol is { } prev2
                                   ? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == prev2) : null)
                               ?? InstrumentPickerFilter.Remembered(SecondInstrumentPersistKey, AllInstruments)
                               ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "QQQ")
                               ?? AllInstruments.Skip(1).FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kalman filter: broker universe load failed, using static catalog");
        }
    }

    /// <summary>Hide-until-search: no term shows only the two current selections; typing filters
    /// <see cref="AllInstruments"/>. Rebuilt in place so neither selection flickers out.</summary>
    private void ApplyInstrumentFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText,
            new[] { SelectedInstrument, SecondInstrument }, MaxInstrumentsDisplayed));

    /// <summary>Remember the two selected instruments so the window reopens on them.</summary>
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) =>
        LastInstrumentStore.Save(InstrumentPersistKey, value?.Contract.Symbol);
    partial void OnSecondInstrumentChanged(SignalInstrument? value) =>
        LastInstrumentStore.Save(SecondInstrumentPersistKey, value?.Contract.Symbol);

    private sealed record Analysis(KalmanChartData Chart, KalmanResult Result, int PointCount);
}

/// <summary>
/// Everything the view's chart renderer needs. For smoothing modes: Observed = closes,
/// Primary = filtered level, Secondary = slope (may be empty). For pairs: Primary = β series,
/// Observed empty. Z is the standardized innovation series in both cases.
/// </summary>
public sealed record KalmanChartData(
    DateTime[] Times,
    double[] Primary,
    double[] Secondary,
    double[] Observed,
    double[] Z,
    bool IsPairs);

/// <summary>Model dropdown row.</summary>
public sealed record ModeOption(string Label, KalmanMode Mode)
{
    public override string ToString() => Label;
}

/// <summary>Bar-size dropdown row. Each tool owns its own copy so the panels stay independent projects.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}
