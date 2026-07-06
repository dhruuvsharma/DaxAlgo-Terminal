namespace TradingTerminal.Core.Hosting;

/// <summary>
/// No-op <see cref="ISidecarController"/> for editions that ship without the Python sidecar (Basic /
/// Intermediate). Reports "not running" and treats a start request as a no-op success-of-nothing, so the
/// login screen and settings can depend on the seam unconditionally. The Professional edition replaces
/// this with the real <c>SidecarHostService</c> via <c>AddSidecar</c>.
/// </summary>
public sealed class NullSidecarController : ISidecarController
{
    public bool IsRunning => false;

    /// <summary>No sidecar in this edition — returns false without starting anything, never throws.</summary>
    public Task<bool> EnsureRunningAsync(CancellationToken ct = default) => Task.FromResult(false);
}
