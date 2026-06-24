namespace TradingTerminal.Infrastructure.Threading;

/// <summary>
/// Synchronous, headless <see cref="IUiDispatcher"/>: there is no UI thread to marshal to, so
/// every action runs inline on the caller's thread. Used on Linux/ARM64 and in the headless
/// backtest CLI where no WPF dispatcher exists. The WPF-backed <c>WpfDispatcher</c> is registered
/// instead on the Windows build.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
