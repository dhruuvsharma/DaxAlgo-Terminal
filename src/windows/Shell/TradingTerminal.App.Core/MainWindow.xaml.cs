using System.Collections.Specialized;
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
        if (DataContext is MainWindowViewModel vm && vm.SelectedStrategy is not null)
            vm.OpenStrategyCommand.Execute(vm.SelectedStrategy.Id);
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
