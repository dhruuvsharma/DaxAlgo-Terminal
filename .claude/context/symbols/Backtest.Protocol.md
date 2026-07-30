# TradingTerminal.Backtest.Protocol — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestJobContracts.cs
```cs
    7: public enum BacktestInputKind
   13: public enum BacktestStrategySource
   19: public enum BacktestStrategyParameterKind
   28: public enum BacktestBundleTrustKind
   35: public sealed record BacktestBundleTrustEvidence
   37: public const string PublisherSignatureAlgorithm = "ECDSA-P256-SHA256-IEEE-P1363";
   39: public required BacktestBundleTrustKind Kind { get; init; }
   40: public string? PublisherKeyId { get; init; }
   41: public string? PublisherKeyFingerprintSha256 { get; init; }
   42: public string SignatureAlgorithm { get; init; } = PublisherSignatureAlgorithm;
   46: public sealed record BacktestStrategyParameter
   48: public required string Key { get; init; }
   49: public required BacktestStrategyParameterKind Kind { get; init; }
   50: public long? IntegerValue { get; init; }
   51: public double? NumberValue { get; init; }
   52: public bool? BooleanValue { get; init; }
   53: public string? StringValue { get; init; }
   60: public sealed record BacktestInstalledBundleReference
   62: public required string PublisherId { get; init; }
   63: public required string StrategyVersion { get; init; }
   64: public required string ContentRootSha256 { get; init; }
   65: public required string ArchiveSha256 { get; init; }
   66: public required BacktestBundleTrustEvidence TrustEvidence { get; init; }
   69: public enum BacktestWorkerPhase
   81: public enum BacktestTerminalStatus
   93: public enum BacktestArtifactKind
   99: public sealed record BacktestStrategyReference
  101: public required string Id { get; init; }
  102: public BacktestStrategySource Source { get; init; } = BacktestStrategySource.Native;
  104: public string ContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
  110: public string? ExpectedAssemblySha256 { get; init; }
  112: public BacktestInstalledBundleReference? InstalledBundle { get; init; }
  118: public IReadOnlyList<BacktestStrategyParameter> ActivationParameters { get; init; } = [];
  122: public sealed record SyntheticInputSpec(
  131: public sealed record BacktestInputReference
  133: public required BacktestInputKind Kind { get; init; }
  134: public required string Schema { get; init; }
  135: public required string Provenance { get; init; }
  136: public string OrderingPolicy { get; init; } = "timestamp_utc_ascending";
  137: public string? Path { get; init; }
  138: public string? Sha256 { get; init; }
  139: public long? LengthBytes { get; init; }
  140: public SyntheticInputSpec? Synthetic { get; init; }
  142: public static BacktestInputReference CreateSynthetic(
  155: public static BacktestInputReference CreateParquet(
  172: public sealed record BacktestArtifactRequest(bool IncludeReport = true);
  175: public sealed record BacktestResourceLimits(
  182: public static BacktestResourceLimits Default { get; } = new();
  186: public sealed record BacktestJobRequest
  189: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  190: public required string JobId { get; init; }
  192: public string EngineVersion { get; init; } = BacktestProtocolVersions.ManagedEngine;
  194: public string SdkVersion { get; init; } = BacktestProtocolVersions.Sdk;
  196: public string StrategyContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
  197: public required string ExpectedHostEngineAssemblySha256 { get; init; }
  198: public int DeterministicSeed { get; init; } = 1;
  199: public required BacktestStrategyReference Strategy { get; init; }
  200: public required string ParametersSha256 { get; init; }
  201: public required RunSpec Run { get; init; }
  202: public required BacktestInputReference Input { get; init; }
  203: public BacktestArtifactRequest Artifacts { get; init; } = new();
  204: public BacktestResourceLimits Limits { get; init; } = BacktestResourceLimits.Default;
  205: public DateTime? DeadlineUtc { get; init; }
  207: public static BacktestJobRequest Create(
  228: public static BacktestJobRequest CreateInstalledBundle(
  262: public sealed record BacktestJobProgress
  265: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  266: public required string JobId { get; init; }
  267: public required long Sequence { get; init; }
  268: public required DateTime TimestampUtc { get; init; }
  269: public required BacktestWorkerPhase Phase { get; init; }
  270: public string? Message { get; init; }
  271: public long? EventsProcessed { get; init; }
  272: public long? EventsTotal { get; init; }
  273: public DateTime? SimulatedTimeUtc { get; init; }
  275: public double? PercentComplete { get; init; }
  276: public int WarningCount { get; init; }
  277: public bool IsHeartbeat { get; init; }
  280: public sealed record BacktestJobError(
  286: public sealed record BacktestArtifactDescriptor(
  297: public sealed record BacktestResultManifest
  300: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  301: public required string JobId { get; init; }
  302: public required BacktestTerminalStatus TerminalStatus { get; init; }
  303: public required DateTime StartedUtc { get; init; }
  304: public required DateTime CompletedUtc { get; init; }
  305: public required string RequestSha256 { get; init; }
  307: public required string EngineVersion { get; init; }
  309: public required string SdkVersion { get; init; }
  311: public required string StrategyContractVersion { get; init; }
  312: public required string EngineFingerprint { get; init; }
  313: public required string HostEngineAssemblySha256 { get; init; }
  314: public required string BackendFingerprint { get; init; }
  315: public required string StrategyId { get; init; }
  316: public required string StrategyAssemblySha256 { get; init; }
  317: public string? StrategyContentRootSha256 { get; init; }
  318: public string? StrategyArchiveSha256 { get; init; }
  319: public BacktestBundleTrustEvidence? StrategyTrustEvidence { get; init; }
  324: public IReadOnlyList<BacktestLoadedAssemblyFingerprint> StrategyAssemblyClosure { get; init; } = [];
  325: public required string ParametersSha256 { get; init; }
  326: public required string InputSha256 { get; init; }
  327: public required IReadOnlyList<BacktestArtifactDescriptor> Artifacts { get; init; }
  328: public BacktestJobError? Error { get; init; }
  331: public sealed record BacktestLoadedAssemblyFingerprint(
  336: public sealed record BacktestJobOutcome(
  346: public bool IsSuccess => Status == BacktestTerminalStatus.Succeeded;
  350: public sealed record BacktestReportArtifact(
  360: public static BacktestReportArtifact FromReport(BacktestReport report) =>
  371: public BacktestReport ToReport() =>
```

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestProtocolJson.cs
```cs
   10: public static class BacktestProtocolJson
   12: public static JsonSerializerOptions Options { get; } = CreateOptions(writeIndented: false);
   13: public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(writeIndented: true);
   15: public static string Serialize<T>(T value, bool writeIndented = false) =>
   18: public static byte[] SerializeToUtf8Bytes<T>(T value, bool writeIndented = false) =>
   21: public static T Deserialize<T>(string json) =>
   25: public static T Deserialize<T>(ReadOnlySpan<byte> json) =>
   45: public static class BacktestProtocolHash
   47: public const string UnknownSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
   49: public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
   52: public static string ComputeSha256(string text) =>
   55: public static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct = default)
   67: public static string ComputeParametersSha256(StrategyParameters parameters)
   81: public static string ComputeActivationParametersSha256(
  131: public static bool IsSha256(string? value) =>
```

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestProtocolValidator.cs
```cs
    3: public sealed class BacktestProtocolException : Exception
    5: public BacktestProtocolException(string code, string message) : base(message) => Code = code;
    6: public BacktestProtocolException(string code, string message, Exception innerException)
    9: public string Code { get; }
   13: public static class BacktestProtocolValidator
   15: public static void Validate(BacktestJobRequest request)
  131: public static void ValidateJobId(string jobId)
```

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestProtocolVersions.cs
```cs
    4: public static class BacktestProtocolVersions
    6: public const int Current = 2;
    7: public const int ReportArtifact = 1;
    8: public const string ManagedEngine = "1.0";
    9: public const string StrategyContract = "1.0";
   10: public const string Sdk = "0.2.0-alpha";
   14: public static class BacktestJobFiles
   16: public const string Request = "request.json";
   17: public const string ArtifactDirectory = "artifacts";
   18: public const string ReportArtifact = "report.json";
   19: public const string ResultManifest = "result.manifest.json";
   20: public const string ResultManifestHash = "result.manifest.sha256";
   21: public const string StrategyDirectory = "strategy";
   22: public const string StrategyManifest = "bundle.manifest.json";
   26: public static class BacktestProtocolLimits
   28: public const int MaxRequestBytes = 1 * 1024 * 1024;
   29: public const int MaxProgressLineCharacters = 16 * 1024;
   30: public const int MaxCapturedErrorCharacters = 64 * 1024;
   31: public const int MaxJobIdCharacters = 64;
   32: public const int MaxStrategyParameters = 256;
   33: public const int MaxStrategyParameterKeyCharacters = 128;
   34: public const int MaxStrategyParameterStringCharacters = 4096;
   35: public const int MaxSyntheticEvents = 50_000_000;
   36: public const int MaxProgressMessages = 10_000;
   37: public const long MaxInputBytes = 1L * 1024 * 1024 * 1024 * 1024;
   38: public const long MaxArtifactBytes = 128L * 1024 * 1024;
   39: public const long MaxWorkingSetBytes = 64L * 1024 * 1024 * 1024;
   40: public const long MaxWallClockMilliseconds = 24L * 60 * 60 * 1000;
```
