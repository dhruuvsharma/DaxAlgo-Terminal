using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research.Sandbox;

/// <summary>
/// Placeholder WSL2 sandbox backend. Not yet implemented — reports <see cref="IsAvailable"/> = false so
/// the orchestrator treats it as unavailable and folds any submit into a failed result. When built out,
/// it must enforce the same deny-by-default controls as <see cref="DockerSandboxRunner"/> (no host
/// mounts of sensitive paths, network deny-by-default, quotas, kill-tree).
/// </summary>
internal sealed class Wsl2SandboxRunner : ISandboxRunner
{
    public SandboxKind Kind => SandboxKind.Wsl2;

    public bool IsAvailable => false;

    public Task<ReproResult> RunAsync(
        ReproSpec spec,
        SandboxQuota quota,
        SandboxPolicy policy,
        IProgress<string> log,
        EnvResolutionPlan? plan = null,
        CancellationToken ct = default) =>
        Task.FromResult(ReproResult.Failed(
            "WSL2 sandbox backend is not implemented yet.",
            spec.Paper.ArxivId,
            spec.Repo.Commit));
}
