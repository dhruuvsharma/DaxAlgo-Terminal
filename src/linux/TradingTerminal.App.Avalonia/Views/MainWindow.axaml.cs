using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Avalonia.ViewModels;
using TradingTerminal.App.Avalonia.Views.Strategies;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;

namespace TradingTerminal.App.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // Opens the Avalonia window for the selected strategy. Only Ornstein-Uhlenbeck is ported so far;
    // anything else logs a note to the shared Activity Log. The per-strategy VM is resolved from DI,
    // so the very same portable view-model drives the window.
    private void OnOpenStrategy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var selected = vm.Catalog.SelectedItem;
        var services = (Application.Current as App)?.Services;
        if (selected is null || services is null) return;

        if (selected.Id == "ornstein.uhlenbeck")
        {
            var strategyVm = services.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>();
            new OrnsteinUhlenbeckWindow { DataContext = strategyVm }.Show();
            vm.ActivityLog.Append("Shell", "INFO", "Opened Ornstein-Uhlenbeck strategy window.");
        }
        else
        {
            vm.ActivityLog.Append("Shell", "WARN",
                $"'{selected.DisplayName}' has no Avalonia window yet — only Ornstein-Uhlenbeck is ported.");
        }
    }
}
