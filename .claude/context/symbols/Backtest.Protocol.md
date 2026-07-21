# TradingTerminal.Backtest.Protocol — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestJobContracts.cs
```cs
    6: public enum BacktestInputKind
   12: public enum BacktestStrategySource
   17: public enum BacktestWorkerPhase
   29: public enum BacktestTerminalStatus
   41: public enum BacktestArtifactKind
   47: public sealed record BacktestStrategyReference
   49: public required string Id { get; init; }
   50: public BacktestStrategySource Source { get; init; } = BacktestStrategySource.Native;
   52: public string ContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
   58: public string? ExpectedAssemblySha256 { get; init; }
   62: public sealed record SyntheticInputSpec(
   71: public sealed record BacktestInputReference
   73: public required BacktestInputKind Kind { get; init; }
   74: public required string Schema { get; init; }
   75: public required string Provenance { get; init; }
   76: public string OrderingPolicy { get; init; } = "timestamp_utc_ascending";
   77: public string? Path { get; init; }
   78: public string? Sha256 { get; init; }
   79: public long? LengthBytes { get; init; }
   80: public SyntheticInputSpec? Synthetic { get; init; }
   82: public static BacktestInputReference CreateSynthetic(
   95: public static BacktestInputReference CreateParquet(
  112: public sealed record BacktestArtifactRequest(bool IncludeReport = true);
  115: public sealed record BacktestResourceLimits(
  122: public static BacktestResourceLimits Default { get; } = new();
  126: public sealed record BacktestJobRequest
  129: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  130: public required string JobId { get; init; }
  132: public string EngineVersion { get; init; } = BacktestProtocolVersions.ManagedEngine;
  134: public string SdkVersion { get; init; } = BacktestProtocolVersions.Sdk;
  136: public string StrategyContractVersion { get; init; } = BacktestProtocolVersions.StrategyContract;
  137: public int DeterministicSeed { get; init; } = 1;
  138: public required BacktestStrategyReference Strategy { get; init; }
  139: public required string ParametersSha256 { get; init; }
  140: public required RunSpec Run { get; init; }
  141: public required BacktestInputReference Input { get; init; }
  142: public BacktestArtifactRequest Artifacts { get; init; } = new();
  143: public BacktestResourceLimits Limits { get; init; } = BacktestResourceLimits.Default;
  144: public DateTime? DeadlineUtc { get; init; }
  146: public static BacktestJobRequest Create(
  163: public sealed record BacktestJobProgress
  166: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  167: public required string JobId { get; init; }
  168: public required long Sequence { get; init; }
  169: public required DateTime TimestampUtc { get; init; }
  170: public required BacktestWorkerPhase Phase { get; init; }
  171: public string? Message { get; init; }
  172: public long? EventsProcessed { get; init; }
  173: public long? EventsTotal { get; init; }
  174: public DateTime? SimulatedTimeUtc { get; init; }
  176: public double? PercentComplete { get; init; }
  177: public int WarningCount { get; init; }
  178: public bool IsHeartbeat { get; init; }
  181: public sealed record BacktestJobError(
  187: public sealed record BacktestArtifactDescriptor(
  198: public sealed record BacktestResultManifest
  201: public int ProtocolVersion { get; init; } = BacktestProtocolVersions.Current;
  202: public required string JobId { get; init; }
  203: public required BacktestTerminalStatus TerminalStatus { get; init; }
  204: public required DateTime StartedUtc { get; init; }
  205: public required DateTime CompletedUtc { get; init; }
  206: public required string RequestSha256 { get; init; }
  208: public required string EngineVersion { get; init; }
  210: public required string SdkVersion { get; init; }
  212: public required string StrategyContractVersion { get; init; }
  213: public required string EngineFingerprint { get; init; }
  214: public required string BackendFingerprint { get; init; }
  215: public required string StrategyId { get; init; }
  216: public required string StrategyAssemblySha256 { get; init; }
  217: public required string ParametersSha256 { get; init; }
  218: public required string InputSha256 { get; init; }
  219: public required IReadOnlyList<BacktestArtifactDescriptor> Artifacts { get; init; }
  220: public BacktestJobError? Error { get; init; }
  224: public sealed record BacktestJobOutcome(
  234: public bool IsSuccess => Status == BacktestTerminalStatus.Succeeded;
  238: public sealed record BacktestReportArtifact(
  247: public static BacktestReportArtifact FromReport(BacktestReport report) =>
  257: public BacktestReport ToReport() =>
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
   81: public static bool IsSha256(string? value) =>
```

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestProtocolValidator.cs
```cs
    3: public sealed class BacktestProtocolException : Exception
    5: public BacktestProtocolException(string code, string message) : base(message) => Code = code;
    6: public BacktestProtocolException(string code, string message, Exception innerException)
    9: public string Code { get; }
   13: public static class BacktestProtocolValidator
   15: public static void Validate(BacktestJobRequest request)
   97: public static void ValidateJobId(string jobId)
```

## src/windows/Backtest/TradingTerminal.Backtest.Protocol/BacktestProtocolVersions.cs
```cs
    4: public static class BacktestProtocolVersions
    6: public const int Current = 1;
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
   24: public static class BacktestProtocolLimits
   26: public const int MaxRequestBytes = 1 * 1024 * 1024;
   27: public const int MaxProgressLineCharacters = 16 * 1024;
   28: public const int MaxCapturedErrorCharacters = 64 * 1024;
   29: public const int MaxJobIdCharacters = 64;
   30: public const int MaxSyntheticEvents = 50_000_000;
   31: public const int MaxProgressMessages = 10_000;
   32: public const long MaxInputBytes = 1L * 1024 * 1024 * 1024 * 1024;
   33: public const long MaxArtifactBytes = 128L * 1024 * 1024;
   34: public const long MaxWorkingSetBytes = 64L * 1024 * 1024 * 1024;
   35: public const long MaxWallClockMilliseconds = 24L * 60 * 60 * 1000;
```
