namespace TradingTerminal.Infrastructure.Threading;

/// <summary>
/// Abstraction over <see cref="System.Windows.Threading.Dispatcher"/> so the repository
/// can be unit-tested without a real WPF dispatcher.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Posts <paramref name="action"/> to the UI thread and returns immediately.</summary>
    void Post(Action action);

    /// <summary>Awaits execution of <paramref name="action"/> on the UI thread.</summary>
    Task InvokeAsync(Action action);
}
