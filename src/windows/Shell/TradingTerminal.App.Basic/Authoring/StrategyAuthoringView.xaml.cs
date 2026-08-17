using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using TradingTerminal.StrategyComposer;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Hyperion agent workspace. Behaviour lives in <see cref="StrategyAuthoringViewModel"/>; code-behind
/// scrolls the transcript, redraws the Prove equity curve, and hosts the live Workspace panels.
/// </summary>
public partial class StrategyAuthoringView : UserControl
{
    private readonly IServiceProvider _services;
    private INotifyCollectionChanged? _messages;
    private StrategyAuthoringViewModel? _vm;
    private HyperionWorkspaceHost? _workspace;

    public StrategyAuthoringView(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachProve();
        if (e.NewValue is StrategyAuthoringViewModel vm)
            AttachProve(vm);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DetachMessages();
        if (DataContext is not StrategyAuthoringViewModel vm) return;

        _messages = vm.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        ChatScroll.ScrollToEnd();

        if (!ReferenceEquals(_vm, vm))
            AttachProve(vm);
        else
            RedrawProveEquity();

        // Prove is selected first — Workspace TabItem content may not exist yet.
        EnsureWorkspace();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachMessages();
        DetachProve();
        _workspace?.Dispose();
        _workspace = null;
        if (WorkspaceHost is not null)
            WorkspaceHost.Content = null;
    }

    private void OnWorkbenchTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkbenchTabs?.SelectedIndex == 1)
            EnsureWorkspace();
    }

    private void OnWorkspaceHostLoaded(object sender, RoutedEventArgs e) => EnsureWorkspace();

    private void EnsureWorkspace()
    {
        if (_workspace is not null || WorkspaceHost is null) return;
        _workspace = new HyperionWorkspaceHost(_services);
        WorkspaceHost.Content = _workspace;
    }

    private void AttachProve(StrategyAuthoringViewModel vm)
    {
        DetachProve();
        _vm = vm;
        _vm.ProveEquityUpdated += OnProveEquityUpdated;
        RedrawProveEquity();
    }

    private void DetachProve()
    {
        if (_vm is null) return;
        _vm.ProveEquityUpdated -= OnProveEquityUpdated;
        _vm = null;
    }

    private void OnProveEquityUpdated(object? sender, EventArgs e) => RedrawProveEquity();

    private void RedrawProveEquity()
    {
        if (_vm is null || ProveEquityPlot is null) return;

        ProveEquityPlot.Plot.Clear();
        if (_vm.ProveEquityCurve.Count >= 2)
        {
            var xs = _vm.ProveEquityCurve.Select(p => p.TimestampUtc.ToOADate()).ToArray();
            var ys = _vm.ProveEquityCurve.Select(p => p.Equity).ToArray();
            ProveEquityPlot.Plot.Add.Scatter(xs, ys);
            ProveEquityPlot.Plot.Axes.DateTimeTicksBottom();
            ProveEquityPlot.Plot.Axes.AutoScale();
        }

        ProveEquityPlot.Refresh();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ChatScroll.ScrollToEnd();
    }

    /// <summary>A pick in any composer flyout closes it — the popups are IsOpen-bound to the pill
    /// toggles. Sync-driven selection changes while everything is closed just re-assert unchecked.</summary>
    private void OnFlyoutSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ModelPill.IsChecked = false;
        BuildPill.IsChecked = false;
        ReasonPill.IsChecked = false;
    }

    private void DetachMessages()
    {
        if (_messages is null) return;
        _messages.CollectionChanged -= OnMessagesChanged;
        _messages = null;
    }
}
