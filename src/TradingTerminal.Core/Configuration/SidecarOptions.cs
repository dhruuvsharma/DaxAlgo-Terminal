namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Controls the managed local Python sidecar (<c>daxalgo-ml</c>) — the loopback HTTP service that
/// powers the AI Market Analyst and Paper Lab paper-resolution. Bound from the <c>Sidecar</c> section.
/// When <see cref="AutoStart"/> is on AND a feature that needs the sidecar is enabled, the app launches
/// it as a child process on startup (and kills it on exit) — so the user never copy-pastes a command.
/// </summary>
public sealed class SidecarOptions
{
    public const string SectionName = "Sidecar";

    /// <summary>Master switch. When true, the app auto-launches the sidecar on startup if the AI analyst
    /// or Paper Lab research feature is enabled. Turn off to manage the sidecar yourself.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Loopback port the sidecar binds. Keep it aligned with the AI analyst / research endpoints
    /// (all default to 8765).</summary>
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
