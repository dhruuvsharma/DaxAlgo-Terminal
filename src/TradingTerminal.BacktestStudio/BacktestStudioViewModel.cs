using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Optimization;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// View-model for the Backtest Studio. Runs the new <see cref="BacktestEngine"/> over a synthetic
/// feed (zero-setup), exposes the resulting report + metrics for the Report tab, and drives the
/// visual replay (a single playback <see cref="DispatcherTimer"/> walks a cursor across the recorded
/// bars; the view redraws once per frame). Owns a run CTS and the timer, so it is
/// <see cref="IDisposable"/> and tears both down on close.
/// </summary>
public sealed partial class BacktestStudioViewModel : ViewModelBase, IDisposable
{
    private readonly IStrategyKernelRegistry _registry;
    private readonly ILogger<BacktestStudioViewModel> _logger;
    private readonly DispatcherTimer _playback;
    private static readonly InstrumentId SynthInstrument = new(1);

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _optCts;

    public BacktestStudioViewModel(IStrategyKernelRegistry registry, ILogger<BacktestStudioViewModel> logger)
    {
        _registry = registry;
        _logger = logger;

        Strategies = new ObservableCollection<StrategyKernelDescriptor>(registry.All);
        Parameters = new ObservableCollection<ParamRowViewModel>();
        Trades = new ObservableCollection<RoundTripTrade>();
        Axes = new ObservableCollection<AxisRowViewModel>();
        OptimizationTrials = new ObservableCollection<TrialRowViewModel>();
        Criteria = Enum.GetValues<OptimizationCriterion>();

        _playback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playback.Tick += OnPlaybackTick;

        SelectedCriterion = OptimizationCriterion.Sharpe;
        SelectedStrategy = Strategies.FirstOrDefault();
    }

    public ObservableCollection<StrategyKernelDescriptor> Strategies { get; }
    public ObservableCollection<ParamRowViewModel> Parameters { get; }
    public ObservableCollection<RoundTripTrade> Trades { get; }
    public ObservableCollection<AxisRowViewModel> Axes { get; }
    public ObservableCollection<TrialRowViewModel> OptimizationTrials { get; }
    public IReadOnlyList<OptimizationCriterion> Criteria { get; }

    /// <summary>The last completed report — read by the view to draw the equity curve.</summary>
    public BacktestReport? Report { get; private set; }

    [ObservableProperty]
    private StrategyKernelDescriptor? _selectedStrategy;

    [ObservableProperty] private double _startingCash = 100_000;
    [ObservableProperty] private int _syntheticTicks = 5_000;
    [ObservableProperty] private int _seed = 1;
    [ObservableProperty] private bool _recordVisual = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    private bool _isRunning;

    [ObservableProperty] private string? _status;

    // Report metrics (set after a run).
    [ObservableProperty] private double _netProfit;
    [ObservableProperty] private double _sharpe;
    [ObservableProperty] private double _maxDrawdown;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private double _profitFactor;
    [ObservableProperty] private int _tradeCount;

    // Visual replay.
    [ObservableProperty] private bool _hasVisual;
    [ObservableProperty] private int _barCount;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _playbackSpeed = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBarText))]
    private int _currentBar;

    // Optimization.
    [ObservableProperty] private OptimizationCriterion _selectedCriterion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotOptimizing))]
    private bool _isOptimizing;

    [ObservableProperty] private string? _optimizeStatus;
    [ObservableProperty] private OptimizationTrial? _bestTrial;

    /// <summary>2D score grid [y, x] when exactly two axes are swept; null otherwise. Read by the view.</summary>
    public double[,]? SurfaceScores { get; private set; }
    public AxisRowViewModel? SurfaceXAxis { get; private set; }
    public AxisRowViewModel? SurfaceYAxis { get; private set; }

    public bool IsNotRunning => !IsRunning;
    public bool IsNotOptimizing => !IsOptimizing;
    public string CurrentBarText => $"{CurrentBar} / {BarCount}";

    /// <summary>Raised after a sweep so the view can draw the 2D score heatmap.</summary>
    public event EventHandler? OptimizationReady;

    /// <summary>Raised after a run so the view can draw the equity curve and reset the replay chart.</summary>
    public event EventHandler? ReportReady;

    /// <summary>Raised each replay frame (and on seek) so the view redraws candles up to <see cref="CurrentBar"/>.</summary>
    public event EventHandler? ReplayFrameChanged;

    partial void OnSelectedStrategyChanged(StrategyKernelDescriptor? value)
    {
        Parameters.Clear();
        Axes.Clear();
        if (value is null) return;
        foreach (var p in value.Schema.Parameters)
        {
            Parameters.Add(new ParamRowViewModel(p));
            Axes.Add(new AxisRowViewModel(p));
        }
    }

    partial void OnCurrentBarChanged(int value) => ReplayFrameChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning || SelectedStrategy is null) return;

        StopPlayback();
        IsRunning = true;
        Status = "Running…";
        Trades.Clear();
        Report = null;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var overrides = Parameters.ToDictionary(p => p.Name, p => p.Resolved);
            var descriptor = SelectedStrategy;
            var spec = new RunSpec(
                Universe: Universe.Single(new InstrumentSpec(SynthInstrument, Contract.UsStock("SYN"), 0.01, 1.0)),
                Data: new DataSpec(),
                StrategyId: descriptor.Id,
                Parameters: descriptor.Schema.Resolve(overrides),
                StartingCash: StartingCash,
                Visual: RecordVisual ? VisualRecording.On : VisualRecording.Off);

            var feed = new SyntheticMarketDataFeed(SynthInstrument, SyntheticTicks, Seed);
            var report = await Task.Run(() =>
                new BacktestEngine(feed).RunAsync(spec, descriptor.Create(), ct), ct);

            Report = report;
            foreach (var t in report.Trades) Trades.Add(t);

            NetProfit = report.Summary.NetProfit;
            Sharpe = report.Metrics.Sharpe;
            MaxDrawdown = report.Metrics.MaxDrawdown;
            WinRate = report.Metrics.WinRate;
            ProfitFactor = report.Metrics.ProfitFactor;
            TradeCount = report.Trades.Count;

            HasVisual = report.Visual is { Bars.Count: > 0 };
            BarCount = report.Visual?.Bars.Count ?? 0;
            CurrentBar = BarCount; // show the whole run; replay rewinds from here

            Status = $"Done. {report.Trades.Count} trades, P&L {report.Summary.NetProfit:C2}, " +
                     $"{report.Summary.EventsProcessed:N0} events in {report.Summary.EngineMilliseconds:F0} ms.";
            ReportReady?.Invoke(this, EventArgs.Empty);
            ReplayFrameChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest Studio run failed");
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasVisual) return;
        if (IsPlaying) { StopPlayback(); return; }
        if (CurrentBar >= BarCount) CurrentBar = 0; // restart if parked at the end
        IsPlaying = true;
        _playback.Start();
    }

    [RelayCommand]
    private void Rewind()
    {
        StopPlayback();
        CurrentBar = 0;
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        var next = CurrentBar + Math.Max(1, (int)PlaybackSpeed);
        if (next >= BarCount)
        {
            CurrentBar = BarCount;
            StopPlayback();
        }
        else
        {
            CurrentBar = next;
        }
    }

    private void StopPlayback()
    {
        _playback.Stop();
        IsPlaying = false;
    }

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (IsOptimizing || SelectedStrategy is null) return;

        var axisRows = Axes.Where(a => a.Enabled).ToList();
        if (axisRows.Count == 0) { OptimizeStatus = "Enable at least one parameter as a sweep axis."; return; }

        IsOptimizing = true;
        OptimizationTrials.Clear();
        BestTrial = null;
        SurfaceScores = null;
        _optCts = new CancellationTokenSource();
        var ct = _optCts.Token;

        try
        {
            var descriptor = SelectedStrategy;
            var axes = axisRows.Select(a => a.ToAxis()).ToList();
            var total = axes.Aggregate(1L, (acc, ax) => acc * Math.Max(1, ax.Values.Count));

            var baseParams = Parameters.ToDictionary(p => p.Name, p => p.Resolved);
            var baseSpec = new RunSpec(
                Universe.Single(new InstrumentSpec(SynthInstrument, Contract.UsStock("SYN"), 0.01, 1.0)),
                new DataSpec(), descriptor.Id, new StrategyParameters(baseParams), StartingCash: StartingCash);
            var optSpec = new OptimizationSpec(baseSpec, axes, SelectedCriterion);

            var ticks = SyntheticTicks;
            var seed = Seed;
            var optimizer = new GridOptimizer(
                () => new SyntheticMarketDataFeed(SynthInstrument, ticks, seed),
                () => descriptor.Create());

            var progress = new Progress<int>(done => OptimizeStatus = $"Evaluating {done:N0} / {total:N0}…");
            var result = await Task.Run(() => optimizer.RunAsync(optSpec, progress, ct), ct);

            foreach (var trial in result.Trials.Take(1000)) OptimizationTrials.Add(new TrialRowViewModel(trial));
            BestTrial = result.Best;
            BuildSurface(axisRows, result);
            OptimizeStatus = $"Done. Best {SelectedCriterion} = {result.Best?.Score:F3} over {result.Evaluations:N0} runs.";
            OptimizationReady?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            OptimizeStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Optimization failed");
            OptimizeStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsOptimizing = false;
            _optCts?.Dispose();
            _optCts = null;
        }
    }

    [RelayCommand]
    private void CancelOptimize() => _optCts?.Cancel();

    [RelayCommand]
    private void ApplyBest()
    {
        if (BestTrial is null) return;
        foreach (var row in Parameters)
            if (BestTrial.Parameters.TryGetValue(row.Name, out var v))
                row.Value = v;
    }

    private void BuildSurface(IReadOnlyList<AxisRowViewModel> axisRows, OptimizationResult result)
    {
        SurfaceScores = null;
        SurfaceXAxis = null;
        SurfaceYAxis = null;
        if (axisRows.Count != 2) return;

        var x = axisRows[0];
        var y = axisRows[1];
        var xs = x.ToAxis().Values;
        var ys = y.ToAxis().Values;
        var grid = new double[ys.Count, xs.Count];
        for (var yi = 0; yi < ys.Count; yi++)
            for (var xi = 0; xi < xs.Count; xi++)
                grid[yi, xi] = double.NaN;

        foreach (var t in result.Trials)
        {
            if (!t.Parameters.TryGetValue(x.Name, out var xv) || !t.Parameters.TryGetValue(y.Name, out var yv)) continue;
            grid[NearestIndex(ys, yv), NearestIndex(xs, xv)] = t.Score;
        }

        SurfaceScores = grid;
        SurfaceXAxis = x;
        SurfaceYAxis = y;
    }

    private static int NearestIndex(IReadOnlyList<double> values, double v)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < values.Count; i++)
        {
            var d = Math.Abs(values[i] - v);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    public void Dispose()
    {
        _playback.Tick -= OnPlaybackTick;
        _playback.Stop();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _optCts?.Cancel();
        _optCts?.Dispose();
        _optCts = null;
    }
}
