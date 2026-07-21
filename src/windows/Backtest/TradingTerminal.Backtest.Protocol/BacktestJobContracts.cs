using System.Text.Json.Serialization;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Protocol;

public enum BacktestInputKind
{
    Synthetic = 0,
    Parquet = 1,
}

public enum BacktestStrategySource
{
    Native = 0,
    InstalledBundle = 1,
}

public enum BacktestStrategyParameterKind
{
    Integer = 0,
    Number = 1,
    Boolean = 2,
    Choice = 3,
    Text = 4,
}

public enum BacktestBundleTrustKind
{
    UnsignedLocalDevelopment = 0,
    VerifiedPublisher = 1,
}

/// <summary>Publisher evidence accepted by the host for one exact installed archive.</summary>
public sealed record BacktestBundleTrustEvidence
{
    public const string PublisherSignatureAlgorithm = "ECDSA-P256-SHA256-IEEE-P1363";

    public required BacktestBundleTrustKind Kind { get; init; }
    public string? PublisherKeyId { get; init; }
    public string? PublisherKeyFingerprintSha256 { get; init; }
    public string SignatureAlgorithm { get; init; } = PublisherSignatureAlgorithm;
}

/// <summary>Typed, canonical activation value for an installed strategy factory.</summary>
public sealed record BacktestStrategyParameter
{
    public required string Key { get; init; }
    public required BacktestStrategyParameterKind Kind { get; init; }
    public long? IntegerValue { get; init; }
    public double? NumberValue { get; init; }
    public bool? BooleanValue { get; init; }
    public string? StringValue { get; init; }
}

/// <summary>
/// Immutable install evidence selected by the host. Store paths are deliberately absent: the client
/// stages the verified engine closure into the fixed worker-job strategy directory.
/// </summary>
public sealed record BacktestInstalledBundleReference
{
    public required string PublisherId { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ContentRootSha256 { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required BacktestBundleTrustEvidence TrustEvidence { get; init; }
}

public enum BacktestWorkerPhase
{
    Accepted = 0,
    Validating = 1,
    LoadingInput = 2,
    Running = 3,
    Publishing = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
}

public enum BacktestTerminalStatus
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
    TimedOut = 3,
    ResourceLimitExceeded = 4,
    WorkerCrashed = 5,
    StartFailed = 6,
    ProtocolError = 7,
}

public enum BacktestArtifactKind
{
    Report = 0,
}

/// <summary>Exact executable strategy identity for either a built-in kernel or an installed bundle.</summary>
public sealed record BacktestStrategyReference
{
    public required string Id { get; init; }
    public BacktestStrategySource Source { get; init; } = BacktestStrategySource.Native;
    [JsonRequired]
    public string ContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;

    /// <summary>
    /// Optional for native kernels and required for installed bundles. The worker hashes the exact
    /// assembly bytes it executes and must match this value before constructing strategy code.
    /// </summary>
    public string? ExpectedAssemblySha256 { get; init; }

    public BacktestInstalledBundleReference? InstalledBundle { get; init; }

    /// <summary>
    /// Complete typed factory parameter bag for installed bundles. Native kernels continue using
    /// <see cref="RunSpec.Parameters"/> so their optimizer contract remains unchanged.
    /// </summary>
    public IReadOnlyList<BacktestStrategyParameter> ActivationParameters { get; init; } = [];
}

/// <summary>Deterministic synthetic generator settings carried entirely in the immutable request.</summary>
public sealed record SyntheticInputSpec(
    int EventCount = 100_000,
    double StartPrice = 100d,
    double Spread = 0.02d);

/// <summary>
/// Immutable bulk-input identity. Parquet is bound by absolute path, byte length, and SHA-256; the
/// worker holds a read-sharing file handle for the complete run so ordinary writers cannot mutate it.
/// </summary>
public sealed record BacktestInputReference
{
    public required BacktestInputKind Kind { get; init; }
    public required string Schema { get; init; }
    public required string Provenance { get; init; }
    public string OrderingPolicy { get; init; } = "timestamp_utc_ascending";
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public long? LengthBytes { get; init; }
    public SyntheticInputSpec? Synthetic { get; init; }

    public static BacktestInputReference CreateSynthetic(
        int eventCount,
        string provenance = "worker_request",
        double startPrice = 100d,
        double spread = 0.02d) =>
        new()
        {
            Kind = BacktestInputKind.Synthetic,
            Schema = "synthetic-quotes-v1",
            Provenance = provenance,
            Synthetic = new SyntheticInputSpec(eventCount, startPrice, spread),
        };

    public static BacktestInputReference CreateParquet(
        string path,
        string sha256,
        long lengthBytes,
        string provenance,
        string schema = "daxalgo-ticks-parquet-v1") =>
        new()
        {
            Kind = BacktestInputKind.Parquet,
            Schema = schema,
            Provenance = provenance,
            Path = path,
            Sha256 = sha256,
            LengthBytes = lengthBytes,
        };
}

public sealed record BacktestArtifactRequest(bool IncludeReport = true);

/// <summary>Per-job limits enforced independently by the client and worker where applicable.</summary>
public sealed record BacktestResourceLimits(
    long MaxWallClockMilliseconds = 10 * 60 * 1000,
    long MaxInputBytes = 16L * 1024 * 1024 * 1024,
    long MaxArtifactBytes = 128L * 1024 * 1024,
    long MaxWorkingSetBytes = 2L * 1024 * 1024 * 1024,
    int MaxProgressMessages = 1_000)
{
    public static BacktestResourceLimits Default { get; } = new();
}

/// <summary>The complete, versioned description consumed by a one-shot worker process.</summary>
public sealed record BacktestJobRequest
{
    [JsonRequired]
    public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
    public required string JobId { get; init; }
    [JsonRequired]
    public string EngineVersion { get; init; } = BacktestProtocolVersions.ManagedEngine;
    [JsonRequired]
    public string SdkVersion { get; init; } = BacktestProtocolVersions.Sdk;
    [JsonRequired]
    public string StrategyContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
    public required string ExpectedHostEngineAssemblySha256 { get; init; }
    public int DeterministicSeed { get; init; } = 1;
    public required BacktestStrategyReference Strategy { get; init; }
    public required string ParametersSha256 { get; init; }
    public required RunSpec Run { get; init; }
    public required BacktestInputReference Input { get; init; }
    public BacktestArtifactRequest Artifacts { get; init; } = new();
    public BacktestResourceLimits Limits { get; init; } = BacktestResourceLimits.Default;
    public DateTime? DeadlineUtc { get; init; }

    public static BacktestJobRequest Create(
        string jobId,
        RunSpec run,
        BacktestInputReference input,
        string hostEngineAssemblySha256,
        int deterministicSeed = 1) =>
        new()
        {
            JobId = jobId,
            DeterministicSeed = deterministicSeed,
            ExpectedHostEngineAssemblySha256 = hostEngineAssemblySha256,
            Strategy = new BacktestStrategyReference
            {
                Id = run.StrategyId,
                ExpectedAssemblySha256 = hostEngineAssemblySha256,
            },
            ParametersSha256 = BacktestProtocolHash.ComputeParametersSha256(run.ParametersOrEmpty),
            Run = run,
            Input = input,
        };

    public static BacktestJobRequest CreateInstalledBundle(
        string jobId,
        RunSpec run,
        BacktestInputReference input,
        BacktestInstalledBundleReference bundle,
        string hostEngineAssemblySha256,
        string strategyAssemblySha256,
        IReadOnlyList<BacktestStrategyParameter> activationParameters,
        int deterministicSeed = 1)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(activationParameters);
        var strategy = new BacktestStrategyReference
        {
            Id = run.StrategyId,
            Source = BacktestStrategySource.InstalledBundle,
            ExpectedAssemblySha256 = strategyAssemblySha256,
            InstalledBundle = bundle,
            ActivationParameters = activationParameters,
        };
        return new BacktestJobRequest
        {
            JobId = jobId,
            DeterministicSeed = deterministicSeed,
            ExpectedHostEngineAssemblySha256 = hostEngineAssemblySha256,
            Strategy = strategy,
            ParametersSha256 = BacktestProtocolHash.ComputeActivationParametersSha256(activationParameters),
            Run = run,
            Input = input,
        };
    }
}

/// <summary>One coarse NDJSON status message. Market events never cross the control stream.</summary>
public sealed record BacktestJobProgress
{
    [JsonRequired]
    public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
    public required string JobId { get; init; }
    public required long Sequence { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required BacktestWorkerPhase Phase { get; init; }
    public string? Message { get; init; }
    public long? EventsProcessed { get; init; }
    public long? EventsTotal { get; init; }
    public DateTime? SimulatedTimeUtc { get; init; }
    /// <summary>Coarse 0-100 phase progress; null means the running phase is indeterminate.</summary>
    public double? PercentComplete { get; init; }
    public int WarningCount { get; init; }
    public bool IsHeartbeat { get; init; }
}

public sealed record BacktestJobError(
    string Code,
    string Message,
    string? Detail = null,
    bool Retryable = false);

public sealed record BacktestArtifactDescriptor(
    BacktestArtifactKind Kind,
    [property: JsonRequired] int SchemaVersion,
    string RelativePath,
    long LengthBytes,
    string Sha256);

/// <summary>
/// Published last via atomic rename. A sibling SHA-256 file authenticates these bytes; every artifact
/// named here has its own length and SHA-256 and is relative to the bounded job directory.
/// </summary>
public sealed record BacktestResultManifest
{
    [JsonRequired]
    public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
    public required string JobId { get; init; }
    public required BacktestTerminalStatus TerminalStatus { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime CompletedUtc { get; init; }
    public required string RequestSha256 { get; init; }
    [JsonRequired]
    public required string EngineVersion { get; init; }
    [JsonRequired]
    public required string SdkVersion { get; init; }
    [JsonRequired]
    public required string StrategyContractVersion { get; init; }
    public required string EngineFingerprint { get; init; }
    public required string HostEngineAssemblySha256 { get; init; }
    public required string BackendFingerprint { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyAssemblySha256 { get; init; }
    public string? StrategyContentRootSha256 { get; init; }
    public string? StrategyArchiveSha256 { get; init; }
    public BacktestBundleTrustEvidence? StrategyTrustEvidence { get; init; }
    /// <summary>
    /// Exact manifest-resolved engine closure staged and revalidated by the worker. This is not a
    /// claim that every dependency was loaded on the executed code path.
    /// </summary>
    public IReadOnlyList<BacktestLoadedAssemblyFingerprint> StrategyAssemblyClosure { get; init; } = [];
    public required string ParametersSha256 { get; init; }
    public required string InputSha256 { get; init; }
    public required IReadOnlyList<BacktestArtifactDescriptor> Artifacts { get; init; }
    public BacktestJobError? Error { get; init; }
}

public sealed record BacktestLoadedAssemblyFingerprint(
    string Name,
    string Sha256);

/// <summary>Host-side terminal outcome, including failures that occur before a worker can publish.</summary>
public sealed record BacktestJobOutcome(
    BacktestTerminalStatus Status,
    string JobId,
    string JobDirectory,
    BacktestResultManifest? Manifest = null,
    BacktestReport? Report = null,
    BacktestJobError? Error = null,
    int? WorkerExitCode = null,
    string? WorkerStandardError = null)
{
    public bool IsSuccess => Status == BacktestTerminalStatus.Succeeded;
}

/// <summary>JSON-stable report payload; unlike <see cref="MetricSet"/>, its metric bag round-trips.</summary>
public sealed record BacktestReportArtifact(
    [property: JsonRequired] int SchemaVersion,
    RunSummary Summary,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<RoundTripTrade> Trades,
    IReadOnlyList<EquitySample> Equity,
    IReadOnlyList<InstrumentReport> PerInstrument,
    VisualTimeline? Visual)
{
    public static BacktestReportArtifact FromReport(BacktestReport report) =>
        new(
            BacktestProtocolVersions.ReportArtifact,
            report.Summary,
            report.Metrics.All,
            report.Trades,
            report.Equity,
            report.PerInstrument,
            report.Visual);

    public BacktestReport ToReport() =>
        new(Summary, new MetricSet(Metrics), Trades, Equity, PerInstrument, Visual);
}
