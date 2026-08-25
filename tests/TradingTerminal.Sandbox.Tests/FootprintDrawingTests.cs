using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// The volume footprint — the most demanding picture in the benchmark set, and therefore the real
/// test of whether the surface primitives are expressive enough.
/// </summary>
public sealed class FootprintDrawingTests
{
    [Fact]
    public void CellsAreShadedPerBar_SoOneHeavyBarDoesNotWashOutTheRest()
    {
        // The reason to look at a footprint is the distribution WITHIN each bar. Scaling shading
        // across the whole window would flatten every column next to a single high-volume one.
        var surface = Surface(400d, 200d);
        var quiet = Bar(100d, [(100d, 5L, 5L, false), (101d, 4L, 4L, false)]);
        var heavy = Bar(100d, [(100d, 5_000L, 5_000L, false), (101d, 10L, 10L, false)]);

        Footprint.Draw(surface, [quiet, heavy], new FootprintOptions(ColumnWidth: 80d, RowHeight: 20d));

        // Assert on the QUIET bar specifically. Counting saturated cells across both bars passes
        // even with global scaling, because the heavy bar saturates either way — a vacuous test that
        // I confirmed passes against the broken implementation.
        //
        // Quiet bar is column 0, so its cells sit left of the second column at x = 60 + 80.
        var quietCells = surface.Rectangles.Where(rect => rect.X < 140d).ToArray();
        Assert.NotEmpty(quietCells);
        Assert.Contains(quietCells, rect => rect.Style.Alpha >= 1d);
    }

    [Fact]
    public void SellIsDrawnLeftAndBuyRight()
    {
        var surface = Surface(400d, 200d);

        Footprint.Draw(
            surface,
            [Bar(100d, [(100d, 90L, 10L, false)])],
            new FootprintOptions(ColumnWidth: 80d, RowHeight: 20d, PriceWidth: 60d, ShowValueArea: false));

        // Sell (10) on the left half, buy (90) on the right — so the darker cell is the right one.
        var left = surface.Rectangles.Where(rect => rect.X == 60d).ToArray();
        var right = surface.Rectangles.Where(rect => rect.X == 100d).ToArray();
        Assert.NotEmpty(left);
        Assert.NotEmpty(right);
        Assert.True(right.Max(rect => rect.Style.Alpha) > left.Max(rect => rect.Style.Alpha));
    }

    [Fact]
    public void ATradedButTinyCellStaysVisibleAgainstAnEmptyOne()
    {
        // A cell that traded once must not be indistinguishable from one that never traded, or the
        // footprint stops answering the question it exists for.
        var surface = Surface(400d, 200d);

        Footprint.Draw(
            surface,
            [Bar(100d, [(100d, 1L, 0L, false), (101d, 10_000L, 10_000L, false)])],
            new FootprintOptions(ColumnWidth: 80d, RowHeight: 20d, ShowValueArea: false));

        var alphas = surface.Rectangles.Select(rect => rect.Style.Alpha).Distinct().ToArray();
        // The single-lot cell floors at 0.08; the untraded side sits below it at 0.04.
        Assert.Contains(alphas, alpha => Math.Abs(alpha - 0.08d) < 1e-9);
        Assert.Contains(alphas, alpha => Math.Abs(alpha - 0.04d) < 1e-9);
    }

    [Fact]
    public void TheValueAreaCoversSeventyPercentOfVolume()
    {
        // Two heavy rows out of four carry 90% of the volume, so the value area is those two and
        // must not stretch to the thin extremes.
        var bar = Bar(100d, [(100d, 5L, 5L, false), (101d, 400L, 400L, false), (102d, 450L, 450L, false), (103d, 5L, 5L, false)]);

        var (low, high) = Footprint.ValueArea(bar);

        Assert.Equal(101d, low);
        Assert.Equal(102d, high);
    }

    [Fact]
    public void AnEmptyBarHasNoValueArea()
    {
        var (low, high) = Footprint.ValueArea(Bar(100d, []));

        Assert.True(double.IsNaN(low));
        Assert.True(double.IsNaN(high));
    }

    [Fact]
    public void ThePointOfControlIsMarked()
    {
        var surface = Surface(400d, 200d);

        Footprint.Draw(
            surface,
            [Bar(poc: 101d, [(100d, 10L, 10L, false), (101d, 900L, 900L, false)])],
            new FootprintOptions(ColumnWidth: 80d, RowHeight: 20d, ShowValueArea: false));

        // A horizontal rule spanning exactly one column width.
        Assert.Contains(surface.Lines, line =>
            Math.Abs(line.Y1 - line.Y2) < 1e-9 && Math.Abs(line.X2 - line.X1 - 80d) < 1e-9);
    }

    [Fact]
    public void ImbalancedRowsAreOutlinedRatherThanFilled()
    {
        // An imbalance is a property OF a cell, so it must not compete with the volume shading the
        // cell already encodes.
        var surface = Surface(400d, 200d);

        Footprint.Draw(
            surface,
            [Bar(100d, [(100d, 50L, 50L, BidImbalance: true)])],
            new FootprintOptions(ColumnWidth: 80d, RowHeight: 20d, ShowValueArea: false));

        Assert.Contains(surface.Rectangles, rect => !rect.Filled);
    }

    [Fact]
    public void NoBarsDrawNothing()
    {
        var surface = Surface(400d, 200d);

        Footprint.Draw(surface, null);
        Footprint.Draw(surface, []);

        Assert.Empty(surface.Rectangles);
    }

    [Fact]
    public void ABarWithNoVolumeAtAllIsSkippedRatherThanDividedBy()
    {
        // Every cell zero means the per-bar peak is zero; shading would divide by it.
        var surface = Surface(400d, 200d);

        var fault = Record.Exception(() => Footprint.Draw(
            surface,
            [Bar(100d, [(100d, 0L, 0L, false), (101d, 0L, 0L, false)])],
            new FootprintOptions(ColumnWidth: 80d, RowHeight: 20d)));

        Assert.Null(fault);
        Assert.DoesNotContain(surface.Rectangles, rect => !double.IsFinite(rect.Style.Alpha));
    }

    [Fact]
    public void CellVolumesAreDroppedWhenRowsAreTooShortToReadThem()
    {
        // Printing 8pt text into a 6px row produces a smear, not information.
        var surface = Surface(400d, 200d);

        Footprint.Draw(
            surface,
            [Bar(100d, [(100d, 50L, 50L, false)])],
            new FootprintOptions(ColumnWidth: 80d, RowHeight: 6d, ShowValueArea: false));

        Assert.DoesNotContain(surface.Texts, item => item.Text == "50");
    }

    private static FootprintBar Bar(
        double poc,
        IReadOnlyList<(double Price, long Buy, long Sell, bool BidImbalance)> rows) =>
        new(
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddMinutes(1),
            rows.Select(r => new FootprintFeatureRow(
                r.Price, r.Buy, r.Sell, r.BidImbalance, false, false, false)).ToArray(),
            poc,
            poc, poc, poc,
            rows.Sum(r => r.Buy),
            rows.Sum(r => r.Sell),
            rows.Sum(r => r.Buy - r.Sell),
            0L,
            0,
            0,
            FeedQuality.RealTape);

    /// <summary>Records draw calls with the style in force, so shading decisions are assertable.</summary>

    /// <summary>The shared recorder from the SDK, sized for these tests. It is the same class the draw
    /// probe (#46) verifies authored units with, and the same one an author tests their own Draw with —
    /// there were three private copies of this idea before it moved into the SDK.</summary>
    private static RecordingRenderSurface Surface(double width, double height, RenderCursor? cursor = null) =>
        new(new RenderViewport(width, height, 1d), cursor);
}
