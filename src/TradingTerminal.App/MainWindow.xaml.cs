using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Layout;
using MahApps.Metro.Controls;

namespace TradingTerminal.App;

public partial class MainWindow : MetroWindow
{
    private bool _suppressVisibilityFeedback;

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
        {
            // VM → pane: react when the menu toggles flip the visibility properties.
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyPaneVisibility(vm);
            await vm.StartAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm) return;
        if (e.PropertyName is nameof(MainWindowViewModel.IsStrategiesVisible)
                           or nameof(MainWindowViewModel.IsLogsVisible))
        {
            ApplyPaneVisibility(vm);
        }
    }

    /// <summary>Sync the AvalonDock anchorables to the view-model's visibility flags.</summary>
    private void ApplyPaneVisibility(MainWindowViewModel vm)
    {
        _suppressVisibilityFeedback = true;
        try
        {
            SetAnchorableVisible(StrategiesPane, vm.IsStrategiesVisible);
            SetAnchorableVisible(LogsPane, vm.IsLogsVisible);
        }
        finally { _suppressVisibilityFeedback = false; }
    }

    private static void SetAnchorableVisible(LayoutAnchorable? pane, bool visible)
    {
        if (pane is null) return;
        if (visible)
        {
            if (pane.IsHidden) pane.Show();
        }
        else
        {
            if (!pane.IsHidden) pane.Hide();
        }
    }

    // Pane → VM: when the user clicks the X on a pane, push the new visibility back into the menu.
    private void StrategiesPane_IsVisibleChanged(object sender, EventArgs e)
    {
        if (_suppressVisibilityFeedback) return;
        if (DataContext is MainWindowViewModel vm && sender is LayoutAnchorable pane)
        {
            var visible = !pane.IsHidden;
            if (vm.IsStrategiesVisible != visible) vm.IsStrategiesVisible = visible;
        }
    }

    private void LogsPane_IsVisibleChanged(object sender, EventArgs e)
    {
        if (_suppressVisibilityFeedback) return;
        if (DataContext is MainWindowViewModel vm && sender is LayoutAnchorable pane)
        {
            var visible = !pane.IsHidden;
            if (vm.IsLogsVisible != visible) vm.IsLogsVisible = visible;
        }
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
