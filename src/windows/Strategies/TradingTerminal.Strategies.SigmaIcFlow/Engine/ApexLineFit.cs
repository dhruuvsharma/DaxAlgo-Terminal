namespace TradingTerminal.Strategies.SigmaIcFlow.Engine;

/// <summary>
/// A fitted line over a footprint feature series (buy centroid, sell centroid, or POC across
/// recent bars), with regression diagnostics. The v2 Apex strategy fits three of these — buy,
/// sell, POC — and reads the wedge/control geometry off them. Computed from an exponentially
/// weighted regression with a Newey-West HAC standard error on the slope.
/// </summary>
/// <param name="Slope">β: per-bar drift of the fitted quantity.</param>
/// <param name="Intercept">α: fitted value at x = 0 (oldest bar in the window).</param>
/// <param name="FittedEndpoint">ŷ at the newest bar — the line's current projected level.</param>
/// <param name="RSquared">R² ∈ [0, 1] of the fit.</param>
/// <param name="NeweyWestStandardError">HAC standard error of <see cref="Slope"/>.</param>
/// <param name="ResidualStdev">σ_res: residual standard deviation about the line.</param>
public sealed record ApexLineFit(
    double Slope,
    double Intercept,
    double FittedEndpoint,
    double RSquared,
    double NeweyWestStandardError,
    double ResidualStdev)
{
    /// <summary>An empty / unfitted line (all zeros), used before the window warms up.</summary>
    public static ApexLineFit Empty => new(0, 0, 0, 0, 0, 0);

    /// <summary>t-statistic of the slope: β / SE (0 when SE is non-positive).</summary>
    public double SlopeTStat => NeweyWestStandardError > 1e-300 ? Slope / NeweyWestStandardError : 0.0;
}
