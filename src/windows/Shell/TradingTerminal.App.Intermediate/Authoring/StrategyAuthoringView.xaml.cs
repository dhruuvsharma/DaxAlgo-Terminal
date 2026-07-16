using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// AI Strategy Builder pane: a chat with the model, the files it writes, and the parameter editor.
/// No behaviour here — it all lives in <see cref="StrategyAuthoringViewModel"/>. The only code-behind is
/// keeping the transcript scrolled to the newest message, which is a pure view concern (and is unhooked
/// on unload so a closed window doesn't keep the view-model's collection alive).
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

    private void Detach()
    {
        if (_messages is null) return;
        _messages.CollectionChanged -= OnMessagesChanged;
        _messages = null;
    }
}
