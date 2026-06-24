namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>
/// Output of a univariate-observation Kalman filter run. All arrays align 1:1 with the input
/// observations. <see cref="Level"/> is the filtered first state (smoothed price / intercept);
/// <see cref="Slope"/> is the second state where the model has one (trend slope / hedge beta),
/// else empty. Innovations are the one-step prediction errors with their standardized z-scores.
/// </summary>
public sealed record KalmanResult(
    double[] Level,
    double[] Slope,
    double[] Innovations,
    double[] StandardizedInnovations,
    double LogLikelihood,
    double ObservationNoise,
    double ProcessNoise);

/// <summary>
/// Closed-form scalar/2-state Kalman filters for the ML tool windows — no matrix library needed:
/// <list type="bullet">
/// <item><b>Local level</b>: xₜ = xₜ₋₁ + wₜ, yₜ = xₜ + vₜ — an adaptive EWMA whose effective
/// smoothing follows the signal-to-noise ratio q/r.</item>
/// <item><b>Local linear trend</b>: state (level, slope), level integrates the slope — tracks
/// drifting prices without the lag a fixed MA has.</item>
/// <item><b>Dynamic regression</b> (pairs hedge): yₜ = αₜ + βₜ·xₜ + vₜ with (α, β) random
/// walks — the time-varying hedge ratio classic.</item>
/// </list>
/// The observation noise R is estimated from the data (variance of first differences); the
/// process noise is R scaled by the caller's q/r ratio, which is the single intuitive knob the
/// UI exposes (bigger ⇒ faster tracking, noisier state).
/// </summary>
public static class KalmanFilters
{
    public const int MinObservations = 10;

    /// <summary>Local level model. <paramref name="qOverR"/> is the process/observation noise ratio.</summary>
    public static KalmanResult? LocalLevel(IReadOnlyList<double> y, double qOverR)
    {
        var n = y.Count;
        if (n < MinObservations || qOverR <= 0) return null;

        var r = DiffVariance(y);
        if (r <= 0) return null;
        var q = qOverR * r;

        var level = new double[n];
        var innov = new double[n];
        var stdInnov = new double[n];
        double ll = 0;

        var x = y[0];
        var p = r; // diffuse-ish start
        level[0] = x;

        for (var t = 1; t < n; t++)
        {
            // Predict.
            var pPred = p + q;
            // Update.
            var s = pPred + r;
            var e = y[t] - x;
            var k = pPred / s;
            x += k * e;
            p = (1 - k) * pPred;

            level[t] = x;
            innov[t] = e;
            stdInnov[t] = s > 1e-300 ? e / Math.Sqrt(s) : 0;
            ll += -0.5 * (Math.Log(2 * Math.PI * s) + e * e / s);
        }

        return new KalmanResult(level, Array.Empty<double>(), innov, stdInnov, ll, r, q);
    }

    /// <summary>
    /// Local linear trend model — state (level, slope) with F = [[1,1],[0,1]], H = [1,0],
    /// Q = q·diag(1, 0.1) (slope drifts an order of magnitude slower than the level).
    /// </summary>
    public static KalmanResult? LocalLinearTrend(IReadOnlyList<double> y, double qOverR)
    {
        var n = y.Count;
        if (n < MinObservations || qOverR <= 0) return null;

        var r = DiffVariance(y);
        if (r <= 0) return null;
        var qL = qOverR * r;
        var qS = 0.1 * qL;

        var level = new double[n];
        var slope = new double[n];
        var innov = new double[n];
        var stdInnov = new double[n];
        double ll = 0;

        // State and covariance (symmetric 2x2 as scalars p11, p12, p22).
        double xL = y[0], xS = 0;
        double p11 = r, p12 = 0, p22 = r;
        level[0] = xL;

        for (var t = 1; t < n; t++)
        {
            // Predict: x = F x; P = F P Fᵀ + Q with F = [[1,1],[0,1]].
            var xlPred = xL + xS;
            var p11p = p11 + 2 * p12 + p22 + qL;
            var p12p = p12 + p22;
            var p22p = p22 + qS;

            // Update with H = [1,0]: S = p11p + r; K = (p11p, p12p)/S.
            var s = p11p + r;
            var e = y[t] - xlPred;
            var k1 = p11p / s;
            var k2 = p12p / s;

            xL = xlPred + k1 * e;
            xS += k2 * e;

            p11 = (1 - k1) * p11p;
            p12 = (1 - k1) * p12p;
            p22 = p22p - k2 * p12p;

            level[t] = xL;
            slope[t] = xS;
            innov[t] = e;
            stdInnov[t] = s > 1e-300 ? e / Math.Sqrt(s) : 0;
            ll += -0.5 * (Math.Log(2 * Math.PI * s) + e * e / s);
        }

        return new KalmanResult(level, slope, innov, stdInnov, ll, r, qL);
    }

    /// <summary>
    /// Dynamic regression yₜ = αₜ + βₜ·xₜ + vₜ with random-walk coefficients — the time-varying
    /// pairs hedge ratio. Returns Level = α series, Slope = β series; the innovation is the
    /// one-step spread surprise (the tradeable signal when standardized).
    /// </summary>
    public static KalmanResult? DynamicRegression(IReadOnlyList<double> y, IReadOnlyList<double> x, double qOverR)
    {
        var n = y.Count;
        if (n < MinObservations || x.Count != n || qOverR <= 0) return null;

        var r = DiffVariance(y);
        if (r <= 0) return null;
        var q = qOverR * r;

        var alpha = new double[n];
        var beta = new double[n];
        var innov = new double[n];
        var stdInnov = new double[n];
        double ll = 0;

        // Seed β from a static OLS over the first chunk so the filter doesn't spend half the
        // sample walking from 0; α seeds to match the first observation.
        var seedLen = Math.Min(n, 30);
        var b0 = SeedBeta(y, x, seedLen);
        double a = y[0] - b0 * x[0], b = b0;
        double p11 = r, p12 = 0, p22 = r / Math.Max(Variance(x, seedLen), 1e-12);

        alpha[0] = a;
        beta[0] = b;

        for (var t = 1; t < n; t++)
        {
            // Predict (F = I): P += Q.
            var p11p = p11 + q;
            var p12p = p12;
            var p22p = p22 + q;

            // Update with Hₜ = [1, xₜ].
            var h2 = x[t];
            var s = p11p + 2 * h2 * p12p + h2 * h2 * p22p + r;
            var e = y[t] - (a + b * h2);
            var k1 = (p11p + h2 * p12p) / s;
            var k2 = (p12p + h2 * p22p) / s;

            a += k1 * e;
            b += k2 * e;

            // Joseph-lite covariance update (sufficient at these scales).
            var p11n = p11p - k1 * (p11p + h2 * p12p);
            var p12n = p12p - k1 * (p12p + h2 * p22p);
            var p22n = p22p - k2 * (p12p + h2 * p22p);
            p11 = p11n; p12 = p12n; p22 = p22n;

            alpha[t] = a;
            beta[t] = b;
            innov[t] = e;
            stdInnov[t] = s > 1e-300 ? e / Math.Sqrt(s) : 0;
            ll += -0.5 * (Math.Log(2 * Math.PI * s) + e * e / s);
        }

        return new KalmanResult(alpha, beta, innov, stdInnov, ll, r, q);
    }

    /// <summary>Variance of first differences — a robust observation-noise scale for price series.</summary>
    private static double DiffVariance(IReadOnlyList<double> y)
    {
        var n = y.Count;
        if (n < 3) return 0;
        double mean = 0;
        for (var i = 1; i < n; i++) mean += y[i] - y[i - 1];
        mean /= n - 1;
        double v = 0;
        for (var i = 1; i < n; i++)
        {
            var d = y[i] - y[i - 1] - mean;
            v += d * d;
        }
        v /= n - 2;
        return v > 1e-300 ? v : 0;
    }

    private static double SeedBeta(IReadOnlyList<double> y, IReadOnlyList<double> x, int len)
    {
        double mx = 0, my = 0;
        for (var i = 0; i < len; i++) { mx += x[i]; my += y[i]; }
        mx /= len; my /= len;
        double sxx = 0, sxy = 0;
        for (var i = 0; i < len; i++)
        {
            sxx += (x[i] - mx) * (x[i] - mx);
            sxy += (x[i] - mx) * (y[i] - my);
        }
        return sxx > 1e-300 ? sxy / sxx : 0.0;
    }

    private static double Variance(IReadOnlyList<double> x, int len)
    {
        double m = 0;
        for (var i = 0; i < len; i++) m += x[i];
        m /= len;
        double v = 0;
        for (var i = 0; i < len; i++) v += (x[i] - m) * (x[i] - m);
        return v / Math.Max(len - 1, 1);
    }
}
