using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.IO;
#if WINDOWS
using System.Windows;
#endif
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtest.Fast;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.UI;
using TradingTerminal.UI.Presets;

namespace TradingTerminal.Backtest;

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
        PresetNames = new ObservableCollection<string>(_presetStore.Names);
    }

    // ── Named presets (run config: engine, strategy, costs; never symbol/dates) ──

    private readonly ToolPresetStore<BacktestRunPreset> _presetStore = new("backtest");

    public ObservableCollection<string> PresetNames { get; }

    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string? _selectedPreset;

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null) return;
        PresetName = value;
        if (_presetStore.Get(value) is { } preset) ApplyPreset(preset);
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0) return;
        _presetStore.Save(name, new BacktestRunPreset(
            SelectedStrategy?.Id, UseFastEngine, TickSize, SlippageTicks, ContractMultiplier, StartingCash));
        RefreshPresetNames(selected: name);
        _logger.LogInformation("Backtest: preset '{Name}' saved", name);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var name = SelectedPreset ?? PresetName.Trim();
        if (string.IsNullOrEmpty(name) || !_presetStore.Delete(name)) return;
        RefreshPresetNames(selected: null);
        _logger.LogInformation("Backtest: preset '{Name}' deleted", name);
    }

    private void ApplyPreset(BacktestRunPreset preset)
    {
        if (preset.StrategyId is { Length: > 0 } id &&
            Strategies.FirstOrDefault(o => o.Id == id) is { } match)
            SelectedStrategy = match;
        UseFastEngine = preset.UseFastEngine;
        if (preset.TickSize > 0) TickSize = preset.TickSize;
        if (preset.SlippageTicks >= 0) SlippageTicks = preset.SlippageTicks;
        if (preset.ContractMultiplier > 0) ContractMultiplier = preset.ContractMultiplier;
        if (preset.StartingCash > 0) StartingCash = preset.StartingCash;
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG stays view-side) ──

    /// <summary>Exports the trade list of the last run.</summary>
    [RelayCommand]
    private async Task ExportTradesCsvAsync()
    {
        if (Trades.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("entry_utc,exit_utc,side,quantity,entry_price,exit_price,gross_pnl");
        foreach (var t in Trades)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{t.EntryUtc:O},{t.ExitUtc:O},{t.Side},{t.Quantity},{t.EntryPrice},{t.ExitPrice},{t.GrossPnl}"));
        await SaveCsvAsync("backtest-trades", sb.ToString());
    }

    /// <summary>Exports the equity curve of the last run.</summary>
    [RelayCommand]
    private async Task ExportEquityCsvAsync()
    {
        if (EquityCurve.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,equity");
        foreach (var pt in EquityCurve)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{pt.TimestampUtc:O},{pt.Equity}"));
        await SaveCsvAsync("backtest-equity", sb.ToString());
    }

    private async Task SaveCsvAsync(string baseName, string content)
    {
        try
        {
            var strategy = SelectedStrategy?.Id ?? "run";
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"{baseName}-{strategy}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, content);
            Status = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backtest CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
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
    public async Task BrowseDataPath()
    {
        // Portable file picker (WPF Win32 dialog / Avalonia StorageProvider) via the UiFile seam.
        var path = await UiFile.OpenAsync("Parquet files", new[] { "parquet" });
        if (!string.IsNullOrEmpty(path)) DataPath = path;
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
#if WINDOWS
            MessageBox.Show(ex.Message, "Backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
#endif
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

/// <summary>A named snapshot of the backtest run configuration (strategy, engine and cost
/// model), persisted per user by <see cref="ToolPresetStore{T}"/> (tool-presets/backtest.json).
/// Symbol, data path and date range are run-specific and deliberately excluded.</summary>
public sealed record BacktestRunPreset(
    string? StrategyId,
    bool UseFastEngine,
    double TickSize,
    int SlippageTicks,
    double ContractMultiplier,
    double StartingCash);
