using Avalonia.Threading;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.App.Avalonia;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by Avalonia's UI-thread dispatcher. Registered in the Avalonia
/// head to replace the headless <c>ImmediateUiDispatcher</c> default, so repository broker-callback
/// marshalling and the Paper Lab job-update stream land on the real UI thread (Rule 3 — one-layer
/// threading) rather than running inline on a background thread.
/// </summary>
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
