using System.Reflection;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// The drawing routines a visualizer composes from. They are pure functions over
/// <see cref="IRenderSurface"/>, so they are tested against a recording surface with no window
/// involved — which is the point of putting the library on the surface rather than on WPF.
/// </summary>
public sealed class DrawingRoutineTests
{
    // ── Ranges ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARangeIgnoresNonFiniteValues_RatherThanBeingPoisonedByOne()
    {
        // One NaN in a price series would otherwise make the whole axis unusable.
        var range = PlotRange.Empty
            .Include(10d)
            .Include(double.NaN)
            .Include(20d)
            .Include(double.PositiveInfinity);

        Assert.True(range.IsValid);
        Assert.Equal(10d, range.Minimum);
        Assert.Equal(20d, range.Maximum);
    }

    [Fact]
    public void AFlatRangeIsGivenWidth_SoIdenticalPricesStillPlot()
    {
        // A quiet instrument prints the same price for a whole window. A zero-height range would
        // divide by zero in every transform that used it.
        var padded = PlotRange.Empty.Include(100d).Include(100d).Padded();

        Assert.True(padded.IsValid);
        Assert.True(padded.Span > 0d);
        Assert.InRange(100d, padded.Minimum, padded.Maximum);
    }

    [Fact]
    public void AnEmptyRangeStaysInvalid_RatherThanPretendingToBeZeroToZero()
    {
        var range = PlotRange.Empty;

        Assert.False(range.IsValid);
        Assert.False(range.Padded().IsValid);
    }

    [Theory]
    // The 1/2/5 progression: labels land where a person would have put them.
    [InlineData(0.9d, 1d)]
    [InlineData(1.1d, 2d)]
    [InlineData(3d, 5d)]
    [InlineData(7d, 10d)]
    [InlineData(23d, 50d)]
    [InlineData(0.011d, 0.02d)]
    public void StepsAreRoundedToNumbersAHumanWouldPick(double raw, double expected)
    {
        Assert.Equal(expected, Plot.NiceStep(raw), 10);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void ADegenerateStepIsRefused(double raw)
    {
        Assert.Equal(0d, Plot.NiceStep(raw));
    }

    [Fact]
    public void ValueToPixelAndBackAgree()
    {
        var range = new PlotRange(100d, 200d);

        var y = Plot.ToY(150d, range, 400d);

        Assert.Equal(200d, y);
        Assert.Equal(150d, Plot.FromY(y, range, 400d), 10);
    }

    // ── Ladder ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLadderScalesToTheLargestSizeInView_NotTheWholeBook()
    {
        // A far-touch iceberg must not flatten the bars at the touch, which is where attention is.
        var surface = Surface(200d, 200d);
        var depth = Depth(
            bids: [(99d, 100L), (98d, 50L)],
            asks: [(100d, 25L), (101d, 10L)]);

        Ladder.Draw(surface, depth, new LadderOptions(Levels: 2, RowHeight: 20d, PriceWidth: 60d));

        // Widest bar belongs to the 100-lot bid and spans the full bar area.
        var widest = surface.Rectangles.Max(rect => rect.Width);
        Assert.Equal(140d, widest, 6);
    }

    [Fact]
    public void TheLadderPutsAsksAboveBids()
    {
        var surface = Surface(200d, 200d);
        var depth = Depth(bids: [(99d, 10L)], asks: [(100d, 10L)]);

        Ladder.Draw(surface, depth, new LadderOptions(Levels: 1, RowHeight: 20d));

        var askRow = surface.Texts.Single(item => item.Text == "100");
        var bidRow = surface.Texts.Single(item => item.Text == "99");
        Assert.True(askRow.Y < bidRow.Y, "the sell side belongs above the buy side");
    }

    [Fact]
    public void TheLadderCanBeScrolledThroughADeepBook()
    {
        // Scrolling a book used to mean handing this routine a SLICED DepthSnapshot, which is two new
        // lists built on the render thread every frame — the one thing the drawing rules tell an author
        // never to do. An index says the same and costs nothing.
        var depth = Depth(
            bids: [(99d, 10L), (98d, 10L), (97d, 10L)],
            asks: [(100d, 10L), (101d, 10L), (102d, 10L)]);

        var top = Surface(200d, 200d);
        Ladder.Draw(top, depth, new LadderOptions(Levels: 1, RowHeight: 20d));
        Assert.Contains(top.Texts, t => t.Text == "100");
        Assert.DoesNotContain(top.Texts, t => t.Text == "101");

        var scrolled = Surface(200d, 200d);
        Ladder.Draw(scrolled, depth, new LadderOptions(Levels: 1, RowHeight: 20d, FirstLevel: 1));
        Assert.Contains(scrolled.Texts, t => t.Text == "101");
        Assert.DoesNotContain(scrolled.Texts, t => t.Text == "100");

        // And the bid side scrolls with it, so the two halves stay the same distance from the touch.
        Assert.Contains(scrolled.Texts, t => t.Text == "98");
    }

    [Fact]
    public void ScrollingPastTheEndOfTheBookRunsOutOfRowsRatherThanThrowing()
    {
        // A drag has no idea how deep the book is, so a value nobody can reach has to be harmless.
        var surface = Surface(200d, 200d);
        var depth = Depth(bids: [(99d, 10L)], asks: [(100d, 10L)]);

        Ladder.Draw(surface, depth, new LadderOptions(Levels: 4, RowHeight: 20d, FirstLevel: 50));

        Assert.DoesNotContain(surface.Texts, t => t.Text is "99" or "100");
    }

    [Fact]
    public void ALadderWithNoDepthDrawsNothing()
    {
        var surface = Surface(200d, 200d);

        Ladder.Draw(surface, depth: null);
        Ladder.Draw(surface, Depth([], []));

        Assert.Empty(surface.Rectangles);
    }

    [Fact]
    public void ZeroSizedLevelsDoNotDrawABar()
    {
        // An exhausted level still has a price row, but a zero-length bar is noise.
        var surface = Surface(200d, 200d);
        var depth = Depth(bids: [(99d, 0L)], asks: [(100d, 10L)]);

        Ladder.Draw(surface, depth, new LadderOptions(Levels: 1, RowHeight: 20d));

        Assert.DoesNotContain(surface.Rectangles, rect => rect.Width == 0d);
    }

    // ── Candles ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CandlesAutoScaleToTheirOwnHighsAndLows()
    {
        var surface = Surface(300d, 200d);

        var range = Candles.Draw(surface, [Bar(10d, 12d, 9d, 11d), Bar(11d, 15d, 10d, 14d)]);

        Assert.True(range.IsValid);
        // Padded, so the extremes are inside rather than flush against the edge.
        Assert.True(range.Minimum < 9d);
        Assert.True(range.Maximum > 15d);
    }

    [Fact]
    public void ADojiStillGetsAVisibleBody()
    {
        // Open == close is a zero-height rectangle, which would simply disappear.
        var surface = Surface(300d, 200d);

        Candles.Draw(surface, [Bar(10d, 11d, 9d, 10d)]);

        Assert.All(surface.Rectangles, rect => Assert.True(rect.Height >= 1d));
    }

    [Fact]
    public void NoBarsDrawsNothingAndReportsAnInvalidRange()
    {
        var surface = Surface(300d, 200d);

        var range = Candles.Draw(surface, []);

        Assert.False(range.IsValid);
        Assert.Empty(surface.Rectangles);
    }

    // ── Crosshair ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCrosshairDrawsNothingWhenThePointerIsElsewhere()
    {
        // Callable unconditionally: a visualizer should not have to test the cursor itself.
        var surface = Surface(200d, 200d);

        Plot.Crosshair(surface, new PlotRange(0d, 100d));

        Assert.Empty(surface.Lines);
    }

    [Fact]
    public void TheCrosshairReadsOutTheValueUnderThePointer()
    {
        var surface = Surface(200d, 200d, new RenderCursor(50d, 100d, true, false));

        Plot.Crosshair(surface, new PlotRange(0d, 200d));

        // Half way down a 0..200 range over 200px is 100.
        Assert.Contains(surface.Texts, item => item.Text == "100");
        Assert.Equal(2, surface.Lines.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static DepthSnapshot Depth(
        IReadOnlyList<(double Price, long Size)> bids,
        IReadOnlyList<(double Price, long Size)> asks) =>
        new(
            DateTime.UnixEpoch,
            bids.Select(item => new DepthLevel(item.Price, item.Size)).ToArray(),
            asks.Select(item => new DepthLevel(item.Price, item.Size)).ToArray());

    private static OhlcvBar Bar(double open, double high, double low, double close) =>
        new(new InstrumentId(1), BarSize.OneMinute, DateTime.UnixEpoch, open, high, low, close, 1L, BrokerKind.Binance, true);

    /// <summary>The shared recorder from the SDK, sized for these tests. It is the same class the draw
    /// probe (#46) verifies authored units with, and the same one an author tests their own Draw with —
    /// there were three private copies of this idea before it moved into the SDK.</summary>
    private static RecordingRenderSurface Surface(double width, double height, RenderCursor? cursor = null) =>
        new(new RenderViewport(width, height, 1d), cursor);

    [Fact]
    public void EveryWidgetCanBePlaced()
    {
        // The library's whole claim for composition is that a widget can be PUT somewhere. Three could
        // not: `Ladder`, `Candles` and `Footprint` read `surface.Viewport` directly, so a book beside a
        // chart, or a footprint above a delta strip, drew across everything else in the panel. Each was
        // found by composing a real picture, never by reading a signature — which is exactly why this is
        // reflected rather than remembered.
        var missing = typeof(Plot).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "DaxAlgo.Sdk.Drawing" && t.IsAbstract && t.IsSealed)
            .Select(t => new
            {
                t.Name,
                Draws = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Draw")
                    .ToArray(),
            })
            .Where(x => x.Draws.Length > 0)
            .Where(x => x.Draws.All(m => m.GetParameters().All(p => p.ParameterType != typeof(PlotArea))))
            .Select(x => x.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"these widgets cannot be placed in a composed window: {string.Join(", ", missing)}");
    }

    [Fact]
    public void APlacedWidgetStaysInsideItsArea()
    {
        // The reflected guard above proves the parameter exists; this proves it is honoured. A widget
        // that accepts an area and ignores it is the same bug wearing a signature that says otherwise.
        var surface = new RecordingRenderSurface(new RenderViewport(400d, 400d, 1d));
        var area = new PlotArea(200d, 200d, 200d, 200d);

        Candles.Draw(surface, [Bar(10d, 12d, 9d, 11d), Bar(11d, 13d, 10d, 12d)], area: area);

        Assert.NotEmpty(surface.Lines);
        Assert.All(surface.Lines, line =>
        {
            Assert.InRange(line.X1, area.X - 1d, area.Right + 1d);
            Assert.InRange(line.Y1, area.Y - 1d, area.Bottom + 1d);
        });
    }

    [Fact]
    public void APlacedLadderStaysInsideItsArea()
    {
        // The book is the widget an order-book brief needs most, and the one whose absent area was
        // found by an exemplar that could not place it.
        var surface = new RecordingRenderSurface(new RenderViewport(400d, 400d, 1d));
        var area = new PlotArea(150d, 100d, 250d, 300d);
        var depth = new DepthSnapshot(
            DateTime.UnixEpoch,
            [new DepthLevel(99d, 10L), new DepthLevel(98d, 20L)],
            [new DepthLevel(101d, 15L), new DepthLevel(102d, 5L)]);

        Ladder.Draw(surface, depth, LadderOptions.Default, area);

        Assert.NotEmpty(surface.Rectangles);
        Assert.All(surface.Rectangles, rect =>
        {
            Assert.InRange(rect.X, area.X - 1d, area.Right + 1d);
            Assert.InRange(rect.Y, area.Y - 1d, area.Bottom + 1d);
        });
    }
}
