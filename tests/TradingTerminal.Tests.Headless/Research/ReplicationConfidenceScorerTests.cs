using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// The replication-confidence scorer over canned results: a failed run scores 0, a clean
/// success-with-signals-and-env scores high, and an empty manifest / missing env hash drag the score
/// down by their components. Pure and deterministic — no Docker, no sidecar.
/// </summary>
public sealed class ReplicationConfidenceScorerTests
{
    private static readonly ReplicationConfidenceScorer Scorer = new();

    private static ReproResult Success(EnvHash? env = null) => new(
        Success: true,
        PaperArxivId: "2507.22712",
        RepoCommit: "abc123",
        EnvHash: env ?? new EnvHash("env-hash-abc"),
        Artifacts: new[] { new ReproArtifact("result.json", "deadbeef", 7) },
        CostEstimate: null,
        Error: null);

    private static ReproSignalManifest WithSignals() =>
        new(new PaperRef("2507.22712", "T", "u"), "abc123", new EnvHash("env-hash-abc"),
            new[] { new ReproducedSignal(new InstrumentId(1), DateTime.UtcNow, 1.0, "2507.22712", "abc123", new EnvHash("env-hash-abc")) });

    [Fact]
    public void Failed_result_scores_zero()
    {
        var result = ReproResult.Failed("boom", "2507.22712", "abc123");
        var confidence = Scorer.Score(result, ReproSignalManifest.Empty());

        confidence.Score.Should().Be(0.0);
        confidence.Components["build_run_success"].Should().Be(0.0);
    }

    [Fact]
    public void Clean_success_with_signals_and_env_scores_high()
    {
        var confidence = Scorer.Score(Success(), WithSignals());

        confidence.Score.Should().BeGreaterThan(0.8);
        confidence.Components["build_run_success"].Should().Be(1.0);
        confidence.Components["signal_completeness"].Should().Be(1.0);
        confidence.Components["output_determinism"].Should().Be(1.0);
    }

    [Fact]
    public void Empty_manifest_lowers_completeness_but_not_below_a_failed_run()
    {
        var withSignals = Scorer.Score(Success(), WithSignals());
        var noSignals = Scorer.Score(Success(), ReproSignalManifest.Empty());

        noSignals.Components["signal_completeness"].Should().Be(0.0);
        noSignals.Score.Should().BeLessThan(withSignals.Score);
        noSignals.Score.Should().BeGreaterThan(0.0, "the build/run still succeeded");
    }

    [Fact]
    public void Missing_env_hash_lowers_determinism_component()
    {
        var resolved = Scorer.Score(Success(env: new EnvHash("h")), WithSignals());
        var unresolved = Scorer.Score(Success(env: EnvHash.None), WithSignals());

        unresolved.Components["output_determinism"].Should().Be(0.0);
        unresolved.Score.Should().BeLessThan(resolved.Score);
    }
}
