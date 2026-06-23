namespace TradingTerminal.Core.QuantConnect;

/// <summary>How LEAN backtests are executed. The seam is engine-agnostic so the cloud client can be
/// dropped in later behind the same <see cref="ILeanClient"/> without touching the UI.</summary>
public enum LeanEngineMode
{
    /// <summary>Shell out to the local open-source <c>lean</c> CLI (Docker). Offline, free.</summary>
    LocalCli,

    /// <summary>Drive QuantConnect's cloud over its REST API (user-id + token). Not yet wired.</summary>
    Cloud,
}

/// <summary>A LEAN algorithm project discoverable by the client (a folder on disk for the local CLI,
/// or a cloud project id later).</summary>
public sealed record LeanProject(
    string Name,
    string Path,
    string Language);

/// <summary>Whether a backtest engine is usable, plus a human-readable detail for the UI status line.</summary>
public sealed record LeanAvailability(
    bool IsAvailable,
    string? Version,
    string Detail)
{
    public static LeanAvailability Unavailable(string detail) => new(false, null, detail);
}

/// <summary>A request to run one backtest.</summary>
public sealed record LeanBacktestRequest(
    string Project,
    LeanEngineMode Mode = LeanEngineMode.LocalCli);

/// <summary>One named LEAN summary statistic (e.g. "Sharpe Ratio" → "1.23").</summary>
public sealed record LeanStatistic(string Name, string Value);

/// <summary>One point on the equity / strategy curve.</summary>
public sealed record LeanEquityPoint(DateTime TimeUtc, double Equity);

/// <summary>The parsed outcome of a backtest run: success flag, summary statistics, the equity curve,
/// and the raw engine log (always populated, even on failure, so the UI can show what happened).</summary>
public sealed record LeanBacktestResult(
    bool Success,
    string? Error,
    IReadOnlyList<LeanStatistic> Statistics,
    IReadOnlyList<LeanEquityPoint> Equity,
    string Log)
{
    public static LeanBacktestResult Failed(string error, string log = "") =>
        new(false, error, Array.Empty<LeanStatistic>(), Array.Empty<LeanEquityPoint>(),
            string.IsNullOrEmpty(log) ? error : log);
}

/// <summary>A request to download market data into LEAN's data folder via <c>lean data download</c>.</summary>
public sealed record LeanDataDownloadRequest(
    string Dataset,
    string Tickers,
    string Resolution,
    DateOnly? Start = null,
    DateOnly? End = null);

/// <summary>Outcome of a data-download run.</summary>
public sealed record LeanDataResult(bool Success, string? Error, string Log)
{
    public static LeanDataResult Failed(string error, string log = "") =>
        new(false, error, string.IsNullOrEmpty(log) ? error : log);
}
