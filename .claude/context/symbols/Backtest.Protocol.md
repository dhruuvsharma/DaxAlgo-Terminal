# TradingTerminal.Backtest.Protocol — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestJobContracts.cs
```cs
    6: public enum BacktestInputKind
   12: public enum BacktestStrategySource
   18: public enum BacktestStrategyParameterKind
   27: public enum BacktestBundleTrustKind
   34: public sealed record BacktestBundleTrustEvidence
   36: public const string PublisherSignatureAlgorithm = "ECDSA-P256-SHA256-IEEE-P1363";
   38: public required BacktestBundleTrustKind Kind { get; init; }
   39: public string? PublisherKeyId { get; init; }
   40: public string? PublisherKeyFingerprintSha256 { get; init; }
   41: public string SignatureAlgorithm { get; init; } = PublisherSignatureAlgorithm;
   45: public sealed record BacktestStrategyParameter
   47: public required string Key { get; init; }
   48: public required BacktestStrategyParameterKind Kind { get; init; }
   49: public long? IntegerValue { get; init; }
   50: public double? NumberValue { get; init; }
   51: public bool? BooleanValue { get; init; }
   52: public string? StringValue { get; init; }
   59: public sealed record BacktestInstalledBundleReference
   61: public required string PublisherId { get; init; }
   62: public required string StrategyVersion { get; init; }
   63: public required string ContentRootSha256 { get; init; }
   64: public required string ArchiveSha256 { get; init; }
   65: public required BacktestBundleTrustEvidence TrustEvidence { get; init; }
   68: public enum BacktestWorkerPhase
   80: public enum BacktestTerminalStatus
   92: public enum BacktestArtifactKind
   98: public sealed record BacktestStrategyReference
  100: public required string Id { get; init; }
  101: public BacktestStrategySource Source { get; init; } = BacktestStrategySource.Native;
  103: public string ContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
  109: public string? ExpectedAssemblySha256 { get; init; }
  111: public BacktestInstalledBundleReference? InstalledBundle { get; init; }
  117: public IReadOnlyList<BacktestStrategyParameter> ActivationParameters { get; init; } = [];
  121: public sealed record SyntheticInputSpec(
  130: public sealed record BacktestInputReference
  132: public required BacktestInputKind Kind { get; init; }
  133: public required string Schema { get; init; }
  134: public required string Provenance { get; init; }
  135: public string OrderingPolicy { get; init; } = "timestamp_utc_ascending";
  136: public string? Path { get; init; }
  137: public string? Sha256 { get; init; }
  138: public long? LengthBytes { get; init; }
  139: public SyntheticInputSpec? Synthetic { get; init; }
  141: public static BacktestInputReference CreateSynthetic(
  154: public static BacktestInputReference CreateParquet(
  171: public sealed record BacktestArtifactRequest(bool IncludeReport = true);
  174: public sealed record BacktestResourceLimits(
  181: public static BacktestResourceLimits Default { get; } = new();
  185: public sealed record BacktestJobRequest
  188: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  189: public required string JobId { get; init; }
  191: public string EngineVersion { get; init; } = BacktestProtocolVersions.ManagedEngine;
  193: public string SdkVersion { get; init; } = BacktestProtocolVersions.Sdk;
  195: public string StrategyContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
  196: public required string ExpectedHostEngineAssemblySha256 { get; init; }
  197: public int DeterministicSeed { get; init; } = 1;
  198: public required BacktestStrategyReference Strategy { get; init; }
  199: public required string ParametersSha256 { get; init; }
  200: public required RunSpec Run { get; init; }
  201: public required BacktestInputReference Input { get; init; }
  202: public BacktestArtifactRequest Artifacts { get; init; } = new();
  203: public BacktestResourceLimits Limits { get; init; } = BacktestResourceLimits.Default;
  204: public DateTime? DeadlineUtc { get; init; }
  206: public static BacktestJobRequest Create(
  227: public static BacktestJobRequest CreateInstalledBundle(
  261: public sealed record BacktestJobProgress
  264: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  265: public required string JobId { get; init; }
  266: public required long Sequence { get; init; }
  267: public required DateTime TimestampUtc { get; init; }
  268: public required BacktestWorkerPhase Phase { get; init; }
  269: public string? Message { get; init; }
  270: public long? EventsProcessed { get; init; }
  271: public long? EventsTotal { get; init; }
  272: public DateTime? SimulatedTimeUtc { get; init; }
  274: public double? PercentComplete { get; init; }
  275: public int WarningCount { get; init; }
  276: public bool IsHeartbeat { get; init; }
  279: public sealed record BacktestJobError(
  285: public sealed record BacktestArtifactDescriptor(
  296: public sealed record BacktestResultManifest
  299: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  300: public required string JobId { get; init; }
  301: public required BacktestTerminalStatus TerminalStatus { get; init; }
  302: public required DateTime StartedUtc { get; init; }
  303: public required DateTime CompletedUtc { get; init; }
  304: public required string RequestSha256 { get; init; }
  306: public required string EngineVersion { get; init; }
  308: public required string SdkVersion { get; init; }
  310: public required string StrategyContractVersion { get; init; }
  311: public required string EngineFingerprint { get; init; }
  312: public required string HostEngineAssemblySha256 { get; init; }
  313: public required string BackendFingerprint { get; init; }
  314: public required string StrategyId { get; init; }
  315: public required string StrategyAssemblySha256 { get; init; }
  316: public string? StrategyContentRootSha256 { get; init; }
  317: public string? StrategyArchiveSha256 { get; init; }
  318: public BacktestBundleTrustEvidence? StrategyTrustEvidence { get; init; }
  323: public IReadOnlyList<BacktestLoadedAssemblyFingerprint> StrategyAssemblyClosure { get; init; } = [];
  324: public required string ParametersSha256 { get; init; }
  325: public required string InputSha256 { get; init; }
  326: public required IReadOnlyList<BacktestArtifactDescriptor> Artifacts { get; init; }
  327: public BacktestJobError? Error { get; init; }
  330: public sealed record BacktestLoadedAssemblyFingerprint(
  335: public sealed record BacktestJobOutcome(
  345: public bool IsSuccess => Status == BacktestTerminalStatus.Succeeded;
  349: public sealed record BacktestReportArtifact(
  358: public static BacktestReportArtifact FromReport(BacktestReport report) =>
  368: public BacktestReport ToReport() =>
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
