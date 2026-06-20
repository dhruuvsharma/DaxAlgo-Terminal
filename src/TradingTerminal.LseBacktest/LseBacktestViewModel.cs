using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.UI;

namespace TradingTerminal.LseBacktest;

/// <summary>
/// View-model for the LSE backtester. Unlike the regular Backtest tool — which replays a local
/// parquet tick file — this one pulls historical OHLCV bars straight from the London Strategic
/// Edge broker (<see cref="BrokerKind.LondonStrategicEdge"/>) over its REST API, synthesizes an
/// intrabar quote path from each bar, writes those synthetic ticks to a throwaway temp parquet,
/// then replays them through the shared engine (<see cref="IBacktestSession"/>). The temp file is
/// deleted when the run finishes, so the user never has to capture or hold data locally.
///
/// Because the source is bar data (no genuine prints or depth), strategies that consume the trade
/// tape or order book won't see real microstructure — the synthetic spread is one tick wide. This
/// is best for L1/bar strategies; that limitation is surfaced in the window's hint text.
/// </summary>
public sealed partial class LseBacktestViewModel : ViewModelBase
{
    private const BrokerKind Lse = BrokerKind.LondonStrategicEdge;

    private readonly ILogger<LseBacktestViewModel> _logger;
    private readonly IBacktestSession _session;
    private readonly IBrokerSelector _brokers;
    private CancellationTokenSource? _runCts;

    public LseBacktestViewModel(
        IBacktestStrategyRegistry registry,
        IBacktestSession session,
        IBrokerSelector brokers,
        ILogger<LseBacktestViewModel> logger)
    {
        _logger = logger;
        _session = session;
        _brokers = brokers;
        Strategies = new ObservableCollection<BacktestStrategyOption>(registry.All);
        SelectedStrategy = Strategies.FirstOrDefault();
        BarSizes = new ObservableCollection<BarSize>(new[]
        {
            BarSize.OneMinute, BarSize.FiveMinutes, BarSize.FifteenMinutes, BarSize.OneHour, BarSize.OneDay,
        });
        Trades = new ObservableCollection<Trade>();
        EquityCurve = new ObservableCollection<EquityPoint>();
    }

    public ObservableCollection<BacktestStrategyOption> Strategies { get; }
    public ObservableCollection<BarSize> BarSizes { get; }
    public ObservableCollection<Trade> Trades { get; }
    public ObservableCollection<EquityPoint> EquityCurve { get; }

    [ObservableProperty] private BacktestStrategyOption? _selectedStrategy;
    [ObservableProperty] private string _symbol = "AAPL";
    [ObservableProperty] private BarSize _selectedBarSize = BarSize.FiveMinutes;
    [ObservableProperty] private int _lookbackBars = 1000;
    [ObservableProperty] private double _tickSize = 0.01;
    [ObservableProperty] private int _slippageTicks = 1;
    [ObservableProperty] private double _contractMultiplier = 1;
    [ObservableProperty] private double _startingCash = 100_000;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;

    [ObservableProperty] private BacktestStatistics? _stats;
    [ObservableProperty] private double _totalPnl;

    /// <summary>Raised after a run completes so the view can redraw the ScottPlot equity curve.</summary>
    public event EventHandler? EquityCurveUpdated;

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (SelectedStrategy is null) { Status = "Select a strategy."; return; }
        if (string.IsNullOrWhiteSpace(Symbol)) { Status = "Enter a symbol (e.g. AAPL, EUR/USD, BTC/USD)."; return; }
        if (!_brokers.IsAvailable(Lse))
        {
            Status = "London Strategic Edge is not registered. Check the LSE API key in configuration.";
            return;
        }
        if (LookbackBars is < 1 or > 5000)
        {
            Status = "Lookback bars must be between 1 and 5000 (LSE caps a single request at 5000).";
            return;
        }

        IsRunning = true;
        Status = "Fetching bars from London Strategic Edge…";
        Trades.Clear();
        EquityCurve.Clear();
        Stats = null;
        TotalPnl = 0;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var contract = new Contract(Symbol.Trim(), "STK", "LSE", "USD", PrimaryExchange: string.Empty);
        var barSpan = SelectedBarSize.ToTimeSpan();
        var duration = barSpan * LookbackBars;
        string? tempPath = null;

        try
        {
            var client = _brokers.Get(Lse);
            var bars = await client.RequestHistoricalBarsAsync(contract, SelectedBarSize, duration, ct)
                .ConfigureAwait(true);

            if (bars.Count == 0)
            {
                Status = $"No bars returned from LSE for '{contract.Symbol}'. Check the symbol and timeframe.";
                return;
            }

            Status = $"Replaying {bars.Count} {SelectedBarSize} bars through the engine…";

            tempPath = Path.Combine(Path.GetTempPath(), $"lse-backtest-{Guid.NewGuid():N}.parquet");
            await WriteSyntheticTicksAsync(tempPath, bars, barSpan, TickSize, ct).ConfigureAwait(true);

            var config = new BacktestConfig(
                Contract: contract,
                TickDataPath: tempPath,
                TickSize: TickSize,
                SlippageTicks: SlippageTicks,
                ContractMultiplier: ContractMultiplier,
                StartingCash: StartingCash,
                Source: BacktestDataSource.ParquetFile);

            var strategy = SelectedStrategy.Create(contract);
            var result = await Task.Run(() => _session.RunAsync(config, strategy, risk: null, ct), ct)
                .ConfigureAwait(true);

            foreach (var t in result.Trades) Trades.Add(t);
            foreach (var p in result.EquityCurve) EquityCurve.Add(p);
            Stats = result.Stats;
            TotalPnl = result.EndingCash - result.StartingCash;
            Status = $"Done. {result.Trades.Count} trades over {bars.Count} bars, " +
                     $"P&L {TotalPnl.ToString("C2", CultureInfo.CurrentCulture)}.";
            EquityCurveUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSE backtest run failed");
            Status = $"Failed: {ex.Message}";
            MessageBox.Show(ex.Message, "LSE backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not delete temp tick file {Path}", tempPath); }
            }
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _runCts?.Cancel();

    /// <summary>
    /// Projects OHLCV bars into a synthetic quote stream the tick-primary engine can replay.
    /// Each bar becomes four prints walking open → (low/high, ordered by the bar's direction so the
    /// extremes are visited in a plausible order) → close, spread evenly across the bar's interval.
    /// Each print is a one-tick-wide quote centred on the price; per-print size is the bar volume
    /// split four ways. Timestamps are strictly increasing, which the engine's clock requires.
    /// </summary>
    private static async Task WriteSyntheticTicksAsync(
        string path, IReadOnlyList<Bar> bars, TimeSpan barSpan, double tickSize, CancellationToken ct)
    {
        var half = Math.Max(tickSize, 1e-9) / 2.0;
        var step = barSpan / 4;

        await using var writer = new ParquetTickWriter(path);
        foreach (var bar in bars)
        {
            ct.ThrowIfCancellationRequested();

            var path4 = bar.Close >= bar.Open
                ? new[] { bar.Open, bar.Low, bar.High, bar.Close }
                : new[] { bar.Open, bar.High, bar.Low, bar.Close };
            var sizePer = Math.Max(1, bar.Volume / 4);

            for (var i = 0; i < path4.Length; i++)
            {
                var px = path4[i];
                var ts = bar.TimestampUtc + step * i;
                await writer.WriteAsync(new Tick(ts, px - half, px + half, sizePer, sizePer), ct)
                    .ConfigureAwait(false);
            }
        }
    }
}
