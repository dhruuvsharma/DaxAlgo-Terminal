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
        var surface = new RecordingSurface(200d, 200d);
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
        var surface = new RecordingSurface(200d, 200d);
        var depth = Depth(bids: [(99d, 10L)], asks: [(100d, 10L)]);

        Ladder.Draw(surface, depth, new LadderOptions(Levels: 1, RowHeight: 20d));

        var askRow = surface.Texts.Single(item => item.Text == "100");
        var bidRow = surface.Texts.Single(item => item.Text == "99");
        Assert.True(askRow.Y < bidRow.Y, "the sell side belongs above the buy side");
    }

    [Fact]
    public void ALadderWithNoDepthDrawsNothing()
    {
        var surface = new RecordingSurface(200d, 200d);

        Ladder.Draw(surface, depth: null);
        Ladder.Draw(surface, Depth([], []));

        Assert.Empty(surface.Rectangles);
    }

    [Fact]
    public void ZeroSizedLevelsDoNotDrawABar()
    {
        // An exhausted level still has a price row, but a zero-length bar is noise.
        var surface = new RecordingSurface(200d, 200d);
        var depth = Depth(bids: [(99d, 0L)], asks: [(100d, 10L)]);

        Ladder.Draw(surface, depth, new LadderOptions(Levels: 1, RowHeight: 20d));

        Assert.DoesNotContain(surface.Rectangles, rect => rect.Width == 0d);
    }

    // ── Candles ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CandlesAutoScaleToTheirOwnHighsAndLows()
    {
        var surface = new RecordingSurface(300d, 200d);

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
        var surface = new RecordingSurface(300d, 200d);

        Candles.Draw(surface, [Bar(10d, 11d, 9d, 10d)]);

        Assert.All(surface.Rectangles, rect => Assert.True(rect.Height >= 1d));
    }

    [Fact]
    public void NoBarsDrawsNothingAndReportsAnInvalidRange()
    {
        var surface = new RecordingSurface(300d, 200d);

        var range = Candles.Draw(surface, []);

        Assert.False(range.IsValid);
        Assert.Empty(surface.Rectangles);
    }

    // ── Crosshair ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCrosshairDrawsNothingWhenThePointerIsElsewhere()
    {
        // Callable unconditionally: a visualizer should not have to test the cursor itself.
        var surface = new RecordingSurface(200d, 200d);

        Plot.Crosshair(surface, new PlotRange(0d, 100d));

        Assert.Empty(surface.Lines);
    }

    [Fact]
    public void TheCrosshairReadsOutTheValueUnderThePointer()
    {
        var surface = new RecordingSurface(200d, 200d) { CursorState = new RenderCursor(50d, 100d, true, false) };

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

    /// <summary>A surface that remembers what it was asked to draw, so routines are testable headlessly.</summary>
    private sealed class RecordingSurface(double width, double height) : IRenderSurface
    {
        internal List<(double X, double Y, double Width, double Height)> Rectangles { get; } = [];

        internal List<(double X1, double Y1, double X2, double Y2)> Lines { get; } = [];

        internal List<(double X, double Y, string Text)> Texts { get; } = [];

        internal RenderCursor CursorState { get; init; } = new(0d, 0d, false, false);

        public RenderViewport Viewport => new(width, height, 1d);

        public RenderCursor Cursor => CursorState;

        public RenderColor Theme(RenderThemeColor token) => new(1, 2, 3);

        public void SetStyle(RenderStyle style) { }

        public IDisposable Panel(string title, RenderPanelKind kind) => new Scope();

        public void AxisX(double minimum, double maximum, string? format = null) { }

        public void AxisY(double minimum, double maximum, string? format = null) { }

        public IDisposable Series(string name, RenderSeriesKind kind) => new Scope();

        public void Push(double x, double y) { }

        public void Line(double x1, double y1, double x2, double y2) => Lines.Add((x1, y1, x2, y2));

        public void Rect(double x, double y, double width, double height, bool filled = true) =>
            Rectangles.Add((x, y, width, height));

        public void Text(double x, double y, string text) => Texts.Add((x, y, text));

        public void Marker(double x, double y, RenderMarkerShape shape) { }

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
