using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant.TimeSeries;
using TradingTerminal.UI;

namespace TradingTerminal.Ml.Stationarity;

/// <summary>
/// View-model for the Stationarity &amp; Differencing tool (Machine Learning menu). Pulls a window
/// of historical bars, applies the selected transform (level / log / first difference / log
/// returns / fractional difference), and runs ADF + KPSS off the UI thread. The view draws the
/// transformed series with rolling mean ± 2σ bands and the ACF on the <see cref="Updated"/>
/// event (the sanctioned ScottPlot-in-MVVM pattern). It also
/// sweeps ADF across all transforms to recommend the mildest stationary one — the practical
/// "what d do I feed ARIMA?" answer.
/// </summary>
public sealed partial class StationarityViewModel : ViewModelBase
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<StationarityViewModel> _logger;
    private CancellationTokenSource? _runCts;

    public const int MaxInstrumentsDisplayed = 500;
    private const int AcfLags = 24;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute),
        new("5m",  BarSize.FiveMinutes),
        new("15m", BarSize.FifteenMinutes),
        new("1h",  BarSize.OneHour),
        new("1d",  BarSize.OneDay),
    };

    private static readonly IReadOnlyList<TransformOption> AllTransforms = new TransformOption[]
    {
        new("Price (level)", SeriesTransform.Level),
        new("Log price", SeriesTransform.Log),
        new("First difference", SeriesTransform.FirstDifference),
        new("Log returns", SeriesTransform.LogReturns),
        new("Fractional difference", SeriesTransform.FractionalDifference),
    };

    public StationarityViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<StationarityViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);
        Transforms = new ObservableCollection<TransformOption>(AllTransforms);
        SelectedTransform = Transforms[0];

        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<TransformOption> Transforms { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    /// <summary>Chart payload for the view renderer. Null until the first successful run.</summary>
    public StationarityChartData? ChartData { get; private set; }

    /// <summary>Raised after a successful run so the code-behind can redraw the plots.</summary>
    public event EventHandler? Updated;

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private TransformOption? _selectedTransform;
    [ObservableProperty] private int _barCount = 500;
    [ObservableProperty] private double _fracD = 0.4;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _adfStatistic = "—";
    [ObservableProperty] private string _adfVerdict = "—";
    [ObservableProperty] private string _adfVerdictColor = "#9E9E9E";
    [ObservableProperty] private string _adfDetail = "";
    [ObservableProperty] private string _kpssStatistic = "—";
    [ObservableProperty] private string _kpssVerdict = "—";
    [ObservableProperty] private string _kpssVerdictColor = "#9E9E9E";
    [ObservableProperty] private string _kpssDetail = "";
    [ObservableProperty] private string _agreementText = "";
    [ObservableProperty] private string _recommendationText = "";
    [ObservableProperty] private string _runStatus = "Not run.";

    public bool IsFracDVisible => SelectedTransform?.Transform == SeriesTransform.FractionalDifference;

    partial void OnSelectedTransformChanged(TransformOption? value) => OnPropertyChanged(nameof(IsFracDVisible));
    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument first."; return; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return; }
        if (SelectedTransform is null) { ErrorMessage = "Pick a transform."; return; }
        if (BarCount < 60) { ErrorMessage = "Need at least 60 bars for stable test statistics."; return; }
        if (FracD is <= 0 or >= 1) { ErrorMessage = "Fractional d must be in (0, 1)."; return; }

        ErrorMessage = null;
        IsRunning = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var broker = ResolveBroker(SelectedInstrument);
            var duration = EstimateDuration(SelectedTimeframe.BarSize, BarCount);
            var bars = await _repository.GetHistoricalBarsAsync(
                SelectedInstrument.Contract, broker, SelectedTimeframe.BarSize, duration, ct);

            if (bars.Count < 60)
            {
                ErrorMessage = $"Only {bars.Count} bars returned; need at least 60.";
                return;
            }

            var transform = SelectedTransform.Transform;
            var fracD = FracD;
            var result = await Task.Run(() => Analyze(bars, transform, fracD), ct);
            if (result is null)
            {
                ErrorMessage = "Analysis failed — series too short or degenerate after the transform.";
                return;
            }

            Apply(result, SelectedInstrument.DisplayName, SelectedTimeframe.Label);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stationarity analysis failed");
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

    private static StationarityAnalysis? Analyze(IReadOnlyList<Bar> bars, SeriesTransform transform, double fracD)
    {
        var closes = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++) closes[i] = bars[i].Close;

        var (series, consumed) = SeriesTransforms.Apply(closes, transform, fracD);
        if (series.Length < 40) return null;

        var times = new DateTime[series.Length];
        for (var i = 0; i < series.Length; i++) times[i] = bars[consumed + i].TimestampUtc;

        var adf = StationarityTests.Adf(series);
        var kpss = StationarityTests.Kpss(series);
        if (adf is null || kpss is null) return null;

        var window = Math.Clamp(series.Length / 5, 10, 60);
        var (rollMean, rollStd) = SeriesTransforms.RollingMeanStd(series, window);
        var acf = StationarityTests.Acf(series, AcfLags);
        var band = StationarityTests.AcfConfidenceBand(series.Length);

        // Recommendation sweep: mildest transform (most information kept) that passes ADF at 5%.
        var sweep = new List<(string Label, bool Stationary)>();
        foreach (var opt in AllTransforms)
        {
            var (s, _) = SeriesTransforms.Apply(closes, opt.Transform, fracD);
            if (s.Length < 40) continue;
            var t = StationarityTests.Adf(s);
            if (t is not null) sweep.Add((opt.Label, t.IsStationary));
        }

        return new StationarityAnalysis(
            new StationarityChartData(times, series, rollMean, rollStd, acf, band, window),
            adf, kpss, sweep);
    }

    private void Apply(StationarityAnalysis a, string symbol, string timeframe)
    {
        ChartData = a.Chart;
        HasResult = true;

        AdfStatistic = a.Adf.Statistic.ToString("0.000");
        AdfVerdict = a.Adf.IsStationary ? "STATIONARY" : "UNIT ROOT";
        AdfVerdictColor = a.Adf.IsStationary ? "#00C853" : "#FF5252";
        AdfDetail = $"5% CV {a.Adf.CriticalValues[0.05]:0.000} · 1% {a.Adf.CriticalValues[0.01]:0.000} · {a.Adf.LagsUsed} lags (AIC) · n = {a.Adf.N}";

        KpssStatistic = a.Kpss.Statistic.ToString("0.000");
        KpssVerdict = a.Kpss.IsStationary ? "STATIONARY" : "NON-STATIONARY";
        KpssVerdictColor = a.Kpss.IsStationary ? "#00C853" : "#FF5252";
        KpssDetail = $"5% CV {a.Kpss.CriticalValues[0.05]:0.000} · 1% {a.Kpss.CriticalValues[0.01]:0.000} · bandwidth {a.Kpss.LagsUsed} · n = {a.Kpss.N}";

        AgreementText = (a.Adf.IsStationary, a.Kpss.IsStationary) switch
        {
            (true, true) => "ADF and KPSS agree: the series is stationary — safe to model directly.",
            (false, false) => "ADF and KPSS agree: the series is non-stationary — difference (or transform) before modelling.",
            (true, false) => "Tests disagree (ADF rejects the unit root, KPSS rejects stationarity) — typical of a near-unit-root or trend-stationary series; treat with caution.",
            (false, true) => "Tests disagree (ADF can't reject the unit root, KPSS can't reject stationarity) — the sample may be too short for a confident verdict.",
        };

        var firstStationary = a.Sweep.FirstOrDefault(s => s.Stationary);
        RecommendationText = firstStationary.Label is null
            ? "No transform in the sweep achieved ADF stationarity — try more bars or a coarser timeframe."
            : $"Mildest ADF-stationary transform: {firstStationary.Label}." +
              (a.Sweep.Count > 0 ? $"  (sweep: {string.Join(" · ", a.Sweep.Select(s => $"{s.Label} {(s.Stationary ? "✓" : "✗")}"))})" : string.Empty);

        RunStatus = $"{a.Chart.Values.Length} points · {timeframe} · {symbol} · rolling window {a.Chart.RollingWindow}";
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
            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stationarity: broker universe load failed, using static catalog");
        }
    }

    private void ApplyInstrumentFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<SignalInstrument> query = AllInstruments;
        if (term.Length > 0)
            query = AllInstruments.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));
        var shown = query.Take(MaxInstrumentsDisplayed).ToList();

        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);

        Instruments = new ObservableCollection<SignalInstrument>(shown);
        OnPropertyChanged(nameof(Instruments));
        SelectedInstrument = keep is not null && Instruments.Contains(keep) ? keep : Instruments.FirstOrDefault();
    }

    private sealed record StationarityAnalysis(
        StationarityChartData Chart,
        StationarityTestResult Adf,
        StationarityTestResult Kpss,
        IReadOnlyList<(string Label, bool Stationary)> Sweep);
}

/// <summary>Everything the view's chart renderer needs, computed off the UI thread in one shot.</summary>
public sealed record StationarityChartData(
    DateTime[] Times,
    double[] Values,
    double[] RollingMean,
    double[] RollingStd,
    double[] Acf,
    double AcfBand,
    int RollingWindow);

/// <summary>Transform dropdown row.</summary>
public sealed record TransformOption(string Label, SeriesTransform Transform)
{
    public override string ToString() => Label;
}

/// <summary>Bar-size dropdown row. Each tool owns its own copy so the panels stay independent projects.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}
