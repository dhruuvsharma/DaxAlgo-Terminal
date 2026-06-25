using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Avalonia.Charts;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Avalonia.Shell;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnReconnect(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) _ = vm.ReconnectAllAsync();
    }

    private void OnToggleActivityLog(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsLogVisible = !vm.IsLogVisible;
    }

    /// <summary>Shows a tool/strategy window and — matching the WPF shell — disposes its view-model
    /// when the window closes if the VM owns resources (timers / hub subscriptions / pumps). Without
    /// this the VM (and its render timer + feed buffers) would be pinned for the app's life — RAM
    /// never drops after Close (memory-safety pattern 5).</summary>
    private static void ShowDisposing(Window window, object? viewModel)
    {
        if (viewModel is IDisposable disposable)
            window.Closed += (_, _) => disposable.Dispose();
        window.Show();
    }

    private void OnCharts(object? sender, RoutedEventArgs e)
    {
        ShowDisposing(new ChartsWindow { DataContext = new ChartsViewModel() }, null);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Charts (ScottPlot candlestick).");
    }

    private void OnVolumeFootprint(object? sender, RoutedEventArgs e)
    {
        var vm = new VolumeFootprintViewModel();
        ShowDisposing(new VolumeFootprintWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Volume Footprint.");
    }

    private void OnOrderBook(object? sender, RoutedEventArgs e)
    {
        var vm = new OrderBookViewModel();
        ShowDisposing(new OrderBookWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Order Book.");
    }

    private void OnHeatmap(object? sender, RoutedEventArgs e)
    {
        var vm = new HeatmapViewModel();
        ShowDisposing(new HeatmapWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Liquidity Heatmap.");
    }

    private void OnStationarity(object? sender, RoutedEventArgs e)
    {
        var vm = new MachineLearning.StationarityViewModel();
        ShowDisposing(new MachineLearning.StationarityWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("ML", "INFO", "Opened Stationarity & Differencing.");
    }

    private void OnArimaGarch(object? sender, RoutedEventArgs e)
    {
        var vm = new MachineLearning.ArimaGarchViewModel();
        ShowDisposing(new MachineLearning.ArimaGarchWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("ML", "INFO", "Opened ARIMA & GARCH.");
    }

    private void OnKalman(object? sender, RoutedEventArgs e)
    {
        var vm = new MachineLearning.KalmanViewModel();
        ShowDisposing(new MachineLearning.KalmanWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("ML", "INFO", "Opened Kalman Filter.");
    }

    private void OnCorrelation(object? sender, RoutedEventArgs e)
    {
        var vm = new Tools.CorrelationViewModel();
        ShowDisposing(new Tools.CorrelationWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Correlation Matrix.");
    }

    private void OnFactorResearch(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.FactorResearch.FactorResearchViewModel>();
        ShowDisposing(new TradingTerminal.Ai.FactorResearch.AvaloniaUi.FactorResearchAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened Factor research.");
    }

    private void OnMlFeatures(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.MlFeatures.MlFeaturesViewModel>();
        ShowDisposing(new TradingTerminal.Ai.MlFeatures.AvaloniaUi.MlFeaturesAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened ML features.");
    }

    private void OnBacktestAnalysis(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.BacktestAnalysis.BacktestAnalysisViewModel>();
        ShowDisposing(new TradingTerminal.Ai.BacktestAnalysis.AvaloniaUi.BacktestAnalysisAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened Backtest analysis.");
    }

    // Menu items whose target window is ported in a later step — log a note rather than no-op silently.
    private void OnNotPorted(object? sender, RoutedEventArgs e)
    {
        var header = (sender as MenuItem)?.Header?.ToString()?.Replace("_", "") ?? "That window";
        Vm?.ActivityLog.Append("Shell", "INFO", $"{header} — not yet ported to the cross-platform shell (coming in a later step).");
    }

    // Opens the source paper for a research-derived strategy (the 📄 pill). URL is on the button's Tag.
    private void OnOpenResearchPaper(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url && !string.IsNullOrWhiteSpace(url))
            OpenUrl(url);
    }

    // Opens the selected strategy through the plug-in seam — IStrategyFactory.Create(id). The shell
    // never names a concrete strategy: each strategy project ships its own Avalonia view + registration.
    // The VM is disposed on window close (it owns the render timer + hub subscriptions).
    private void OnOpenStrategy(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } shell || shell.SelectedStrategy is not { } selected) return;
        var services = (Application.Current as App)?.Services;
        if (services is null) return;

        Window? window = null;
        object? strategyVm = null;
        try
        {
            var host = services.GetRequiredService<IStrategyFactory>().Create(selected.Id);
            window = host.View as Window;
            strategyVm = host.ViewModel;
        }
        catch (KeyNotFoundException)
        {
            // Not yet ported — fall through to the warning below.
        }

        if (window is not null)
        {
            ShowDisposing(window, strategyVm);
            shell.ActivityLog.Append("Shell", "INFO", $"Opened '{selected.DisplayName}' strategy window.");
        }
        else
        {
            shell.ActivityLog.Append("Shell", "WARN",
                $"'{selected.DisplayName}' has no Avalonia view registered yet (lands when its project is ported).");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
        }
        catch { /* best-effort: a missing/blocked browser shouldn't crash the shell */ }
    }
}
