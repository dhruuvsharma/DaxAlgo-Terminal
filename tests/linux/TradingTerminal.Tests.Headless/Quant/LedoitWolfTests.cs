using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class LedoitWolfTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>Generates n samples from a k-variate normal with the given (lower-triangular) Cholesky factor.</summary>
    private static double[,] SampleNormal(int n, double[,] cholesky, Random rng)
    {
        var k = cholesky.GetLength(0);
        var data = new double[n, k];
        for (var i = 0; i < n; i++)
        {
            var z = new double[k];
            for (var j = 0; j < k; j++) z[j] = rng.NextGaussian();
            for (var a = 0; a < k; a++)
            {
                double v = 0;
                for (var b = 0; b <= a; b++) v += cholesky[a, b] * z[b];
                data[i, a] = v;
            }
        }
        return data;
    }

    /// <summary>True if the symmetric matrix is PSD (Cholesky succeeds with a tiny tolerance).</summary>
    private static bool IsPsd(double[,] m, double tol = 1e-9)
    {
        var k = m.GetLength(0);
        var l = new double[k, k];
        for (var i = 0; i < k; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                double sum = m[i, j];
                for (var p = 0; p < j; p++) sum -= l[i, p] * l[j, p];
                if (i == j)
                {
                    if (sum < -tol) return false;
                    l[i, j] = Math.Sqrt(Math.Max(0, sum));
                }
                else
                {
                    l[i, j] = l[j, j] > tol ? sum / l[j, j] : 0;
                }
            }
        }
        return true;
    }

    private static bool IsSymmetric(double[,] m, double tol = 1e-12)
    {
        var k = m.GetLength(0);
        for (var a = 0; a < k; a++)
            for (var b = a + 1; b < k; b++)
                if (Math.Abs(m[a, b] - m[b, a]) > tol) return false;
        return true;
    }

    // ── tests ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Output_IsSymmetricAndPsd_WithIntensityInUnitInterval()
    {
        var rng = new Random(11);
        var chol = new double[,] { { 1.0, 0, 0 }, { 0.5, 0.8, 0 }, { 0.2, 0.1, 0.9 } };
        var data = SampleNormal(40, chol, rng);

        var res = LedoitWolf.Estimate(data);

        res.Dimension.Should().Be(3);
        IsSymmetric(res.Covariance).Should().BeTrue();
        IsPsd(res.Covariance).Should().BeTrue();
        res.ShrinkageIntensity.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void LargeSample_FromKnownCovariance_RecoversTruth()
    {
        var rng = new Random(2718);
        // True Σ = L Lᵀ.
        var chol = new double[,] { { 2.0, 0 }, { 0.6, 1.5 } };
        var trueCov = new double[2, 2];
        for (var a = 0; a < 2; a++)
            for (var b = 0; b < 2; b++)
            {
                double v = 0;
                for (var p = 0; p < 2; p++) v += chol[a, p] * chol[b, p];
                trueCov[a, b] = v;
            }

        var data = SampleNormal(20000, chol, rng); // n ≫ p
        var res = LedoitWolf.Estimate(data);

        // With n ≫ p, shrinkage should be tiny and the estimate close to truth.
        res.ShrinkageIntensity.Should().BeLessThan(0.05);
        res.Covariance[0, 0].Should().BeApproximately(trueCov[0, 0], 0.3);
        res.Covariance[1, 1].Should().BeApproximately(trueCov[1, 1], 0.3);
        res.Covariance[0, 1].Should().BeApproximately(trueCov[0, 1], 0.3);
    }

    [Fact]
    public void HighDimensionRelativeToSampleSize_ShrinksMore()
    {
        var rng = new Random(909);
        var k = 10;
        // Correlated truth (banded Cholesky) so the sample covariance carries real off-diagonal
        // structure; with ample data shrinkage toward the scaled identity is small, while with few
        // samples the estimator must shrink hard.
        var chol = new double[k, k];
        for (var i = 0; i < k; i++)
        {
            chol[i, i] = 1.0;
            if (i > 0) chol[i, i - 1] = 0.6;
        }

        var ample = LedoitWolf.Estimate(SampleNormal(2000, chol, rng)); // n ≫ p
        var tight = LedoitWolf.Estimate(SampleNormal(12, chol, rng));   // p close to n

        tight.ShrinkageIntensity.Should().BeGreaterThan(ample.ShrinkageIntensity);
        ample.ShrinkageIntensity.Should().BeLessThan(0.5);
    }

    [Fact]
    public void IdentityInput_ProducesIdentityIshOutput()
    {
        var rng = new Random(31);
        var k = 4;
        var chol = new double[k, k];
        for (var i = 0; i < k; i++) chol[i, i] = 1.0;
        var data = SampleNormal(2000, chol, rng);

        var res = LedoitWolf.Estimate(data);
        var corr = LedoitWolf.ToCorrelation(res.Covariance);

        for (var i = 0; i < k; i++)
        {
            corr[i, i].Should().BeApproximately(1.0, 1e-9);
            res.Covariance[i, i].Should().BeApproximately(1.0, 0.25);
            for (var j = 0; j < k; j++)
                if (i != j) Math.Abs(corr[i, j]).Should().BeLessThan(0.25);
        }
    }

    [Fact]
    public void SafeInverse_OfSingularMatrix_SucceedsViaRidge()
    {
        // Rank-deficient: two identical columns ⇒ singular.
        var singular = new double[,]
        {
            { 2.0, 2.0, 0.0 },
            { 2.0, 2.0, 0.0 },
            { 0.0, 0.0, 3.0 },
        };

        var inv = LedoitWolf.SafeInverse(singular);

        // The well-conditioned block (index 2) must invert cleanly: row·col ≈ 1 on the diagonal.
        // singular[2,2]=3 ⇒ inverse[2,2] ≈ 1/3 (the tiny ridge barely perturbs it).
        inv[2, 2].Should().BeApproximately(1.0 / 3.0, 1e-3);
        inv.Should().NotBeNull();
    }

    [Fact]
    public void SafeInverse_OfWellConditioned_TimesMatrix_IsIdentity()
    {
        var m = new double[,]
        {
            { 4.0, 1.0, 0.5 },
            { 1.0, 3.0, 0.2 },
            { 0.5, 0.2, 2.0 },
        };

        var inv = LedoitWolf.SafeInverse(m);

        var k = 3;
        for (var a = 0; a < k; a++)
            for (var b = 0; b < k; b++)
            {
                double prod = 0;
                for (var p = 0; p < k; p++) prod += inv[a, p] * m[p, b];
                prod.Should().BeApproximately(a == b ? 1.0 : 0.0, 1e-9);
            }
    }

    [Fact]
    public void SafeInverse_NonSquare_Throws()
    {
        var rect = new double[2, 3];
        var act = () => LedoitWolf.SafeInverse(rect);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Estimate_TooFewSamples_Throws()
    {
        var act = () => LedoitWolf.Estimate(new double[1, 3]);
        act.Should().Throw<ArgumentException>();
    }
}
