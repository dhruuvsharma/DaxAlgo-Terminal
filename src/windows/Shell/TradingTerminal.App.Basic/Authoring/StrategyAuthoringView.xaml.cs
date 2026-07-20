using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// The Vibe Quant agent workspace. No behaviour here — it all lives in
/// <see cref="StrategyAuthoringViewModel"/>. The only code-behind is pure view plumbing: keeping the
/// transcript scrolled to the newest message (unhooked on unload so a closed window doesn't keep the
/// view-model's collection alive), and closing the composer's pill flyouts after a pick.
/// </summary>
public partial class StrategyAuthoringView : UserControl
{
    private INotifyCollectionChanged? _messages;

    public StrategyAuthoringView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();
        if (DataContext is not StrategyAuthoringViewModel vm) return;

        _messages = vm.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        ChatScroll.ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

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

    private void Detach()
    {
        if (_messages is null) return;
        _messages.CollectionChanged -= OnMessagesChanged;
        _messages = null;
    }
}
