using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant.TimeSeries;
using TradingTerminal.UI;

namespace TradingTerminal.Ml.ArimaGarch;

/// <summary>
/// View-model for the ARIMA &amp; GARCH tool (Machine Learning menu). Fits ARIMA(p,d,q) to the
/// instrument's log prices (Hannan-Rissanen, optionally AIC-searched over (p,q)) and GARCH(1,1)
/// to its log returns, all off the UI thread. The view draws the price history with the h-step
/// forecast + 95% band and the conditional-volatility track on the <see cref="Updated"/> event
/// (the sanctioned ScottPlot-in-MVVM pattern). Forecasts are exponentiated back to price space,
/// so the bands are asymmetric the way log-normal price bands should be.
/// </summary>
public sealed partial class ArimaGarchViewModel : ViewModelBase
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<ArimaGarchViewModel> _logger;
    private CancellationTokenSource? _runCts;

    public const int MaxInstrumentsDisplayed = 500;
    private const int OrderSearchMax = 3;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute),
        new("5m",  BarSize.FiveMinutes),
        new("15m", BarSize.FifteenMinutes),
        new("1h",  BarSize.OneHour),
        new("1d",  BarSize.OneDay),
    };

    public ArimaGarchViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<ArimaGarchViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);
        Orders = new ObservableCollection<int> { 0, 1, 2, 3 };
        DiffOrders = new ObservableCollection<int> { 0, 1, 2 };

        AllInstruments = SignalInstrumentCatalog.All;
        // Hide-until-search: empty visible list; ApplyInstrumentFilter (below) collapses it to the selection.
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(InstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? AllInstruments.FirstOrDefault());
        ApplyInstrumentFilter();

        OrderSearchRows = new ObservableCollection<OrderSearchRow>();
        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<int> Orders { get; }
    public ObservableCollection<int> DiffOrders { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
    public ObservableCollection<OrderSearchRow> OrderSearchRows { get; }

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "ml.arimagarch";

    /// <summary>Chart payload for the view renderer. Null until the first successful fit.</summary>
    public ArimaGarchChartData? ChartData { get; private set; }

    /// <summary>Raised after a successful fit so the code-behind can redraw the plots.</summary>
    public event EventHandler? Updated;

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private int _p = 1;
    [ObservableProperty] private int _d = 1;
    [ObservableProperty] private int _q = 1;
    [ObservableProperty] private bool _autoOrder = true;
    [ObservableProperty] private int _barCount = 500;
    [ObservableProperty] private int _horizon = 20;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _arimaTitle = "—";
    [ObservableProperty] private string _arimaCoefficients = "";
    [ObservableProperty] private string _arimaCriteria = "";
    [ObservableProperty] private string _forecastSummary = "—";
    [ObservableProperty] private string _garchParams = "—";
    [ObservableProperty] private string _garchPersistence = "—";
    [ObservableProperty] private string _garchPersistenceColor = "#9E9E9E";
    [ObservableProperty] private string _garchVolSummary = "";
    [ObservableProperty] private string _runStatus = "Not fitted.";

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    [RelayCommand]
    public async Task FitAsync()
    {
        if (IsRunning) return;
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument first."; return; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return; }
        if (BarCount < 120) { ErrorMessage = "Need at least 120 bars for a meaningful ARIMA/GARCH fit."; return; }
        if (Horizon is < 1 or > 200) { ErrorMessage = "Forecast horizon must be 1–200 bars."; return; }

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

            if (bars.Count < 120)
            {
                ErrorMessage = $"Only {bars.Count} bars returned; need at least 120.";
                return;
            }

            var p = P; var d = D; var q = Q; var auto = AutoOrder; var horizon = Horizon;
            var result = await Task.Run(() => Analyze(bars, p, d, q, auto, horizon), ct);
            if (result is null)
            {
                ErrorMessage = "Fit failed — the regressions were degenerate at every searched order.";
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
            _logger.LogError(ex, "ARIMA/GARCH fit failed");
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

    private static Analysis? Analyze(IReadOnlyList<Bar> bars, int p, int d, int q, bool auto, int horizon)
    {
        var n = bars.Count;
        var logPrices = new double[n];
        var times = new DateTime[n];
        for (var i = 0; i < n; i++)
        {
            logPrices[i] = Math.Log(Math.Max(bars[i].Close, 1e-12));
            times[i] = bars[i].TimestampUtc;
        }

        // Order search (AIC at the user's d) or the pinned (p,q).
        var searchRows = new List<OrderSearchRow>();
        ArimaFit? best = null;
        if (auto)
        {
            for (var pp = 0; pp <= OrderSearchMax; pp++)
            for (var qq = 0; qq <= OrderSearchMax; qq++)
            {
                if (pp == 0 && qq == 0) continue;
                var f = ArimaModel.Fit(logPrices, pp, d, qq);
                if (f is null) continue;
                searchRows.Add(new OrderSearchRow($"({pp},{d},{qq})", f.Aic, f.Bic));
                if (best is null || f.Aic < best.Aic) best = f;
            }
        }
        else
        {
            best = ArimaModel.Fit(logPrices, p, d, q);
            if (best is not null)
                searchRows.Add(new OrderSearchRow($"({p},{d},{q})", best.Aic, best.Bic));
        }
        if (best is null) return null;

        var forecast = ArimaModel.Forecast(best, logPrices, horizon);

        // Forecast timestamps continue the bar grid.
        var step = n >= 2 ? times[^1] - times[^2] : TimeSpan.FromHours(1);
        if (step <= TimeSpan.Zero) step = TimeSpan.FromHours(1);
        var fTimes = new DateTime[horizon];
        for (var h = 0; h < horizon; h++) fTimes[h] = times[^1] + step * (h + 1);

        // Back to price space (log-normal bands).
        var fMean = new double[horizon];
        var fLower = new double[horizon];
        var fUpper = new double[horizon];
        for (var h = 0; h < horizon; h++)
        {
            fMean[h] = Math.Exp(forecast[h].Mean);
            fLower[h] = Math.Exp(forecast[h].Lower95);
            fUpper[h] = Math.Exp(forecast[h].Upper95);
        }

        // GARCH on log returns.
        var returns = new double[n - 1];
        for (var i = 1; i < n; i++) returns[i - 1] = logPrices[i] - logPrices[i - 1];
        var garch = GarchModel.Fit(returns);

        double[] volPct = Array.Empty<double>();
        DateTime[] volTimes = Array.Empty<DateTime>();
        if (garch is not null)
        {
            volPct = new double[garch.ConditionalVariance.Length];
            volTimes = new DateTime[garch.ConditionalVariance.Length];
            for (var i = 0; i < volPct.Length; i++)
            {
                volPct[i] = Math.Sqrt(Math.Max(garch.ConditionalVariance[i], 0)) * 100.0;
                volTimes[i] = times[i + 1];
            }
        }

        var closes = new double[n];
        for (var i = 0; i < n; i++) closes[i] = bars[i].Close;

        searchRows.Sort((a, b) => a.Aic.CompareTo(b.Aic));
        return new Analysis(
            new ArimaGarchChartData(times, closes, fTimes, fMean, fLower, fUpper, volTimes, volPct,
                garch is null ? 0 : Math.Sqrt(Math.Max(garch.LongRunVariance, 0)) * 100.0),
            best, forecast, garch,
            searchRows.Take(6).ToList());
    }

    private void Apply(Analysis a, string symbol, string timeframe)
    {
        ChartData = a.Chart;
        HasResult = true;

        var fit = a.Arima;
        ArimaTitle = $"ARIMA({fit.P},{fit.D},{fit.Q})";
        var ar = fit.ArCoefficients.Length > 0
            ? "φ " + string.Join(", ", fit.ArCoefficients.Select(c => c.ToString("0.000")))
            : "no AR terms";
        var ma = fit.MaCoefficients.Length > 0
            ? "θ " + string.Join(", ", fit.MaCoefficients.Select(c => c.ToString("0.000")))
            : "no MA terms";
        ArimaCoefficients = $"c {fit.Constant:0.00000} · {ar} · {ma} · σ {Math.Sqrt(fit.SigmaSquared):0.00000}";
        ArimaCriteria = $"AIC {fit.Aic:0.0} · BIC {fit.Bic:0.0} · n {fit.N}";

        if (a.Forecast.Length > 0)
        {
            var last = a.Chart.Closes[^1];
            var endMean = a.Chart.ForecastMean[^1];
            var chg = last > 0 ? (endMean / last - 1.0) : 0.0;
            ForecastSummary =
                $"{a.Forecast.Length} bars ahead: {endMean:0.####} ({chg:+0.00%;-0.00%;0.00%} vs last) · " +
                $"95% [{a.Chart.ForecastLower[^1]:0.####} … {a.Chart.ForecastUpper[^1]:0.####}]";
        }

        OrderSearchRows.Clear();
        foreach (var row in a.SearchRows) OrderSearchRows.Add(row);

        if (a.Garch is { } g)
        {
            GarchParams = $"ω {g.Omega:0.000e+0} · α {g.Alpha:0.000} · β {g.Beta:0.000}";
            GarchPersistence = $"{g.Persistence:0.000}";
            GarchPersistenceColor = g.Persistence switch
            {
                >= 0.98 => "#FF5252",  // near-integrated — shocks essentially permanent
                >= 0.90 => "#FFA726",  // typical financial persistence
                _ => "#00C853",
            };
            var halfLife = g.Persistence is > 0 and < 1 ? Math.Log(0.5) / Math.Log(g.Persistence) : double.PositiveInfinity;
            var curVol = g.ConditionalVariance.Length > 0 ? Math.Sqrt(g.ConditionalVariance[^1]) * 100 : 0;
            var nextVol = Math.Sqrt(Math.Max(g.ForecastVariance(1), 0)) * 100;
            var lrVol = Math.Sqrt(Math.Max(g.LongRunVariance, 0)) * 100;
            GarchVolSummary =
                $"current σ {curVol:0.000}%/bar · next-bar {nextVol:0.000}% · long-run {lrVol:0.000}% · " +
                $"vol-shock half-life {(double.IsInfinity(halfLife) ? "∞" : $"{halfLife:0} bars")} · logL {g.LogLikelihood:0.0}";
        }
        else
        {
            GarchParams = "GARCH fit unavailable (series too short or degenerate)";
            GarchPersistence = "—";
            GarchPersistenceColor = "#9E9E9E";
            GarchVolSummary = "";
        }

        RunStatus = $"{a.Chart.Closes.Length} bars · {timeframe} · {symbol}";
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
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ARIMA/GARCH: broker universe load failed, using static catalog");
        }
    }

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="AllInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyInstrumentFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText, SelectedInstrument, MaxInstrumentsDisplayed));

    /// <summary>Remember the last selected instrument so the window reopens on it.</summary>
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) =>
        LastInstrumentStore.Save(InstrumentPersistKey, value?.Contract.Symbol);

    private sealed record Analysis(
        ArimaGarchChartData Chart,
        ArimaFit Arima,
        ArimaForecastPoint[] Forecast,
        GarchFit? Garch,
        IReadOnlyList<OrderSearchRow> SearchRows);
}

/// <summary>Everything the view's chart renderer needs, computed off the UI thread in one shot.</summary>
public sealed record ArimaGarchChartData(
    DateTime[] Times,
    double[] Closes,
    DateTime[] ForecastTimes,
    double[] ForecastMean,
    double[] ForecastLower,
    double[] ForecastUpper,
    DateTime[] VolTimes,
    double[] VolPct,
    double LongRunVolPct);

/// <summary>One row of the AIC order-search table.</summary>
public sealed record OrderSearchRow(string Order, double Aic, double Bic)
{
    public string AicText => Aic.ToString("0.0");
    public string BicText => Bic.ToString("0.0");
}

/// <summary>Bar-size dropdown row. Each tool owns its own copy so the panels stay independent projects.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}
