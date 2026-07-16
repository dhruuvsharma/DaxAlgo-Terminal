using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MahApps.Metro.Controls;
using TradingTerminal.Core.Strategies;
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

        // Route by the tile that was actually double-clicked: the trailing Vibe card opens Vibe Quant,
        // a strategy tile opens that strategy. (Container-based so it works even though the Vibe card
        // is never the ListBox selection.)
        var container = ItemsControl.ContainerFromElement(StrategiesList, e.OriginalSource as DependencyObject) as ListBoxItem;
        switch (container?.DataContext)
        {
            case VibeCardItem:
                vm.OpenStrategyAuthoringCommand.Execute(null);
                break;
            case StrategyCatalogItemViewModel item:
                vm.OpenStrategyCommand.Execute(item.Id);
                break;
        }
    }

    // Catalog card right-click → Edit: opens the presentation editor for the selected strategy.
    private void EditStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.EditStrategyCommand.Execute(null);
    }

    // Right-click selects the row under the cursor (so Open / Quick-backtest / Edit act on it), except
    // the trailing Vibe card, which carries its own menu and never becomes the selection.
    private void StrategyItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: not VibeCardItem } item)
            item.IsSelected = true;
    }

    // The trailing "Vibe Quant" tile is a call-to-action, not a real strategy — keep it from becoming
    // the ListBox selection (SelectedStrategy is typed ITradingStrategy) by unselecting it immediately.
    private void StrategyItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: VibeCardItem } item)
            item.IsSelected = false;
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

    // A "Launch CLI" menu item (in the top Strategy Studio menu or the Vibe card's right-click menu).
    // Bound by EventSetter so it works regardless of how deep the item sits in the menu tree.
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
