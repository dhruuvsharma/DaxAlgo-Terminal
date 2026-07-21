using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Optimization;
using TradingTerminal.Backtest.Engine.Optimization.Gpu;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Backtest.Worker;
using TradingTerminal.UI;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// View-model for the Backtest Studio. Sends supported single runs to the isolated backtest worker,
/// retains explicit in-process paths for not-yet-migrated strategies/optimizers, exposes the resulting
/// report + metrics for the Report tab, and drives the
/// visual replay (a single coalescing render timer walks a cursor across the recorded
/// bars; the view redraws once per frame). Owns a run CTS and the timer, so it is
/// <see cref="IDisposable"/> and tears both down on close.
/// </summary>
public sealed partial class BacktestStudioViewModel : ViewModelBase, IDisposable
{
    private readonly IStrategyKernelRegistry _registry;
    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _instruments;
    private readonly IBacktestJobClient _jobClient;
    private readonly ILogger<BacktestStudioViewModel> _logger;
    private IDisposable? _playback;
    private static readonly InstrumentId SynthInstrument = new(1);

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _optCts;
    private string? _activeWorkerJobId;
    private int _playbackGeneration;

    public BacktestStudioViewModel(
        IStrategyKernelRegistry registry, IMarketDataStore store, IInstrumentRegistry instruments,
        IBacktestJobClient jobClient,
        ILogger<BacktestStudioViewModel> logger)
    {
        _registry = registry;
        _store = store;
        _instruments = instruments;
        _jobClient = jobClient;
        _logger = logger;

        Strategies = new ObservableCollection<StrategyKernelDescriptor>(registry.All);
        Parameters = new ObservableCollection<ParamRowViewModel>();
        Trades = new ObservableCollection<RoundTripTrade>();
        Axes = new ObservableCollection<AxisRowViewModel>();
        OptimizationTrials = new ObservableCollection<TrialRowViewModel>();
        WalkForwardRows = new ObservableCollection<WalkForwardRowViewModel>();
        Criteria = Enum.GetValues<OptimizationCriterion>();
        Methods = Enum.GetValues<OptimizationMethod>();

        SelectedCriterion = OptimizationCriterion.Sharpe;
        SelectedStrategy = Strategies.FirstOrDefault();
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG stays view-side) ──

    /// <summary>Exports the round-trip trades of the last single run.</summary>
    [RelayCommand]
    private async Task ExportTradesCsvAsync()
    {
        if (Trades.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("entry_utc,exit_utc,side,quantity,entry_price,exit_price,gross_pnl,fees");
        foreach (var t in Trades)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{t.EntryUtc:O},{t.ExitUtc:O},{t.Side},{t.Quantity},{t.EntryPrice},{t.ExitPrice},{t.GrossPnl},{t.Fees}"));
        await SaveCsvAsync("studio-trades", sb.ToString());
    }

    /// <summary>Exports the optimization trial grid (score, net profit, trades, parameters).</summary>
    [RelayCommand]
    private async Task ExportTrialsCsvAsync()
    {
        if (OptimizationTrials.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("score,net_profit,trade_count,parameters");
        foreach (var t in OptimizationTrials)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{t.Score},{t.NetProfit},{t.TradeCount},\"{t.Parameters.Replace("\"", "\"\"")}\""));
        await SaveCsvAsync("studio-trials", sb.ToString());
    }

    private async Task SaveCsvAsync(string baseName, string content)
    {
        try
        {
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"{baseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, content);
            Status = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }

    public ObservableCollection<StrategyKernelDescriptor> Strategies { get; }
    public ObservableCollection<ParamRowViewModel> Parameters { get; }
    public ObservableCollection<RoundTripTrade> Trades { get; }
    public ObservableCollection<AxisRowViewModel> Axes { get; }
    public ObservableCollection<TrialRowViewModel> OptimizationTrials { get; }
    public ObservableCollection<WalkForwardRowViewModel> WalkForwardRows { get; }
    public IReadOnlyList<OptimizationCriterion> Criteria { get; }
    public IReadOnlyList<OptimizationMethod> Methods { get; }
    public IReadOnlyList<DataSourceKind> DataSources { get; } = Enum.GetValues<DataSourceKind>();
    public IReadOnlyList<BrokerKind> Brokers { get; } = Enum.GetValues<BrokerKind>();

    private static string GpuExePath => Path.Combine(AppContext.BaseDirectory, "gpu_optimizer.exe");

    // Data source.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecutionTarget))]
    private DataSourceKind _selectedDataSource = DataSourceKind.Synthetic;
    [ObservableProperty] private string _parquetPath = "";
    [ObservableProperty] private string _symbol = "ES";
    [ObservableProperty] private BrokerKind _selectedBroker = BrokerKind.Simulated;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;

    /// <summary>The last completed report — read by the view to draw the equity curve.</summary>
    public BacktestReport? Report { get; private set; }

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string? _lastRunStrategyName;
    [ObservableProperty] private string? _lastRunDataSource;
    [ObservableProperty] private double _lastRunStartingCash;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecutionTarget))]
    private StrategyKernelDescriptor? _selectedStrategy;

    [ObservableProperty] private double _startingCash = 100_000;
    [ObservableProperty] private int _syntheticTicks = 20_000;
    [ObservableProperty] private int _seed = 1;
    [ObservableProperty] private bool _recordVisual = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    [NotifyPropertyChangedFor(nameof(IsNotOptimizing))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRunning;

    [ObservableProperty] private double _runProgress;
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
    [ObservableProperty] private OptimizationMethod _selectedMethod = OptimizationMethod.Exhaustive;
    [ObservableProperty] private int _geneticPopulation = 24;
    [ObservableProperty] private int _geneticGenerations = 10;
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private string? _gpuStatus;
    [ObservableProperty] private int _walkForwardFolds = 4;
    [ObservableProperty] private string? _walkForwardSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotOptimizing))]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isOptimizing;

    [ObservableProperty] private string? _optimizeStatus;
    [ObservableProperty] private OptimizationTrial? _bestTrial;

    /// <summary>2D score grid [y, x] when exactly two axes are swept; null otherwise. Read by the view.</summary>
    public double[,]? SurfaceScores { get; private set; }
    public AxisRowViewModel? SurfaceXAxis { get; private set; }
    public AxisRowViewModel? SurfaceYAxis { get; private set; }

    public bool IsBusy => IsRunning || IsOptimizing;
    public bool IsNotRunning => !IsBusy;
    public bool IsNotOptimizing => !IsBusy;
    public string CurrentBarText => $"{CurrentBar} / {BarCount}";
    public string ExecutionTarget => SelectedStrategy is { } strategy && SupportsWorker(strategy)
        ? "SINGLE RUN · ISOLATED WORKER"
        : "SINGLE RUN · IN-PROCESS";

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
    private async Task BrowseParquet()
    {
        var path = await UiFile.OpenAsync("Parquet files", new[] { "parquet" });
        if (path is not null) ParquetPath = path;
    }

    /// <summary>Builds the feed factory + base run spec for the selected data source. Returns false with
    /// a user-facing <paramref name="error"/> when inputs are incomplete or an instrument can't resolve.</summary>
    private bool TryBuildContext(
        string strategyId, StrategyParameters parameters,
        out Func<IMarketDataFeed> feedFactory, out RunSpec baseSpec, out string? error)
    {
        error = null;
        feedFactory = null!;
        baseSpec = null!;
        var from = NormalizeUtcDate(FromDate);
        var to = NormalizeUtcDate(ToDate);

        switch (SelectedDataSource)
        {
            case DataSourceKind.Synthetic:
            {
                var ticks = SyntheticTicks;
                var seed = Seed;
                // Tape-capable synthetic feed: emits quotes + an aggressor-tagged trade tape in an
                // active (London/NY) session with momentum bursts, so tape-primary, session-gated
                // strategies (e.g. SigmaIcFlow) actually arm and trade. Quote-only kernels ignore the
                // extra trade events.
                feedFactory = () => new SyntheticTapeFeed(SynthInstrument, ticks, seed);
                baseSpec = new RunSpec(
                    Universe.Single(new InstrumentSpec(SynthInstrument, Contract.UsStock("SYN"), 0.25, 1.0)),
                    new DataSpec(), strategyId, parameters, StartingCash: StartingCash);
                return true;
            }
            case DataSourceKind.Parquet:
            {
                if (string.IsNullOrWhiteSpace(ParquetPath) || !File.Exists(ParquetPath))
                {
                    error = "Pick a valid parquet file.";
                    return false;
                }
                var path = ParquetPath;
                feedFactory = () => new ParquetMarketDataFeed(SynthInstrument, path, from, to);
                baseSpec = new RunSpec(
                    Universe.Single(new InstrumentSpec(SynthInstrument, Contract.UsStock("FILE"), 0.01, 1.0)),
                    new DataSpec(BacktestDataSource.ParquetFile, from, to, ParquetPath: path),
                    strategyId, parameters, StartingCash: StartingCash);
                return true;
            }
            case DataSourceKind.Store:
            {
                if (from is null || to is null) { error = "Store mode needs both From and To dates."; return false; }
                if (_instruments.Resolve(SelectedBroker, Symbol) is not { } iid)
                {
                    error = $"'{Symbol}' is not in the store for {SelectedBroker}.";
                    return false;
                }
                feedFactory = () => new StoreMarketDataFeed(_store);
                baseSpec = new RunSpec(
                    Universe.Single(new InstrumentSpec(iid, Contract.UsStock(Symbol), 0.01, 1.0, SelectedBroker)),
                    new DataSpec(BacktestDataSource.LocalStore, from, to),
                    strategyId, parameters, StartingCash: StartingCash);
                return true;
            }
            default:
                error = "Unknown data source.";
                return false;
        }
    }

    private static DateTime? NormalizeUtcDate(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value.Value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy || SelectedStrategy is null) return;

        StopPlayback();
        ResetReportPresentation();
        IsRunning = true;
        RunProgress = 0;
        Status = "Preparing isolated run…";

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var overrides = Parameters.ToDictionary(p => p.Name, p => p.Resolved);
            var descriptor = SelectedStrategy;
            var parameters = descriptor.Schema.Resolve(overrides);
            if (!TryBuildContext(descriptor.Id, parameters, out var feedFactory, out var baseSpec, out var error))
            {
                Status = error;
                return;
            }
            var spec = baseSpec with { Visual = RecordVisual ? VisualRecording.On : VisualRecording.Off };
            var report = await RunSingleAsync(descriptor, feedFactory, spec, ct);

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
            LastRunStrategyName = descriptor.Name;
            LastRunDataSource = SelectedDataSource.ToString();
            LastRunStartingCash = spec.StartingCash;
            HasReport = true;

            RunProgress = 100;
            Status = $"Done. {report.Trades.Count} trades, P&L {report.Summary.NetProfit:C2}, " +
                     $"{report.Summary.EventsProcessed:N0} events in {report.Summary.EngineMilliseconds:F0} ms.";
            ReportReady?.Invoke(this, EventArgs.Empty);
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

    private void ResetReportPresentation()
    {
        Report = null;
        Trades.Clear();
        NetProfit = 0;
        Sharpe = 0;
        MaxDrawdown = 0;
        WinRate = 0;
        ProfitFactor = 0;
        TradeCount = 0;
        HasVisual = false;
        BarCount = 0;
        CurrentBar = 0;
        HasReport = false;
        LastRunStrategyName = null;
        LastRunDataSource = null;
        LastRunStartingCash = 0;
        ReportReady?.Invoke(this, EventArgs.Empty);
        ReplayFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<BacktestReport> RunSingleAsync(
        StrategyKernelDescriptor descriptor,
        Func<IMarketDataFeed> feedFactory,
        RunSpec spec,
        CancellationToken ct)
    {
        if (!SupportsWorker(descriptor))
        {
            RunProgress = 5;
            Status = "Running in-process; this strategy/data source has not migrated to the worker yet…";
            return await Task.Run(
                () => new BacktestEngine(feedFactory()).RunAsync(spec, descriptor.Create(), ct), ct);
        }

        var input = await CreateWorkerInputAsync(ct);
        var jobId = $"studio-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var request = BacktestJobRequest.Create(jobId, spec, input, Seed);
        var engineAssemblyPath = typeof(BacktestEngine).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(engineAssemblyPath) && File.Exists(engineAssemblyPath))
        {
            var expectedHash = await BacktestProtocolHash.ComputeFileSha256Async(engineAssemblyPath, ct);
            request = request with
            {
                Strategy = request.Strategy with { ExpectedAssemblySha256 = expectedHash },
            };
        }
        var progress = new Progress<BacktestJobProgress>(OnWorkerProgress);
        _activeWorkerJobId = jobId;
        BacktestJobOutcome outcome;
        try
        {
            outcome = await _jobClient.RunAsync(request, progress, ct);
        }
        finally
        {
            _activeWorkerJobId = null;
        }

        if (outcome.IsSuccess && outcome.Report is not null)
        {
            _logger.LogInformation(
                "Backtest worker job {JobId} completed; artifacts: {JobDirectory}",
                outcome.JobId,
                outcome.JobDirectory);
            return outcome.Report;
        }

        if (outcome.Status == BacktestTerminalStatus.Cancelled || ct.IsCancellationRequested)
            throw new OperationCanceledException("The isolated backtest was cancelled.", ct);

        var error = outcome.Error?.Message
                    ?? outcome.WorkerStandardError?.Trim()
                    ?? $"Worker ended with status {outcome.Status}.";
        _logger.LogWarning(
            "Backtest worker job {JobId} ended with {Status}: {Error}. Artifacts: {JobDirectory}",
            outcome.JobId,
            outcome.Status,
            error,
            outcome.JobDirectory);

        var mayFallback = outcome.Status == BacktestTerminalStatus.StartFailed &&
                          outcome.Error?.Code is "worker_not_found" or "worker_start_failed";
        if (mayFallback && IsWorkerFallbackEnabled())
        {
            Status = $"Worker {outcome.Status}; using the temporary in-process fallback…";
            return await RunWorkerEquivalentFallbackAsync(input, spec, descriptor, ct);
        }

        throw new InvalidOperationException($"Backtest worker {outcome.Status}: {error}");
    }

    private bool SupportsWorker(StrategyKernelDescriptor descriptor) =>
        (SelectedDataSource is DataSourceKind.Synthetic or DataSourceKind.Parquet) &&
        NativeKernels.All.Any(native =>
            string.Equals(native.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));

    private async Task<BacktestInputReference> CreateWorkerInputAsync(CancellationToken ct)
    {
        if (SelectedDataSource == DataSourceKind.Synthetic)
        {
            return BacktestInputReference.CreateSynthetic(
                SyntheticTicks,
                provenance: "backtest-studio",
                startPrice: 5_000d,
                spread: 0.25d);
        }

        var path = Path.GetFullPath(ParquetPath);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return BacktestInputReference.CreateParquet(
            path,
            sha256,
            length,
            provenance: "backtest-studio");
    }

    private async Task<BacktestReport> RunWorkerEquivalentFallbackAsync(
        BacktestInputReference input,
        RunSpec spec,
        StrategyKernelDescriptor descriptor,
        CancellationToken ct)
    {
        FileStream? parquetLease = null;
        try
        {
            if (input.Kind == BacktestInputKind.Parquet)
                parquetLease = await OpenVerifiedParquetLeaseAsync(input, ct);

            return await Task.Run(
                () => new BacktestEngine(CreateWorkerEquivalentFeed(input, spec))
                    .RunAsync(spec, descriptor.Create(), ct),
                ct);
        }
        finally
        {
            if (parquetLease is not null) await parquetLease.DisposeAsync();
        }
    }

    private static async Task<FileStream> OpenVerifiedParquetLeaseAsync(
        BacktestInputReference input,
        CancellationToken ct)
    {
        var stream = new FileStream(
            input.Path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != input.LengthBytes)
                throw new InvalidDataException("The parquet file changed after the worker request was prepared.");

            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            stream.Position = 0;
            if (!string.Equals(actualHash, input.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The parquet file hash changed after the worker request was prepared.");
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private IMarketDataFeed CreateWorkerEquivalentFeed(BacktestInputReference input, RunSpec spec) =>
        input.Kind switch
        {
            BacktestInputKind.Synthetic => new SyntheticMarketDataFeed(
                spec.Universe.Primary.Id,
                input.Synthetic!.EventCount,
                Seed,
                input.Synthetic.StartPrice,
                input.Synthetic.Spread),
            BacktestInputKind.Parquet => new ParquetMarketDataFeed(
                spec.Universe.Primary.Id,
                input.Path!,
                spec.Data.FromUtc,
                spec.Data.ToUtc),
            _ => throw new NotSupportedException($"Worker input kind '{input.Kind}' is not supported."),
        };

    private void OnWorkerProgress(BacktestJobProgress progress)
    {
        if (!string.Equals(progress.JobId, _activeWorkerJobId, StringComparison.Ordinal))
            return;

        if (progress.PercentComplete is { } percent)
            RunProgress = Math.Clamp(percent, 0, 100);

        var detail = string.IsNullOrWhiteSpace(progress.Message)
            ? progress.Phase.ToString()
            : $"{progress.Phase}: {progress.Message}";
        Status = $"Worker · {detail}";
    }

    private static bool IsWorkerFallbackEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DAXALGO_BACKTEST_IN_PROCESS_FALLBACK"),
            "1",
            StringComparison.Ordinal);

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasVisual) return;
        if (IsPlaying) { StopPlayback(); return; }
        if (CurrentBar >= BarCount) CurrentBar = 0; // restart if parked at the end
        IsPlaying = true;
        var generation = ++_playbackGeneration;
        // Portable playback timer — UI-thread ticks via the UiThread hook (was a WPF DispatcherTimer).
        _playback = UiThread.CreateRenderTimer(
            TimeSpan.FromMilliseconds(33),
            () => OnPlaybackTick(generation));
    }

    [RelayCommand]
    private void Rewind()
    {
        StopPlayback();
        CurrentBar = 0;
    }

    private void OnPlaybackTick(int generation)
    {
        if (!IsPlaying || generation != _playbackGeneration) return;

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
        _playbackGeneration++;
        _playback?.Dispose();
        _playback = null;
        IsPlaying = false;
    }

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (IsBusy || SelectedStrategy is null) return;

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

            var baseParams = new StrategyParameters(Parameters.ToDictionary(p => p.Name, p => p.Resolved));
            if (!TryBuildContext(descriptor.Id, baseParams, out var feedFactory, out var baseSpec, out var error))
            {
                OptimizeStatus = error;
                return;
            }
            var optSpec = new OptimizationSpec(baseSpec, axes, SelectedCriterion, SelectedMethod);

            IStrategyKernel KernelFactory() => descriptor.Create();
            var progress = new Progress<int>(done => OptimizeStatus = $"Evaluating {done:N0} / {total:N0}…");

            OptimizationResult result;
            var usedGpu = false;
            if (SelectedMethod == OptimizationMethod.Genetic)
            {
                var options = new GeneticOptions(PopulationSize: GeneticPopulation, Generations: GeneticGenerations, Seed: Seed);
                result = await Task.Run(() => new GeneticOptimizer(feedFactory, KernelFactory).RunAsync(optSpec, options, progress, ct), ct);
            }
            else if (UseGpu)
            {
                var hybrid = new HybridGridOptimizer(new ProcessGpuOptimizer(GpuExePath), feedFactory, KernelFactory);
                (result, usedGpu) = await Task.Run(() => hybrid.RunAsync(optSpec, progress, ct), ct);
            }
            else
            {
                result = await Task.Run(() => new GridOptimizer(feedFactory, KernelFactory).RunAsync(optSpec, progress, ct), ct);
            }

            GpuStatus = usedGpu ? "Ran on GPU" : (UseGpu ? "GPU unavailable — ran on CPU" : "CPU");

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
    private async Task WalkForwardAsync()
    {
        if (IsBusy || SelectedStrategy is null) return;

        var axisRows = Axes.Where(a => a.Enabled).ToList();
        if (axisRows.Count == 0) { OptimizeStatus = "Enable at least one axis for walk-forward."; return; }

        IsOptimizing = true;
        WalkForwardRows.Clear();
        WalkForwardSummary = null;
        _optCts = new CancellationTokenSource();
        var ct = _optCts.Token;

        try
        {
            var descriptor = SelectedStrategy;
            var axes = axisRows.Select(a => a.ToAxis()).ToList();
            var baseParams = new StrategyParameters(Parameters.ToDictionary(p => p.Name, p => p.Resolved));
            if (!TryBuildContext(descriptor.Id, baseParams, out var feedFactory, out var baseSpec, out var error))
            {
                OptimizeStatus = error;
                return;
            }
            var optSpec = new OptimizationSpec(baseSpec, axes, SelectedCriterion);

            var folds = WalkForwardFolds;
            var result = await Task.Run(async () =>
            {
                var events = new List<MarketEvent>();
                await foreach (var ev in feedFactory().StreamAsync(baseSpec, ct))
                    events.Add(ev);
                return await new WalkForwardOptimizer(events, () => descriptor.Create()).RunAsync(optSpec, folds, ct);
            }, ct);

            foreach (var fold in result.Folds) WalkForwardRows.Add(new WalkForwardRowViewModel(fold));
            WalkForwardSummary = $"{result.Folds.Count} folds · efficiency {result.Efficiency:P0} · OOS net {result.TotalOutOfSampleNetProfit:C0}";
            OptimizeStatus = "Walk-forward complete.";
        }
        catch (OperationCanceledException)
        {
            OptimizeStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Walk-forward failed");
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
        StopPlayback();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _optCts?.Cancel();
        _optCts?.Dispose();
        _optCts = null;
    }
}
