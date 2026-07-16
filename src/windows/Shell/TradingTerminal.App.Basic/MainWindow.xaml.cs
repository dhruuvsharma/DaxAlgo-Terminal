using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MahApps.Metro.Controls;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.App;

public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // Auto-scroll the Activity Log to the newest entry (console "tail" behaviour).
        if (LogList.Items is INotifyCollectionChanged incc)
            incc.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.StartAsync();
    }

    private void StrategiesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Route by the row that was actually double-clicked rather than the selection, so a double-click
        // on empty list space does nothing.
        var container = ItemsControl.ContainerFromElement(StrategiesList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is StrategyCatalogItemViewModel item)
            vm.OpenStrategyCommand.Execute(item.Id);
    }

    // The floating Vibe Quant button — left-click opens the builder. Right-click is left to its attached
    // ContextMenu (Vibe Quant · Launch CLI), which WPF opens for us.
    private void VibeFab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenStrategyAuthoringCommand.Execute(null);
    }

    // Catalog card right-click → Edit: opens the presentation editor for the selected strategy.
    private void EditStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.EditStrategyCommand.Execute(null);
    }

    // Right-click selects the row under the cursor, so Open / Quick-backtest / Edit act on it.
    private void StrategyItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OpenStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedStrategy is not null)
            vm.OpenStrategyCommand.Execute(vm.SelectedStrategy.Id);
    }

    private void QuickBacktest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedStrategy is not null)
            vm.QuickBacktestCommand.Execute(vm.SelectedStrategy.Id);
    }

    // A "Launch CLI" menu item (in the top Strategy Studio menu, or the Vibe Quant button's right-click
    // menu). Bound by EventSetter so it works regardless of how deep the item sits in the menu tree.
    private void LaunchCliMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CliLaunchChoice choice
            && DataContext is MainWindowViewModel vm)
            vm.LaunchCliCommand.Execute(choice);
    }

    // Opens a research-paper-derived strategy's source paper in the default browser (the "ⓘ" link
    // on the RESEARCH PAPER pill in the Strategies pane).
    private void ResearchPaper_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        var url = e.Uri?.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best-effort: a missing/blocked browser shouldn't crash the shell */ }
        e.Handled = true;
    }
}
