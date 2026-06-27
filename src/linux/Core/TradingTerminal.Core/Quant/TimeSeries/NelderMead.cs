namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>
/// Minimal derivative-free Nelder-Mead simplex minimizer for the low-dimensional likelihood
/// surfaces in this folder (GARCH is 3-D). Standard coefficients (reflect 1, expand 2,
/// contract ½, shrink ½); deterministic; bounded by iteration count and simplex-size tolerance.
/// Constraint handling is the caller's job — return a large penalty from the objective.
/// </summary>
public static class NelderMead
{
    public static (double[] X, double F)? Minimize(
        Func<double[], double> f,
        double[] start,
        double step = 0.1,
        int maxIterations = 500,
        double tolerance = 1e-8)
    {
        var dim = start.Length;
        if (dim == 0) return null;

        // Initial simplex: start point + one vertex per axis.
        var simplex = new double[dim + 1][];
        var values = new double[dim + 1];
        simplex[0] = (double[])start.Clone();
        values[0] = f(simplex[0]);
        for (var i = 0; i < dim; i++)
        {
            var v = (double[])start.Clone();
            v[i] += step * (Math.Abs(v[i]) > 1e-12 ? Math.Abs(v[i]) : 1.0);
            simplex[i + 1] = v;
            values[i + 1] = f(v);
        }

        for (var iter = 0; iter < maxIterations; iter++)
        {
            Array.Sort(values, simplex);

            if (Math.Abs(values[^1] - values[0]) < tolerance) break;

            // Centroid of all but the worst.
            var centroid = new double[dim];
            for (var i = 0; i <= dim - 1; i++)
                for (var j = 0; j < dim; j++) centroid[j] += simplex[i][j] / dim;

            var worst = simplex[^1];
            var reflected = Blend(centroid, worst, 1.0);
            var fr = f(reflected);

            if (fr < values[0])
            {
                var expanded = Blend(centroid, worst, 2.0);
                var fe = f(expanded);
                if (fe < fr) { simplex[^1] = expanded; values[^1] = fe; }
                else { simplex[^1] = reflected; values[^1] = fr; }
            }
            else if (fr < values[^2])
            {
                simplex[^1] = reflected;
                values[^1] = fr;
            }
            else
            {
                var contracted = Blend(centroid, worst, -0.5);
                var fc = f(contracted);
                if (fc < values[^1]) { simplex[^1] = contracted; values[^1] = fc; }
                else
                {
                    // Shrink toward the best vertex.
                    for (var i = 1; i <= dim; i++)
                    {
                        for (var j = 0; j < dim; j++)
                            simplex[i][j] = simplex[0][j] + 0.5 * (simplex[i][j] - simplex[0][j]);
                        values[i] = f(simplex[i]);
                    }
                }
            }
        }

        Array.Sort(values, simplex);
        return double.IsFinite(values[0]) ? (simplex[0], values[0]) : null;
    }

    /// <summary>x = centroid + coeff·(centroid − worst); coeff −0.5 gives the inside contraction.</summary>
    private static double[] Blend(double[] centroid, double[] worst, double coeff)
    {
        var x = new double[centroid.Length];
        for (var j = 0; j < x.Length; j++) x[j] = centroid[j] + coeff * (centroid[j] - worst[j]);
        return x;
    }
}
