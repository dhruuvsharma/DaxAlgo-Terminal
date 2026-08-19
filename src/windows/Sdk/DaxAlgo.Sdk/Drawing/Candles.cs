using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a candle series is drawn.</summary>
/// <param name="BodyFraction">Body width as a fraction of the column, leaving the rest as a gap.</param>
/// <param name="PriceFormat">Numeric format for the price axis.</param>
/// <param name="ShowGrid">Whether to draw the price grid and axis labels.</param>
/// <param name="GridLines">Approximate number of horizontal gridlines.</param>
public readonly record struct CandleOptions(
    double BodyFraction = 0.7d,
    string? PriceFormat = null,
    bool ShowGrid = true,
    int GridLines = 5)
{
    public static CandleOptions Default { get; } = new();
}

/// <summary>
/// OHLC candles with an auto-scaled price axis.
///
/// <para>Draws in panel pixel space rather than declaring axes on the surface, because the price
/// range comes from the bars themselves and a caller should not have to compute it before it can see
/// anything. A visualizer that wants its own scale can declare axes and draw the primitives itself —
/// this is the one-liner, not the only way.</para>
/// </summary>
public static class Candles
{
    /// <summary>Draws candles into the current panel and returns the price range that was used.</summary>
    public static PlotRange Draw(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar>? bars,
        CandleOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (bars is null || bars.Count == 0)
            return PlotRange.Empty;

        if (options.BodyFraction <= 0d)
            options = CandleOptions.Default;

        var viewport = surface.Viewport;
        if (viewport.Width <= 0d || viewport.Height <= 0d)
            return PlotRange.Empty;

        var range = PlotRange.Empty;
        for (var index = 0; index < bars.Count; index++)
        {
            range = range.Include(bars[index].High);
            range = range.Include(bars[index].Low);
        }

        range = range.Padded();
        if (!range.IsValid)
            return PlotRange.Empty;

        if (options.ShowGrid)
            Plot.HorizontalGrid(surface, range, options.GridLines, options.PriceFormat);

        var column = viewport.Width / bars.Count;
        var body = Math.Max(column * Math.Clamp(options.BodyFraction, 0.1d, 1d), 1d);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var bearish = surface.Theme(RenderThemeColor.Bearish);

        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            var centre = (index + 0.5d) * column;
            var colour = bar.Close >= bar.Open ? bullish : bearish;
            surface.SetStyle(new RenderStyle(colour, Thickness: 1d));

            var high = Plot.ToY(bar.High, range, viewport.Height);
            var low = Plot.ToY(bar.Low, range, viewport.Height);
            surface.Line(centre, high, centre, low);

            var open = Plot.ToY(bar.Open, range, viewport.Height);
            var close = Plot.ToY(bar.Close, range, viewport.Height);
            var top = Math.Min(open, close);
            // A doji has no body height at all; give it a hairline so the bar does not vanish.
            var height = Math.Max(Math.Abs(close - open), 1d);
            surface.Rect(centre - (body / 2d), top, body, height);
        }

        return range;
    }
}
