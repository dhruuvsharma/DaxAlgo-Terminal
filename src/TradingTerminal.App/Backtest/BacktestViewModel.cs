using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtest.Fast;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.UI;

namespace TradingTerminal.App.Backtest;

/// <summary>
/// View-model for the Backtest tab. Holds the run configuration, kicks off the engine
/// via <see cref="IBacktestSession"/> on a background task, and exposes the resulting
/// trades + equity curve + stats for binding. The XAML embeds a <c>ScottPlot.WPF.WpfPlot</c>
/// and gets the equity samples through <see cref="EquityCurve"/>.
/// </summary>
public sealed partial class BacktestViewModel : ViewModelBase
{
    private readonly ILogger<BacktestViewModel> _logger;
    private readonly IBacktestSession _session;
    private readonly IFastBacktestRunner _fastRunner;
    private CancellationTokenSource? _runCts;

    public BacktestViewModel(
        IBacktestStrategyRegistry registry,
        IBacktestSession session,
        IFastBacktestRunner fastRunner,
        ILogger<BacktestViewModel> logger)
    {
        _logger = logger;
        _session = session;
        _fastRunner = fastRunner;
        Strategies = new ObservableCollection<BacktestStrategyOption>(registry.All);
        SelectedStrategy = Strategies.FirstOrDefault();
        Trades = new ObservableCollection<Trade>();
        EquityCurve = new ObservableCollection<EquityPoint>();
    }

    public ObservableCollection<BacktestStrategyOption> Strategies { get; }
    public ObservableCollection<Trade> Trades { get; }
    public ObservableCollection<EquityPoint> EquityCurve { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFastAvailable))]
    private BacktestStrategyOption? _selectedStrategy;

    [ObservableProperty] private bool _useFastEngine;
    [ObservableProperty] private string _symbol = "ES";
    [ObservableProperty] private string _dataPath = "";
    [ObservableProperty] private DateTime? _fromUtc;
    [ObservableProperty] private DateTime? _toUtc;
    [ObservableProperty] private double _tickSize = 0.25;
    [ObservableProperty] private int _slippageTicks = 1;
    [ObservableProperty] private double _contractMultiplier = 50;
    [ObservableProperty] private double _startingCash = 100_000;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;

    [ObservableProperty] private BacktestStatistics? _stats;
    [ObservableProperty] private double _totalPnl;

    /// <summary>
    /// True when the C++ tick_backtester is on disk AND the selected strategy is wired
    /// on the C++ side (BacktestStrategyOption.Fast). The Fast checkbox in the XAML
    /// binds IsEnabled to this so it greys out for unsupported strategies.
    /// </summary>
    public bool IsFastAvailable =>
        _fastRunner.IsAvailable && (SelectedStrategy?.Fast ?? false);

    /// <summary>Raised after a run completes so the view can redraw the ScottPlot equity curve.</summary>
    public event EventHandler? EquityCurveUpdated;

    [RelayCommand]
    public void BrowseDataPath()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose tick parquet file",
            Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true) DataPath = dlg.FileName;
    }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (SelectedStrategy is null) { Status = "Select a strategy."; return; }
        if (string.IsNullOrWhiteSpace(DataPath) || !File.Exists(DataPath))
        {
            Status = "Pick a valid parquet file before running.";
            return;
        }

        IsRunning = true;
        Status = "Running…";
        Trades.Clear();
        EquityCurve.Clear();
        Stats = null;
        TotalPnl = 0;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var contract = Contract.UsStock(Symbol);

        try
        {
            if (UseFastEngine && IsFastAvailable)
            {
                var request = new FastBacktestRequest(
                    StrategyId: SelectedStrategy.Id,
                    Symbol: Symbol,
                    TickDataParquetPath: DataPath,
                    TickSize: TickSize,
                    ContractMultiplier: ContractMultiplier,
                    StartingCash: StartingCash,
                    SlippageTicks: SlippageTicks);

                var fast = await _fastRunner.RunAsync(request, ct);

                Stats = fast.Stats;
                TotalPnl = fast.EndingCash - StartingCash;
                Status = $"Done (Fast / C++). {fast.Stats.TradeCount} trades, P&L {TotalPnl.ToString("C2", CultureInfo.CurrentCulture)} in {fast.EngineMilliseconds:F0} ms.";
                EquityCurveUpdated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                var config = new BacktestConfig(
                    Contract: contract,
                    TickDataPath: DataPath,
                    FromUtc: FromUtc?.ToUniversalTime(),
                    ToUtc: ToUtc?.ToUniversalTime(),
                    TickSize: TickSize,
                    SlippageTicks: SlippageTicks,
                    ContractMultiplier: ContractMultiplier,
                    StartingCash: StartingCash);

                var strategy = SelectedStrategy.Build(contract);
                var result = await Task.Run(() =>
                    _session.RunAsync(config, strategy, risk: null, ct), ct);

                foreach (var t in result.Trades) Trades.Add(t);
                foreach (var p in result.EquityCurve) EquityCurve.Add(p);
                Stats = result.Stats;
                TotalPnl = result.EndingCash - result.StartingCash;
                Status = $"Done. {result.Trades.Count} trades, P&L {TotalPnl.ToString("C2", CultureInfo.CurrentCulture)}.";
                EquityCurveUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest run failed");
            Status = $"Failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _runCts?.Cancel();
    }
}
