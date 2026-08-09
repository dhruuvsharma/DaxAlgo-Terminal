namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Controls the managed local Python sidecar (<c>daxalgo-ml</c>), an optional loopback HTTP service
/// retained for on-demand Python/ML workloads. Bound from the <c>Sidecar</c> section.
/// </summary>
public sealed class SidecarOptions
{
    public const string SectionName = "Sidecar";

    /// <summary>Startup preference retained for on-demand consumers. Registering the sidecar does not
    /// launch it; a consumer must explicitly request it.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Loopback port used by the sidecar health and API endpoints (default 8765).</summary>
    public int Port { get; set; } = 8765;

    /// <summary>Optional explicit path to the frozen <c>daxalgo-ml.exe</c>. Empty → auto-discover next to
    /// the app, then fall back to running the dev Python module.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Optional explicit Python interpreter for the dev fallback. Empty → the repo venv, then
    /// <c>python</c> on PATH.</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>How long to wait for the sidecar's <c>/healthz</c> to answer after launch.</summary>
    public int StartupTimeoutSeconds { get; set; } = 40;
}
