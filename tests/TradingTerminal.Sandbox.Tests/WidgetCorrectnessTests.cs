using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// What the widgets actually compute, as opposed to whether they emit pixels.
///
/// <para>The contract suite next door proves every widget draws something and survives bad input. It
/// cannot tell a correct value area from a plausible one — and a widget that draws a confident, wrong
/// picture is worse than one that draws nothing, because nothing is obviously broken and wrong is not.
/// These are the numbers a strategy would trade on.</para>
/// </summary>
public sealed class WidgetCorrectnessTests
{
    private static RecordingRenderSurface Surface(double width = 400d, double height = 240d) =>
        new(new RenderViewport(width, height, 1d));

    // ── volume profile ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ProfileRow> Profile() =>
    [
        ProfileRow.At(10d, 5d),
        ProfileRow.At(11d, 20d),
        ProfileRow.At(12d, 100d),   // the point of control
        ProfileRow.At(13d, 60d),
        ProfileRow.At(14d, 10d),
        ProfileRow.At(15d, 5d),
    ];

    [Fact]
    public void ThePointOfControlIsTheBusiestPrice()
    {
        var (_, _, poc) = VolumeProfile.ValueArea(Profile());

        Assert.Equal(12d, poc);
    }

    [Fact]
    public void TheValueAreaGrowsTowardsTheBusierNeighbour()
    {
        // 200 total, so 70% is 140. From the POC's 100, the richer side is 13 (60) rather than 11 (20),
        // and taking it alone reaches 160 — so the area is 12…13 and is NOT centred on the POC. A
        // routine that grew symmetrically would return 11…13 and quietly widen every level a strategy
        // derived from it.
        var (low, high, poc) = VolumeProfile.ValueArea(Profile());

        Assert.Equal(12d, low);
        Assert.Equal(13d, high);
        Assert.Equal(12d, poc);
    }

    [Fact]
    public void AWiderShareTakesInMorePrices()
    {
        var (narrow, narrowHigh, _) = VolumeProfile.ValueArea(Profile(), share: 0.5d);
        var (wide, wideHigh, _) = VolumeProfile.ValueArea(Profile(), share: 0.95d);

        Assert.True(wideHigh - wide >= narrowHigh - narrow);
        Assert.True(wide <= narrow);
    }

    [Fact]
    public void AnEmptyProfileReportsNothingRatherThanZero()
    {
        // Zero is a price. Reporting it as the point of control would put a level on the chart at zero
        // and let a strategy reference it.
        var (low, high, poc) = VolumeProfile.ValueArea([]);

        Assert.True(double.IsNaN(low));
        Assert.True(double.IsNaN(high));
        Assert.True(double.IsNaN(poc));
    }

    [Fact]
    public void TheDrawnProfileReportsTheSameLevelsAsTheCalculation()
    {
        // The picture and the numbers must come from one source, or a strategy trades a level its own
        // chart does not show.
        var expected = VolumeProfile.ValueArea(Profile());
        var drawn = VolumeProfile.Draw(Surface(), Profile());

        Assert.Equal(expected, drawn);
    }

    // ── equity ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWorstDrawdownIsPeakToTroughNotStartToEnd()
    {
        // Up to 150, down to 90, back to 120. The fall that matters is 60 from the peak — not the 30 the
        // start-to-end difference would report, and not the 60 measured from the wrong peak.
        var summary = Equity.Draw(Surface(), [100d, 150d, 120d, 90d, 120d]);

        Assert.Equal(150d, summary.Peak);
        Assert.Equal(60d, summary.MaxDrawdown, 6);
        Assert.Equal(0.4d, summary.MaxDrawdownShare, 6);
    }

    [Fact]
    public void ACurveThatOnlyRisesHasNoDrawdown()
    {
        var summary = Equity.Draw(Surface(), [100d, 110d, 130d, 180d]);

        Assert.Equal(0d, summary.MaxDrawdown);
    }

    [Fact]
    public void TheBaselineIsInsideTheRangeSoALosingCurveShowsItsLoss()
    {
        // A curve scaled to its own values can exclude where it started, and then a strategy that is down
        // 20% draws as a line filling the panel with no reference to say so.
        var summary = Equity.Draw(Surface(), [90d, 85d, 80d], new EquityOptions { Baseline = 100d });

        Assert.True(summary.Range.Minimum <= 100d && summary.Range.Maximum >= 100d);
    }

    // ── signals ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EverySignalKindHasItsOwnSilhouette()
    {
        // Colour alone is not enough: roughly one man in twelve cannot separate the bullish and bearish
        // roles reliably, and signals are the one thing on a strategy chart that must read at a glance.
        var shapes = Enum.GetValues<SignalKind>().Select(Signals.ShapeOf).ToArray();

        Assert.Equal(shapes.Length, shapes.Distinct().Count());
    }

    [Fact]
    public void BuysAndSellsAreDrawnWithDifferentShapes()
    {
        var surface = Surface();
        Signals.Draw(
            surface,
            [new Signal(0, 100d, SignalKind.Buy), new Signal(1, 101d, SignalKind.Sell)],
            4, new PlotRange(95d, 105d));

        Assert.Equal(2, surface.Markers.Count);
        Assert.NotEqual(surface.Markers[0].Shape, surface.Markers[1].Shape);
    }

    // ── levels ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALevelOutsideTheRangeIsSkippedRatherThanPinnedToTheEdge()
    {
        // A line clamped to the top of a panel reads as a real level at that price. Skipping it is the
        // honest answer to "your stop is off the chart".
        var surface = Surface();
        Levels.Draw(surface, [new Level(500d, "stop")], new PlotRange(95d, 105d));

        Assert.Empty(surface.Lines);
    }

    // ── histogram ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBaselineIsAlwaysInsideTheHistogramsRange()
    {
        // All-positive values scaled to themselves would put zero off the bottom of the panel, and then
        // every bar points the same way whatever its sign — which is the one thing a histogram is for.
        var range = Histogram.Draw(Surface(), [5d, 7d, 9d]);

        Assert.True(range.Minimum <= 0d);
    }

    [Fact]
    public void BarsAboveAndBelowTheBaselineAreDrawnInDifferentRoles()
    {
        var surface = Surface();
        Histogram.Draw(surface, [4d, -4d]);

        Assert.Contains(RenderThemeColor.Bullish, surface.ThemeTokens);
        Assert.Contains(RenderThemeColor.Bearish, surface.ThemeTokens);
    }

    // ── shared scales ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AChartScalesEverySeriesTogether()
    {
        // Two series auto-scaled separately look like they agree when they do not — the single most
        // misleading chart a generated visualizer produces, and it looks exactly like a correct one.
        var surface = Surface();
        var range = Series.Chart(surface, [
            SeriesData.Line("small", [1d, 2d, 3d]),
            SeriesData.Line("large", [100d, 200d, 300d]),
        ]);

        Assert.True(range.Minimum <= 1d);
        Assert.True(range.Maximum >= 300d);
    }

    [Fact]
    public void BandsAndPriceCanShareOneScale()
    {
        var surface = Surface();
        var band = Bands.Draw(surface, [12d, 13d, 14d], [8d, 7d, 6d]);
        var series = Series.Draw(surface, "price", [10d, 10d, 10d], SeriesOptions.Default, band);

        Assert.Equal(band, series);
    }

    // ── colour scale ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADivergingScaleSeparatesTheSigns()
    {
        var surface = Surface();

        Assert.NotEqual(
            ColorScale.Diverging(surface, 0.8d, 1d),
            ColorScale.Diverging(surface, -0.8d, 1d));
    }

    [Fact]
    public void ADivergingScaleIsNeutralAtZero()
    {
        var surface = Surface();

        Assert.Equal(surface.Theme(RenderThemeColor.Surface), ColorScale.Diverging(surface, 0d, 1d));
    }

    [Fact]
    public void AZeroExtentIsNeutralRatherThanADivideByZero()
    {
        var surface = Surface();

        Assert.Equal(surface.Theme(RenderThemeColor.Surface), ColorScale.Diverging(surface, 5d, 0d));
    }

    [Fact]
    public void FlowStrengthComesFromTheTotalNotTheImbalance()
    {
        // A big balanced cell and an empty one have the same imbalance. Shading on imbalance alone would
        // draw them identically, and hiding where the volume was is the opposite of a footprint's job.
        var surface = Surface();

        Assert.NotEqual(
            ColorScale.Flow(surface, 500d, 500d, 1000d),
            ColorScale.Flow(surface, 5d, 5d, 1000d));
    }

    // ── depth ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothSidesOfTheBookShareOneSizeScale()
    {
        // Scaling each side to its own peak makes a lopsided book look balanced, which is the single
        // thing a depth chart exists to reveal.
        var surface = Surface();

        // Decorations off: the spread shading spans from the bid to the ask, so it lands in both halves
        // and would be counted as whichever side the partition looked at first.
        DepthCurve.Draw(
            surface,
            new TradingTerminal.Core.Domain.DepthSnapshot(
                DateTime.UnixEpoch,
                [new TradingTerminal.Core.Domain.DepthLevel(99d, 10)],
                [new TradingTerminal.Core.Domain.DepthLevel(101d, 1000)]),
            DepthCurveOptions.Default with { ShowSpread = false, ShowMid = false });

        // The heavy ask side reaches the top of the panel; the thin bid side, a hundredth of its size,
        // must not. Scaled per side they would be identical bars.
        var bid = surface.Rectangles.Where(r => r.X < 200d).Select(r => r.Height).DefaultIfEmpty(0d).Max();
        var ask = surface.Rectangles.Where(r => r.X >= 200d).Select(r => r.Height).DefaultIfEmpty(0d).Max();

        Assert.True(ask > bid * 5d, $"bid {bid:0.#} vs ask {ask:0.#} — the sides are not on one scale");
    }
}
