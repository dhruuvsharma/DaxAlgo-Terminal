namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Relays strategy callback failures that a host catches to keep its stream alive. The shell fault
/// watchdog subscribes and performs the same plugin attribution/strike tracking it uses for
/// dispatcher and unobserved-task faults.
/// </summary>
public static class PluginFaultEvents
{
    public static event Action<Exception>? Reported;

    public static void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (Action<Exception> observer in Reported?.GetInvocationList() ?? [])
        {
            try { observer(exception); }
            catch { /* a fault observer must never make the strategy failure worse */ }
        }
    }
}
