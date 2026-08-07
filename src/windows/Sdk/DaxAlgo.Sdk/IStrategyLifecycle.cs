namespace DaxAlgo.Sdk;

/// <summary>Host-facing lifecycle for one sandboxed strategy session.</summary>
public interface IStrategyLifecycle
{
    /// <summary>Whether a kernel session is active, including while paused.</summary>
    bool IsRunning { get; }

    /// <summary>Whether the active session is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Builds and starts a fresh kernel using launch-time defaults.</summary>
    Task RunAsync(CancellationToken ct = default);

    /// <summary>Pauses the active kernel session.</summary>
    Task PauseAsync(CancellationToken ct = default);

    /// <summary>
    /// Resumes by rebuilding and starting a fresh kernel from the current source and parameter
    /// values. Resume is never an in-place hot reload of the paused instance.
    /// </summary>
    Task ResumeAsync(CancellationToken ct = default);

    /// <summary>Stops the active kernel session.</summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Host-facing lifecycle for one visualizer session. Visualizers start automatically when opened,
/// so this contract intentionally has no Run operation.
/// </summary>
public interface IVisualizerLifecycle
{
    /// <summary>Whether the auto-started visualizer session is active, including while paused.</summary>
    bool IsRunning { get; }

    /// <summary>Whether the active visualizer session is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Freezes data processing while preserving the current view.</summary>
    Task PauseAsync(CancellationToken ct = default);

    /// <summary>
    /// Resumes by rebuilding a fresh visualizer from the current source and parameters; it never
    /// hot-reloads the paused instance.
    /// </summary>
    Task ResumeAsync(CancellationToken ct = default);

    /// <summary>Stops the visualizer session.</summary>
    Task StopAsync(CancellationToken ct = default);
}
