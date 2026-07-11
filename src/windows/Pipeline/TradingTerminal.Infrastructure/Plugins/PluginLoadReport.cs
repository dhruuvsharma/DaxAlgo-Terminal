namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>Why a plugin folder did not contribute a loaded plugin (or that it did).</summary>
public enum PluginLoadOutcome
{
    /// <summary>Discovered, trusted, registered — in the catalog.</summary>
    Loaded,

    /// <summary>Skipped: the user disabled it in the Plugin Manager. Not an error.</summary>
    Disabled,

    /// <summary>Skipped: auto-quarantined after a previous fault (or revocation). Re-enable in the
    /// Plugin Manager once the cause is fixed.</summary>
    Quarantined,

    /// <summary>The active <see cref="PluginTrustPolicy"/> refused it before any code loaded.</summary>
    RejectedByTrust,

    /// <summary>Its <c>Register</c> tried to replace a service the host owns (e.g. re-register
    /// <c>ICredentialStore</c>) — see <see cref="GuardedServiceCollection"/>. Nothing it staged was
    /// committed, and it is quarantined.</summary>
    PolicyViolation,

    /// <summary>The static IL scan (<see cref="PluginPolicyScanner"/>) found a Block-level capability —
    /// P/Invoke, starting processes, the registry, Reflection.Emit, loading assemblies. Refused before
    /// the assembly was loaded, and quarantined.</summary>
    BlockedByScan,

    /// <summary>Its declared TargetSdkVersion is incompatible with the host SDK.</summary>
    IncompatibleSdk,

    /// <summary>A plugin.json is present but unreadable/malformed.</summary>
    ManifestInvalid,

    /// <summary>The assembly failed to load or threw inside <c>IStrategyPlugin.Register</c>.
    /// Auto-quarantined so a crash-looping plugin runs once, not at every startup.</summary>
    Faulted,
}

/// <summary>One plugin folder that did NOT load, with the classified reason. <paramref name="PluginFolderName"/>
/// is the loader's stable key (folder name == main assembly name by convention).</summary>
public sealed record PluginLoadProblem(
    string PluginFolderName,
    string AssemblyPath,
    PluginLoadOutcome Outcome,
    string Reason);

/// <summary>
/// The complete outcome of a plugin scan: what loaded and, crucially, what didn't and why — so the
/// shells can surface failures (status chip, Plugin Manager rows) instead of a plugin silently
/// vanishing from the catalog with only a log line.
/// </summary>
public sealed record PluginLoadReport(
    IReadOnlyList<LoadedPlugin> Loaded,
    IReadOnlyList<PluginLoadProblem> Problems)
{
    public static PluginLoadReport Empty { get; } = new([], []);

    /// <summary>Problems worth interrupting the user for — everything except a deliberate
    /// user disable.</summary>
    public int AttentionCount => Problems.Count(p => p.Outcome is not PluginLoadOutcome.Disabled);
}
