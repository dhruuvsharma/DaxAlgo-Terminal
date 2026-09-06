using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// How the price itself is drawn — the choice a reader makes first on a real terminal, and the one an
/// authored picture never had.
/// </summary>
public enum ChartStyle
{
    /// <summary>Filled bodies with wicks. What a trader expects unless told otherwise.</summary>
    Candles = 0,

    /// <summary>Bodies outlined when the bar closed up and filled when it closed down. Reads better on
    /// a dense chart, where a wall of filled bodies hides the direction it is supposed to show.</summary>
    HollowCandles = 1,

    /// <summary>OHLC bars: a high-low stick with the open ticked left and the close ticked right. Half
    /// the ink of a candle, which is why it survives at a hundred bars to the inch.</summary>
    Bars = 2,

    /// <summary>Closes joined. The honest choice when the opens and the wicks are not the point.</summary>
    Line = 3,

    /// <summary>Closes joined and filled to the floor.</summary>
    Area = 4,

    /// <summary>Closes filled to a reference and coloured either side of it — a session open, an entry,
    /// a fair value. Says "relative to this" in a way a line never does.</summary>
    Baseline = 5,

    /// <summary>Heikin-Ashi: each candle averaged into the last, so the trend survives and the noise
    /// does not. <b>These are not real prices</b> and nothing should be executed off them.</summary>
    HeikinAshi = 6,
}

/// <summary>How a price series is drawn.</summary>
/// <param name="Style">Candles, bars, line, area, baseline or Heikin-Ashi.</param>
/// <param name="BodyFraction">Body width as a fraction of the column, leaving the rest as a gap.</param>
/// <param name="Thickness">Stroke width for wicks and for the line styles.</param>
/// <param name="FillAlpha">Opacity of the fill under an area or a baseline.</param>
/// <param name="Baseline">Where <see cref="ChartStyle.Baseline"/> measures from. NaN takes the first
/// visible close, which makes the picture "since the left edge" and follows the viewer as they pan.</param>
public readonly record struct ChartSeriesOptions(
    ChartStyle Style = ChartStyle.Candles,
    double BodyFraction = 0.72d,
    double Thickness = 1.4d,
    double FillAlpha = 0.16d,
    double Baseline = double.NaN)
{
    /// <summary>The intended defaults. Written with explicit arguments because <c>new()</c> on a record
    /// struct binds the implicit parameterless constructor and lands every field on zero — a
    /// zero-width, hairline-free, fully transparent series.</summary>
    public static ChartSeriesOptions Default { get; } =
        new(BodyFraction: 0.72d, Thickness: 1.4d, FillAlpha: 0.16d, Baseline: double.NaN);
}

/// <summary>
/// The price series of a chart, in whichever style was asked for, over whichever slice is on screen.
///
/// <para>Split out from <see cref="PriceChart"/> so the styles are reachable on their own: a unit that
/// has already built its own furniture — a footprint's price column, a strategy's own grid — can draw
/// candles into it without taking the whole control. <see cref="Candles"/> remains the one-liner for a
/// panel that is nothing but candles; this one takes a <see cref="ChartWindow"/> and an explicit
/// range, which is what lets several things share one scale.</para>
/// </summary>
public static class ChartSeries
{
    /// <summary>
    /// How many bars of history before the window a Heikin-Ashi transform is seeded from.
    ///
    /// <para>The transform is recursive — each candle's open is the average of the last one's open and
    /// close — so starting it at the left edge of the window would make the picture depend on where
    /// the viewer happened to scroll. The recursion halves its error every bar, so a few dozen bars of
    /// warm-up is indistinguishable from running it over the whole history, and costs a bounded amount
    /// of work per frame instead of a growing one.</para>
    /// </summary>
    public const int HeikinAshiWarmUp = 64;

    /// <summary>Draws the price series and returns the range it was drawn against.</summary>
    /// <param name="surface">The surface to draw onto.</param>
    /// <param name="bars">The history. Indices are into this list.</param>
    /// <param name="window">Which bars to draw. Omitted, all of them.</param>
    /// <param name="options">Style and geometry.</param>
    /// <param name="range">The price scale to draw against. Omitted, one is computed from the visible
    /// bars — right for a lone chart, wrong the moment something else shares the panel.</param>
    /// <param name="area">Where to draw. Omitted, the whole panel.</param>
    public static PlotRange Draw(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar>? bars,
        ChartWindow window = default,
        ChartSeriesOptions options = default,
        PlotRange range = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (bars is null || bars.Count == 0) return PlotRange.Empty;

        if (options.BodyFraction <= 0d) options = ChartSeriesOptions.Default;

        window = window.IsEmpty ? ChartWindow.All(bars.Count) : window.ClampedTo(bars.Count);
        if (window.IsEmpty) return PlotRange.Empty;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        var painted = options.Style == ChartStyle.HeikinAshi ? HeikinAshi(bars, window) : null;
        var first = window.First;
        Ohlc Read(int index) => painted is null ? Ohlc.Of(bars[index]) : painted[index - first];

        var baseline = double.IsFinite(options.Baseline) ? options.Baseline : Read(window.First).Close;

        if (!range.IsValid) range = Fold(Read, window, options.Style, baseline).Padded();
        if (!range.IsValid) return PlotRange.Empty;

        var column = area.Width / window.Count;
        var body = Math.Max(column * Math.Clamp(options.BodyFraction, 0.1d, 1d), 1d);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var bearish = surface.Theme(RenderThemeColor.Bearish);

        switch (options.Style)
        {
            case ChartStyle.Line:
            case ChartStyle.Area:
                Joined(surface, Read, window, options, range, area, column);
                break;

            case ChartStyle.Baseline:
                Baseline(surface, Read, window, options, range, area, column, baseline, bullish, bearish);
                break;

            default:
                Columns(surface, Read, window, options, range, area, column, body, bullish, bearish);
                break;
        }

        return range;
    }

    /// <summary>
    /// The price range the visible bars occupy, unpadded — fold your overlays into it and call
    /// <see cref="PlotRange.Padded"/> yourself, or a moving average runs off the top of its own chart.
    /// </summary>
    /// <param name="bars">The history.</param>
    /// <param name="window">Which bars count. Omitted, all of them.</param>
    /// <param name="style">Which extremes count. The line styles fit the closes, because fitting a line
    /// chart to the wicks leaves it in a band down the middle of a mostly empty panel.</param>
    /// <param name="baseline">The reference a baseline chart measures from, folded in so it cannot fall
    /// outside its own scale.</param>
    public static PlotRange RangeOf(
        IReadOnlyList<OhlcvBar>? bars,
        ChartWindow window = default,
        ChartStyle style = ChartStyle.Candles,
        double baseline = double.NaN)
    {
        if (bars is null || bars.Count == 0) return PlotRange.Empty;

        window = window.IsEmpty ? ChartWindow.All(bars.Count) : window.ClampedTo(bars.Count);
        if (window.IsEmpty) return PlotRange.Empty;

        var painted = style == ChartStyle.HeikinAshi ? HeikinAshi(bars, window) : null;
        var first = window.First;
        Ohlc Read(int index) => painted is null ? Ohlc.Of(bars[index]) : painted[index - first];

        return Fold(Read, window, style, double.IsFinite(baseline) ? baseline : Read(window.First).Close);
    }

    /// <summary>
    /// A value series drawn on the chart's own columns — a moving average, a VWAP, a band edge, a
    /// forecast.
    ///
    /// <para><b>Values are indexed like the bars</b>, not like the window: <c>values[i]</c> belongs to
    /// <c>bars[i]</c>, and this takes the slice. That is the alignment a unit already has, since it
    /// computed the average as the bars arrived — and it means panning cannot slide an overlay off its
    /// own prices, which is what happens the moment two arrays are trimmed independently.</para>
    ///
    /// <para>Non-finite values are skipped rather than plotted, so the warm-up of an average starts
    /// late instead of dragging the line to zero.</para>
    /// </summary>
    public static void Overlay(
        IRenderSurface surface,
        string name,
        IReadOnlyList<double>? values,
        ChartWindow window,
        PlotRange range,
        SeriesOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (values is null || values.Count == 0 || !range.IsValid) return;

        if (options.Thickness <= 0d) options = SeriesOptions.Default;

        window = window.IsEmpty ? ChartWindow.All(values.Count) : window.ClampedTo(values.Count);
        if (window.IsEmpty) return;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var column = area.Width / window.Count;
        surface.SetStyle(new RenderStyle(
            surface.Theme(options.Color), options.Thickness, options.Alpha, options.Dashed));

        using var series = surface.Series(name ?? string.Empty, options.Kind);
        for (var index = window.First; index <= window.Last && index < values.Count; index++)
        {
            var value = values[index];
            if (!double.IsFinite(value)) continue;

            surface.Push(Center(area, window, index, column), area.ToY(value, range));
        }
    }

    /// <summary>The X of a bar's column centre. Candles occupy a column each, so they sit half a column
    /// in from where an evenly-spaced point series would put them — and an overlay drawn on the point
    /// spacing is visibly half a candle out of step with the bar it describes.</summary>
    internal static double Center(PlotArea area, ChartWindow window, int index, double column) =>
        area.X + ((index - window.First + 0.5d) * column);

    private static void Columns(
        IRenderSurface surface,
        Func<int, Ohlc> read,
        ChartWindow window,
        ChartSeriesOptions options,
        PlotRange range,
        PlotArea area,
        double column,
        double body,
        RenderColor bullish,
        RenderColor bearish)
    {
        for (var index = window.First; index <= window.Last; index++)
        {
            var bar = read(index);
            if (!bar.IsFinite) continue;

            var up = bar.Close >= bar.Open;
            var color = up ? bullish : bearish;
            var center = Center(area, window, index, column);

            surface.SetStyle(new RenderStyle(color, options.Thickness));

            var high = area.ToY(bar.High, range);
            var low = area.ToY(bar.Low, range);
            var open = area.ToY(bar.Open, range);
            var close = area.ToY(bar.Close, range);

            if (options.Style == ChartStyle.Bars)
            {
                surface.Line(center, high, center, low);
                surface.Line(center - (body / 2d), open, center, open);
                surface.Line(center, close, center + (body / 2d), close);
                continue;
            }

            surface.Line(center, high, center, low);

            var top = Math.Min(open, close);
            // A doji has no body height at all; give it a hairline so the bar does not vanish.
            var height = Math.Max(Math.Abs(close - open), 1d);
            var hollow = options.Style == ChartStyle.HollowCandles && up;
            surface.Rect(center - (body / 2d), top, body, height, filled: !hollow);
        }
    }

    private static void Joined(
        IRenderSurface surface,
        Func<int, Ohlc> read,
        ChartWindow window,
        ChartSeriesOptions options,
        PlotRange range,
        PlotArea area,
        double column)
    {
        var kind = options.Style == ChartStyle.Area ? RenderSeriesKind.Area : RenderSeriesKind.Line;
        surface.SetStyle(new RenderStyle(
            surface.Theme(RenderThemeColor.Accent),
            options.Thickness,
            options.Style == ChartStyle.Area ? Math.Max(options.FillAlpha, 0.5d) : 1d));

        using var series = surface.Series("Price", kind);
        for (var index = window.First; index <= window.Last; index++)
        {
            var close = read(index).Close;
            if (!double.IsFinite(close)) continue;

            surface.Push(Center(area, window, index, column), area.ToY(close, range));
        }
    }

    private static void Baseline(
        IRenderSurface surface,
        Func<int, Ohlc> read,
        ChartWindow window,
        ChartSeriesOptions options,
        PlotRange range,
        PlotArea area,
        double column,
        double baseline,
        RenderColor bullish,
        RenderColor bearish)
    {
        var zero = area.ToY(baseline, range);

        surface.SetStyle(new RenderStyle(
            surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.8d, Dashed: true));
        surface.Line(area.X, zero, area.Right, zero);

        var previous = double.NaN;
        var previousX = 0d;

        for (var index = window.First; index <= window.Last; index++)
        {
            var close = read(index).Close;
            if (!double.IsFinite(close)) continue;

            var x = Center(area, window, index, column);
            var y = area.ToY(close, range);
            var color = close >= baseline ? bullish : bearish;

            // Filled to the reference rather than to the floor: the shaded height IS the distance from
            // the baseline, which is the only quantity this style exists to show.
            surface.SetStyle(new RenderStyle(color, Alpha: Math.Max(options.FillAlpha, 0.04d)));
            surface.Rect(x - (column / 2d), Math.Min(y, zero), Math.Max(column, 1d), Math.Abs(y - zero));

            if (double.IsFinite(previous))
            {
                surface.SetStyle(new RenderStyle(color, options.Thickness));
                surface.Line(previousX, previous, x, y);
            }

            previous = y;
            previousX = x;
        }
    }

    private static PlotRange Fold(Func<int, Ohlc> read, ChartWindow window, ChartStyle style, double baseline)
    {
        var range = PlotRange.Empty;
        var wicks = style is ChartStyle.Candles or ChartStyle.HollowCandles
            or ChartStyle.Bars or ChartStyle.HeikinAshi;

        for (var index = window.First; index <= window.Last; index++)
        {
            var bar = read(index);
            if (wicks)
            {
                range = range.Include(bar.High);
                range = range.Include(bar.Low);
                continue;
            }

            range = range.Include(bar.Close);
        }

        return style == ChartStyle.Baseline ? range.Include(baseline) : range;
    }

    /// <summary>
    /// The window's bars, averaged into each other.
    ///
    /// <para>Close is the bar's own mean; open is the midpoint of the previous Heikin-Ashi candle, so a
    /// body only turns once the move has actually turned. Allocated per frame because it is derived
    /// data the caller does not hold — bounded by the window, so it does not grow with the history.</para>
    /// </summary>
    private static Ohlc[] HeikinAshi(IReadOnlyList<OhlcvBar> bars, ChartWindow window)
    {
        var painted = new Ohlc[window.Count];
        var previousOpen = double.NaN;
        var previousClose = double.NaN;

        for (var index = Math.Max(0, window.First - HeikinAshiWarmUp); index <= window.Last; index++)
        {
            var bar = bars[index];
            var close = (bar.Open + bar.High + bar.Low + bar.Close) / 4d;
            var open = double.IsFinite(previousOpen) && double.IsFinite(previousClose)
                ? (previousOpen + previousClose) / 2d
                : (bar.Open + bar.Close) / 2d;

            previousOpen = open;
            previousClose = close;

            if (index >= window.First)
            {
                painted[index - window.First] = new Ohlc(
                    open,
                    Math.Max(bar.High, Math.Max(open, close)),
                    Math.Min(bar.Low, Math.Min(open, close)),
                    close);
            }
        }

        return painted;
    }

    /// <summary>Four prices, so one drawing loop serves both the real bars and the transformed ones.</summary>
    private readonly record struct Ohlc(double Open, double High, double Low, double Close)
    {
        internal static Ohlc Of(OhlcvBar bar) => new(bar.Open, bar.High, bar.Low, bar.Close);

        internal bool IsFinite =>
            double.IsFinite(Open) && double.IsFinite(High)
            && double.IsFinite(Low) && double.IsFinite(Close);
    }
}
