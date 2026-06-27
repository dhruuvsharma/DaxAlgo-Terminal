using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// Round-trips for the SQLite reproduction job store: cache-key lookup (only succeeded jobs hit) and
/// the unfinished-job recovery used to requeue work after an app restart.
/// </summary>
public sealed class ReproJobStoreTests : IDisposable
{
    private readonly string _dbPath;

    public ReproJobStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"repro-jobs-test-{Guid.NewGuid():N}.db");
    }

    private static ReproSpec MakeSpec(string arxiv = "2507.22712", string commit = "abc123", string? cfgValue = null)
    {
        var cfg = new Dictionary<string, string>();
        if (cfgValue is not null) cfg["mode"] = cfgValue;
        return new ReproSpec(
            new PaperRef(arxiv, "Test Paper", $"https://arxiv.org/abs/{arxiv}"),
            new RepoRef("https://github.com/example/repo.git", commit),
            cfg);
    }

    [Fact]
    public void FindCached_returns_only_succeeded_job_for_matching_cache_key()
    {
        using var store = new ReproJobStore(_dbPath);
        var spec = MakeSpec();

        // A queued job for the spec must NOT be returned as a cache hit.
        var queued = ReproJob.Create(spec);
        store.Save(queued);
        store.FindCached(spec.CacheKey).Should().BeNull("only succeeded jobs are cache hits");

        // Advance the same job to Succeeded with a result; now it should be the cache hit.
        var result = new ReproResult(
            Success: true,
            PaperArxivId: spec.Paper.ArxivId,
            RepoCommit: spec.Repo.Commit,
            EnvHash: new EnvHash("env-hash-1234"),
            Artifacts: new[] { new ReproArtifact("result.json", "deadbeef", 42) },
            CostEstimate: ReplicationCostEstimate.Unknown,
            Error: null);
        var succeeded = queued.With(ReproStatus.Succeeded, result);
        store.Save(succeeded);

        var hit = store.FindCached(spec.CacheKey);
        hit.Should().NotBeNull();
        hit!.Id.Should().Be(succeeded.Id);
        hit.Status.Should().Be(ReproStatus.Succeeded);
        hit.Result.Should().NotBeNull();
        hit.Result!.EnvHash.Value.Should().Be("env-hash-1234");
        hit.Result.Artifacts.Should().ContainSingle().Which.Sha256Hex.Should().Be("deadbeef");

        // A spec with a different config must produce a different cache key → no hit.
        store.FindCached(MakeSpec(cfgValue: "full").CacheKey).Should().BeNull();
    }

    [Fact]
    public void Save_round_trips_spec_and_result_including_config()
    {
        using var store = new ReproJobStore(_dbPath);
        var spec = MakeSpec(cfgValue: "minimal");
        var job = ReproJob.Create(spec);
        store.Save(job);

        var loaded = store.Find(job.Id);
        loaded.Should().NotBeNull();
        loaded!.Spec.Paper.ArxivId.Should().Be("2507.22712");
        loaded.Spec.Repo.Commit.Should().Be("abc123");
        loaded.Spec.Config.Should().ContainKey("mode").WhoseValue.Should().Be("minimal");
        loaded.Spec.CacheKey.Should().Be(spec.CacheKey, "the persisted spec must reproduce the same cache key");
    }

    [Fact]
    public void LoadUnfinished_returns_non_terminal_jobs_only()
    {
        using var store = new ReproJobStore(_dbPath);

        var queued = ReproJob.Create(MakeSpec(commit: "c-queued"));
        var running = ReproJob.Create(MakeSpec(commit: "c-running")).With(ReproStatus.RunningMinimal);
        var succeeded = ReproJob.Create(MakeSpec(commit: "c-ok")).With(ReproStatus.Succeeded);
        var failed = ReproJob.Create(MakeSpec(commit: "c-fail")).With(ReproStatus.Failed, error: "boom");
        var cancelled = ReproJob.Create(MakeSpec(commit: "c-cancel")).With(ReproStatus.Cancelled);

        foreach (var j in new[] { queued, running, succeeded, failed, cancelled }) store.Save(j);

        var unfinished = store.LoadUnfinished();
        unfinished.Select(j => j.Id).Should().BeEquivalentTo(new[] { queued.Id, running.Id });
        unfinished.Should().OnlyContain(j => !j.IsTerminal);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
