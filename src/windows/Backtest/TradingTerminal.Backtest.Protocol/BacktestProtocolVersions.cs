namespace TradingTerminal.Backtest.Protocol;

/// <summary>Independent compatibility versions for the worker control and artifact boundary.</summary>
public static class BacktestProtocolVersions
{
    public const int Current = 1;
    public const int ReportArtifact = 1;
    public const string ManagedEngine = "1.0";
    public const string StrategyContract = "1.0";
    public const string Sdk = "0.2.0-alpha";
}

/// <summary>Fixed file names in one bounded worker job directory.</summary>
public static class BacktestJobFiles
{
    public const string Request = "request.json";
    public const string ArtifactDirectory = "artifacts";
    public const string ReportArtifact = "report.json";
    public const string ResultManifest = "result.manifest.json";
    public const string ResultManifestHash = "result.manifest.sha256";
}

/// <summary>Hard protocol ceilings. Requests may choose smaller values but never expand these.</summary>
public static class BacktestProtocolLimits
{
    public const int MaxRequestBytes = 1 * 1024 * 1024;
    public const int MaxProgressLineCharacters = 16 * 1024;
    public const int MaxCapturedErrorCharacters = 64 * 1024;
    public const int MaxJobIdCharacters = 64;
    public const int MaxSyntheticEvents = 50_000_000;
    public const int MaxProgressMessages = 10_000;
    public const long MaxInputBytes = 1L * 1024 * 1024 * 1024 * 1024;
    public const long MaxArtifactBytes = 128L * 1024 * 1024;
    public const long MaxWorkingSetBytes = 64L * 1024 * 1024 * 1024;
    public const long MaxWallClockMilliseconds = 24L * 60 * 60 * 1000;
}
