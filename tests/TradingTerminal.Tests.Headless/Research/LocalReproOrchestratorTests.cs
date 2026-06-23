using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// Orchestrator behaviour over a stub <see cref="ISandboxRunner"/> + a real temp-file
/// <see cref="ReproJobStore"/>: cache-hit short-circuits the runner, cancel-before-start ends Cancelled,
/// and unfinished jobs are requeued on construction. The stub returns immediately, so no Docker is
/// required and these run fully offline.
/// </summary>
public sealed class LocalReproOrchestratorTests : IDisposable
{
    private readonly string _dbPath;

    public LocalReproOrchestratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"repro-orch-test-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task Cache_hit_returns_stored_job_without_invoking_the_runner()
    {
        using var store = new ReproJobStore(_dbPath);
        var spec = MakeSpec();

        // Seed a prior succeeded job for the same cache key — the cache hit.
        var seeded = ReproJob.Create(spec).With(ReproStatus.Succeeded, MakeResult(spec));
        store.Save(seeded);

        var runner = new StubRunner(); // throws if RunAsync is ever called
        var resolver = new StubEnvResolver(SamplePlan());
        using var orch = MakeOrchestrator(store, runner, resolver);

        var job = await orch.SubmitAsync(spec);

        job.Id.Should().Be(seeded.Id, "the orchestrator must return the cached succeeded job");
        job.Status.Should().Be(ReproStatus.Succeeded);
        runner.RunCount.Should().Be(0, "a cache hit must never spawn a new sandbox run");
        resolver.ResolveCount.Should().Be(0, "a cache hit must short-circuit BEFORE env resolution");
    }

    [Fact]
    public async Task Resolves_env_plan_then_hands_it_to_the_runner()
    {
        using var store = new ReproJobStore(_dbPath);
        var spec = MakeSpec(commit: "cafebabe");

        var plan = SamplePlan();
        var resolver = new StubEnvResolver(plan);
        var runner = new StubRunner(succeed: true, spec);
        using var orch = MakeOrchestrator(store, runner, resolver);

        var succeeded = orch.JobUpdates
            .Where(j => j.Status == ReproStatus.Succeeded)
            .FirstAsync()
            .ToTask();

        await orch.SubmitAsync(spec);
        var done = await succeeded.WaitAsync(TimeSpan.FromSeconds(5));

        resolver.ResolveCount.Should().Be(1, "the env resolver runs before the sandbox");
        runner.RunCount.Should().Be(1);
        runner.LastPlan.Should().NotBeNull("the resolved plan must flow into the runner");
        runner.LastPlan!.Image.Should().Be(plan.Image);
        done.Status.Should().Be(ReproStatus.Succeeded);
    }

    [Fact]
    public async Task Cancel_before_start_ends_cancelled()
    {
        using var store = new ReproJobStore(_dbPath);
        var spec = MakeSpec(commit: "feedfacecafe");

        // A runner that blocks until released so the job stays queued behind the (size-1) slot while we
        // cancel a second, never-started job.
        var blocker = new BlockingRunner();
        var sandboxOpts = SandboxOpts(maxConcurrent: 1);
        using var orch = new LocalReproOrchestrator(
            store, blocker, new StubEnvResolver(), sandboxOpts, NullLogger<LocalReproOrchestrator>.Instance);

        // First job grabs the only slot and blocks inside the runner.
        var first = await orch.SubmitAsync(spec);
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second job is queued behind the slot. Cancel it before it can start.
        var second = await orch.SubmitAsync(MakeSpec(commit: "0badc0debeef"));

        var cancelledSeen = orch.JobUpdates
            .Where(j => j.Id == second.Id && j.Status == ReproStatus.Cancelled)
            .FirstAsync()
            .ToTask();

        await orch.CancelAsync(second.Id);

        var cancelled = await cancelledSeen.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Status.Should().Be(ReproStatus.Cancelled);

        // Release the first job so it can finish cleanly.
        blocker.Release();
    }

    [Fact]
    public async Task Unfinished_jobs_are_requeued_on_construction()
    {
        var spec = MakeSpec(commit: "abcdef0");

        // Pre-seed an UNFINISHED (non-terminal) job, as if the app had crashed mid-run.
        using (var seedStore = new ReproJobStore(_dbPath))
        {
            var crashed = ReproJob.Create(spec).With(ReproStatus.RunningMinimal);
            seedStore.Save(crashed);
        }

        using var store = new ReproJobStore(_dbPath);
        var runner = new StubRunner(succeed: true, spec);

        // Subscribe BEFORE construction is racy; instead poll the store for a terminal result. The
        // orchestrator requeues unfinished jobs in its constructor and drives them through the stub.
        using var orch = MakeOrchestrator(store, runner);

        ReproJob? finished = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            finished = store.List(10).FirstOrDefault(j => j.IsTerminal);
            if (finished is not null) break;
            await Task.Delay(25);
        }

        runner.RunCount.Should().BeGreaterThan(0, "the requeued job must be driven through the runner");
        finished.Should().NotBeNull("the requeued job must reach a terminal state");
        finished!.Status.Should().Be(ReproStatus.Succeeded);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private LocalReproOrchestrator MakeOrchestrator(
        IReproJobStore store, ISandboxRunner runner, StubEnvResolver? resolver = null) =>
        new(store, runner, resolver ?? new StubEnvResolver(),
            SandboxOpts(maxConcurrent: 1), NullLogger<LocalReproOrchestrator>.Instance);

    private static IOptionsMonitor<SandboxOptions> SandboxOpts(int maxConcurrent)
    {
        var monitor = Substitute.For<IOptionsMonitor<SandboxOptions>>();
        monitor.CurrentValue.Returns(new SandboxOptions { MaxConcurrent = maxConcurrent });
        return monitor;
    }

    private static ReproSpec MakeSpec(string arxiv = "2507.22712", string commit = "abc123") =>
        new(new PaperRef(arxiv, "Test Paper", $"https://arxiv.org/abs/{arxiv}"),
            new RepoRef("https://github.com/example/repo.git", commit),
            new Dictionary<string, string>());

    private static EnvResolutionPlan SamplePlan() => new(
        Image: "python:3.11-slim",
        SetupCommands: new[] { "pip install -r requirements.txt" },
        Entrypoint: "python repro.py --out $RESULT_JSON",
        DeclaredDataDeps: new[] { "binance:BTCUSDT" },
        EnvHash: new EnvHash("deadbeefcafe0000"));

    private static ReproResult MakeResult(ReproSpec spec) => new(
        Success: true,
        PaperArxivId: spec.Paper.ArxivId,
        RepoCommit: spec.Repo.Commit,
        EnvHash: new EnvHash("env-hash-cache"),
        Artifacts: new[] { new ReproArtifact("result.json", "deadbeef", 7) },
        CostEstimate: null,
        Error: null);

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    /// <summary>Returns a canned result immediately. Fails the test if invoked when not expected.</summary>
    private sealed class StubRunner : ISandboxRunner
    {
        private readonly bool _succeed;
        private readonly ReproSpec? _spec;
        private int _runCount;

        public StubRunner() : this(succeed: false, spec: null, allowRun: false) { }
        public StubRunner(bool succeed, ReproSpec? spec) : this(succeed, spec, allowRun: true) { }

        private StubRunner(bool succeed, ReproSpec? spec, bool allowRun)
        {
            _succeed = succeed;
            _spec = spec;
            AllowRun = allowRun;
        }

        public bool AllowRun { get; }
        public int RunCount => _runCount;
        public SandboxKind Kind => SandboxKind.Docker;
        public bool IsAvailable => true;

        /// <summary>The plan handed to the last RunAsync call, so tests can assert it flowed through.</summary>
        public EnvResolutionPlan? LastPlan { get; private set; }

        public Task<ReproResult> RunAsync(
            ReproSpec spec, SandboxQuota quota, SandboxPolicy policy, IProgress<string> log,
            EnvResolutionPlan? plan = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runCount);
            LastPlan = plan;
            if (!AllowRun) throw new InvalidOperationException("Runner must not be invoked on a cache hit.");
            var result = _succeed
                ? MakeResult(_spec ?? spec)
                : ReproResult.Failed("stub failure", spec.Paper.ArxivId, spec.Repo.Commit);
            return Task.FromResult(result);
        }
    }

    /// <summary>Blocks inside RunAsync until released, so a job can be held "running" while another queues.</summary>
    private sealed class BlockingRunner : ISandboxRunner
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SandboxKind Kind => SandboxKind.Docker;
        public bool IsAvailable => true;

        public void Release() => _gate.TrySetResult();

        public async Task<ReproResult> RunAsync(
            ReproSpec spec, SandboxQuota quota, SandboxPolicy policy, IProgress<string> log,
            EnvResolutionPlan? plan = null, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return ReproResult.Failed("released", spec.Paper.ArxivId, spec.Repo.Commit);
        }
    }

    /// <summary>Returns a canned resolved plan; records call count and order vs. the runner.</summary>
    private sealed class StubEnvResolver : IEnvResolverClient
    {
        private int _resolveCount;
        private readonly EnvResolutionPlan? _plan;

        public StubEnvResolver(EnvResolutionPlan? plan = null) => _plan = plan;

        public int ResolveCount => _resolveCount;
        public bool IsAvailable => _plan is not null;

        public Task<MinimalReproPlan> ResolvePlanAsync(RepoRef repo, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _resolveCount);
            return Task.FromResult(_plan is null
                ? MinimalReproPlan.Empty("no plan")
                : MinimalReproPlan.Ok(_plan));
        }
    }
}
