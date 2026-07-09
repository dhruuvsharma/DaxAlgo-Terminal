using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
#if WINDOWS
using System.Windows.Media;
#endif
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.QuantConnect;
using TradingTerminal.UI;

namespace TradingTerminal.QuantConnect;

/// <summary>
/// Host VM for the QuantConnect / LEAN tool window. Four tabs over a single <see cref="ILeanClient"/>:
/// <list type="bullet">
/// <item>Backtest — pick a project, Run, watch the streamed engine log, then read the statistics and
/// equity curve.</item>
/// <item>Projects — discover algorithm projects in the configured folder.</item>
/// <item>Data — drive <c>lean data download</c>.</item>
/// <item>Settings — edit CLI path / folders and see a live availability status.</item>
/// </list>
/// Long-running calls await the subprocess off the UI thread; streamed log lines arrive via an
/// <see cref="IProgress{T}"/> created on the UI thread, so they marshal back automatically.
/// </summary>
public sealed partial class QuantConnectViewModel : ViewModelBase, IDisposable
{
    // Equity curve is pre-projected into this logical box; a Viewbox stretches it to the panel.
    private const double CurveWidth = 1000, CurveHeight = 260;

    private readonly ILeanClient _lean;
    private readonly LeanRuntimeSettings _settings;
    private readonly ILogger<QuantConnectViewModel> _logger;
    private CancellationTokenSource? _runCts;

    public QuantConnectViewModel(
        ILeanClient lean,
        LeanRuntimeSettings settings,
        ILogger<QuantConnectViewModel> logger)
    {
        _lean = lean;
        _settings = settings;
        _logger = logger;

        CliPath = settings.CliPath;
        ProjectsFolder = settings.ProjectsFolder;
        DataFolder = settings.DataFolder;
        EngineMode = lean.Mode.ToString();

        Projects = new ObservableCollection<LeanProject>();
        Statistics = new ObservableCollection<LeanStatistic>();
#if WINDOWS
        EquityCurve = new PointCollection();
#endif

        _ = InitializeAsync();
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG stays view-side) ──

    /// <summary>Exports the LEAN statistics table of the last run.</summary>
    [RelayCommand]
    private async Task ExportStatsCsvAsync()
    {
        if (Statistics.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("statistic,value");
        foreach (var stat in Statistics)
            sb.AppendLine($"{stat.Name.Replace(',', ';')},{stat.Value.Replace(',', ';')}");
        try
        {
            var project = SelectedProject?.Name?.Replace(' ', '-') ?? "lean";
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"lean-stats-{project}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, sb.ToString());
            BacktestStatus = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LEAN stats CSV export failed");
            BacktestStatus = $"Export failed: {ex.Message}";
        }
    }

    public ObservableCollection<LeanProject> Projects { get; }
    public ObservableCollection<LeanStatistic> Statistics { get; }

    /// <summary>0=Backtest, 1=Projects, 2=Data, 3=Settings — driven by the menu deep-links.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    // ── Settings / availability ───────────────────────────────────────────────────────────────
    [ObservableProperty] private string _cliPath = "";
    [ObservableProperty] private string _projectsFolder = "";
    [ObservableProperty] private string _dataFolder = "";
    [ObservableProperty] private string _engineMode = "LocalCli";
    [ObservableProperty] private string _availabilityStatus = "Checking LEAN…";
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _leanVersion;

    // ── Projects ──────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBusyProjects;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBacktestCommand))]
    private LeanProject? _selectedProject;

    // ── Backtest ──────────────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBacktestCommand))]
    private bool _isRunning;
    [ObservableProperty] private string _runLog = "";
    [ObservableProperty] private string _backtestStatus = "Idle — pick a project and Run.";
#if WINDOWS
    [ObservableProperty] private PointCollection _equityCurve;
#endif
    /// <summary>Portable equity samples (raw values) for the cross-platform head's ScottPlot line.</summary>
    [ObservableProperty] private double[] _equityValues = Array.Empty<double>();
    [ObservableProperty] private bool _hasEquity;

    // ── Data download ─────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _dataDataset = "";
    [ObservableProperty] private string _dataTickers = "";
    [ObservableProperty] private string _dataResolution = "Daily";
    [ObservableProperty] private string _dataLog = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadDataCommand))]
    private bool _isDownloading;

    private async Task InitializeAsync()
    {
        await CheckAvailabilityAsync();
        if (IsAvailable) await RefreshProjectsAsync();
    }

    // ── Commands ──────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckAvailabilityAsync()
    {
        AvailabilityStatus = "Checking LEAN…";
        try
        {
            var a = await _lean.CheckAvailabilityAsync();
            IsAvailable = a.IsAvailable;
            LeanVersion = a.Version;
            AvailabilityStatus = a.Detail;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AvailabilityStatus = $"Availability check failed: {ex.Message}";
            _logger.LogDebug(ex, "LEAN availability check threw");
        }
    }

    [RelayCommand]
    private async Task RefreshProjectsAsync()
    {
        IsBusyProjects = true;
        try
        {
            var list = await _lean.ListProjectsAsync();
            Projects.Clear();
            foreach (var p in list) Projects.Add(p);
            SelectedProject ??= Projects.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing LEAN projects threw");
        }
        finally { IsBusyProjects = false; }
    }

    /// <summary>Apply the edited paths to the shared runtime settings, then re-probe + re-list.</summary>
    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        _settings.CliPath = CliPath?.Trim() ?? "";
        _settings.ProjectsFolder = ProjectsFolder?.Trim() ?? "";
        _settings.DataFolder = DataFolder?.Trim() ?? "";
        await CheckAvailabilityAsync();
        await RefreshProjectsAsync();
    }

    private bool CanRunBacktest() => !IsRunning && SelectedProject is not null;

    [RelayCommand(CanExecute = nameof(CanRunBacktest))]
    private async Task RunBacktestAsync()
    {
        if (SelectedProject is null) return;
        IsRunning = true;
        RunLog = "";
        Statistics.Clear();
        SetEquity(Array.Empty<LeanEquityPoint>());
        BacktestStatus = $"Running backtest for {SelectedProject.Name}…";
        SelectedTabIndex = 0;

        _runCts = new CancellationTokenSource();
        var progress = new Progress<string>(line => RunLog += line + Environment.NewLine);
        try
        {
            var result = await _lean.RunBacktestAsync(
                new LeanBacktestRequest(SelectedProject.Name, _lean.Mode), progress, _runCts.Token);

            foreach (var s in result.Statistics) Statistics.Add(s);
            SetEquity(result.Equity);
            BacktestStatus = result.Success
                ? $"Done — {result.Statistics.Count} statistics, {result.Equity.Count} equity points."
                : $"Failed — {result.Error}";
        }
        catch (Exception ex)
        {
            BacktestStatus = $"Error: {ex.Message}";
            _logger.LogError(ex, "LEAN backtest run threw");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private void CancelRun() => _runCts?.Cancel();

    private bool CanDownload() => !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadDataAsync()
    {
        IsDownloading = true;
        DataLog = "";
        var progress = new Progress<string>(line => DataLog += line + Environment.NewLine);
        try
        {
            var result = await _lean.DownloadDataAsync(
                new LeanDataDownloadRequest(DataDataset?.Trim() ?? "", DataTickers?.Trim() ?? "", DataResolution),
                progress);
            DataLog += result.Success
                ? Environment.NewLine + "[download complete]"
                : Environment.NewLine + $"[failed: {result.Error}]";
        }
        catch (Exception ex)
        {
            DataLog += Environment.NewLine + $"[error: {ex.Message}]";
            _logger.LogError(ex, "LEAN data download threw");
        }
        finally { IsDownloading = false; }
    }

    private void SetEquity(IReadOnlyList<LeanEquityPoint> points)
    {
        // Portable raw samples for the Avalonia head's ScottPlot line.
        EquityValues = points.Select(p => p.Equity).ToArray();
        HasEquity = EquityValues.Length >= 2;
#if WINDOWS
        // WPF: pre-project into a normalized PointCollection for the Polyline.
        var pc = new PointCollection();
        if (points.Count >= 2)
        {
            double min = points.Min(p => p.Equity);
            double max = points.Max(p => p.Equity);
            var range = max - min;
            if (range <= 0) range = 1;
            for (var i = 0; i < points.Count; i++)
            {
                var x = (double)i / (points.Count - 1) * CurveWidth;
                var y = CurveHeight - (points[i].Equity - min) / range * CurveHeight;
                pc.Add(new System.Windows.Point(x, y));
            }
        }
        EquityCurve = pc;
#endif
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }
}
