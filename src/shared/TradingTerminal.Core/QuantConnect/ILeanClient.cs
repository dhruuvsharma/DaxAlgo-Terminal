namespace TradingTerminal.Core.QuantConnect;

/// <summary>
/// Polyglot seam to the QuantConnect / LEAN backtest engine. The default implementation shells out to
/// the local <c>lean</c> CLI as a subprocess (the same subprocess + structured-output pattern used by
/// the C++ backtester and the Python AI sidecar); a cloud REST implementation can be swapped in later
/// behind this interface. All calls are degrade-not-throw: a missing CLI surfaces as an unavailable
/// status / failed result rather than an exception, so the UI stays responsive.
/// </summary>
public interface ILeanClient
{
    /// <summary>Which backend this instance drives.</summary>
    LeanEngineMode Mode { get; }

    /// <summary>Probe whether the engine is usable (CLI present + responding) for the status panel.</summary>
    Task<LeanAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>Discover available algorithm projects (local folders today, cloud projects later).</summary>
    Task<IReadOnlyList<LeanProject>> ListProjectsAsync(CancellationToken ct = default);

    /// <summary>Run one backtest, streaming engine log lines to <paramref name="progress"/> as they
    /// arrive, and returning the parsed statistics + equity curve when it finishes.</summary>
    Task<LeanBacktestResult> RunBacktestAsync(
        LeanBacktestRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>Download market data into LEAN's data folder, streaming progress lines.</summary>
    Task<LeanDataResult> DownloadDataAsync(
        LeanDataDownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
