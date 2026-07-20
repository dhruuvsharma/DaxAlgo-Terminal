# TradingTerminal.Core / QuantConnect — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/QuantConnect/ILeanClient.cs
```cs
   10: public interface ILeanClient
   13:     LeanEngineMode Mode { get; }
   16:     Task<LeanAvailability> CheckAvailabilityAsync(CancellationToken ct = default);
   19:     Task<IReadOnlyList<LeanProject>> ListProjectsAsync(CancellationToken ct = default);
   23:     Task<LeanBacktestResult> RunBacktestAsync(
   24:     LeanBacktestRequest request,
   25:     IProgress<string>? progress = null,
   26:     CancellationToken ct = default);
   29:     Task<LeanDataResult> DownloadDataAsync(
   30:     LeanDataDownloadRequest request,
   31:     IProgress<string>? progress = null,
   32:     CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/QuantConnect/LeanModels.cs
```cs
    5: public enum LeanEngineMode
   16: public sealed record LeanProject(
   22: public sealed record LeanAvailability(
   27: public static LeanAvailability Unavailable(string detail) => new(false, null, detail);
   31: public sealed record LeanBacktestRequest(
   36: public sealed record LeanStatistic(string Name, string Value);
   39: public sealed record LeanEquityPoint(DateTime TimeUtc, double Equity);
   43: public sealed record LeanBacktestResult(
   50: public static LeanBacktestResult Failed(string error, string log = "") =>
   56: public sealed record LeanDataDownloadRequest(
   64: public sealed record LeanDataResult(bool Success, string? Error, string Log)
   66: public static LeanDataResult Failed(string error, string log = "") =>
```

## src/windows/Core/TradingTerminal.Core/QuantConnect/LeanOptions.cs
```cs
    8: public sealed class LeanOptions
   10: public const string SectionName = "QuantConnect";
   13: public LeanEngineMode Mode { get; set; } = LeanEngineMode.LocalCli;
   16: public string CliPath { get; set; } = "";
   20: public string ProjectsFolder { get; set; } = "";
   24: public string DataFolder { get; set; } = "";
   27: public string DefaultProject { get; set; } = "";
   30: public int RunTimeoutSeconds { get; set; } = 1800;
   33: public string CloudUserId { get; set; } = "";
   34: public string CloudApiToken { get; set; } = "";
```
