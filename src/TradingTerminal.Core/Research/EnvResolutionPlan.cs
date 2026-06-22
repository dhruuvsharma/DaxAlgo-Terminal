namespace TradingTerminal.Core.Research;

/// <summary>
/// The statically-resolved execution environment for a paper's repo: the base container
/// <see cref="Image"/>, the ordered <see cref="SetupCommands"/> that install its dependencies, the
/// best-guess <see cref="Entrypoint"/> command that runs the minimal reproduction, the
/// <see cref="DeclaredDataDeps"/> the repo says it needs, and a deterministic <see cref="EnvHash"/>
/// computed from the lockfiles.
///
/// <para>This plan is produced by the Python sidecar via <em>static analysis only</em> — the sidecar
/// clones and reads the repo's manifest files (Dockerfile / requirements.txt / pyproject /
/// environment.yml) but NEVER executes the repo's code. The plan then drives the C# Docker sandbox,
/// which is the only place untrusted code actually runs. Records, SDK-free.</para>
/// </summary>
public sealed record EnvResolutionPlan(
    string Image,
    IReadOnlyList<string> SetupCommands,
    string Entrypoint,
    IReadOnlyList<string> DeclaredDataDeps,
    EnvHash EnvHash)
{
    /// <summary>An empty/unresolved plan carrying just an <see cref="EnvHash.None"/> hash.</summary>
    public static EnvResolutionPlan None(string fallbackImage) =>
        new(fallbackImage, Array.Empty<string>(), string.Empty, Array.Empty<string>(), EnvHash.None);

    /// <summary>True when no entrypoint was resolved (nothing to run).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Entrypoint);
}
