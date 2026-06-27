using TradingTerminal.Core.Research;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Sandbox execution knobs. Bound from the <c>Sandbox</c> section of appsettings. The default quota is
/// the strict deny-by-default cap; concurrency is bounded so one runaway reproduction can't starve the
/// machine.
/// </summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    /// <summary>Which runner implementation <c>AddPaperResearch</c> registers.</summary>
    public SandboxKind Kind { get; set; } = SandboxKind.Docker;

    /// <summary>When true, the Docker sandbox runner starts the Docker engine itself
    /// (<c>docker desktop start</c>) before a reproduction run if the daemon is down — so Paper Lab
    /// doesn't ask the user to launch Docker manually.</summary>
    public bool AutoStartDocker { get; set; } = true;

    /// <summary>Max concurrent sandbox runs (the orchestrator's <c>SemaphoreSlim</c> bound).</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>The default container base image untrusted code runs in. The image itself is trusted;
    /// the code mounted into it is not.</summary>
    public string BaseImage { get; set; } = "python:3.11-slim";

    // ── Quota knobs (flattened so they bind cleanly from JSON; assembled into SandboxQuota) ──
    public double Cpus { get; set; } = SandboxQuota.Strict.Cpus;
    public int MemoryMb { get; set; } = SandboxQuota.Strict.MemoryMb;
    public int PidsLimit { get; set; } = SandboxQuota.Strict.PidsLimit;
    public int DiskMb { get; set; } = SandboxQuota.Strict.DiskMb;
    public int WallClockSeconds { get; set; } = (int)SandboxQuota.Strict.WallClock.TotalSeconds;

    /// <summary>The configured caps assembled into a <see cref="SandboxQuota"/>.</summary>
    public SandboxQuota ToQuota() =>
        new(Cpus, MemoryMb, PidsLimit, DiskMb, TimeSpan.FromSeconds(Math.Max(1, WallClockSeconds)));
}
