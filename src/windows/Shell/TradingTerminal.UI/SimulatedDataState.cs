namespace TradingTerminal.UI;

/// <summary>
/// App-wide flag: is a synthetic (Simulated-broker) feed currently connected? Set by the shell's
/// broker-state refresh and observed by <see cref="Controls.SimulatedDataBanner"/>, so every tool,
/// chart and strategy window can surface the same "not a live feed" warning without each one taking
/// a dependency on <c>IBrokerSelector</c>.
/// </summary>
/// <remarks>
/// Mirrors the static-hook pattern the codebase already uses to keep the UI layer host-free
/// (<c>SignalInstrumentCatalog.Source</c>, <c>InMemoryLogSink.UiPost</c>): the composition root owns
/// the truth and pushes it in. Read/written on the UI thread; <see cref="Changed"/> fires on whichever
/// thread calls <see cref="Set"/> (the shell refresh runs on the dispatcher), and the banner marshals.
/// </remarks>
public static class SimulatedDataState
{
    private static bool _isActive;

    /// <summary>True while the Simulated broker is among the connected set.</summary>
    public static bool IsActive => _isActive;

    /// <summary>Raised whenever <see cref="IsActive"/> flips.</summary>
    public static event EventHandler? Changed;

    /// <summary>The shell calls this from its broker-state refresh. No-op when unchanged.</summary>
    public static void Set(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
