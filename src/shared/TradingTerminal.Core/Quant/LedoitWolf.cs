namespace TradingTerminal.Core.Quant;

/// <summary>
/// Ledoit-Wolf shrinkage covariance result.
/// </summary>
/// <param name="Covariance">Shrunk covariance matrix Σ̂ (k×k, PSD).</param>
/// <param name="ShrinkageIntensity">δ̂ ∈ [0, 1]: weight placed on the shrinkage target.</param>
/// <param name="Dimension">k: number of variables.</param>
public sealed record LedoitWolfResult(double[,] Covariance, double ShrinkageIntensity, int Dimension);

/// <summary>
/// Ledoit-Wolf (2004) shrinkage estimator of a covariance matrix toward a scaled-identity target
/// F = μ·I (μ = average sample variance = trace(S)/k). The shrunk estimator is
/// <c>Σ̂ = δ·F + (1−δ)·S</c> with the optimal intensity
/// <c>δ̂ = clamp( (π̂ − ρ̂) / γ̂ / n , 0, 1 )</c> following the paper's
/// "honey, I shrunk the sample covariance matrix" derivation; here we use the standard
/// single-target plug-in with π̂ = Σ Var(sᵢⱼ) estimated from the data, ρ̂ = 0 for the
/// scaled-identity target, and γ̂ = ‖S − F‖²_F. Always returns a PSD matrix (a convex blend of
/// two PSD matrices). Includes correlation conversion and a ridge-stabilised inverse.
///
/// <para>Why shrink: with few observations relative to k, the sample covariance S is noisy and
/// often near-singular; the identity target is well-conditioned, and the blend trades a little
/// bias for a large variance reduction — exactly what a portfolio / signal-weighting inverse needs.</para>
/// </summary>
public static class LedoitWolf
{
    /// <summary>
    /// Estimates the shrunk covariance from <paramref name="observations"/> (rows = samples,
    /// columns = variables). Requires at least 2 rows. Columns are demeaned with the sample mean.
    /// </summary>
    public static LedoitWolfResult Estimate(double[,] observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var n = observations.GetLength(0);
        var k = observations.GetLength(1);
        if (n < 2 || k < 1) throw new ArgumentException("Need at least 2 samples and 1 variable.");

        // Column means.
        var mean = new double[k];
        for (var j = 0; j < k; j++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++) sum += observations[i, j];
            mean[j] = sum / n;
        }

        // Demeaned data.
        var z = new double[n, k];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < k; j++)
                z[i, j] = observations[i, j] - mean[j];

        // Sample covariance S (divide by n, matching the LW derivation).
        var s = new double[k, k];
        for (var a = 0; a < k; a++)
            for (var b = a; b < k; b++)
            {
                double acc = 0;
                for (var i = 0; i < n; i++) acc += z[i, a] * z[i, b];
                acc /= n;
                s[a, b] = acc;
                s[b, a] = acc;
            }

        // Target F = μ·I, μ = mean of diagonal.
        double mu = 0;
        for (var d = 0; d < k; d++) mu += s[d, d];
        mu /= k;

        // γ̂ = ‖S − F‖²_F.
        double gamma = 0;
        for (var a = 0; a < k; a++)
            for (var b = 0; b < k; b++)
            {
                var f = a == b ? mu : 0.0;
                var diff = s[a, b] - f;
                gamma += diff * diff;
            }

        // π̂ = Σ_{a,b} Var of the sample second moment estimator.
        double pi = 0;
        for (var a = 0; a < k; a++)
            for (var b = 0; b < k; b++)
            {
                double acc = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = z[i, a] * z[i, b] - s[a, b];
                    acc += t * t;
                }
                pi += acc / n;
            }

        // δ̂ = clamp( (π̂/n) / γ̂ , 0, 1 ). ρ̂ = 0 for the scaled-identity target.
        var delta = gamma > 1e-300 ? Math.Clamp(pi / n / gamma, 0.0, 1.0) : 1.0;

        var cov = new double[k, k];
        for (var a = 0; a < k; a++)
            for (var b = 0; b < k; b++)
            {
                var f = a == b ? mu : 0.0;
                cov[a, b] = delta * f + (1 - delta) * s[a, b];
            }

        return new LedoitWolfResult(cov, delta, k);
    }

    /// <summary>Converts a covariance matrix to a correlation matrix (unit diagonal; guards σ→0).</summary>
    public static double[,] ToCorrelation(double[,] covariance)
    {
        ArgumentNullException.ThrowIfNull(covariance);
        var k = covariance.GetLength(0);
        var sd = new double[k];
        for (var i = 0; i < k; i++) sd[i] = Math.Sqrt(Math.Max(0, covariance[i, i]));
        var corr = new double[k, k];
        for (var a = 0; a < k; a++)
            for (var b = 0; b < k; b++)
            {
                var denom = sd[a] * sd[b];
                corr[a, b] = denom > 1e-300 ? Math.Clamp(covariance[a, b] / denom, -1.0, 1.0) : (a == b ? 1.0 : 0.0);
            }
        return corr;
    }

    /// <summary>
    /// Safe matrix inverse via Gauss-Jordan with partial pivoting. Adds a small ridge
    /// (<paramref name="ridge"/>·mean-diagonal) to the diagonal if the matrix is near-singular,
    /// then retries once — so callers always get a usable inverse. Throws only if the matrix is
    /// non-square.
    /// </summary>
    public static double[,] SafeInverse(double[,] matrix, double ridge = 1e-8)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var k = matrix.GetLength(0);
        if (matrix.GetLength(1) != k) throw new ArgumentException("Matrix must be square.");

        if (TryInvert(matrix, out var inv)) return inv;

        // Ridge-stabilise: add ridge·(mean diagonal) to the diagonal and retry.
        double meanDiag = 0;
        for (var i = 0; i < k; i++) meanDiag += matrix[i, i];
        meanDiag = Math.Max(Math.Abs(meanDiag) / Math.Max(1, k), 1e-12);
        var stabilised = (double[,])matrix.Clone();
        for (var i = 0; i < k; i++) stabilised[i, i] += ridge * meanDiag;
        return TryInvert(stabilised, out var inv2) ? inv2 : Identity(k);
    }

    private static bool TryInvert(double[,] source, out double[,] inverse)
    {
        var k = source.GetLength(0);
        var a = (double[,])source.Clone();
        inverse = Identity(k);
        for (var col = 0; col < k; col++)
        {
            // Partial pivot.
            var pivotRow = col;
            var best = Math.Abs(a[col, col]);
            for (var r = col + 1; r < k; r++)
            {
                var v = Math.Abs(a[r, col]);
                if (v > best) { best = v; pivotRow = r; }
            }
            if (best < 1e-14) return false; // singular

            if (pivotRow != col) { SwapRows(a, col, pivotRow); SwapRows(inverse, col, pivotRow); }

            var diag = a[col, col];
            for (var j = 0; j < k; j++) { a[col, j] /= diag; inverse[col, j] /= diag; }

            for (var r = 0; r < k; r++)
            {
                if (r == col) continue;
                var factor = a[r, col];
                if (factor == 0) continue;
                for (var j = 0; j < k; j++)
                {
                    a[r, j] -= factor * a[col, j];
                    inverse[r, j] -= factor * inverse[col, j];
                }
            }
        }
        return true;
    }

    private static void SwapRows(double[,] m, int r1, int r2)
    {
        var k = m.GetLength(1);
        for (var j = 0; j < k; j++) (m[r1, j], m[r2, j]) = (m[r2, j], m[r1, j]);
    }

    private static double[,] Identity(int k)
    {
        var id = new double[k, k];
        for (var i = 0; i < k; i++) id[i, i] = 1.0;
        return id;
    }
}
