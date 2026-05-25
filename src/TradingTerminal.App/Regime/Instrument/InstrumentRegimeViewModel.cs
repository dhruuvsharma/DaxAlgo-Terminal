using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Regime.Instrument;
using TradingTerminal.UI;

namespace TradingTerminal.App.Regime.Instrument;

/// <summary>
/// View-model for the per-instrument regime analyser. Drives an instrument dropdown
/// (<see cref="SignalInstrumentCatalog"/> backed by the connected broker's universe), a
/// timeframe + bar-count picker, and an on-demand Analyze command that pulls bars + optional
/// depth and renders the signed composite breakdown.
/// </summary>
public sealed partial class InstrumentRegimeViewModel : ViewModelBase
{
    private readonly IInstrumentRegimeProvider _provider;
    private readonly IMarketDataRepository _repository;
    private readonly ILogger<InstrumentRegimeViewModel> _logger;
    private CancellationTokenSource? _runCts;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute),
        new("3m",  BarSize.ThreeMinutes),
        new("5m",  BarSize.FiveMinutes),
        new("15m", BarSize.FifteenMinutes),
        new("1h",  BarSize.OneHour),
        new("1d",  BarSize.OneDay),
    };

    public InstrumentRegimeViewModel(
        IInstrumentRegimeProvider provider,
        IMarketDataRepository repository,
        ILogger<InstrumentRegimeViewModel> logger)
    {
        _provider = provider;
        _repository = repository;
        _logger = logger;

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);

        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();
        Signals = new ObservableCollection<InstrumentSignalRow>();

        // Pull the connected broker's tradable universe in the background; replace the static
        // fallback when the list lands.
        _ = LoadInstrumentsAsync();
    }

    public const int MaxInstrumentsDisplayed = 500;

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
    public ObservableCollection<InstrumentSignalRow> Signals { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private int _barCount = 200;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private bool _unavailable = true;
    [ObservableProperty] private double _compositeScore;
    [ObservableProperty] private string _label = "—";
    [ObservableProperty] private string _scoreColor = "#888888";
    [ObservableProperty] private string _lastClose = "—";
    [ObservableProperty] private string _atrPercent = "—";
    [ObservableProperty] private string _volatilityRank = "—";
    [ObservableProperty] private string _depthStatus = "—";
    [ObservableProperty] private string _barsStatus = "—";
    [ObservableProperty] private string _lastUpdated = "never";

    /// <summary>Composite as a 0..100 progress value (so the bar can fill from left). Maps
    /// -100 to 0, +100 to 100. The needle / colour come from <see cref="CompositeScore"/>.</summary>
    public double ProgressValue => Math.Clamp((CompositeScore + 100.0) * 0.5, 0, 100);

    partial void OnCompositeScoreChanged(double value) => OnPropertyChanged(nameof(ProgressValue));

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            AllInstruments = list.Select(i => new SignalInstrument(i.DisplayName, i.Category, i.Contract)).ToList();
            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "AAPL")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instrument regime: broker universe load failed, using static catalog");
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

    [RelayCommand]
    public async Task AnalyzeAsync()
    {
        if (IsAnalyzing) return;
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument first."; return; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return; }
        if (BarCount < 30) { ErrorMessage = "Need at least 30 bars."; return; }

        ErrorMessage = null;
        IsAnalyzing = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var snapshot = await _provider.AnalyseAsync(
                SelectedInstrument.Contract,
                SelectedInstrument.DisplayName,
                SelectedTimeframe.BarSize,
                BarCount,
                ct);
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Instrument regime analyse failed");
            ErrorMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _runCts?.Cancel();
    }

    private void ApplySnapshot(InstrumentRegimeSnapshot s)
    {
        Unavailable = s.Unavailable;
        CompositeScore = s.CompositeScore;
        Label = s.Label;
        ScoreColor = ColorForBand(s.Band);
        LastClose = s.LastClose is double c ? c.ToString("F4") : "—";
        AtrPercent = s.AtrPercent is double a ? $"{a:F2} %" : "—";
        VolatilityRank = s.VolatilityRank is double v ? $"{v:F0} pct" : "—";
        BarsStatus = $"{s.BarCount} bars · {s.Timeframe}";
        DepthStatus = s.DepthLevels > 0 ? $"{s.DepthLevels} levels" : "no L2";
        LastUpdated = s.GeneratedAtUtc == DateTime.MinValue ? "never" : s.GeneratedAtUtc.ToString("HH:mm:ss") + " UTC";

        Signals.Clear();
        foreach (var sig in s.Signals)
            Signals.Add(new InstrumentSignalRow(
                Name: SignalLabel(sig.Signal),
                Score: sig.Score,
                Weight: sig.Weight,
                Contribution: sig.Contribution,
                Valid: sig.Valid,
                Detail: sig.Detail,
                BarValue: Math.Clamp((sig.Score + 1.0) * 50.0, 0, 100),
                BarColor: sig.Valid ? (sig.Score >= 0 ? "#00C853" : "#FF1744") : "#666666"));
    }

    private static string ColorForBand(InstrumentRegimeBand band) => band switch
    {
        InstrumentRegimeBand.StrongSell => "#FF1744",
        InstrumentRegimeBand.Sell       => "#FF5252",
        InstrumentRegimeBand.Neutral    => "#888888",
        InstrumentRegimeBand.Buy        => "#69F0AE",
        InstrumentRegimeBand.StrongBuy  => "#00C853",
        _                                => "#888888",
    };

    private static string SignalLabel(InstrumentRegimeSignal s) => s switch
    {
        InstrumentRegimeSignal.Trend                => "Trend",
        InstrumentRegimeSignal.Momentum             => "Momentum",
        InstrumentRegimeSignal.Strength             => "RSI strength",
        InstrumentRegimeSignal.MeanReversion        => "Mean-reversion",
        InstrumentRegimeSignal.Volume               => "Volume",
        InstrumentRegimeSignal.CumulativeImbalance  => "Cumulative imbalance",
        InstrumentRegimeSignal.ObiShallow           => "OBI (shallow)",
        InstrumentRegimeSignal.ObiDeep              => "OBI (deep)",
        _                                            => s.ToString(),
    };
}

/// <summary>UI row for one sub-signal. <see cref="BarValue"/> maps Score [-1,+1] to [0,100]
/// so a ProgressBar can render it; <see cref="BarColor"/> is green when bullish, red when
/// bearish, grey when invalid.</summary>
public sealed record InstrumentSignalRow(
    string Name,
    double Score,
    double Weight,
    double Contribution,
    bool Valid,
    string Detail,
    double BarValue,
    string BarColor)
{
    public string WeightPct => Valid ? $"{Weight * 100:F0} %" : "—";
    public string ScoreText => Valid ? Score.ToString("+0.00;-0.00;0.00") : "—";
    public string ContributionText => Valid ? Contribution.ToString("+0.0;-0.0;0.0") : "—";
}

/// <summary>Bar-size dropdown row. Display label and BarSize value pair.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}
