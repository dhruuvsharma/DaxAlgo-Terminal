namespace TradingTerminal.Core.Quant;

/// <summary>Curve families available to chart overlay fits (e.g. the footprint POC regressions).
/// Each is fit and sampled by <see cref="CurveFitting.FitEvaluate"/>.</summary>
public enum CurveFitKind
{
    Linear,
    Quadratic,
    Cubic,
    TheilSen,
    Exponential,
    Logarithmic,
    Lowess,
}

/// <summary>
/// Small deterministic curve-fitting toolkit for chart overlays. Fits one of
/// <see cref="CurveFitKind"/> to (x, y) samples and returns the fitted ŷ at arbitrary abscissae —
/// sampled values rather than coefficients, so closed-form fits and local regression (LOWESS)
/// share one shape the renderer can draw as a polyline.
///
/// <para>Inputs are assumed finite (callers filter NaN gaps before fitting). Polynomial fits
/// centre/scale x before building the normal equations so raw column indices stay
/// well-conditioned; degenerate geometry (coincident x, singular system, non-positive y for the
/// exponential) yields null rather than garbage.</para>
/// </summary>
public static class CurveFitting
{
    /// <summary>Minimum number of (x, y) samples for the kind to produce a meaningful fit.</summary>
    public static int MinPoints(CurveFitKind kind) => kind switch
    {
        CurveFitKind.Quadratic => 3,
        CurveFitKind.Cubic => 4,
        CurveFitKind.Lowess => 3,
        _ => 2,
    };

    /// <summary>
    /// Fits <paramref name="kind"/> to the (x, y) samples and returns ŷ at each
    /// <paramref name="evalX"/>, or null when no fit is possible (too few points, degenerate
    /// geometry, or — for the exponential — any non-positive y).
    /// </summary>
    public static double[]? FitEvaluate(
        CurveFitKind kind,
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        IReadOnlyList<double> evalX)
    {
        if (x.Count != y.Count || x.Count < MinPoints(kind) || evalX.Count == 0) return null;
        return kind switch
        {
            CurveFitKind.Linear => Polynomial(x, y, evalX, 1),
            CurveFitKind.Quadratic => Polynomial(x, y, evalX, 2),
            CurveFitKind.Cubic => Polynomial(x, y, evalX, 3),
            CurveFitKind.TheilSen => TheilSen(x, y, evalX),
            CurveFitKind.Exponential => Exponential(x, y, evalX),
            CurveFitKind.Logarithmic => Logarithmic(x, y, evalX),
            CurveFitKind.Lowess => Lowess(x, y, evalX),
            _ => null,
        };
    }

    /// <summary>Least-squares polynomial of the given degree via normal equations on x
    /// centred/scaled to [−1, 1] (a raw Vandermonde on bar indices is ill-conditioned by cubic).</summary>
    private static double[]? Polynomial(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> evalX, int degree)
    {
        var n = x.Count;
        double mean = 0;
        for (var i = 0; i < n; i++) mean += x[i];
        mean /= n;
        double scale = 0;
        for (var i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(x[i] - mean));
        if (scale < 1e-12) return null;

        var terms = degree + 1;
        var s = new double[2 * degree + 1]; // Σ tᵏ moments
        var b = new double[terms];          // Σ y·tᵏ
        for (var i = 0; i < n; i++)
        {
            var t = (x[i] - mean) / scale;
            double p = 1;
            for (var k = 0; k < s.Length; k++)
            {
                s[k] += p;
                if (k < terms) b[k] += y[i] * p;
                p *= t;
            }
        }

        var m = new double[terms, terms];
        for (var r = 0; r < terms; r++)
            for (var c = 0; c < terms; c++)
                m[r, c] = s[r + c];

        var coef = Solve(m, b);
        if (coef is null) return null;

        var result = new double[evalX.Count];
        for (var i = 0; i < evalX.Count; i++)
        {
            var t = (evalX[i] - mean) / scale;
            double v = 0, p = 1;
            for (var k = 0; k < terms; k++)
            {
                v += coef[k] * p;
                p *= t;
            }
            result[i] = v;
        }
        return result;
    }

    /// <summary>Robust line: median of all pairwise slopes, intercept = median residual. O(n²)
    /// pairs — intended for overlay-sized inputs (tens of points), not bulk series.</summary>
    private static double[]? TheilSen(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> evalX)
    {
        var n = x.Count;
        var slopes = new List<double>(n * (n - 1) / 2);
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var dx = x[j] - x[i];
                if (Math.Abs(dx) < 1e-12) continue;
                slopes.Add((y[j] - y[i]) / dx);
            }
        if (slopes.Count == 0) return null;
        var slope = Median(slopes);

        var residuals = new List<double>(n);
        for (var i = 0; i < n; i++) residuals.Add(y[i] - slope * x[i]);
        var intercept = Median(residuals);

        var result = new double[evalX.Count];
        for (var i = 0; i < evalX.Count; i++) result[i] = intercept + slope * evalX[i];
        return result;
    }

    /// <summary>y = a·e^(bx), fit as a line on ln y. Requires strictly positive y.</summary>
    private static double[]? Exponential(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> evalX)
    {
        var logY = new double[y.Count];
        for (var i = 0; i < y.Count; i++)
        {
            if (y[i] <= 0) return null;
            logY[i] = Math.Log(y[i]);
        }
        var line = Polynomial(x, logY, evalX, 1);
        if (line is null) return null;
        for (var i = 0; i < line.Length; i++) line[i] = Math.Exp(line[i]);
        return line;
    }

    /// <summary>y = a + b·ln(x + shift), with shift chosen so every log argument (fit and eval
    /// abscissae alike) is ≥ 1. For 0-based column indices this is the classic a + b·ln(x + 1).</summary>
    private static double[]? Logarithmic(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> evalX)
    {
        var min = double.PositiveInfinity;
        for (var i = 0; i < x.Count; i++) min = Math.Min(min, x[i]);
        for (var i = 0; i < evalX.Count; i++) min = Math.Min(min, evalX[i]);
        var shift = 1 - min;

        var u = new double[x.Count];
        for (var i = 0; i < x.Count; i++) u[i] = Math.Log(x[i] + shift);
        var evalU = new double[evalX.Count];
        for (var i = 0; i < evalX.Count; i++) evalU[i] = Math.Log(evalX[i] + shift);
        return Polynomial(u, y, evalU, 1);
    }

    /// <summary>Locally weighted linear regression (tricube kernel, span = half the sample, no
    /// robustness iterations). Degenerate local geometry falls back to the weighted mean.</summary>
    private static double[] Lowess(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> evalX)
    {
        var n = x.Count;
        var k = Math.Min(n, Math.Max(3, (int)Math.Ceiling(0.5 * n)));
        var result = new double[evalX.Count];
        var dist = new double[n];
        var idx = new int[n];

        for (var e = 0; e < evalX.Count; e++)
        {
            var x0 = evalX[e];
            for (var i = 0; i < n; i++)
            {
                dist[i] = Math.Abs(x[i] - x0);
                idx[i] = i;
            }
            Array.Sort(dist, idx);
            var dmax = dist[k - 1];

            double sw = 0, swx = 0, swy = 0, swxx = 0, swxy = 0;
            for (var i = 0; i < k; i++)
            {
                var w = dmax < 1e-12 ? 1.0 : Tricube(dist[i] / dmax);
                if (w <= 0) continue;
                double xi = x[idx[i]], yi = y[idx[i]];
                sw += w; swx += w * xi; swy += w * yi; swxx += w * xi * xi; swxy += w * xi * yi;
            }
            if (sw < 1e-12)
            {
                // Every neighbour sat exactly at dmax (weight 0) — average them unweighted.
                double sum = 0;
                for (var i = 0; i < k; i++) sum += y[idx[i]];
                result[e] = sum / k;
                continue;
            }

            var denom = sw * swxx - swx * swx;
            if (Math.Abs(denom) < 1e-9)
            {
                result[e] = swy / sw;
            }
            else
            {
                var slope = (sw * swxy - swx * swy) / denom;
                result[e] = (swy - slope * swx) / sw + slope * x0;
            }
        }
        return result;
    }

    private static double Tricube(double u)
    {
        if (u >= 1) return 0;
        var t = 1 - u * u * u;
        return t * t * t;
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        var mid = v.Count / 2;
        return v.Count % 2 == 1 ? v[mid] : 0.5 * (v[mid - 1] + v[mid]);
    }

    /// <summary>Gaussian elimination with partial pivoting; null when the system is singular.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = new double[n, n + 1];
        for (var r = 0; r < n; r++)
        {
            for (var c = 0; c < n; c++) m[r, c] = a[r, c];
            m[r, n] = b[r];
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-10) return null;
            if (pivot != col)
                for (var c = col; c <= n; c++)
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = col + 1; r < n; r++)
            {
                var f = m[r, col] / m[col, col];
                for (var c = col; c <= n; c++) m[r, c] -= f * m[col, c];
            }
        }

        var sol = new double[n];
        for (var r = n - 1; r >= 0; r--)
        {
            var v = m[r, n];
            for (var c = r + 1; c < n; c++) v -= m[r, c] * sol[c];
            sol[r] = v / m[r, r];
        }
        return sol;
    }
}
