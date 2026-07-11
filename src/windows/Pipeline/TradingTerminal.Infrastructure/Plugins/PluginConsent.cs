namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>Everything the user needs in order to decide whether to run an unsigned plugin — shown by
/// the consent dialog. <paramref name="Sha256"/> is what the decision is keyed to: change the file and
/// the consent no longer applies.</summary>
public sealed record PluginConsentRequest(
    string PluginFolderName,
    string DisplayName,
    string? Publisher,
    string AssemblyPath,
    string Sha256,
    PluginScanReport Scan);

/// <summary>
/// Asks the user whether to run a plugin the host cannot vouch for — unsigned, unpinned, not from a
/// trusted publisher. Implemented by each shell as a modal dialog; absent (null) in headless hosts
/// (the CLI, tests), where the answer is always <b>no</b>: nothing is ever silently trusted just
/// because there is nobody to ask.
/// <para>
/// The decision is persisted against the assembly's sha256 (<see cref="PluginStateStore"/>), so it
/// survives restarts but is revoked the moment the file changes — a consented plugin that updates
/// itself has to ask again.
/// </para>
/// </summary>
public interface IPluginConsentPrompt
{
    /// <summary>True to load the plugin this once and remember the decision for this exact build.</summary>
    bool RequestConsent(PluginConsentRequest request);
}
