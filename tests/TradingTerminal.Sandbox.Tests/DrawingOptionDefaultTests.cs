using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// Every routine falls back to its options' <c>Default</c> when the caller supplies nothing, so a
/// <c>Default</c> that is quietly all zeros means the routine draws nothing at all — which is exactly
/// what happened, and it survived a whole suite of drawing tests because every one of them passed
/// explicit options.
///
/// <para>The trap is a language one: <c>new()</c> on a record struct binds to the implicit
/// parameterless constructor, not the primary one, so the primary constructor's declared defaults are
/// skipped. It is invisible at the call site and will be reintroduced by anyone adding the next
/// options struct, which is why it is pinned here.</para>
/// </summary>
public sealed class DrawingOptionDefaultTests
{
    [Fact]
    public void LadderDefaultsAreTheDeclaredOnes()
    {
        Assert.Equal(10, LadderOptions.Default.Levels);
        Assert.Equal(18d, LadderOptions.Default.RowHeight);
        Assert.Equal(64d, LadderOptions.Default.PriceWidth);
        Assert.True(LadderOptions.Default.ShowSize);
    }

    [Fact]
    public void FootprintDefaultsAreTheDeclaredOnes()
    {
        Assert.Equal(74d, FootprintOptions.Default.ColumnWidth);
        Assert.Equal(14d, FootprintOptions.Default.RowHeight);
        Assert.Equal(60d, FootprintOptions.Default.PriceWidth);
        Assert.True(FootprintOptions.Default.ShowCellVolumes);
        Assert.True(FootprintOptions.Default.ShowPointOfControl);
        Assert.True(FootprintOptions.Default.ShowValueArea);
        Assert.True(FootprintOptions.Default.ShowImbalances);
    }

    [Fact]
    public void CandleDefaultsAreTheDeclaredOnes()
    {
        Assert.Equal(0.7d, CandleOptions.Default.BodyFraction);
        Assert.True(CandleOptions.Default.ShowGrid);
        Assert.Equal(5, CandleOptions.Default.GridLines);
    }

    [Fact]
    public void ALadderDrawnWithNoOptionsAtAllStillDrawsSomething()
    {
        // The call an author writes first, and the one that was silently blank.
        var surface = new CountingSurface(640d, 400d);

        Ladder.Draw(
            surface,
            new DepthSnapshot(
                DateTime.UnixEpoch,
                [new DepthLevel(99.9d, 40L)],
                [new DepthLevel(100.1d, 55L)]));

        Assert.True(surface.Operations > 0, "Ladder.Draw with default options drew nothing");
    }

    [Fact]
    public void AFootprintDrawnWithNoOptionsAtAllStillDrawsSomething()
    {
        var surface = new CountingSurface(640d, 400d);

        Footprint.Draw(
            surface,
            [
                new FootprintBar(
                    DateTime.UnixEpoch,
                    DateTime.UnixEpoch.AddMinutes(1),
                    [new FootprintFeatureRow(100d, 40L, 20L, false, false, false, false)],
                    100d,
                    100d, 100d, 100d,
                    40L, 20L, 20L, 0L, 0, 0,
                    FeedQuality.RealTape),
            ]);

        Assert.True(surface.Operations > 0, "Footprint.Draw with default options drew nothing");
    }

    [Fact]
    public void PriceChartDefaultsAreTheDeclaredOnes()
    {
        Assert.Equal(ChartWindow.DefaultBars, PriceChartOptions.Default.MaximumBars);
        Assert.Equal(62d, PriceChartOptions.Default.ScaleWidth);
        Assert.Equal(0.18d, PriceChartOptions.Default.VolumeShare);
        Assert.True(PriceChartOptions.Default.ShowVolume);
        Assert.True(PriceChartOptions.Default.ShowLegend);
        Assert.True(PriceChartOptions.Default.ShowTimeAxis);
        Assert.True(PriceChartOptions.Default.ShowCrosshair);
        Assert.True(PriceChartOptions.Default.ShowLastPrice);
    }

    [Fact]
    public void TheChartFurnitureDefaultsAreTheDeclaredOnes()
    {
        Assert.Equal(0.72d, ChartSeriesOptions.Default.BodyFraction);
        Assert.Equal(1.4d, ChartSeriesOptions.Default.Thickness);
        Assert.Equal(0.16d, ChartSeriesOptions.Default.FillAlpha);
        Assert.True(double.IsNaN(ChartSeriesOptions.Default.Baseline));

        Assert.Equal(62d, PriceScaleOptions.Default.Width);
        Assert.Equal(6, PriceScaleOptions.Default.ApproximateTicks);
        Assert.True(PriceScaleOptions.Default.ShowGrid);

        Assert.Equal(18d, TimeAxisOptions.Default.Height);
        Assert.Equal(6, TimeAxisOptions.Default.ApproximateTicks);
        Assert.True(TimeAxisOptions.Default.ShowGrid);
    }

    [Fact]
    public void AChartDrawnWithNoOptionsAtAllStillDrawsSomething()
    {
        // The whole control behind one fallback: every piece of furniture on it is a bool that lands on
        // false, so a zeroed options record would have drawn bare candles with no scale, no axis and no
        // legend — and looked deliberate.
        var surface = new CountingSurface(640d, 400d);

        PriceChart.Draw(surface, Bars());

        Assert.True(surface.Operations > 0, "PriceChart.Draw with default options drew nothing");
    }

    private static IReadOnlyList<OhlcvBar> Bars(int count = 12)
    {
        var bars = new OhlcvBar[count];
        for (var index = 0; index < count; index++)
        {
            var open = 100d + Math.Sin(index / 2d);
            bars[index] = new OhlcvBar(
                new InstrumentId(1), BarSize.OneMinute, DateTime.UnixEpoch.AddMinutes(index),
                open, open + 1d, open - 1d, open + 0.5d, 1_000L, BrokerKind.Simulated, IsFinal: true);
        }

        return bars;
    }

    private sealed class CountingSurface(double width, double height) : IRenderSurface
    {
        internal int Operations { get; private set; }

        public RenderViewport Viewport => new(width, height, 1d);

        public RenderCursor Cursor => new(0d, 0d, false, false);

        public DateTime Now => DateTime.MinValue;

        public RenderColor Theme(RenderThemeColor token) => new(1, 2, 3);

        public void SetStyle(RenderStyle style) { }

        public IDisposable Panel(string title, RenderPanelKind kind) => new Scope();

        public void AxisX(double minimum, double maximum, string? format = null) { }

        public void AxisY(double minimum, double maximum, string? format = null) { }

        public IDisposable Series(string name, RenderSeriesKind kind) => new Scope();

        public void Push(double x, double y) => Operations++;

        public void Line(double x1, double y1, double x2, double y2) => Operations++;

        public void Rect(double x, double y, double width, double height, bool filled = true) => Operations++;

        public void Text(double x, double y, string text) => Operations++;

        public void Marker(double x, double y, RenderMarkerShape shape) => Operations++;

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
