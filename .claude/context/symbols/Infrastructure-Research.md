# TradingTerminal.Infrastructure / Research — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Bridge/ReproSignalBridge.cs
```cs
   35: public ReproSignalBridge(ILogger<ReproSignalBridge> logger) => _logger = logger;
   37: public async Task<ReproSignalManifest> MapAsync(ReproResult result, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/HttpEnvResolverClient.cs
```cs
   24: public const string HttpClientName = "env-resolver";
   37: public HttpEnvResolverClient(
   47: public bool IsAvailable
   56: public async Task<MinimalReproPlan> ResolvePlanAsync(RepoRef repo, CancellationToken ct = default)
  124: public MinimalReproPlan ToPlan()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/HttpPaperIngestClient.cs
```cs
   22: public const string HttpClientName = "paper-ingest";
   35: public HttpPaperIngestClient(
   45: public bool IsAvailable
   54: public async Task<PaperIngestResult> ResolveAsync(string url, CancellationToken ct = default)
  118: public PaperIngestResult ToResult()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/LocalReproOrchestrator.cs
```cs
   37: public LocalReproOrchestrator(
   54: public IObservable<ReproJob> JobUpdates => _updates;
   56: public IReadOnlyList<ReproJob> ActiveJobs => _active.Values.ToList();
   58: public Task<ReproJob> SubmitAsync(ReproSpec spec, CancellationToken ct = default)
   78: public Task CancelAsync(Guid jobId, CancellationToken ct = default)
  223: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/ReplicationConfidenceScorer.cs
```cs
   23: public sealed class ReplicationConfidenceScorer : IReplicationConfidenceScorer
   31: public ReplicationConfidence Score(ReproResult result, ReproSignalManifest manifest)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/ReproJobStore.cs
```cs
   30: public ReproJobStore(string databasePath)
   65: public void Save(ReproJob job)
   94: public ReproJob? Find(Guid id)
  105: public ReproJob? FindCached(string cacheKey)
  121: public IReadOnlyList<ReproJob> LoadUnfinished()
  147: public IReadOnlyList<ReproJob> List(int maxRows)
  160: public int PruneOlderThan(int retentionDays)
  200: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/ResearchReproServiceCollectionExtensions.cs
```cs
   13: public static class ResearchReproServiceCollectionExtensions
   22: public static IServiceCollection AddPaperResearch(this IServiceCollection services, IConfiguration configuration)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Sandbox/DockerSandboxRunner.cs
```cs
   53: public DockerSandboxRunner(IOptionsMonitor<SandboxOptions> options, ILogger<DockerSandboxRunner> logger)
   60: public SandboxKind Kind => SandboxKind.Docker;
   62: public bool IsAvailable => _dockerAvailable.Value;
   64: public async Task<ReproResult> RunAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Sandbox/RepoFetcher.cs
```cs
   26: public static async Task<RepoFetchResult> FetchAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Sandbox/SandboxProcess.cs
```cs
   10: public bool Success => ExitCode == 0;
   28: public static async Task<SandboxProcessOutcome> RunAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Sandbox/Wsl2SandboxRunner.cs
```cs
   13: public SandboxKind Kind => SandboxKind.Wsl2;
   15: public bool IsAvailable => false;
   17: public Task<ReproResult> RunAsync(
```
