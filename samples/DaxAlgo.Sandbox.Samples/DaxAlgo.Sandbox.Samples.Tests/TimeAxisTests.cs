using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using Xunit;

namespace DaxAlgo.Sandbox.Samples.Tests;

/// <summary>
/// A series placed by CLOCK rather than by index — gap 5 in the authored-unit backlog.
///
/// <para>Every array widget spaced its points evenly whatever the timestamps said, so a unit that
/// wanted a real time axis had to hand-draw the whole panel. <c>PlotArea.ToY</c> had mapped a value
/// through a range all along; <c>ToX</c> only ever mapped an index, and that asymmetry was the gap.</para>
///
/// <para>Asserted on the PUSHED COORDINATES, because that is the claim. A test that only checked the
/// call did not throw would pass with the positions ignored, which is precisely the failure mode this
/// area keeps producing.</para>
/// </summary>
public sealed class TimeAxisTests
{
    private static RecordingRenderSurface Surface() => new(new RenderViewport(400d, 200d, 1d));

    [Fact]
    public void AGapInTheDataIsAGapInThePicture()
    {
        // Three samples where the third is far away in time. Index-spaced they are equidistant; placed
        // by clock the last one sits at the far edge and the first two bunch at the left.
        var surface = Surface();
        Series.Draw(surface, "v", [1d, 2d, 3d], at: [0d, 1d, 99d]);

        var xs = surface.Points.Select(p => p.X).ToArray();
        Assert.Equal(3, xs.Length);

        var early = xs[1] - xs[0];
        var late = xs[2] - xs[1];
        Assert.True(late > early * 20d,
            $"the long gap should dominate the panel, got {early:F1} then {late:F1}");
    }

    [Fact]
    public void WithoutPositionsNothingChanges()
    {
        // The default has to stay index-spaced: for a bar series each column IS an interval, and this
        // is the path every existing unit is on.
        var surface = Surface();
        Series.Draw(surface, "v", [1d, 2d, 3d]);

        var xs = surface.Points.Select(p => p.X).ToArray();
        Assert.Equal(xs[1] - xs[0], xs[2] - xs[1], 3);
    }

    [Fact]
    public void PositionsThatDoNotCoverTheSeriesAreIgnored()
    {
        // Half a series against the clock and half against nothing is worse than none of it, and it
        // hides a caller bug worth noticing.
        var surface = Surface();
        Series.Draw(surface, "v", [1d, 2d, 3d], at: [0d, 1d]);

        var xs = surface.Points.Select(p => p.X).ToArray();
        Assert.Equal(xs[1] - xs[0], xs[2] - xs[1], 3);
    }

    [Fact]
    public void AChartSharesOneAxisAcrossEverySeries()
    {
        // Two series over different spans must not each fill the panel: they would cross at a point
        // that means nothing. The later series starts where its own time says, not at the left edge.
        var surface = Surface();
        Series.Chart(surface,
        [
            SeriesData.Line("early", [1d, 2d], at: [0d, 10d]),
            SeriesData.Line("late", [3d, 4d], at: [90d, 100d]),
        ]);

        var xs = surface.Points.Select(p => p.X).ToArray();
        Assert.Equal(4, xs.Length);
        Assert.True(xs[2] > xs[1] + 100d, "the second series should start far to the right");
    }

    [Fact]
    public void TheProjectionOverloadPositionsToo()
    {
        // The overload a unit plotting from its own sample records uses -- which is most of them. A
        // capability on one of two paths is one half the callers cannot reach.
        var samples = new[] { (T: 0d, V: 1d), (T: 1d, V: 2d), (T: 99d, V: 3d) };

        var surface = Surface();
        Series.Draw(surface, "v", samples, static s => s.V, position: static s => s.T);

        var xs = surface.Points.Select(p => p.X).ToArray();
        Assert.True(xs[2] - xs[1] > (xs[1] - xs[0]) * 20d);
    }

    [Fact]
    public void ReadingThePositionOffTheItemCannotDriftFromTheValues()
    {
        // The reason `position` exists beside `at`: a parallel array can fall out of step with the
        // values it positions, and a selector cannot.
        var samples = new[] { (T: 0d, V: 1d), (T: 50d, V: 2d) };

        var byArray = Surface();
        Series.Draw(byArray, "v", samples, static s => s.V, at: [0d, 50d]);

        var bySelector = Surface();
        Series.Draw(bySelector, "v", samples, static s => s.V, position: static s => s.T);

        Assert.Equal(
            byArray.Points.Select(p => p.X).ToArray(),
            bySelector.Points.Select(p => p.X).ToArray());
    }

    [Fact]
    public void TheDeclaredAxisMatchesWhereThePointsWent()
    {
        // The host maps a pointer back through the DECLARED axis. Declaring an index range under
        // time-placed points is how a crosshair reads the wrong value.
        var surface = Surface();
        Series.Chart(surface, [SeriesData.Line("v", [1d, 2d, 3d], at: [10d, 20d, 30d])]);

        var axis = surface.Calls.Last(c => c.Kind == "AxisX");
        Assert.Equal(10d, axis.X);
        Assert.Equal(30d, axis.Y);
    }
}
