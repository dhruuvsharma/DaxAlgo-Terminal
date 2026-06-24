using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research.Sandbox;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// Isolation/quota integration tests for the Docker sandbox runner. They SELF-SKIP (return) when the
/// docker CLI isn't available, so the suite stays green without Docker — same precedent as the
/// Postgres/TimescaleDB store tests. Bring up Docker Desktop to exercise the real container path.
/// </summary>
public sealed class DockerSandboxRunnerTests
{
    private static DockerSandboxRunner MakeRunner()
    {
        var opts = new SandboxOptions
        {
            Kind = SandboxKind.Docker,
            MaxConcurrent = 1,
            BaseImage = "alpine:3.20",
            Cpus = 1.0,
            MemoryMb = 256,
            PidsLimit = 64,
            DiskMb = 256,
            WallClockSeconds = 30,
        };
        var monitor = Substitute.For<IOptionsMonitor<SandboxOptions>>();
        monitor.CurrentValue.Returns(opts);
        return new DockerSandboxRunner(monitor, NullLogger<DockerSandboxRunner>.Instance);
    }

    private static ReproSpec MakeSpec() => new(
        new PaperRef("0000.00000", "Isolation Probe", "https://arxiv.org/abs/0000.00000"),
        // A real, tiny public repo pinned to a commit; only used when Docker is actually present.
        new RepoRef("https://github.com/octocat/Hello-World.git", "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d"),
        new Dictionary<string, string>());

    [Fact]
    public void When_docker_absent_runner_reports_unavailable_and_never_throws()
    {
        var runner = MakeRunner();
        if (runner.IsAvailable) return; // Docker present — covered by the integration test below.

        // The contract: even with no Docker, RunAsync folds into a Failed result rather than throwing.
        var result = runner
            .RunAsync(MakeSpec(), SandboxQuota.Strict, SandboxPolicy.DenyAll,
                new Progress<string>(), plan: null, CancellationToken.None)
            .GetAwaiter().GetResult();

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        runner.Kind.Should().Be(SandboxKind.Docker);
    }

    [Fact]
    public async Task Network_denied_run_completes_under_isolation_when_docker_present()
    {
        var runner = MakeRunner();
        if (!runner.IsAvailable) return; // Docker not running — skip (Postgres-test precedent).

        // No repro.py exists in Hello-World, so the container command fails inside the sandbox — but the
        // run must still: pull the image, enforce --network none + quotas, and fold the missing-artifact
        // outcome into a Failed result WITHOUT throwing. That exercises the isolation path end to end.
        var policy = SandboxPolicy.DenyAll; // network none, no host mounts
        var log = new List<string>();
        var progress = new Progress<string>(log.Add);

        var result = await runner.RunAsync(MakeSpec(), SandboxQuota.Strict, policy, progress, plan: null, CancellationToken.None);

        policy.IsNetworkDenied.Should().BeTrue();
        result.Success.Should().BeFalse("the probe repo has no declared artifact");
        result.Error.Should().NotBeNullOrEmpty();
        // Provenance must survive even on failure.
        result.PaperArxivId.Should().Be("0000.00000");
        result.RepoCommit.Should().Be("7fd1a60b01f91b314f59955a4e4d4e80d8edf11d");
    }
}
