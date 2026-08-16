using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Hyperion agent workspace. Behaviour lives in <see cref="StrategyAuthoringViewModel"/>; code-behind
/// only scrolls the transcript and redraws the Prove equity curve.
/// </summary>
public partial class StrategyAuthoringView : UserControl
{
    private INotifyCollectionChanged? _messages;
    private StrategyAuthoringViewModel? _vm;

    public StrategyAuthoringView()
    {
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachMessages();
        DetachProve();
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
