namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>
/// A fitted GARCH(1,1): σ²ₜ = ω + α·r²ₜ₋₁ + β·σ²ₜ₋₁ over demeaned returns. Persistence is α+β;
/// <see cref="LongRunVariance"/> = ω/(1−α−β). <see cref="ConditionalVariance"/> aligns 1:1 with
/// the input returns.
/// </summary>
public sealed record GarchFit(
    double Omega,
    double Alpha,
    double Beta,
    double Persistence,
    double LongRunVariance,
    double LogLikelihood,
    double[] ConditionalVariance,
    double MeanReturn,
    int N)
{
    /// <summary>
    /// h-step-ahead conditional-variance forecast:
    /// σ²_{t+h} = σ²_LR + (α+β)^{h−1}·(σ²_{t+1} − σ²_LR).
    /// </summary>
    public double ForecastVariance(int h)
    {
        if (ConditionalVariance.Length == 0 || h < 1) return LongRunVariance;
        // Seed with E[σ²_{t+1}] = ω + (α+β)·σ²ₜ (E[r²ₜ|σ²ₜ] = σ²ₜ), then decay toward long-run.
        var next = Omega + Persistence * ConditionalVariance[^1];
        if (Persistence >= 1.0) return next; // IGARCH-ish: no mean reversion, hold the level
        return LongRunVariance + Math.Pow(Persistence, h - 1) * (next - LongRunVariance);
    }
}

/// <summary>
/// GARCH(1,1) by Gaussian maximum likelihood: the (ω, α, β) surface is climbed with Nelder-Mead
/// from the standard textbook start (α=0.08, β=0.90, ω targeting the sample variance), with hard
/// penalties enforcing ω&gt;0, α,β≥0, α+β&lt;1. Deterministic — no RNG in the simplex.
/// </summary>
public static class GarchModel
{
    public const int MinObservations = 60;

    /// <summary>Fits GARCH(1,1) to a return series (fractional returns, not %). Null when too short/degenerate.</summary>
    public static GarchFit? Fit(IReadOnlyList<double> returns)
    {
        var n = returns.Count;
        if (n < MinObservations) return null;

        double mean = 0;
        for (var i = 0; i < n; i++) mean += returns[i];
        mean /= n;

        var r = new double[n];
        double sampleVar = 0;
        for (var i = 0; i < n; i++)
        {
            r[i] = returns[i] - mean;
            sampleVar += r[i] * r[i];
        }
        sampleVar /= n;
        if (sampleVar <= 1e-300) return null;

        // Optimize in scaled space: ω is parameterized as a fraction of sample variance so all
        // three simplex axes have comparable magnitudes.
        double NegLogLik(double[] th)
        {
            var omega = th[0] * sampleVar;
            var alpha = th[1];
            var beta = th[2];
            if (omega <= 0 || alpha < 0 || beta < 0 || alpha + beta >= 0.9995) return 1e10;

            var v = sampleVar;
            double ll = 0;
            for (var t = 0; t < n; t++)
            {
                if (t > 0) v = omega + alpha * r[t - 1] * r[t - 1] + beta * v;
                if (v <= 1e-300) return 1e10;
                ll += Math.Log(v) + r[t] * r[t] / v;
            }
            return 0.5 * ll;
        }

        var start = new[] { 0.02, 0.08, 0.90 };
        var best = NelderMead.Minimize(NegLogLik, start, step: 0.05, maxIterations: 800, tolerance: 1e-9);
        if (best is null) return null;

        var w = best.Value.X[0] * sampleVar;
        var a = best.Value.X[1];
        var b = best.Value.X[2];
        if (w <= 0 || a < 0 || b < 0 || a + b >= 1.0) return null;

        // Final conditional-variance pass at the optimum.
        var cv = new double[n];
        cv[0] = sampleVar;
        for (var t = 1; t < n; t++) cv[t] = w + a * r[t - 1] * r[t - 1] + b * cv[t - 1];

        var persistence = a + b;
        var lrVar = persistence < 1.0 ? w / (1.0 - persistence) : sampleVar;
        var logLik = -best.Value.F - 0.5 * n * Math.Log(2.0 * Math.PI);

        return new GarchFit(w, a, b, persistence, lrVar, logLik, cv, mean, n);
    }
}
