using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Regime.Markov;
using TradingTerminal.UI;

namespace TradingTerminal.MarkovRegime;

/// <summary>
/// View-model for the Markov Regime Detection tool. Pulls a window of historical bars for the
/// selected instrument/timeframe from <see cref="IMarketDataRepository"/>, fits a Gaussian HMM via
/// <see cref="MarkovRegimeDetector"/> off the UI thread, and exposes the labelled states, the
/// transition matrix, and the current regime. The price + regime-ribbon chart is drawn by the
/// view's code-behind on the <see cref="RegimeUpdated"/> event (the sanctioned ScottPlot-in-MVVM
/// pattern — same as the Backtest view).
/// </summary>
public sealed partial class MarkovRegimeViewModel : ViewModelBase
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<MarkovRegimeViewModel> _logger;
    private CancellationTokenSource? _runCts;

    public const int MaxInstrumentsDisplayed = 500;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute),
        new("3m",  BarSize.ThreeMinutes),
        new("5m",  BarSize.FiveMinutes),
        new("15m", BarSize.FifteenMinutes),
        new("1h",  BarSize.OneHour),
        new("1d",  BarSize.OneDay),
    };

    public MarkovRegimeViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<MarkovRegimeViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);
        StateCounts = new ObservableCollection<int> { 2, 3, 4 };

        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        States = new ObservableCollection<MarkovStateRow>();
        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<int> StateCounts { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
    public ObservableCollection<MarkovStateRow> States { get; }

    /// <summary>The latest fit, for the view's chart renderer. Null until the first successful Fit.</summary>
    public MarkovRegimeResult? Result { get; private set; }

    /// <summary>Raised after a successful fit so the code-behind can redraw the ScottPlot chart.</summary>
    public event EventHandler? RegimeUpdated;

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private int _stateCount = 3;
    [ObservableProperty] private int _barCount = 400;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isFitting;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _currentLabel = "—";
    [ObservableProperty] private string _currentProbability = "—";
    [ObservableProperty] private string _currentColor = "#888888";
    [ObservableProperty] private string _transitionMatrixText = "";
    [ObservableProperty] private string _fitStatus = "Not fitted.";
    [ObservableProperty] private string _barsStatus = "—";

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    [RelayCommand]
    public async Task FitAsync()
    {
        if (IsFitting) return;
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument first."; return; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return; }

        var minBars = MarkovRegimeDetector.MinBars(StateCount);
        if (BarCount < minBars) { ErrorMessage = $"Need at least {minBars} bars for {StateCount} states."; return; }

        ErrorMessage = null;
        IsFitting = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var broker = ResolveBroker(SelectedInstrument);
            var duration = EstimateDuration(SelectedTimeframe.BarSize, BarCount);
            var bars = await _repository.GetHistoricalBarsAsync(
                SelectedInstrument.Contract, broker, SelectedTimeframe.BarSize, duration, ct);

            if (bars.Count < minBars)
            {
                ErrorMessage = $"Only {bars.Count} bars returned; need {minBars} for {StateCount} states.";
                return;
            }

            // The fit is CPU-bound (EM over the whole window) — keep it off the UI thread.
            var states = StateCount;
            var result = await Task.Run(() => MarkovRegimeDetector.Detect(bars, states), ct);
            Apply(result, SelectedInstrument.DisplayName, SelectedTimeframe.Label);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Markov regime fit failed");
            ErrorMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsFitting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    private void Apply(MarkovRegimeResult result, string symbol, string timeframe)
    {
        Result = result;
        HasResult = true;

        States.Clear();
        foreach (var s in result.States.OrderByDescending(s => s.MeanLogReturn))
            States.Add(new MarkovStateRow(
                Label: s.Label.ToString(),
                MeanPct: (Math.Exp(s.MeanLogReturn) - 1.0).ToString("+0.000%;-0.000%;0.000%"),
                DurationBars: double.IsInfinity(s.ExpectedDurationBars) ? "∞" : $"{s.ExpectedDurationBars:F0}",
                OccupancyPct: s.OccupancyProbability.ToString("0.0%"),
                StationaryPct: s.StationaryProbability.ToString("0.0%"),
                OccupancyBar: Math.Clamp(s.OccupancyProbability * 100.0, 0, 100),
                Color: ColorFor(s.Label)));

        var current = result.Current;
        if (current is not null)
        {
            CurrentLabel = current.Label.ToString();
            var p = current.Posterior[current.State];
            CurrentProbability = $"p = {p:0.0%}";
            CurrentColor = ColorFor(current.Label);
        }

        TransitionMatrixText = FormatTransitionMatrix(result);
        FitStatus = $"{(result.Converged ? "Converged" : "Stopped")} in {result.Iterations} iters · logL {result.LogLikelihood:F1}";
        BarsStatus = $"{result.Series.Count + 1} bars · {timeframe} · {symbol}";

        RegimeUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatTransitionMatrix(MarkovRegimeResult r)
    {
        // Short per-state tags by mean rank so columns are readable for K = 2..4.
        var tags = r.States.OrderBy(s => s.Index).Select(s => Tag(s.Label)).ToArray();
        var sb = new StringBuilder();
        sb.Append("from\\to ");
        for (var j = 0; j < r.StateCount; j++) sb.Append($"  →{tags[j],-4}");
        sb.AppendLine();
        for (var i = 0; i < r.StateCount; i++)
        {
            sb.Append($"{tags[i],-7} ");
            for (var j = 0; j < r.StateCount; j++) sb.Append($"  {r.TransitionMatrix[i][j],5:0.00}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string Tag(RegimeLabel label) => label switch
    {
        RegimeLabel.Bearish => "Bear",
        RegimeLabel.Bullish => "Bull",
        _ => "Neut",
    };

    /// <summary>Hex colour per regime label, shared by the cards and the chart ribbon.</summary>
    public static string ColorFor(RegimeLabel label) => label switch
    {
        RegimeLabel.Bearish => "#FF5252",
        RegimeLabel.Bullish => "#00C853",
        _ => "#9E9E9E",
    };

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
        BarSize.ThreeMinutes   => TimeSpan.FromMinutes(barCount * 3 * 1.5),
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
            // Broker is shown as a coloured pill by the dropdown template — keep DisplayName clean.
            AllInstruments = list.Select(i => new SignalInstrument(
                i.DisplayName, i.Category, i.Contract, i.Broker)).ToList();
            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Markov regime: broker universe load failed, using static catalog");
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
}

/// <summary>One labelled state row for the summary table.</summary>
public sealed record MarkovStateRow(
    string Label,
    string MeanPct,
    string DurationBars,
    string OccupancyPct,
    string StationaryPct,
    double OccupancyBar,
    string Color);

/// <summary>Bar-size dropdown row. Display label and BarSize value pair. Each tool owns its own
/// copy so the regime panels stay independent projects.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}
