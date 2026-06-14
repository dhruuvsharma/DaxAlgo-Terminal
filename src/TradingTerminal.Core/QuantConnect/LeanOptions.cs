namespace TradingTerminal.Core.QuantConnect;

/// <summary>
/// Configuration for the QuantConnect / LEAN integration. Bound from the <c>QuantConnect</c> section
/// of <c>appsettings.json</c>. Everything is optional — when nothing is set the client falls back to
/// auto-detecting <c>lean</c> on the PATH and degrades to an "unavailable" status the UI surfaces.
/// </summary>
public sealed class LeanOptions
{
    public const string SectionName = "QuantConnect";

    /// <summary>Local CLI now; Cloud is reserved for the future REST client.</summary>
    public LeanEngineMode Mode { get; set; } = LeanEngineMode.LocalCli;

    /// <summary>Path to the <c>lean</c> CLI executable. Empty → resolve "lean" from PATH.</summary>
    public string CliPath { get; set; } = "";

    /// <summary>Root folder that contains LEAN algorithm projects (each a subfolder). Empty → the
    /// current working directory, which is how the <c>lean</c> CLI itself behaves.</summary>
    public string ProjectsFolder { get; set; } = "";

    /// <summary>LEAN data folder (the <c>data</c> dir created by <c>lean init</c>). Optional — only
    /// needed to surface a path in the UI; the CLI reads its own <c>lean.json</c>.</summary>
    public string DataFolder { get; set; } = "";

    /// <summary>Pre-selected project name for the backtest tab. Optional.</summary>
    public string DefaultProject { get; set; } = "";

    /// <summary>Seconds before a backtest/data subprocess is force-killed. 0 → no timeout.</summary>
    public int RunTimeoutSeconds { get; set; } = 1800;

    // ── Cloud slot (not yet wired; here so the section is forward-compatible) ──────────────────
    public string CloudUserId { get; set; } = "";
    public string CloudApiToken { get; set; } = "";
}
