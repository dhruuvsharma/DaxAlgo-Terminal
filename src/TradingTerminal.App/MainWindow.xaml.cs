using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;

namespace TradingTerminal.App;

public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.StartAsync();
    }

    private void StrategiesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedStrategy is not null)
            vm.OpenStrategyCommand.Execute(vm.SelectedStrategy.Id);
    }

    private void OpenStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedStrategy is not null)
            vm.OpenStrategyCommand.Execute(vm.SelectedStrategy.Id);
    }
}
