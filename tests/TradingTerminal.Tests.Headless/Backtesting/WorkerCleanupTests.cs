using FluentAssertions;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Infrastructure.Backtest.Worker;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class WorkerCleanupTests
{
    [Fact]
    public void Cleanup_removes_only_old_worker_staging_directories()
    {
        using var temp = new WorkerTempDirectory();
        var job = System.IO.Path.Combine(temp.Path, "job-a");
        var oldStaging = System.IO.Path.Combine(job, ".staging-00000000000000000000000000000001");
        var freshStaging = System.IO.Path.Combine(job, ".staging-00000000000000000000000000000002");
        var similarName = System.IO.Path.Combine(job, ".staging-unowned");
        var artifacts = System.IO.Path.Combine(job, "artifacts");
        Directory.CreateDirectory(oldStaging);
        Directory.CreateDirectory(freshStaging);
        Directory.CreateDirectory(similarName);
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(
            System.IO.Path.Combine(job, BacktestJobFiles.Request),
            BacktestProtocolJson.Serialize(WorkerTestData.Request("job-a")));
        File.WriteAllText(System.IO.Path.Combine(oldStaging, "partial.json"), "partial");
        File.WriteAllText(System.IO.Path.Combine(artifacts, "report.json"), "complete");
        var now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(oldStaging, now - TimeSpan.FromDays(3));
        Directory.SetLastWriteTimeUtc(freshStaging, now - TimeSpan.FromMinutes(1));
        Directory.SetLastWriteTimeUtc(similarName, now - TimeSpan.FromDays(3));
        Directory.SetLastWriteTimeUtc(artifacts, now - TimeSpan.FromDays(3));

        var removed = AbandonedWorkerStagingCleaner.Cleanup(temp.Path, TimeSpan.FromDays(2), now);

        removed.Should().Be(1);
        Directory.Exists(oldStaging).Should().BeFalse();
        Directory.Exists(freshStaging).Should().BeTrue();
        Directory.Exists(similarName).Should().BeTrue();
        Directory.Exists(artifacts).Should().BeTrue();
        Directory.Exists(job).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_ignores_unmarked_directories()
    {
        using var temp = new WorkerTempDirectory();
        var staging = System.IO.Path.Combine(
            temp.Path,
            "unowned",
            ".staging-00000000000000000000000000000003");
        Directory.CreateDirectory(staging);
        var now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(staging, now - TimeSpan.FromDays(3));

        var removed = AbandonedWorkerStagingCleaner.Cleanup(temp.Path, TimeSpan.FromDays(2), now);

        removed.Should().Be(0);
        Directory.Exists(staging).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_rejects_a_filesystem_root()
    {
        using var temp = new WorkerTempDirectory();
        var filesystemRoot = System.IO.Path.GetPathRoot(temp.Path)!;

        var act = () => AbandonedWorkerStagingCleaner.Cleanup(
            filesystemRoot,
            TimeSpan.FromDays(2),
            DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cleanup_ignores_a_job_directory_with_a_mismatched_request_identity()
    {
        using var temp = new WorkerTempDirectory();
        var job = System.IO.Path.Combine(temp.Path, "job-a");
        var staging = System.IO.Path.Combine(job, ".staging-00000000000000000000000000000004");
        Directory.CreateDirectory(staging);
        File.WriteAllText(
            System.IO.Path.Combine(job, BacktestJobFiles.Request),
            BacktestProtocolJson.Serialize(WorkerTestData.Request("different-job")));
        var now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(staging, now - TimeSpan.FromDays(3));

        var removed = AbandonedWorkerStagingCleaner.Cleanup(temp.Path, TimeSpan.FromDays(2), now);

        removed.Should().Be(0);
        Directory.Exists(staging).Should().BeTrue();
    }

    [Fact]
    public void Resolver_prefers_staged_subdirectory_before_base_directory()
    {
        using var temp = new WorkerTempDirectory();
        var staged = System.IO.Path.Combine(temp.Path, "backtest-worker");
        Directory.CreateDirectory(staged);
        var stagedDll = System.IO.Path.Combine(staged, "TradingTerminal.Backtest.Worker.dll");
        var baseDll = System.IO.Path.Combine(temp.Path, "TradingTerminal.Backtest.Worker.dll");
        File.WriteAllText(stagedDll, "staged");
        File.WriteAllText(baseDll, "base");

        var resolved = BacktestWorkerExecutableResolver.TryResolve(
            new BacktestWorkerOptions(),
            temp.Path,
            environmentPath: null,
            out var launch,
            out var error);

        resolved.Should().BeTrue(error);
        launch.Should().NotBeNull();
        launch!.ResolvedWorkerPath.Should().Be(stagedDll);
        launch.PrefixArguments.Should().ContainSingle().Which.Should().Be(stagedDll);
    }
}
