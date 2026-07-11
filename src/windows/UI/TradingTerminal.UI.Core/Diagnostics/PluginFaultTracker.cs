namespace TradingTerminal.UI.Diagnostics;

/// <summary>
/// Session-scoped strike counter behind the plugin fault watchdog: each unhandled fault attributed
/// to a plugin records a strike; reaching <see cref="StrikeLimit"/> strikes the plugin out exactly
/// once (further faults keep counting but never re-trigger the strike-out action). WPF-free — the
/// dispatcher/task-exception wiring lives in <c>TradingTerminal.UI</c>; this part is the testable
/// policy. Keys are plugin folder names, case-insensitive.
/// </summary>
public sealed class PluginFaultTracker(int strikeLimit)
{
    private readonly Dictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _struckOut = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int StrikeLimit { get; } = strikeLimit;

    /// <summary>Records one fault for <paramref name="plugin"/>. <c>StruckOutNow</c> is true only on
    /// the fault that crosses the limit — the caller's one-shot quarantine trigger.</summary>
    public (int Strikes, bool StruckOutNow) RecordFault(string plugin)
    {
        lock (_gate)
        {
            var strikes = _strikes.GetValueOrDefault(plugin) + 1;
            _strikes[plugin] = strikes;
            if (strikes >= StrikeLimit && _struckOut.Add(plugin))
                return (strikes, true);
            return (strikes, false);
        }
    }
}
