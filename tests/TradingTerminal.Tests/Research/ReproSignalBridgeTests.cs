using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research.Bridge;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// The artifact → manifest bridge over a real on-disk <c>result.json</c>: snake_case parse + canonical
/// InstrumentId mapping + time-sort + provenance stamping, the provenance-mismatch guard, malformed-row
/// dropping, and the never-throw contract (failed result / missing artifact → empty manifest). Offline.
/// </summary>
public sealed class ReproSignalBridgeTests : IDisposable
{
    private readonly string _dir;
    private readonly ReproSignalBridge _bridge = new(NullLogger<ReproSignalBridge>.Instance);

    public ReproSignalBridgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"repro-bridge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    private string WriteArtifact(string json)
    {
        var path = Path.Combine(_dir, "result.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static ReproResult ResultFor(string path, bool success = true) => new(
        Success: success,
        PaperArxivId: "2507.22712",
        RepoCommit: "abc123",
        EnvHash: new EnvHash("env-hash-abc"),
        Artifacts: new[] { new ReproArtifact("result.json", "sha", 0, path) },
        CostEstimate: null,
        Error: null);

    [Fact]
    public async Task Maps_well_formed_artifact_to_time_sorted_canonical_signals()
    {
        var path = WriteArtifact("""
            {
              "paper_id": "2507.22712",
              "commit": "abc123",
              "env_hash": "env-hash-abc",
              "signals": [
                { "instrument_id": 7, "timestamp_utc": "2024-01-01T00:00:30Z", "value": -1.0, "kind": "position" },
                { "instrument_id": 7, "timestamp_utc": "2024-01-01T00:00:05Z", "value":  1.0 }
              ]
            }
            """);

        var manifest = await _bridge.MapAsync(ResultFor(path));

        manifest.HasSignals.Should().BeTrue();
        manifest.Signals.Should().HaveCount(2);
        manifest.Signals[0].EventTimeUtc.Should().BeBefore(manifest.Signals[1].EventTimeUtc, "rows are time-sorted");
        manifest.Signals[0].Instrument.Value.Should().Be(7);
        // Provenance stamped from the result.
        manifest.Signals.Should().OnlyContain(s => s.PaperArxivId == "2507.22712" && s.RepoCommit == "abc123");
        manifest.Paper.ArxivId.Should().Be("2507.22712");
    }

    [Fact]
    public async Task Drops_malformed_rows_but_keeps_the_good_ones()
    {
        var path = WriteArtifact("""
            {
              "paper_id": "2507.22712", "commit": "abc123", "env_hash": "h",
              "signals": [
                { "instrument_id": 0,  "timestamp_utc": "2024-01-01T00:00:05Z", "value": 1.0 },
                { "instrument_id": 7,  "timestamp_utc": "not-a-date",            "value": 1.0 },
                { "instrument_id": 7,  "timestamp_utc": "2024-01-01T00:00:05Z",  "value": 1.0 }
              ]
            }
            """);

        var manifest = await _bridge.MapAsync(ResultFor(path));

        manifest.Signals.Should().ContainSingle("only the fully-valid row survives");
    }

    [Fact]
    public async Task Provenance_mismatch_yields_empty_manifest()
    {
        var path = WriteArtifact("""
            { "paper_id": "9999.99999", "commit": "abc123",
              "signals": [ { "instrument_id": 7, "timestamp_utc": "2024-01-01T00:00:05Z", "value": 1.0 } ] }
            """);

        var manifest = await _bridge.MapAsync(ResultFor(path));

        manifest.HasSignals.Should().BeFalse("an artifact claiming a different paper must not be trusted");
    }

    [Fact]
    public async Task Failed_result_and_missing_artifact_yield_empty_manifest_without_throwing()
    {
        var failed = await _bridge.MapAsync(ReproResult.Failed("boom", "2507.22712", "abc123"));
        failed.HasSignals.Should().BeFalse();

        var noPath = await _bridge.MapAsync(new ReproResult(
            true, "2507.22712", "abc123", new EnvHash("h"),
            new[] { new ReproArtifact("result.json", "sha", 0, LocalPath: null) }, null, null));
        noPath.HasSignals.Should().BeFalse();
    }

    [Fact]
    public async Task Malformed_json_yields_empty_manifest_without_throwing()
    {
        var path = WriteArtifact("{ this is not json ");
        var manifest = await _bridge.MapAsync(ResultFor(path));
        manifest.HasSignals.Should().BeFalse();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
