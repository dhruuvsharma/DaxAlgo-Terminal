using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Shell;
// Pro-only per-tool projects (not referenced by App.Core).
using TradingTerminal.BubbleChart;
using TradingTerminal.SurfaceLab;
using TradingTerminal.LseBacktest;
using TradingTerminal.QuantConnect;
using TradingTerminal.Ml.Stationarity;
using TradingTerminal.Ml.ArimaGarch;
using TradingTerminal.Ml.KalmanFilter;
using TradingTerminal.Ai.MarketAnalyst;
using TradingTerminal.Ai.FactorResearch;
using TradingTerminal.Ai.MlFeatures;
using TradingTerminal.Ai.BacktestAnalysis;
using TradingTerminal.Ai.PaperLab;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Professional-edition tier-exclusive launch commands: the LSE backtester, the Machine-Learning
/// windows (Stationarity / ARIMA-GARCH / Kalman), the AI panels (Factor research / ML features /
/// Backtest analysis / Market analyst / Paper Lab), QuantConnect / LEAN, the 3D Surface Lab and the
/// experimental Volume-Bubble-Line chart. Registered as <see cref="IShellExtendedToolCommands"/>; the
/// shell VM exposes it as <c>ExtendedTools</c> and the Professional menu binds Pro-only items through it.
/// Every open reuses the shared <see cref="IShellWindowHost"/> for single-instance / dispose behaviour.
/// </summary>
public sealed partial class ProfessionalToolCommands : ObservableObject, IShellExtendedToolCommands
{
    // Stable per-window keys for the single-instance window registry (owned by IShellWindowHost).
    private const string LseBacktestWindowId = "lse.backtest";
    private const string ResearchWindowId = "tools.research";
    private const string MlFeaturesWindowId = "ai.mlfeatures";
    private const string BacktestAnalysisWindowId = "ai.backtestanalysis";
    private const string AiAnalystWindowId = "ai.marketanalyst";
    private const string PaperLabWindowId = "ai.paperlab";
    private const string StationarityWindowId = "ml.stationarity";
    private const string ArimaGarchWindowId = "ml.arimagarch";
    private const string KalmanFilterWindowId = "ml.kalmanfilter";
    private const string QuantConnectWindowId = "tools.quantconnect";
    private const string BubbleChartWindowId = "charts.bubbleline";
    private const string SurfaceLabWindowId = "charts.surfacelab";

    private readonly IShellWindowHost _host;
    private readonly IServiceProvider _services;
    private readonly ILogger<ProfessionalToolCommands> _logger;

    public ProfessionalToolCommands(
        IShellWindowHost host,
        IServiceProvider services,
        ILogger<ProfessionalToolCommands> logger)
    {
        _host = host;
        _services = services;
        _logger = logger;
    }

    [RelayCommand]
    public void OpenLseBacktest() =>
        _host.OpenHostedTool<LseBacktestViewModel, LseBacktestView>(LseBacktestWindowId, "LSE backtester", "Loading the LSE backtester…");

    [RelayCommand]
    public void OpenResearch() =>
        _host.OpenHostedTool<FactorResearchViewModel, FactorResearchView>(ResearchWindowId, "Factor research", "Loading factor research…");

    [RelayCommand]
    public void OpenMlFeatures() =>
        _host.OpenHostedTool<MlFeaturesViewModel, MlFeaturesView>(MlFeaturesWindowId, "ML features", "Computing feature definitions…");

    [RelayCommand]
    public void OpenBacktestAnalysis() =>
        _host.OpenHostedTool<BacktestAnalysisViewModel, BacktestAnalysisView>(BacktestAnalysisWindowId, "Backtest analysis", "Loading backtest analysis…");

    [RelayCommand]
    public void OpenAiAnalyst() =>
        _host.OpenHostedTool<AiAnalystViewModel, AiAnalystView>(AiAnalystWindowId, "AI market analyst", "Connecting to the AI analyst…");

    [RelayCommand]
    public void OpenPaperLab() =>
        _host.OpenHostedTool<PaperLabViewModel, PaperLabView>(PaperLabWindowId, "Paper Lab", "Loading Paper Lab…");

    // Experimental: price line + per-bar growing volume bubble. Kept separate from the live charts.
    [RelayCommand]
    public void OpenBubbleChart() =>
        _host.OpenWindowTool<BubbleChartViewModel, BubbleChartWindow>(BubbleChartWindowId, "Volume bubble line", "Building the volume-bubble line…");

    [RelayCommand]
    public void OpenSurfaceLab() =>
        _host.OpenHostedTool<SurfaceLabViewModel, SurfaceLabView>(SurfaceLabWindowId, "3D Surface Lab", "Preparing the surface workspace…");

    [RelayCommand]
    public void OpenStationarity() =>
        _host.OpenHostedTool<StationarityViewModel, StationarityView>(StationarityWindowId, "Stationarity & differencing", "Loading the time-series workspace…");

    [RelayCommand]
    public void OpenArimaGarch() =>
        _host.OpenHostedTool<ArimaGarchViewModel, ArimaGarchView>(ArimaGarchWindowId, "ARIMA & GARCH", "Loading the time-series workspace…");

    [RelayCommand]
    public void OpenKalmanFilter() =>
        _host.OpenHostedTool<KalmanFilterViewModel, KalmanFilterView>(KalmanFilterWindowId, "Kalman filter", "Loading the time-series workspace…");

    // ── QuantConnect / LEAN ─────────────────────────────────────────────────────────────────
    // One single-instance window with four tabs; each menu item deep-links to a tab index.
    [RelayCommand] public void OpenQuantConnectBacktest() => OpenQuantConnect(0);
    [RelayCommand] public void OpenQuantConnectProjects() => OpenQuantConnect(1);
    [RelayCommand] public void OpenQuantConnectData() => OpenQuantConnect(2);
    [RelayCommand] public void OpenQuantConnectSettings() => OpenQuantConnect(3);

    private QuantConnectViewModel? _quantConnectVm;

    private void OpenQuantConnect(int tab)
    {
        if (_host.IsOpen(QuantConnectWindowId) && _quantConnectVm is not null)
        {
            _quantConnectVm.SelectedTabIndex = tab;
            _host.TryActivate(QuantConnectWindowId);
            return;
        }

        _host.OpenWithOverlay("Opening QuantConnect / LEAN…", "Loading the LEAN workspace…", () =>
        {
            var vm = _services.GetRequiredService<QuantConnectViewModel>();
            vm.SelectedTabIndex = tab;
            var window = _services.GetRequiredService<QuantConnectWindow>();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            window.Closed += (_, _) =>
            {
                _host.Unregister(QuantConnectWindowId);
                _quantConnectVm = null;
                vm.Dispose();
            };
            _host.Register(QuantConnectWindowId, window);
            _quantConnectVm = vm;
            window.Show();
            _logger.LogInformation("Opened QuantConnect / LEAN window (tab {Tab})", tab);
        });
    }
}
