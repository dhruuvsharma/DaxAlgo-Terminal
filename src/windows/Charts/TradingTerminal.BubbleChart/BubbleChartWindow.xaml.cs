using System.Windows;
using MahApps.Metro.Controls;

namespace TradingTerminal.BubbleChart;

/// <summary>
/// Experimental Bookmap-style window: a <see cref="HeatmapBubbleSurface"/> (liquidity heatmap + trade
/// volume bubbles) plus the instrument/timeframe toolbar and live read-outs. Pure presentation — the
/// <see cref="BubbleChartViewModel"/> owns the data and the broker subscriptions; this code-behind only
/// hands the VM to the surface and releases it on close.
///
/// <para>The surface is created in code-behind and inserted behind the read-out overlay, rather than
/// referenced in XAML, to sidestep this codebase's same-assembly XAML markup-compile (MC3074) issue.</para>
/// </summary>
public partial class BubbleChartWindow : MetroWindow
{
    private readonly HeatmapBubbleSurface _surface = new();

    public BubbleChartWindow()
    {
        InitializeComponent();
        ChartArea.Children.Insert(0, _surface); // behind the floating read-out
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _surface.ViewModel = e.NewValue as BubbleChartViewModel;

    private void OnClosed(object? sender, EventArgs e)
    {
        // The VM is disposed by the shell's OpenWindowTool Closed handler; just release the surface's
        // subscription so a closing window can't be poked by a late render tick.
        _surface.Detach();
    }
}
