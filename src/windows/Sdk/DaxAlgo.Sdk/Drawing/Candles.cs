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
    /// <summary>
    /// The intended defaults.
    ///
    /// <para>Written with an explicit argument on purpose. <c>new()</c> on a record struct binds to the
    /// implicit parameterless constructor rather than the primary one, so every field lands on zero and
    /// the primary constructor's declared defaults are silently skipped — which made this the
    /// all-zero value, and made every routine that fell back to it draw nothing at all.</para>
    /// </summary>
    public static CandleOptions Default { get; } = new(BodyFraction: 0.7d);
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
    /// <summary>Draws candles and returns the price range that was used.</summary>
    /// <param name="area">Where to draw them. Omitted, they fill the panel — right for a chart that
    /// owns one, wrong the moment anything sits beside it. Until this existed a candle chart could not
    /// be PLACED: it read the viewport directly, so composing it with a book or a delta strip drew it
    /// across both.</param>
    public static PlotRange Draw(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar>? bars,
        CandleOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (bars is null || bars.Count == 0)
            return PlotRange.Empty;

        if (options.BodyFraction <= 0d)
            options = CandleOptions.Default;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid)
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
            Plot.HorizontalGrid(surface, range, options.GridLines, options.PriceFormat, area: area);

        var column = area.Width / bars.Count;
        var body = Math.Max(column * Math.Clamp(options.BodyFraction, 0.1d, 1d), 1d);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var bearish = surface.Theme(RenderThemeColor.Bearish);

        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            var centre = area.X + ((index + 0.5d) * column);
            var colour = bar.Close >= bar.Open ? bullish : bearish;
            surface.SetStyle(new RenderStyle(colour, Thickness: 1d));

            // area.ToY offsets by the region's own top, which is the whole difference between a
            // placed chart and one drawn at the panel's origin regardless of where it was put.
            var high = area.ToY(bar.High, range);
            var low = area.ToY(bar.Low, range);
            surface.Line(centre, high, centre, low);

            var open = area.ToY(bar.Open, range);
            var close = area.ToY(bar.Close, range);
            var top = Math.Min(open, close);
            // A doji has no body height at all; give it a hairline so the bar does not vanish.
            var height = Math.Max(Math.Abs(close - open), 1d);
            surface.Rect(centre - (body / 2d), top, body, height);
        }

        return range;
    }
}
