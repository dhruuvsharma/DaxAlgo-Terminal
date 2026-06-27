namespace TradingTerminal.Core.Research;

/// <summary>
/// The deny-by-default security policy for a sandbox run. Untrusted paper code is hostile until proven
/// otherwise, so the default constructor grants <em>nothing</em>: no network egress, no host mounts.
///
/// <para><see cref="EgressAllowlist"/> is opt-in and scoped to a paper's <em>declared</em> data deps
/// only — an empty list means <c>--network none</c>. Never populate it with an allow-all entry.</para>
///
/// <para><see cref="DeniedHostPaths"/> records host paths that must never be bind-mounted (the
/// canonical store, the credential dir, the user profile). The runner never bind-mounts anything by
/// default; this list exists so a future relaxation has to consciously override a recorded denial.</para>
/// </summary>
public sealed record SandboxPolicy(
    IReadOnlyList<string> EgressAllowlist,
    IReadOnlyList<string> DeniedHostPaths,
    SandboxQuota Quota)
{
    /// <summary>True when no egress host is allowed — the runner must pass <c>--network none</c>.</summary>
    public bool IsNetworkDenied => EgressAllowlist.Count == 0;

    /// <summary>
    /// The locked-down default: no egress, no host mounts, strict quota. This is what the orchestrator
    /// uses unless a caller deliberately widens it.
    /// </summary>
    public static SandboxPolicy DenyAll { get; } =
        new(EgressAllowlist: Array.Empty<string>(),
            DeniedHostPaths: Array.Empty<string>(),
            Quota: SandboxQuota.Strict);

    /// <summary>Parameterless default = <see cref="DenyAll"/> (deny-by-default constructor).</summary>
    public SandboxPolicy() : this(Array.Empty<string>(), Array.Empty<string>(), SandboxQuota.Strict)
    {
    }
}
