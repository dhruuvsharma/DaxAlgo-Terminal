using System.Windows;
using System.Windows.Threading;

namespace TradingTerminal.Infrastructure.Threading;

/// <summary>WPF-backed dispatcher. Posts onto <see cref="Application.Current"/>'s dispatcher.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher()
    {
        _dispatcher = Application.Current?.Dispatcher
                      ?? Dispatcher.CurrentDispatcher;
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action) =>
        _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    public Task InvokeAsync(Action action) =>
        _dispatcher.InvokeAsync(action).Task;
}
