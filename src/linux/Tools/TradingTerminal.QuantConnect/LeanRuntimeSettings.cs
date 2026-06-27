using TradingTerminal.Core.QuantConnect;

namespace TradingTerminal.QuantConnect;

/// <summary>
/// Mutable, process-wide LEAN settings shared by the client and the Settings panel. Seeded from
/// <see cref="LeanOptions"/> (appsettings) at startup; the UI can edit the paths and have them take
/// effect for the current session without an app restart. Persisting back to appsettings is a future
/// nicety — for now edits live until exit.
/// </summary>
public sealed class LeanRuntimeSettings
{
    public LeanEngineMode Mode { get; set; } = LeanEngineMode.LocalCli;
    public string CliPath { get; set; } = "";
    public string ProjectsFolder { get; set; } = "";
    public string DataFolder { get; set; } = "";
    public int RunTimeoutSeconds { get; set; } = 1800;
}
