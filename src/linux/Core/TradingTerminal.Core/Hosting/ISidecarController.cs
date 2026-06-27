namespace TradingTerminal.Core.Hosting;

/// <summary>
/// Manages the local Python sidecar (<c>daxalgo-ml</c>) process so callers never have to launch it by
/// hand. The app auto-starts it on launch (when enabled); the login screen / settings can also kick it
/// on demand via <see cref="EnsureRunningAsync"/>. Exposed as a Core abstraction so UI layers that must
/// stay implementation-agnostic (the login screen) can trigger it.
/// </summary>
public interface ISidecarController
{
    /// <summary>True once the sidecar's health endpoint has answered.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the sidecar if it isn't already reachable, then waits for its health endpoint.
    /// Idempotent and safe to call repeatedly; returns true once the sidecar is reachable. Never throws.</summary>
    Task<bool> EnsureRunningAsync(CancellationToken ct = default);
}
