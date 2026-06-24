using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Avalonia.Charts;
using TradingTerminal.App.Avalonia.Strategies;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.FilteredOrderFlow;
using TradingTerminal.Strategies.ImbalanceHeatFront;
using TradingTerminal.Strategies.OrderFlowToxicity;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.Strategies.SigmaIcFlow;
using TradingTerminal.Strategies.VolatilityTargeted;
using TradingTerminal.UI;

namespace TradingTerminal.App.Avalonia.Shell;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnVolumeFootprint(object? sender, RoutedEventArgs e)
    {
        new VolumeFootprintWindow { DataContext = new VolumeFootprintViewModel() }.Show();
        (DataContext as MainWindowViewModel)?.ActivityLog.Append("Charts", "INFO", "Opened Volume Footprint.");
    }

    private void OnOrderBook(object? sender, RoutedEventArgs e)
    {
        new OrderBookWindow { DataContext = new OrderBookViewModel() }.Show();
        (DataContext as MainWindowViewModel)?.ActivityLog.Append("Charts", "INFO", "Opened Order Book.");
    }

    private void OnToggleActivityLog(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Border>("ActivityDrawer") is { } drawer)
            drawer.IsVisible = !drawer.IsVisible;
    }

    // Opens the Avalonia window for the selected strategy. Ported strategies resolve their portable
    // VM from DI; Ornstein-Uhlenbeck has a bespoke window (with param editors), the others use the
    // GenericStrategyWindow (common base surface). Unported strategies log a note to the Activity Log.
    private void OnOpenStrategy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var selected = vm.Catalog.SelectedItem;
        var services = (Application.Current as App)?.Services;
        if (selected is null || services is null) return;

        Window? window = selected.Id switch
        {
            "ornstein.uhlenbeck" => new OrnsteinUhlenbeckWindow
            {
                DataContext = services.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>(),
            },
            "vol.targeted" => Generic(services.GetRequiredService<VolatilityTargetedStrategyViewModel>()),
            "order.flow.toxicity" => Generic(services.GetRequiredService<OrderFlowToxicityStrategyViewModel>()),
            "filtered.orderflow.imbalance" => Generic(services.GetRequiredService<FilteredOrderFlowViewModel>()),
            "imbalance.heatfront" => Generic(services.GetRequiredService<ImbalanceHeatFrontViewModel>()),
            "sigma.ic.flow" => Generic(services.GetRequiredService<SigmaIcFlowStrategyViewModel>()),
            "cumulative.delta.scalper" => new CumulativeDeltaWindow
            {
                DataContext = services.GetRequiredService<CumulativeDeltaViewModel>(),
            },
            _ => null,
        };

        if (window is not null)
        {
            window.Show();
            vm.ActivityLog.Append("Shell", "INFO", $"Opened '{selected.DisplayName}' strategy window.");
        }
        else
        {
            vm.ActivityLog.Append("Shell", "WARN",
                $"'{selected.DisplayName}' has no Avalonia window yet — ported so far: Ornstein-Uhlenbeck, " +
                "Volatility Targeted, Order-Flow Toxicity.");
        }
    }

    private static GenericStrategyWindow Generic(LiveSignalStrategyViewModelBase vm) =>
        new() { DataContext = vm };
}
