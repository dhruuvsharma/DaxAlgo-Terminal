using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.Rsi;

public sealed partial class RsiStrategyViewModel : ViewModelBase, IDisposable
{
    public const int MaxBarsRetained = 300;
    public const int RsiPeriod = RsiCalculator.DefaultPeriod;

    private readonly IMarketDataRepository _repository;
    private readonly ILogger<RsiStrategyViewModel> _logger;
    private CancellationTokenSource? _streamCts;

    public RsiStrategyViewModel(IMarketDataRepository repository, ILogger<RsiStrategyViewModel> logger)
    {
        _repository = repository;
        _logger = logger;

        Instruments = InstrumentCatalog.All;
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments[0];

        Bars = new ObservableCollection<Bar>();
        RsiSeries = Array.Empty<double>();
    }

    public IReadOnlyList<TradeableInstrument> Instruments { get; }

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

        var contract = SelectedInstrument.Contract;
        var size = BarSize.OneMinute;

        Status = $"Loading {SelectedInstrument.DisplayName} history…";
        Bars.Clear();
        RsiSeries = Array.Empty<double>();
        BarsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var historical = await _repository.GetHistoricalBarsAsync(contract, size,
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

        _ = RunStreamAsync(contract, size, streamCt);
    }

    private async Task RunStreamAsync(Contract contract, BarSize size, CancellationToken ct)
    {
        try
        {
            await foreach (var bar in _repository.SubscribeBarsAsync(contract, size, ct))
            {
                AppendBar(bar);
                EvaluateSignal();
                BarsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RSI stream ended");
            Status = $"Stream stopped: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
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

        var label = SelectedInstrument?.DisplayName ?? "(none)";
        if (rsi >= Overbought)
            _logger.LogInformation("[RSI ARMED] {Symbol} OVERBOUGHT — RSI={Rsi:F2} (would short)", label, rsi);
        else if (rsi <= Oversold)
            _logger.LogInformation("[RSI ARMED] {Symbol} OVERSOLD — RSI={Rsi:F2} (would long)", label, rsi);
    }

    public async Task StopStreamAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
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
    }
}
