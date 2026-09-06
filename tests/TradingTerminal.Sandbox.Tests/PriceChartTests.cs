using System.Globalization;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// What the chart control computes, as opposed to whether it emits pixels.
///
/// <para>The contract suite next door proves it draws something, survives an empty history and never
/// emits a NaN. It cannot tell a chart that shows the newest bars from one that shows the oldest, a
/// crosshair that snaps to a bar from one that reads a time no bar has, or an overlay drawn half a
/// candle out of step from one that lands on its own prices. Those are the failures that look right,
/// and they are the ones a reader would trade on.</para>
/// </summary>
public sealed class PriceChartTests
{
    private static RecordingRenderSurface Surface(
        double width = 800d,
        double height = 480d,
        double zoom = 1d,
        double panX = 0d,
        RenderCursor? cursor = null) =>
        new(new RenderViewport(width, height, 1d) { Zoom = zoom, PanX = panX }, cursor);

    private static IReadOnlyList<OhlcvBar> Bars(int count = 60)
    {
        var bars = new OhlcvBar[count];
        for (var index = 0; index < count; index++)
        {
            var open = 100d + (Math.Sin(index / 4d) * 3d);
            var close = open + (index % 3 == 0 ? 0.8d : -0.6d);

            bars[index] = new OhlcvBar(
                new InstrumentId(1), BarSize.OneMinute, DateTime.UnixEpoch.AddMinutes(index),
                open, Math.Max(open, close) + 0.4d, Math.Min(open, close) - 0.4d, close,
                1_000L + (index * 10L), BrokerKind.Simulated, IsFinal: true);
        }

        return bars;
    }

    private static OhlcvBar Bar(DateTime at, double open, double high, double low, double close, long volume = 100L) =>
        new(new InstrumentId(1), BarSize.OneMinute, at, open, high, low, close, volume, BrokerKind.Simulated, true);

    private static string Price(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // ── the window: what the wheel and the drag actually do ─────────────────────────────────────

    private static readonly PlotArea Plot = new(0d, 0d, 240d, 100d);

    [Fact]
    public void TheWindowIsTheNewestBars()
    {
        // A chart that opens on the oldest bars in the buffer is the one thing nobody wants, and it is
        // what every hand-rolled slice did when the history outgrew the panel.
        var window = ChartWindow.Of(Surface(), 500, 240, Plot);

        Assert.Equal(260, window.First);
        Assert.Equal(240, window.Count);
        Assert.Equal(499, window.Last);
    }

    [Fact]
    public void ZoomingInHalvesTheBarsOnScreen()
    {
        // Zoom applies to the DATA range, per the contract on RenderViewport.Zoom: twice the zoom is
        // half the bars at the same size, not the same bars drawn twice as large.
        var window = ChartWindow.Of(Surface(zoom: 2d), 500, 240, Plot);

        Assert.Equal(120, window.Count);
        Assert.Equal(380, window.First);
    }

    [Fact]
    public void ZoomIsClampedSoAChartCannotBeSpunDownToThreeCandles()
    {
        var window = ChartWindow.Of(Surface(zoom: 1000d), 500, 240, Plot);

        Assert.Equal(ChartWindow.MinimumBars, window.Count);
    }

    [Fact]
    public void AHistoryShorterThanTheWindowIsShownWhole()
    {
        // The first frames of every live unit. Showing 240 columns of which 235 are empty is the wrong
        // answer, and so is clamping to a minimum the data cannot fill.
        var window = ChartWindow.Of(Surface(), 5, 240, Plot);

        Assert.Equal(0, window.First);
        Assert.Equal(5, window.Count);
    }

    [Fact]
    public void DraggingRightWalksBackThroughTheHistory()
    {
        // 240 bars across 240 pixels is a bar per pixel, so a 30-pixel drag is 30 bars — the property
        // that makes a drag feel attached to the pointer at every zoom level.
        var window = ChartWindow.Of(Surface(panX: 30d), 500, 240, Plot);

        Assert.Equal(230, window.First);
        Assert.Equal(240, window.Count);
    }

    [Fact]
    public void DraggingPastTheStartStopsAtTheFirstBar()
    {
        var window = ChartWindow.Of(Surface(panX: 1_000_000d), 500, 240, Plot);

        Assert.Equal(0, window.First);
        Assert.Equal(240, window.Count);
    }

    [Fact]
    public void DraggingForwardCannotPushTheNewestBarOffTheRightEdge()
    {
        var window = ChartWindow.Of(Surface(panX: -1_000_000d), 500, 240, Plot);

        Assert.Equal(260, window.First);
        Assert.Equal(499, window.Last);
    }

    [Fact]
    public void AWindowSurvivesTheBufferBeingTrimmed()
    {
        // A held window indexes a list that grows on every bar and is trimmed when the buffer is
        // bounded. This is the difference between a shorter window and an IndexOutOfRangeException on
        // the render thread.
        var window = new ChartWindow(400, 240).ClampedTo(300);

        Assert.True(window.Last < 300);
        Assert.False(window.IsEmpty);
    }

    [Fact]
    public void AnIndexOutsideTheWindowIsNotInIt()
    {
        var window = new ChartWindow(10, 5);

        Assert.True(window.Contains(10));
        Assert.True(window.Contains(14));
        Assert.False(window.Contains(9));
        Assert.False(window.Contains(15));
    }

    // ── the view: one coordinate system, not two ────────────────────────────────────────────────

    [Fact]
    public void TheViewMapsABarToTheColumnItsCandleWasDrawnOn()
    {
        // The claim the whole return value rests on. If X() and the drawing disagree, every annotation
        // a caller adds lands beside the bar it describes rather than on it.
        var surface = Surface();
        var view = PriceChart.Draw(
            surface, Bars(), PriceChartOptions.Default with { ShowTimeAxis = false, ShowVolume = false });

        var x = view.X(view.Window.First);

        Assert.Contains(surface.Lines, line =>
            Math.Abs(line.X1 - x) < 1e-9 && Math.Abs(line.X2 - x) < 1e-9 && line.Y1 != line.Y2);
    }

    [Fact]
    public void ThePriceUnderAPixelIsTheInverseOfWhereThatPriceWasDrawn()
    {
        var view = PriceChart.Draw(Surface(), Bars());

        Assert.Equal(101.25d, view.PriceAt(view.Y(101.25d)), 6);
    }

    [Fact]
    public void TheBarUnderAPixelIsTheInverseOfWhereThatBarWasDrawn()
    {
        var view = PriceChart.Draw(Surface(), Bars(60));

        for (var index = view.Window.First; index <= view.Window.Last; index += 7)
            Assert.Equal(index, view.IndexAt(view.X(index)));
    }

    // ── the scale ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheScaleFitsAnOverlayThatRunsAboveThePrices()
    {
        // An average drawn off the top of its own chart is the commonest way a composed picture lies:
        // the line is simply absent for the stretch that mattered.
        var bars = Bars(300);
        var overlay = new double[bars.Count];
        Array.Fill(overlay, double.NaN);
        overlay[^1] = 130d;

        var view = PriceChart.Draw(Surface(), bars, overlays: [SeriesData.Line("high", overlay)]);

        Assert.True(view.Range.Maximum >= 130d, $"the scale stopped at {view.Range.Maximum}");
    }

    [Fact]
    public void AnOverlaySpikeOutsideTheWindowDoesNotFlattenTheChart()
    {
        // The other half of the same rule. Folding the whole array in would let a value from a
        // thousand bars ago squash everything on screen into one line.
        var bars = Bars(500);
        var overlay = new double[bars.Count];
        Array.Fill(overlay, 100d);
        overlay[0] = 10_000d;

        var view = PriceChart.Draw(Surface(), bars, overlays: [SeriesData.Line("spike", overlay)]);

        Assert.False(view.Window.Contains(0));
        Assert.True(view.Range.Maximum < 200d, $"an off-screen spike reached the scale at {view.Range.Maximum}");
    }

    [Fact]
    public void TheGutterLabelsOnAOneTwoFiveProgression()
    {
        var surface = Surface();

        PriceScale.Draw(surface, new PlotRange(0d, 100d), new PriceScaleOptions(62d, ApproximateTicks: 5));

        Assert.Contains(surface.Texts, t => t.Text == "20");
        Assert.Contains(surface.Texts, t => t.Text == "40");
        Assert.DoesNotContain(surface.Texts, t => t.Text == "16.6667");
    }

    [Fact]
    public void ATagForAPriceOffTheScaleIsNotDrawn()
    {
        // Clamping it to the edge would put a filled pill against the top of the gutter reading a price
        // that is not there, and somebody will act on it.
        var surface = Surface();

        PriceScale.Tag(surface, new PlotRange(99d, 101d), 250d);

        Assert.Empty(surface.Rectangles);
    }

    // ── the furniture ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VolumeSitsOnTheFloorOfThePricePane()
    {
        var view = PriceChart.Draw(Surface(), Bars());

        Assert.Equal(view.Price.Bottom, view.Volume.Bottom, 6);
        Assert.Equal(view.Price.Height * 0.18d, view.Volume.Height, 6);
    }

    [Fact]
    public void APaneTakesItsHeightFromThePricePaneAndNothingElse()
    {
        // 480 tall: 18 for the time strip, 16 for the legend, 72 for the pane, the rest is price.
        var view = PriceChart.Draw(
            Surface(), Bars(), panes: [new ChartPane(SeriesData.Line("rsi", new double[60]), 72d, 50d)]);

        Assert.Equal(480d - 18d - PriceChart.LegendHeight - 72d, view.Price.Height, 6);
    }

    [Fact]
    public void PanesAreShrunkRatherThanAllowedToSqueezeThePriceOut()
    {
        // A pane taller than the panel is an ordinary mistake — a height in the wrong units, a panel
        // dragged small. The price keeps a usable height either way.
        var view = PriceChart.Draw(
            Surface(), Bars(), panes: [new ChartPane(SeriesData.Line("rsi", new double[60]), 10_000d)]);

        Assert.True(view.Price.Height >= PriceChart.MinimumPriceHeight);
    }

    [Fact]
    public void AMarkerOnABarThatScrolledOffIsNotDrawn()
    {
        // Drawn anyway it lands at the edge of the window, which says the trade happened at a time it
        // did not — and it is the first thing a reader checks the strategy against.
        var surface = Surface();

        var view = PriceChart.Draw(
            surface, Bars(500), markers: [new Signal(5, 100d, SignalKind.Buy, "old")]);

        Assert.False(view.Window.Contains(5));
        Assert.Empty(surface.Markers);
    }

    [Fact]
    public void AMarkerInsideTheWindowIsDrawnWithItsOwnGlyph()
    {
        var surface = Surface();
        var bars = Bars(60);

        PriceChart.Draw(surface, bars, markers: [new Signal(30, bars[30].Close, SignalKind.Sell)]);

        Assert.Contains(surface.Markers, m => m.Shape == Signals.ShapeOf(SignalKind.Sell));
    }

    [Fact]
    public void ALevelIsTaggedInTheGutterAsWellAsRuledAcrossTheChart()
    {
        var surface = Surface();
        var bars = Bars();
        var view = PriceChart.Draw(
            surface,
            bars,
            PriceChartOptions.Default with { ShowVolume = false, ShowLastPrice = false },
            levels: [new Level(bars[^1].Close, "stop", RenderThemeColor.Bearish)]);

        Assert.Single(surface.Rectangles, r => r.X >= view.Scale.X);
    }

    // ── the legend reads the pointer ────────────────────────────────────────────────────────────

    [Fact]
    public void WithNoPointerTheLegendReadsTheLastBar()
    {
        var surface = Surface();
        var bars = Bars();

        var view = PriceChart.Draw(surface, bars);

        Assert.Equal(-1, view.HoveredIndex);
        Assert.Contains(surface.Texts, t => t.Text.StartsWith("O ", StringComparison.Ordinal)
            && t.Text.Contains(Price(bars[^1].Close), StringComparison.Ordinal));
    }

    [Fact]
    public void TheLegendReadsTheBarUnderThePointer()
    {
        // What turns a chart into something inspectable rather than merely current. Drawn twice: once
        // to learn the geometry, then again with the pointer on a bar the first pass located — which
        // also pins that the hit test and X() agree.
        var bars = Bars();
        var geometry = PriceChart.Draw(Surface(), bars);
        var hovered = geometry.Window.First + 10;

        var surface = Surface(cursor: new RenderCursor(
            geometry.X(hovered), geometry.Price.CenterY, IsInside: true, IsPressed: false));
        var view = PriceChart.Draw(surface, bars);

        Assert.Equal(hovered, view.HoveredIndex);
        Assert.Contains(surface.Texts, t => t.Text.StartsWith("O ", StringComparison.Ordinal)
            && t.Text.Contains(Price(bars[hovered].Open), StringComparison.Ordinal));
    }

    [Fact]
    public void TheCrosshairSnapsToTheColumnRatherThanFollowingThePointer()
    {
        // A rule at the raw pointer X sits between two candles and reads a time no bar has.
        var bars = Bars();
        var geometry = PriceChart.Draw(Surface(), bars);
        var hovered = geometry.Window.First + 10;
        var column = geometry.X(hovered);

        var surface = Surface(cursor: new RenderCursor(
            column + (geometry.ColumnWidth / 2d) - 0.5d, geometry.Price.CenterY, IsInside: true, IsPressed: false));
        var view = PriceChart.Draw(surface, bars);

        Assert.Equal(hovered, view.HoveredIndex);
        Assert.Contains(surface.Lines, line =>
            Math.Abs(line.X1 - column) < 1e-9 && Math.Abs(line.X2 - column) < 1e-9 && line.Style.Dashed);
    }

    // ── Heikin-Ashi ─────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<OhlcvBar> Gapped() =>
    [
        Bar(DateTime.UnixEpoch, 100d, 101.2d, 99.8d, 101d),
        Bar(DateTime.UnixEpoch.AddMinutes(1), 105d, 106.2d, 104.8d, 106d),
        Bar(DateTime.UnixEpoch.AddMinutes(2), 110d, 111.2d, 109.8d, 111d),
    ];

    private static IReadOnlyList<RecordedRect> Bodies(ChartStyle style)
    {
        var surface = Surface();
        PriceChart.Draw(surface, Gapped(), PriceChartOptions.Default with
        {
            Style = style,
            ShowVolume = false,
            ShowLastPrice = false,
            ShowLegend = false,
            ShowTimeAxis = false,
        });

        return surface.Rectangles;
    }

    [Fact]
    public void RawCandlesGapWhereThePriceGapped()
    {
        // The control case. Without it the assertion below proves only that three rectangles overlap.
        var bodies = Bodies(ChartStyle.Candles);

        Assert.Equal(3, bodies.Count);
        Assert.False(Overlaps(bodies[0], bodies[1]), "the fixture is supposed to gap");
    }

    [Fact]
    public void HeikinAshiCandlesNeverGap()
    {
        // The defining property: each candle opens at the midpoint of the last one, so consecutive
        // bodies always touch however far the raw price jumped. A transform that opened on the raw bar
        // would reproduce the gap and look almost right.
        var bodies = Bodies(ChartStyle.HeikinAshi);

        Assert.Equal(3, bodies.Count);
        for (var index = 1; index < bodies.Count; index++)
            Assert.True(Overlaps(bodies[index - 1], bodies[index]), $"candle {index} gapped");
    }

    [Fact]
    public void HeikinAshiIsSeededFromHistoryRatherThanFromTheLeftEdgeOfTheWindow()
    {
        // Otherwise the picture depends on where the viewer happened to scroll: the same bar draws one
        // way in a wide window and another in a narrow one, which is a chart that cannot be trusted.
        var bars = new List<OhlcvBar>();
        for (var index = 0; index < 40; index++)
            bars.Add(Bar(DateTime.UnixEpoch.AddMinutes(index), 100d, 101d, 99d, 100d));

        bars.Add(Bar(DateTime.UnixEpoch.AddMinutes(40), 110d, 111d, 109d, 110d));

        var range = ChartSeries.RangeOf(bars, new ChartWindow(bars.Count - 1, 1), ChartStyle.HeikinAshi);

        // Seeded at the window it would open at 110 and the candle would be a doji up there. Carried
        // forward from the flat history it opens near 100, and the candle is the move itself.
        Assert.True(range.Minimum < 101d, $"the transform started at the window edge: {range.Minimum}");
    }

    private static bool Overlaps(RecordedRect a, RecordedRect b) =>
        a.Y <= b.Y + b.Height && b.Y <= a.Y + a.Height;

    // ── the time strip ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAxisStepsOnIntervalsAPersonReadsAClockIn()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), TimeAxis.StepFor(TimeSpan.FromMinutes(11)));
        Assert.Equal(TimeSpan.FromMinutes(30), TimeAxis.StepFor(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromHours(4), TimeAxis.StepFor(TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromDays(365), TimeAxis.StepFor(TimeSpan.FromDays(3650)));
    }

    [Fact]
    public void AnIntradayLabelIsTheClockUntilTheDayChanges()
    {
        var stamp = new DateTime(2026, 3, 9, 14, 30, 0, DateTimeKind.Utc);

        Assert.Equal("14:30", TimeAxis.Label(stamp, TimeSpan.FromMinutes(5)));
        Assert.Equal("9 Mar", TimeAxis.Label(stamp, TimeSpan.FromMinutes(5), dayChanged: true));
        Assert.Equal("Mar 26", TimeAxis.Label(stamp, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void LabelsLandOnBarsRatherThanOnEvenDivisionsOfThePanel()
    {
        // The point of the whole strip. These bars skip six hours; an axis that divided the span into
        // six would confidently label times the market was shut, and it would look right.
        var day = new DateTime(2026, 3, 9, 9, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<OhlcvBar> bars =
        [
            Bar(day, 100d, 101d, 99d, 100.5d),
            Bar(day.AddMinutes(1), 100.5d, 101d, 99d, 100d),
            Bar(day.AddMinutes(2), 100d, 101d, 99d, 100.5d),
            Bar(day.AddHours(6), 100.5d, 101d, 99d, 100d),
        ];

        var surface = Surface();
        TimeAxis.Draw(surface, bars);

        Assert.Contains(surface.Texts, t => t.Text == "15:00");
        Assert.DoesNotContain(surface.Texts, t => t.Text is "10:00" or "11:00" or "12:00");
    }

    // ── the empty frame ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyHistoryWaitsRatherThanDrawingAnEmptyChart()
    {
        var surface = Surface();

        var view = PriceChart.Draw(surface, []);

        Assert.Equal(ChartView.None, view);
        Assert.False(view.IsValid);
        Assert.Contains(surface.Texts, t => t.Text.Contains("Waiting", StringComparison.Ordinal));
    }

    [Fact]
    public void APanelWithNoRoomDrawsNothingRatherThanScribbling()
    {
        var surface = Surface(width: 4d, height: 3d);

        var view = PriceChart.Draw(surface, Bars());

        Assert.False(view.IsValid);
        Assert.False(surface.HasNonFiniteCoordinate);
    }
}
