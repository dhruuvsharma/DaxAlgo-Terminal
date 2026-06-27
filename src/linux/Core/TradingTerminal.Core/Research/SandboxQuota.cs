namespace TradingTerminal.Core.Research;

/// <summary>
/// Hard resource caps for a single sandbox run. These map directly onto container limits
/// (<c>--cpus</c> / <c>--memory</c> / <c>--pids-limit</c> / disk cap / wall-clock timeout). Defaults
/// are deliberately strict so a misconfigured caller cannot grant an unbounded run; callers raise
/// them explicitly via <c>SandboxOptions</c>.
/// </summary>
public sealed record SandboxQuota(
    double Cpus,
    int MemoryMb,
    int PidsLimit,
    int DiskMb,
    TimeSpan WallClock)
{
    /// <summary>The strict default: 1 CPU, 1 GiB RAM, 256 pids, 1 GiB scratch, 5-minute wall clock.</summary>
    public static SandboxQuota Strict { get; } =
        new(Cpus: 1.0, MemoryMb: 1024, PidsLimit: 256, DiskMb: 1024, WallClock: TimeSpan.FromMinutes(5));
}
