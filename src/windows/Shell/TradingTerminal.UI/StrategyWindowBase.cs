using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MahApps.Metro.Controls;
using ScottPlot.WPF;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI.Controls;

namespace TradingTerminal.UI;

/// <summary>
/// MetroWindow base used by every per-strategy live window. Wires the
/// <see cref="LiveSignalStrategyViewModelBase.BarsChanged"/> signal to a strategy-supplied
/// chart redraw, configures the ScottPlot panels for the dark theme on attach,
/// and tears down the stream when the window closes.
///
/// Subclasses override:
///   * <see cref="ChartHosts"/>   — return the WpfPlot panels to dark-theme on attach
///   * <see cref="OnRedrawCharts"/> — re-render bars + indicator series after each new bar
/// </summary>
public abstract class StrategyWindowBase : MetroWindow
{
    private LiveSignalStrategyViewModelBase? _vm;
    private bool _hostsConfigured;

    protected StrategyWindowBase()
    {
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    /// <summary>The WpfPlot hosts that should receive the dark-theme treatment.</summary>
    protected abstract IEnumerable<WpfPlot> ChartHosts { get; }

    /// <summary>Render the latest bars + indicator series onto the strategy's chart(s).</summary>
    protected abstract void OnRedrawCharts(LiveSignalStrategyViewModelBase vm);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostsConfigured) return;
        foreach (var host in ChartHosts) StrategyChartHelpers.ConfigureDarkPlot(host);
        _hostsConfigured = true;
        if (_vm is not null) OnRedrawCharts(_vm);
    }

    /// <summary>
    /// Wraps the window's content with a shared <see cref="BusyOverlay"/> bound to the base VM's
    /// <see cref="LiveSignalStrategyViewModelBase.IsStarting"/>, so every chart-strategy window shows a
    /// loading curtain while Continue/Start builds the strategy and warms up history — no per-window
    /// XAML required. Runs once, before first render, so the existing visual tree stays intact.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_busyOverlayInjected || Content is not FrameworkElement existing) return;
        _busyOverlayInjected = true;

        Content = null;                 // detach before re-parenting into the wrapper grid
        var grid = new Grid();
        grid.Children.Add(existing);

        var overlay = new BusyOverlay();
        overlay.SetBinding(BusyOverlay.IsActiveProperty, new Binding(nameof(LiveSignalStrategyViewModelBase.IsStarting)));
        overlay.SetBinding(BusyOverlay.TitleProperty, new Binding(nameof(LiveSignalStrategyViewModelBase.LoadingTitle)));
        overlay.SetBinding(BusyOverlay.MessageProperty, new Binding(nameof(LiveSignalStrategyViewModelBase.Status)));
        grid.Children.Add(overlay);

        Content = grid;
    }

    private bool _busyOverlayInjected;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.BarsChanged -= OnBarsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as LiveSignalStrategyViewModelBase;

        if (_vm is not null)
        {
            _vm.BarsChanged += OnBarsChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
            if (_hostsConfigured) OnRedrawCharts(_vm);
        }
    }

    /// <summary>Override to redraw on changes beyond new bars (e.g. parameter edits).</summary>
    protected virtual void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) { }

    private void OnBarsChanged(object? sender, EventArgs e)
    {
        if (_vm is not null && _hostsConfigured) OnRedrawCharts(_vm);
    }

    /// <summary>
    /// Applies the shared live axis controls from the param strip after a subclass has plotted
    /// and auto-scaled: trims the X axis to the last <see cref="LiveSignalStrategyViewModelBase.ChartBarsShown"/>
    /// bars (time-based zoom) and pins the Y axis to a manual range when
    /// <see cref="LiveSignalStrategyViewModelBase.YAutoScale"/> is off. Call at the end of
    /// <see cref="OnRedrawCharts"/>, passing the bars used for the X timeline.
    /// </summary>
    protected static void ApplyAxisControls(ScottPlot.Plot plot, LiveSignalStrategyViewModelBase vm, IReadOnlyList<Bar> bars)
    {
        var n = bars.Count;
        if (n >= 2 && vm.ChartBarsShown > 0 && n > vm.ChartBarsShown)
        {
            var left = bars[n - vm.ChartBarsShown].TimestampUtc.ToOADate();
            var right = bars[n - 1].TimestampUtc.ToOADate();
            if (right > left) plot.Axes.SetLimitsX(left, right);
        }
        if (!vm.YAutoScale && vm.YAxisMax > vm.YAxisMin)
            plot.Axes.SetLimitsY(vm.YAxisMin, vm.YAxisMax);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.BarsChanged -= OnBarsChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        await _vm.StopCommand.ExecuteAsync(null);
        _vm.Dispose();
    }
}
