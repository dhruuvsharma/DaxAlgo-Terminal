using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Regime.Markov;
using Xunit;

namespace TradingTerminal.Tests.Regime;

/// <summary>
/// Validates the Gaussian-HMM regime detector against synthetic data with known regimes: a
/// bull / bear / chop sequence built from distinct mean returns. The detector should recover the
/// state separation, label them by mean, decode the path with high accuracy, and learn sticky
/// (diagonal-heavy) transitions.
/// </summary>
public sealed class MarkovRegimeDetectorTests
{
    private const double BullMean = 0.0020;
    private const double BearMean = -0.0020;
    private const double ChopMean = 0.0000;
    private const double Sigma = 0.0010;

    [Fact]
    public void Recovers_three_regimes_from_synthetic_series()
    {
        var (bars, trueLabels) = SyntheticSeries();

        var result = MarkovRegimeDetector.Detect(bars, states: 3, seed: 7);

        // Exactly one bearish + one bullish state; the rest neutral.
        result.States.Count(s => s.Label == RegimeLabel.Bearish).Should().Be(1);
        result.States.Count(s => s.Label == RegimeLabel.Bullish).Should().Be(1);

        // Mean returns are ordered and separated by label.
        var bull = result.States.Single(s => s.Label == RegimeLabel.Bullish);
        var bear = result.States.Single(s => s.Label == RegimeLabel.Bearish);
        bull.MeanLogReturn.Should().BeGreaterThan(bear.MeanLogReturn);
        bull.MeanLogReturn.Should().BeGreaterThan(0.0008);
        bear.MeanLogReturn.Should().BeLessThan(-0.0008);

        // Sticky regimes: every self-transition dominates.
        for (var s = 0; s < result.StateCount; s++)
            result.TransitionMatrix[s][s].Should().BeGreaterThan(0.8);

        // Decoded path matches the known regimes for the large majority of bars.
        var correct = 0;
        for (var i = 0; i < result.Series.Count; i++)
            if (result.Series[i].Label == trueLabels[i]) correct++;
        ((double)correct / result.Series.Count).Should().BeGreaterThan(0.85);

        // Posteriors are valid probability rows.
        foreach (var p in result.Series)
            p.Posterior.Sum().Should().BeApproximately(1.0, 1e-9);

        // Expected durations are positive and finite for sticky states.
        result.States.Should().OnlyContain(s => s.ExpectedDurationBars > 1.0);
    }

    [Fact]
    public void Is_deterministic_for_a_fixed_seed()
    {
        var (bars, _) = SyntheticSeries();
        var a = MarkovRegimeDetector.Detect(bars, states: 3, seed: 42);
        var b = MarkovRegimeDetector.Detect(bars, states: 3, seed: 42);

        a.LogLikelihood.Should().Be(b.LogLikelihood);
        a.States.Select(s => s.MeanLogReturn).Should().Equal(b.States.Select(s => s.MeanLogReturn));
    }

    [Fact]
    public void Rejects_too_few_bars()
    {
        var bars = Enumerable.Range(0, 5)
            .Select(i => new Bar(DateTime.UnixEpoch.AddMinutes(i), 100, 100, 100, 100, 1))
            .ToList();
        var act = () => MarkovRegimeDetector.Detect(bars, states: 3);
        act.Should().Throw<ArgumentException>().WithMessage("*at least*");
    }

    /// <summary>Builds a bull→bear→chop→bull close series from regime-specific mean returns plus
    /// Gaussian noise, and returns the per-observation true labels (aligned to bars[1..]).</summary>
    private static (List<Bar> Bars, List<RegimeLabel> TrueLabels) SyntheticSeries()
    {
        var rng = new Random(2024);
        var blocks = new (double Mean, int Len, RegimeLabel Label)[]
        {
            (BullMean, 160, RegimeLabel.Bullish),
            (BearMean, 160, RegimeLabel.Bearish),
            (ChopMean, 160, RegimeLabel.Neutral),
            (BullMean, 160, RegimeLabel.Bullish),
        };

        var returns = new List<double>();
        var labels = new List<RegimeLabel>();
        foreach (var (mean, len, label) in blocks)
            for (var i = 0; i < len; i++)
            {
                returns.Add(mean + Sigma * NextGaussian(rng));
                labels.Add(label);
            }

        // Closes: one more than returns. bars[i+1] carries returns[i].
        var bars = new List<Bar> { new(DateTime.UnixEpoch, 100, 100, 100, 100, 1) };
        var close = 100.0;
        for (var i = 0; i < returns.Count; i++)
        {
            close *= Math.Exp(returns[i]);
            bars.Add(new Bar(DateTime.UnixEpoch.AddMinutes(i + 1), close, close, close, close, 1));
        }
        return (bars, labels);
    }

    private static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
