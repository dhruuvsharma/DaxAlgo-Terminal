using System.ComponentModel;
using System.Windows;
using MahApps.Metro.Controls;
using ScottPlot.WPF;

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

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.BarsChanged -= OnBarsChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        await _vm.StopCommand.ExecuteAsync(null);
        _vm.Dispose();
    }
}
