# TradingTerminal.Core / Research — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Research/EnvHash.cs
```cs
    9: public sealed record EnvHash(string Value)
   12: public static EnvHash None => new(string.Empty);
   14: public bool IsNone => string.IsNullOrEmpty(Value);
   16: public override string ToString() => Value;
```

## src/linux/Core/TradingTerminal.Core/Research/EnvResolutionPlan.cs
```cs
   15: public sealed record EnvResolutionPlan(
   23: public static EnvResolutionPlan None(string fallbackImage) =>
   27: public bool IsEmpty => string.IsNullOrWhiteSpace(Entrypoint);
```

## src/linux/Core/TradingTerminal.Core/Research/IEnvResolverClient.cs
```cs
   15: public interface IEnvResolverClient
   19:     bool IsAvailable { get; }
   23:     Task<MinimalReproPlan> ResolvePlanAsync(RepoRef repo, CancellationToken ct = default);
```

## src/linux/Core/TradingTerminal.Core/Research/IPaperIngestClient.cs
```cs
    8: public sealed record PaperIngestResult(
   15: public static PaperIngestResult Empty(string reason) =>
   28: public interface IPaperIngestClient
   32:     bool IsAvailable { get; }
   36:     Task<PaperIngestResult> ResolveAsync(string url, CancellationToken ct = default);
```

## src/linux/Core/TradingTerminal.Core/Research/IReplicationConfidenceScorer.cs
```cs
   11: public interface IReplicationConfidenceScorer
   14:     ReplicationConfidence Score(ReproResult result, ReproSignalManifest manifest);
```

## src/linux/Core/TradingTerminal.Core/Research/IReproJobStore.cs
```cs
   11: public interface IReproJobStore
   14:     void Save(ReproJob job);
   17:     ReproJob? Find(Guid id);
   23:     ReproJob? FindCached(string cacheKey);
   26:     IReadOnlyList<ReproJob> LoadUnfinished();
   29:     IReadOnlyList<ReproJob> List(int maxRows);
   32:     int PruneOlderThan(int retentionDays);
```

## src/linux/Core/TradingTerminal.Core/Research/IReproOrchestrator.cs
```cs
   13: public interface IReproOrchestrator
   17:     Task<ReproJob> SubmitAsync(ReproSpec spec, CancellationToken ct = default);
   20:     IObservable<ReproJob> JobUpdates { get; }
   23:     Task CancelAsync(Guid jobId, CancellationToken ct = default);
   26:     IReadOnlyList<ReproJob> ActiveJobs { get; }
```

## src/linux/Core/TradingTerminal.Core/Research/IReproSignalBridge.cs
```cs
   13: public interface IReproSignalBridge
   18:     Task<ReproSignalManifest> MapAsync(ReproResult result, CancellationToken ct = default);
```

## src/linux/Core/TradingTerminal.Core/Research/ISandboxRunner.cs
```cs
   17: public interface ISandboxRunner
   20:     SandboxKind Kind { get; }
   23:     bool IsAvailable { get; }
   35:     Task<ReproResult> RunAsync(
   36:     ReproSpec spec,
   37:     SandboxQuota quota,
   38:     SandboxPolicy policy,
   39:     IProgress<string> log,
   40:     EnvResolutionPlan? plan = null,
   41:     CancellationToken ct = default);
```

## src/linux/Core/TradingTerminal.Core/Research/MinimalReproPlan.cs
```cs
   12: public sealed record MinimalReproPlan(
   18: public static MinimalReproPlan Empty(string reason) =>
   22: public static MinimalReproPlan Ok(EnvResolutionPlan plan) =>
```

## src/linux/Core/TradingTerminal.Core/Research/PaperRef.cs
```cs
    9: public sealed record PaperRef(string ArxivId, string Title, string Url);
```

## src/linux/Core/TradingTerminal.Core/Research/ReplicationConfidence.cs
```cs
    8: public sealed record ReplicationConfidence(
   13: public static ReplicationConfidence None =>
```

## src/linux/Core/TradingTerminal.Core/Research/ReplicationCostEstimate.cs
```cs
    7: public sealed record ReplicationCostEstimate(
   13: public static ReplicationCostEstimate Unknown => new(TimeSpan.Zero, 0, 0m);
```

## src/linux/Core/TradingTerminal.Core/Research/RepoRef.cs
```cs
    9: public sealed record RepoRef(string GitUrl, string Commit);
```

## src/linux/Core/TradingTerminal.Core/Research/ReproArtifact.cs
```cs
   13: public sealed record ReproArtifact(string Name, string Sha256Hex, long SizeBytes, string? LocalPath = null);
```

## src/linux/Core/TradingTerminal.Core/Research/ReproJob.cs
```cs
    9: public sealed record ReproJob(
   19: public bool IsTerminal =>
   23: public static ReproJob Create(ReproSpec spec)
   37: public ReproJob With(ReproStatus status, ReproResult? result = null, string? error = null) =>
```

## src/linux/Core/TradingTerminal.Core/Research/ReproResult.cs
```cs
   12: public sealed record ReproResult(
   22: public static ReproResult Failed(string reason, string? paperArxivId = null, string? repoCommit = null) =>
```

## src/linux/Core/TradingTerminal.Core/Research/ReproSignalKind.cs
```cs
    8: public enum ReproSignalKind
```

## src/linux/Core/TradingTerminal.Core/Research/ReproSignalManifest.cs
```cs
   39: public sealed record ReproSignalManifest(
   47: public static ReproSignalManifest Empty(PaperRef? paper = null, string? commit = null, EnvHash? envHash = null) =>
   54: public bool HasSignals => Signals.Count > 0;
   57: public IReadOnlyCollection<InstrumentId> Instruments =>
```

## src/linux/Core/TradingTerminal.Core/Research/ReproSpec.cs
```cs
   13: public sealed record ReproSpec(
   26: public string CacheKey
   42: public static ReproSpec Minimal(PaperRef paper, RepoRef repo) =>
```

## src/linux/Core/TradingTerminal.Core/Research/ReproStatus.cs
```cs
    8: public enum ReproStatus
```

## src/linux/Core/TradingTerminal.Core/Research/ReproducedSignal.cs
```cs
   15: public sealed record ReproducedSignal(
```

## src/linux/Core/TradingTerminal.Core/Research/SandboxKind.cs
```cs
    8: public enum SandboxKind
```

## src/linux/Core/TradingTerminal.Core/Research/SandboxPolicy.cs
```cs
   14: public sealed record SandboxPolicy(
   20: public bool IsNetworkDenied => EgressAllowlist.Count == 0;
   26: public static SandboxPolicy DenyAll { get; } =
   32: public SandboxPolicy() : this(Array.Empty<string>(), Array.Empty<string>(), SandboxQuota.Strict)
```

## src/linux/Core/TradingTerminal.Core/Research/SandboxQuota.cs
```cs
    9: public sealed record SandboxQuota(
   17: public static SandboxQuota Strict { get; } =
```
