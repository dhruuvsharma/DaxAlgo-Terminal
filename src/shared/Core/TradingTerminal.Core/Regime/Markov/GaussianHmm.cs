namespace TradingTerminal.Core.Regime.Markov;

/// <summary>
/// A 1-D Gaussian hidden Markov model fitted by Baum-Welch (EM). Pure math — no I/O, no clocks,
/// no UI. Given a sequence of scalar observations (for the regime tool, per-bar log returns) it
/// learns <c>K</c> latent states with Gaussian emissions, a row-stochastic transition matrix, and
/// an initial distribution, then decodes the most-likely state path (Viterbi) and the smoothed
/// per-step posteriors (forward-backward).
///
/// Numerics follow Rabiner's scaled formulation: the forward pass normalizes each column so the
/// likelihood is accumulated as a sum of logs of the scaling factors, which keeps long sequences
/// from underflowing. The fit is deterministic for a given <paramref name="seed"/>: it runs a
/// quantile-seeded restart plus a fixed number of perturbed restarts and keeps the best
/// log-likelihood, so results are reproducible and unit-testable.
/// </summary>
public sealed class GaussianHmm
{
    private const double VarianceFloorFraction = 1e-4; // floor each state var at this × global var
    private const double Epsilon = 1e-300;

    public int StateCount { get; }
    public double[] InitialProbabilities { get; private set; }
    public double[][] TransitionMatrix { get; private set; }
    public double[] Means { get; private set; }
    public double[] Variances { get; private set; }
    public double LogLikelihood { get; private set; }
    public int Iterations { get; private set; }
    public bool Converged { get; private set; }

    private GaussianHmm(int states)
    {
        StateCount = states;
        InitialProbabilities = new double[states];
        TransitionMatrix = NewSquare(states);
        Means = new double[states];
        Variances = new double[states];
    }

    /// <summary>Fits a <paramref name="states"/>-state model to <paramref name="observations"/>.
    /// Picks the best of several deterministic restarts by log-likelihood.</summary>
    public static GaussianHmm Fit(
        IReadOnlyList<double> observations, int states,
        int maxIterations = 200, double tolerance = 1e-6, int seed = 12345, int restarts = 4)
    {
        if (states < 1) throw new ArgumentOutOfRangeException(nameof(states), "Need at least one state.");
        if (observations.Count < states + 1)
            throw new ArgumentException($"Need at least {states + 1} observations to fit {states} states.", nameof(observations));

        var obs = observations as double[] ?? observations.ToArray();
        var (globalMean, globalVar) = MeanVar(obs);
        var varFloor = Math.Max(globalVar * VarianceFloorFraction, 1e-12);

        GaussianHmm? best = null;
        var rng = new Random(seed);
        for (var r = 0; r <= restarts; r++)
        {
            var model = new GaussianHmm(states);
            // Restart 0 is the deterministic quantile seed; the rest perturb the means so EM can
            // escape a poor local optimum.
            model.Seed(obs, globalMean, globalVar, perturb: r == 0 ? null : rng);
            model.RunEm(obs, varFloor, maxIterations, tolerance);
            if (best is null || model.LogLikelihood > best.LogLikelihood)
                best = model;
        }
        return best!;
    }

    private void Seed(double[] obs, double globalMean, double globalVar, Random? perturb)
    {
        var sorted = (double[])obs.Clone();
        Array.Sort(sorted);
        var sd = Math.Sqrt(Math.Max(globalVar, 1e-12));
        for (var i = 0; i < StateCount; i++)
        {
            var q = (i + 0.5) / StateCount;
            var idx = Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1);
            var mean = sorted[idx];
            if (perturb is not null) mean += (perturb.NextDouble() - 0.5) * sd;
            Means[i] = mean;
            Variances[i] = Math.Max(globalVar, 1e-12);
            InitialProbabilities[i] = 1.0 / StateCount;
        }
        // Sticky transitions — regimes persist — with the remaining mass spread over the others.
        var stay = StateCount == 1 ? 1.0 : 0.90;
        var leak = StateCount == 1 ? 0.0 : (1.0 - stay) / (StateCount - 1);
        for (var i = 0; i < StateCount; i++)
            for (var j = 0; j < StateCount; j++)
                TransitionMatrix[i][j] = i == j ? stay : leak;
    }

    private void RunEm(double[] obs, double varFloor, int maxIterations, double tolerance)
    {
        int t = obs.Length, k = StateCount;
        var b = NewMatrix(t, k);                 // emission likelihoods
        var alpha = NewMatrix(t, k);
        var beta = NewMatrix(t, k);
        var c = new double[t];                   // scaling factors
        var gamma = NewMatrix(t, k);

        var prevLogL = double.NegativeInfinity;
        for (var iter = 1; iter <= maxIterations; iter++)
        {
            // ---- emissions ----
            for (var i = 0; i < t; i++)
                for (var s = 0; s < k; s++)
                    b[i][s] = Math.Max(Gaussian(obs[i], Means[s], Variances[s]), Epsilon);

            // ---- forward (scaled) ----
            double c0 = 0;
            for (var s = 0; s < k; s++) { alpha[0][s] = InitialProbabilities[s] * b[0][s]; c0 += alpha[0][s]; }
            c[0] = c0 <= 0 ? Epsilon : c0;
            for (var s = 0; s < k; s++) alpha[0][s] /= c[0];
            for (var i = 1; i < t; i++)
            {
                double ci = 0;
                for (var j = 0; j < k; j++)
                {
                    double sum = 0;
                    for (var s = 0; s < k; s++) sum += alpha[i - 1][s] * TransitionMatrix[s][j];
                    alpha[i][j] = sum * b[i][j];
                    ci += alpha[i][j];
                }
                c[i] = ci <= 0 ? Epsilon : ci;
                for (var j = 0; j < k; j++) alpha[i][j] /= c[i];
            }

            double logL = 0;
            for (var i = 0; i < t; i++) logL += Math.Log(c[i]);

            // ---- backward (scaled with the same c) ----
            for (var s = 0; s < k; s++) beta[t - 1][s] = 1.0;
            for (var i = t - 2; i >= 0; i--)
                for (var s = 0; s < k; s++)
                {
                    double sum = 0;
                    for (var j = 0; j < k; j++) sum += TransitionMatrix[s][j] * b[i + 1][j] * beta[i + 1][j];
                    beta[i][s] = sum / c[i + 1];
                }

            // ---- gamma (normalized posterior per step) ----
            for (var i = 0; i < t; i++)
            {
                double norm = 0;
                for (var s = 0; s < k; s++) { gamma[i][s] = alpha[i][s] * beta[i][s]; norm += gamma[i][s]; }
                if (norm <= 0) norm = Epsilon;
                for (var s = 0; s < k; s++) gamma[i][s] /= norm;
            }

            // ---- M-step: initial ----
            for (var s = 0; s < k; s++) InitialProbabilities[s] = gamma[0][s];

            // ---- M-step: transitions (accumulate xi implicitly) ----
            var num = NewSquare(k);
            var den = new double[k];
            for (var i = 0; i < t - 1; i++)
            {
                double norm = 0;
                var xi = NewSquare(k);
                for (var s = 0; s < k; s++)
                    for (var j = 0; j < k; j++)
                    {
                        xi[s][j] = alpha[i][s] * TransitionMatrix[s][j] * b[i + 1][j] * beta[i + 1][j];
                        norm += xi[s][j];
                    }
                if (norm <= 0) norm = Epsilon;
                for (var s = 0; s < k; s++)
                    for (var j = 0; j < k; j++)
                    {
                        var v = xi[s][j] / norm;
                        num[s][j] += v;
                        den[s] += v;
                    }
            }
            for (var s = 0; s < k; s++)
            {
                if (den[s] <= Epsilon) continue; // leave row as-is if state unused
                for (var j = 0; j < k; j++) TransitionMatrix[s][j] = num[s][j] / den[s];
            }

            // ---- M-step: emissions ----
            for (var s = 0; s < k; s++)
            {
                double w = 0, wMean = 0;
                for (var i = 0; i < t; i++) { w += gamma[i][s]; wMean += gamma[i][s] * obs[i]; }
                if (w <= Epsilon) continue;
                var mean = wMean / w;
                double wVar = 0;
                for (var i = 0; i < t; i++) { var d = obs[i] - mean; wVar += gamma[i][s] * d * d; }
                Means[s] = mean;
                Variances[s] = Math.Max(wVar / w, varFloor);
            }

            Iterations = iter;
            LogLikelihood = logL;
            if (Math.Abs(logL - prevLogL) < tolerance) { Converged = true; break; }
            prevLogL = logL;
        }
    }

    /// <summary>Smoothed per-step state posteriors P(state | whole sequence), shape T×K.</summary>
    public double[][] PosteriorProbabilities(IReadOnlyList<double> observations)
    {
        var obs = observations as double[] ?? observations.ToArray();
        int t = obs.Length, k = StateCount;
        var b = NewMatrix(t, k);
        for (var i = 0; i < t; i++)
            for (var s = 0; s < k; s++)
                b[i][s] = Math.Max(Gaussian(obs[i], Means[s], Variances[s]), Epsilon);

        var alpha = NewMatrix(t, k);
        var beta = NewMatrix(t, k);
        var c = new double[t];
        double c0 = 0;
        for (var s = 0; s < k; s++) { alpha[0][s] = InitialProbabilities[s] * b[0][s]; c0 += alpha[0][s]; }
        c[0] = c0 <= 0 ? Epsilon : c0;
        for (var s = 0; s < k; s++) alpha[0][s] /= c[0];
        for (var i = 1; i < t; i++)
        {
            double ci = 0;
            for (var j = 0; j < k; j++)
            {
                double sum = 0;
                for (var s = 0; s < k; s++) sum += alpha[i - 1][s] * TransitionMatrix[s][j];
                alpha[i][j] = sum * b[i][j];
                ci += alpha[i][j];
            }
            c[i] = ci <= 0 ? Epsilon : ci;
            for (var j = 0; j < k; j++) alpha[i][j] /= c[i];
        }
        for (var s = 0; s < k; s++) beta[t - 1][s] = 1.0;
        for (var i = t - 2; i >= 0; i--)
            for (var s = 0; s < k; s++)
            {
                double sum = 0;
                for (var j = 0; j < k; j++) sum += TransitionMatrix[s][j] * b[i + 1][j] * beta[i + 1][j];
                beta[i][s] = sum / c[i + 1];
            }
        var gamma = NewMatrix(t, k);
        for (var i = 0; i < t; i++)
        {
            double norm = 0;
            for (var s = 0; s < k; s++) { gamma[i][s] = alpha[i][s] * beta[i][s]; norm += gamma[i][s]; }
            if (norm <= 0) norm = Epsilon;
            for (var s = 0; s < k; s++) gamma[i][s] /= norm;
        }
        return gamma;
    }

    /// <summary>Most-likely state sequence via the log-space Viterbi algorithm, length T.</summary>
    public int[] Decode(IReadOnlyList<double> observations)
    {
        var obs = observations as double[] ?? observations.ToArray();
        int t = obs.Length, k = StateCount;
        var logA = NewMatrix(k, k);
        for (var i = 0; i < k; i++)
            for (var j = 0; j < k; j++) logA[i][j] = Math.Log(Math.Max(TransitionMatrix[i][j], Epsilon));

        var delta = NewMatrix(t, k);
        var psi = new int[t][];
        for (var i = 0; i < t; i++) psi[i] = new int[k];
        for (var s = 0; s < k; s++)
            delta[0][s] = Math.Log(Math.Max(InitialProbabilities[s], Epsilon)) + LogGaussian(obs[0], Means[s], Variances[s]);
        for (var i = 1; i < t; i++)
            for (var j = 0; j < k; j++)
            {
                var bestVal = double.NegativeInfinity; var bestS = 0;
                for (var s = 0; s < k; s++)
                {
                    var v = delta[i - 1][s] + logA[s][j];
                    if (v > bestVal) { bestVal = v; bestS = s; }
                }
                delta[i][j] = bestVal + LogGaussian(obs[i], Means[j], Variances[j]);
                psi[i][j] = bestS;
            }
        var path = new int[t];
        var last = 0; var lastVal = double.NegativeInfinity;
        for (var s = 0; s < k; s++) if (delta[t - 1][s] > lastVal) { lastVal = delta[t - 1][s]; last = s; }
        path[t - 1] = last;
        for (var i = t - 2; i >= 0; i--) path[i] = psi[i + 1][path[i + 1]];
        return path;
    }

    /// <summary>Stationary distribution of the transition matrix via power iteration.</summary>
    public double[] StationaryDistribution()
    {
        int k = StateCount;
        var v = new double[k];
        for (var i = 0; i < k; i++) v[i] = 1.0 / k;
        for (var iter = 0; iter < 1000; iter++)
        {
            var next = new double[k];
            for (var j = 0; j < k; j++)
            {
                double sum = 0;
                for (var i = 0; i < k; i++) sum += v[i] * TransitionMatrix[i][j];
                next[j] = sum;
            }
            double diff = 0;
            for (var i = 0; i < k; i++) diff += Math.Abs(next[i] - v[i]);
            v = next;
            if (diff < 1e-12) break;
        }
        return v;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static double Gaussian(double x, double mean, double var)
    {
        var d = x - mean;
        return 1.0 / Math.Sqrt(2.0 * Math.PI * var) * Math.Exp(-d * d / (2.0 * var));
    }

    private static double LogGaussian(double x, double mean, double var)
    {
        var d = x - mean;
        return -0.5 * Math.Log(2.0 * Math.PI * var) - d * d / (2.0 * var);
    }

    private static (double Mean, double Var) MeanVar(double[] x)
    {
        double sum = 0; foreach (var v in x) sum += v;
        var mean = sum / x.Length;
        double sq = 0; foreach (var v in x) { var d = v - mean; sq += d * d; }
        return (mean, x.Length > 1 ? sq / x.Length : 1e-12);
    }

    private static double[][] NewMatrix(int rows, int cols)
    {
        var m = new double[rows][];
        for (var i = 0; i < rows; i++) m[i] = new double[cols];
        return m;
    }

    private static double[][] NewSquare(int n) => NewMatrix(n, n);
}
