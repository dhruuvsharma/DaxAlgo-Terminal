using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.UI;

namespace TradingTerminal.App.Ai;

/// <summary>
/// Backtest analysis tab: combines two pre-deployment diagnostics every quant runs before
/// shipping a strategy. Walk-forward (rolling train/test windows) gauges whether a chosen
/// parameter set actually holds out-of-sample; trade-bootstrap Monte Carlo gauges whether
/// the equity curve survives reordering of trades. Both wrap the same Core/Infrastructure
/// types the CLI's `walkforward` and `mc` subcommands use, so the numbers match.
/// </summary>
public sealed partial class BacktestAnalysisViewModel : ViewModelBase
{
    private readonly ILogger<BacktestAnalysisViewModel> _logger;

    public BacktestAnalysisViewModel(ILogger<BacktestAnalysisViewModel> logger)
    {
        _logger = logger;
        StrategyChoices = new[] { "meanReversion", "donchianBreakout", "microprice", "ou" };
        SelectedStrategy = "meanReversion";
    }

    public IReadOnlyList<string> StrategyChoices { get; }
    public ObservableCollection<WalkForwardRow> WalkForwardRows { get; } = new();

    [ObservableProperty] private string _selectedStrategy = "meanReversion";
    [ObservableProperty] private string _symbol = "TEST";
    [ObservableProperty] private string _dataPath = "";
    [ObservableProperty] private int _windows = 5;
    [ObservableProperty] private double _trainFraction = 0.7;
    [ObservableProperty] private int _quantity = 1;
    [ObservableProperty] private string _lookbacks = "50,100,200";
    [ObservableProperty] private string _entries = "0.05,0.10,0.20";
    [ObservableProperty] private string _stops = "0.20,0.40";
    [ObservableProperty] private string _trails = "0.10,0.20,0.40";
    [ObservableProperty] private string _thresholds = "0.0005,0.001,0.002";
    [ObservableProperty] private string _holds = "20,50,100";
    [ObservableProperty] private string _entryZ = "1.5,2.0,2.5";

    [ObservableProperty] private bool _isWalkForwardRunning;
    [ObservableProperty] private string _walkForwardStatus = "Pick a parquet tick file and press Run.";
    [ObservableProperty] private string? _walkForwardError;

    [ObservableProperty] private string _tradesCsvPath = "";
    [ObservableProperty] private int _simulations = 10_000;
    [ObservableProperty] private double _startingCash = 100_000;
    [ObservableProperty] private int _seed = -1;
    [ObservableProperty] private bool _isMonteCarloRunning;
    [ObservableProperty] private string _monteCarloStatus = "Pick a trades.csv (from a prior backtest run) and press Run.";
    [ObservableProperty] private string? _monteCarloError;
    [ObservableProperty] private string _monteCarloSummary = "";

    [RelayCommand]
    private void BrowseData()
    {
        var dlg = new OpenFileDialog { Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) DataPath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowseTradesCsv()
    {
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) TradesCsvPath = dlg.FileName;
    }

    [RelayCommand]
    private async Task RunWalkForwardAsync()
    {
        WalkForwardError = null;
        WalkForwardRows.Clear();

        if (string.IsNullOrWhiteSpace(DataPath) || !File.Exists(DataPath))
        {
            WalkForwardError = "Pick a valid parquet file first.";
            return;
        }
        if (Windows < 2) { WalkForwardError = "Windows must be ≥ 2."; return; }
        if (TrainFraction <= 0.1 || TrainFraction >= 0.95) { WalkForwardError = "TrainFraction must be in (0.1, 0.95)."; return; }

        IsWalkForwardRunning = true;
        WalkForwardStatus = "Scanning dataset time range…";

        try
        {
            DateTime? minTs = null, maxTs = null;
            long tickCount = 0;
            await foreach (var t in ParquetTickReader.ReadAsync(DataPath, ct: CancellationToken.None))
            {
                minTs ??= t.TimestampUtc;
                maxTs = t.TimestampUtc;
                tickCount++;
            }
            if (minTs is null || maxTs is null || tickCount == 0)
            {
                WalkForwardError = "Dataset is empty.";
                return;
            }

            var contract = Contract.UsStock(Symbol);
            var totalSpan = maxTs.Value - minTs.Value;
            var winSpan = totalSpan / Windows;

            var baseConfig = new BacktestConfig(
                Contract: contract,
                TickDataPath: DataPath,
                TickSize: 0.01,
                SlippageTicks: 0,
                ContractMultiplier: 1.0,
                StartingCash: 100_000);

            var grid = WalkForwardGridBuilders.Build(
                SelectedStrategy,
                lookbacks: ParseIntList(Lookbacks),
                entries: ParseDoubleList(Entries),
                stops: ParseDoubleList(Stops),
                trails: ParseDoubleList(Trails),
                thresholds: ParseDoubleList(Thresholds),
                holds: ParseIntList(Holds),
                entryZ: ParseDoubleList(EntryZ),
                quantity: Quantity);

            WalkForwardStatus = $"Running {Windows} windows × {grid.Count} configs…";

            for (var w = 0; w < Windows; w++)
            {
                var winStart = minTs.Value + winSpan * w;
                var winEnd = (w == Windows - 1) ? maxTs.Value : winStart + winSpan;
                var trainCutoff = winStart + (winEnd - winStart) * TrainFraction;

                var trainResults = new List<(string Label, Func<Contract, IBacktestStrategy> Build, double Sharpe)>();
                foreach (var cell in grid)
                {
                    var cfg = baseConfig with { FromUtc = winStart, ToUtc = trainCutoff };
                    var r = await Task.Run(() => new BacktestSession().RunAsync(cfg, cell.Builder(contract)));
                    trainResults.Add((cell.Label, cell.Builder, r.Stats?.Sharpe ?? double.MinValue));
                }
                var best = trainResults.OrderByDescending(t => t.Sharpe).First();

                var oosCfg = baseConfig with { FromUtc = trainCutoff, ToUtc = winEnd };
                var oosResult = await Task.Run(() => new BacktestSession().RunAsync(oosCfg, best.Build(contract)));
                var os = oosResult.Stats;

                WalkForwardRows.Add(new WalkForwardRow(
                    Window: w,
                    TrainFromUtc: winStart,
                    TrainToUtc: trainCutoff,
                    TestFromUtc: trainCutoff,
                    TestToUtc: winEnd,
                    BestParams: best.Label,
                    TrainSharpe: best.Sharpe,
                    OosTrades: os?.TradeCount ?? 0,
                    OosReturn: os?.TotalReturn ?? 0,
                    OosSharpe: os?.Sharpe ?? 0,
                    OosMaxDrawdown: os?.MaxDrawdown ?? 0,
                    OosEndingCash: oosResult.EndingCash));

                WalkForwardStatus = $"Window {w + 1} / {Windows} done";
            }

            WalkForwardStatus = $"Done. {Windows} windows.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Walk-forward failed");
            WalkForwardError = ex.Message;
            WalkForwardStatus = "Failed.";
        }
        finally
        {
            IsWalkForwardRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunMonteCarloAsync()
    {
        MonteCarloError = null;
        MonteCarloSummary = "";

        if (string.IsNullOrWhiteSpace(TradesCsvPath) || !File.Exists(TradesCsvPath))
        {
            MonteCarloError = "Pick a valid trades.csv first.";
            return;
        }
        if (Simulations < 100) { MonteCarloError = "Simulations must be ≥ 100."; return; }

        IsMonteCarloRunning = true;
        MonteCarloStatus = "Parsing trades…";

        try
        {
            var pnls = await Task.Run(() => ParseGrossPnls(TradesCsvPath));
            if (pnls.Count == 0)
            {
                MonteCarloError = "No parseable trades in the CSV. Expected a 'GrossPnl' column.";
                return;
            }

            MonteCarloStatus = $"Running {Simulations} resamples on {pnls.Count} trades…";
            var result = await Task.Run(() => MonteCarlo.Run(pnls, StartingCash, Simulations, Seed));

            var ic = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "                       P5         P25         P50         P75         P95",
                $"Final equity  {Fmt(result.FinalEquityPercentiles, "F2", ic)}",
                $"Sharpe        {Fmt(result.SharpePercentiles,      "F4", ic)}",
                $"Max drawdown  {Fmt(result.MaxDrawdownPercentiles, "F4", ic)}",
                "",
                $"Mean final equity   : {result.MeanFinalEquity.ToString("F2", ic)}  (σ {result.StdFinalEquity.ToString("F2", ic)})",
                $"Mean Sharpe         : {result.MeanSharpe.ToString("F4", ic)}  (σ {result.StdSharpe.ToString("F4", ic)})",
                $"Mean max drawdown   : {result.MeanMaxDrawdown.ToString("P2", ic)}",
                $"P(profit > 0)       : {result.ProbabilityOfProfit.ToString("P1", ic)}",
            };
            MonteCarloSummary = string.Join(Environment.NewLine, lines);
            MonteCarloStatus = $"Done. {Simulations} simulations on {pnls.Count} trades.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monte Carlo failed");
            MonteCarloError = ex.Message;
            MonteCarloStatus = "Failed.";
        }
        finally
        {
            IsMonteCarloRunning = false;
        }
    }

    private static List<double> ParseGrossPnls(string path)
    {
        var pnls = new List<double>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return pnls;

        var header = lines[0].Split(',');
        int grossPnlIdx = -1;
        for (var i = 0; i < header.Length; i++)
            if (header[i].Equals("GrossPnl", StringComparison.OrdinalIgnoreCase) ||
                header[i].Equals("gross_pnl", StringComparison.OrdinalIgnoreCase))
                grossPnlIdx = i;
        if (grossPnlIdx < 0) return pnls;

        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            if (cells.Length <= grossPnlIdx) continue;
            if (double.TryParse(cells[grossPnlIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var pnl))
                pnls.Add(pnl);
        }
        return pnls;
    }

    private static string Fmt(IReadOnlyList<double> p, string fmt, CultureInfo ic) =>
        string.Join("  ", p.Select(v => v.ToString(fmt, ic).PadLeft(10)));

    private static int[] ParseIntList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

    private static double[] ParseDoubleList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();
}

public sealed record WalkForwardRow(
    int Window,
    DateTime TrainFromUtc,
    DateTime TrainToUtc,
    DateTime TestFromUtc,
    DateTime TestToUtc,
    string BestParams,
    double TrainSharpe,
    int OosTrades,
    double OosReturn,
    double OosSharpe,
    double OosMaxDrawdown,
    double OosEndingCash);
