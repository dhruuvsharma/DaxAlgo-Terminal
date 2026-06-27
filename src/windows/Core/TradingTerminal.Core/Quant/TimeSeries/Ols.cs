namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>
/// Multi-regressor OLS fit. <see cref="Beta"/> is ordered like the design-matrix columns;
/// <see cref="StandardErrors"/> are the classical (homoskedastic) SEs from σ²(XᵀX)⁻¹.
/// </summary>
public sealed record OlsResult(
    double[] Beta,
    double[] StandardErrors,
    double[] Residuals,
    double Rss,
    double SigmaSquared,
    int N,
    int K)
{
    public double TStat(int j) => StandardErrors[j] > 1e-300 ? Beta[j] / StandardErrors[j] : 0.0;
}

/// <summary>
/// Plain ordinary least squares on a dense design matrix, solved via the normal equations with
/// Gauss-Jordan elimination (partial pivoting). The regressions in this folder are tiny
/// (k ≤ ~25), so the O(k³) solve and explicit (XᵀX)⁻¹ are fine — no need for QR.
/// </summary>
public static class Ols
{
    /// <summary>
    /// Fits y = Xβ + ε. <paramref name="x"/> is row-major: x[i] is observation i's regressor row.
    /// Returns null when the system is singular / under-determined (caller treats as a failed fit).
    /// </summary>
    public static OlsResult? Fit(double[][] x, double[] y)
    {
        var n = y.Length;
        if (n == 0 || x.Length != n) return null;
        var k = x[0].Length;
        if (k == 0 || n <= k) return null;

        // Normal equations: A = XᵀX (k×k), b = Xᵀy.
        var a = new double[k][];
        for (var i = 0; i < k; i++) a[i] = new double[k];
        var b = new double[k];
        for (var i = 0; i < n; i++)
        {
            var row = x[i];
            for (var p = 0; p < k; p++)
            {
                b[p] += row[p] * y[i];
                for (var q = p; q < k; q++) a[p][q] += row[p] * row[q];
            }
        }
        for (var p = 0; p < k; p++)
            for (var q = 0; q < p; q++) a[p][q] = a[q][p];

        var inv = Invert(a);
        if (inv is null) return null;

        var beta = new double[k];
        for (var p = 0; p < k; p++)
        {
            double s = 0;
            for (var q = 0; q < k; q++) s += inv[p][q] * b[q];
            beta[p] = s;
        }

        var residuals = new double[n];
        double rss = 0;
        for (var i = 0; i < n; i++)
        {
            double fit = 0;
            var row = x[i];
            for (var p = 0; p < k; p++) fit += row[p] * beta[p];
            residuals[i] = y[i] - fit;
            rss += residuals[i] * residuals[i];
        }

        var sigma2 = rss / (n - k);
        var se = new double[k];
        for (var p = 0; p < k; p++)
        {
            var v = sigma2 * inv[p][p];
            se[p] = v > 0 ? Math.Sqrt(v) : 0.0;
        }

        return new OlsResult(beta, se, residuals, rss, sigma2, n, k);
    }

    /// <summary>Gauss-Jordan inverse with partial pivoting; null when singular.</summary>
    private static double[][]? Invert(double[][] a)
    {
        var k = a.Length;
        var aug = new double[k][];
        for (var i = 0; i < k; i++)
        {
            aug[i] = new double[2 * k];
            Array.Copy(a[i], aug[i], k);
            aug[i][k + i] = 1.0;
        }

        for (var col = 0; col < k; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < k; r++)
                if (Math.Abs(aug[r][col]) > Math.Abs(aug[pivot][col])) pivot = r;
            if (Math.Abs(aug[pivot][col]) < 1e-12) return null;
            (aug[col], aug[pivot]) = (aug[pivot], aug[col]);

            var d = aug[col][col];
            for (var c = 0; c < 2 * k; c++) aug[col][c] /= d;
            for (var r = 0; r < k; r++)
            {
                if (r == col) continue;
                var f = aug[r][col];
                if (f == 0) continue;
                for (var c = 0; c < 2 * k; c++) aug[r][c] -= f * aug[col][c];
            }
        }

        var inv = new double[k][];
        for (var i = 0; i < k; i++)
        {
            inv[i] = new double[k];
            Array.Copy(aug[i], k, inv[i], 0, k);
        }
        return inv;
    }
}
