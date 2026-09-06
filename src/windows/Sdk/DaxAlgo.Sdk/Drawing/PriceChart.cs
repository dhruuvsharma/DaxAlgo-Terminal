using System.Globalization;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How the chart is put together.</summary>
/// <param name="Symbol">What is being charted, written into the legend. Null leaves it out.</param>
/// <param name="Interval">The bar interval, written after the symbol. Null reads it off the bars.</param>
/// <param name="Style">Candles, hollow candles, OHLC bars, line, area, baseline or Heikin-Ashi.</param>
/// <param name="MaximumBars">How many bars an unzoomed chart shows. The wheel divides this.</param>
/// <param name="ShowVolume">Whether volume is drawn along the floor of the price pane.</param>
/// <param name="VolumeShare">How much of the price pane's height the tallest volume bar reaches.</param>
/// <param name="ShowLegend">Whether the header row of symbol, interval and OHLC is drawn.</param>
/// <param name="ShowTimeAxis">Whether the time strip and its gridlines are drawn.</param>
/// <param name="ShowCrosshair">Whether hovering draws a crosshair with a price and a time tag.</param>
/// <param name="ShowLastPrice">Whether the last close gets a line across the chart and a tag.</param>
/// <param name="ShowWatermark">Whether the symbol is written faintly behind the chart. Off by default:
/// it is the one piece of furniture here that is decoration rather than information.</param>
/// <param name="PriceFormat">Numeric format for every price on the chart, so the gutter, the tags and
/// the legend cannot disagree about precision.</param>
/// <param name="ScaleWidth">Width of the price gutter.</param>
public readonly record struct PriceChartOptions(
    string? Symbol = null,
    string? Interval = null,
    ChartStyle Style = ChartStyle.Candles,
    int MaximumBars = ChartWindow.DefaultBars,
    bool ShowVolume = true,
    double VolumeShare = 0.18d,
    bool ShowLegend = true,
    bool ShowTimeAxis = true,
    bool ShowCrosshair = true,
    bool ShowLastPrice = true,
    bool ShowWatermark = false,
    string? PriceFormat = null,
    double ScaleWidth = 62d)
{
    /// <summary>The intended defaults — a candle chart with volume, a gutter, an axis, a legend, a last
    /// price and a crosshair. Written with explicit arguments because <c>new()</c> on a record struct
    /// binds the implicit parameterless constructor and lands every field on zero, which here would be
    /// a chart of no bars with every piece of furniture switched off.</summary>
    public static PriceChartOptions Default { get; } = new(
        Style: ChartStyle.Candles,
        MaximumBars: ChartWindow.DefaultBars,
        ShowVolume: true,
        VolumeShare: 0.18d,
        ShowLegend: true,
        ShowTimeAxis: true,
        ShowCrosshair: true,
        ShowLastPrice: true,
        ScaleWidth: 62d);
}

/// <summary>
/// One pane below the price — RSI, MACD, delta, position, anything on its own scale.
///
/// <para>A <see cref="SeriesData"/> and a height, deliberately: the pane is the same series a unit
/// would have drawn beside the chart, so nothing new has to be learned to put it underneath one. It
/// shares the chart's columns and its window, which is the whole point — an oscillator scrolled
/// independently of its own prices is worse than no oscillator.</para>
/// </summary>
/// <param name="Series">What to draw. <c>Options.Kind == RenderSeriesKind.Bars</c> makes it a
/// histogram, coloured by sign; anything else is a line.</param>
/// <param name="Height">Pixels. Shrunk proportionally, and dropped altogether, when the panel is too
/// short to give the price pane a usable height.</param>
/// <param name="Reference">A line to draw across the pane and fold into its scale — zero for a MACD,
/// 50 for an RSI. NaN leaves it out.</param>
public readonly record struct ChartPane(SeriesData Series, double Height = 72d, double Reference = double.NaN);

/// <summary>
/// What was drawn, and the mapping back into it.
///
/// <para><b>This is what keeps the control from being a black box.</b> A widget that draws a picture
/// and returns nothing forces the next thing onto a second, hand-rolled coordinate system that agrees
/// with the first only by accident. Hand this to <see cref="X"/> and <see cref="Y"/> and your own
/// annotation lands on the same bar and the same price the candles did.</para>
/// </summary>
/// <param name="Window">Which bars were drawn.</param>
/// <param name="Range">The price scale they were drawn against.</param>
/// <param name="Price">The price pane, gutter and axis excluded.</param>
/// <param name="Volume">The volume strip along the floor of the price pane, or
/// <see cref="PlotArea.None"/> when volume is off.</param>
/// <param name="Scale">The price gutter.</param>
/// <param name="Axis">The time strip, or <see cref="PlotArea.None"/> when the axis is off.</param>
/// <param name="HoveredIndex">The bar under the pointer, or -1 when the pointer is elsewhere. This is
/// the index a readout, a tooltip or a pinned annotation is written from.</param>
public readonly record struct ChartView(
    ChartWindow Window,
    PlotRange Range,
    PlotArea Price,
    PlotArea Volume,
    PlotArea Scale,
    PlotArea Axis,
    int HoveredIndex)
{
    /// <summary>Nothing was drawn — no bars, or no room. Returned rather than a half-valid view, so a
    /// caller cannot map coordinates against a chart that is not there.</summary>
    public static ChartView None { get; } = new(
        ChartWindow.None, PlotRange.Empty, PlotArea.None, PlotArea.None, PlotArea.None, PlotArea.None, -1);

    /// <summary>True when there is a chart to draw on top of.</summary>
    public bool IsValid => !Window.IsEmpty && Range.IsValid && Price.IsValid;

    /// <summary>True when the pointer is over a bar.</summary>
    public bool IsHovering => HoveredIndex >= 0;

    /// <summary>Width of one bar's column.</summary>
    public double ColumnWidth => Window.Count > 0 && Price.IsValid ? Price.Width / Window.Count : 0d;

    /// <summary>The X of a bar's column centre. Check <c>Window.Contains(index)</c> first: an index
    /// that scrolled off maps to a coordinate outside the plot, and drawing it there says the event
    /// happened at the edge of the screen.</summary>
    public double X(int index) => Price.X + ((index - Window.First + 0.5d) * ColumnWidth);

    /// <summary>The Y of a price.</summary>
    public double Y(double price) => Price.ToY(price, Range);

    /// <summary>The price at a Y — for turning the pointer, or a pinned click, back into a number.</summary>
    public double PriceAt(double y) => Range.IsValid && Price.Height > 0d
        ? Range.Minimum + ((Price.Bottom - y) / Price.Height * Range.Span)
        : double.NaN;

    /// <summary>The bar at an X, clamped into the window. -1 when there is no chart.</summary>
    public int IndexAt(double x)
    {
        var column = ColumnWidth;
        if (column <= 0d || !double.IsFinite(x)) return -1;

        return Math.Clamp(Window.First + (int)Math.Floor((x - Price.X) / column), Window.First, Window.Last);
    }
}

/// <summary>
/// The chart a trader recognises: candles on a price gutter and a time axis, with volume along the
/// floor, the last price tagged, a legend that reads the bar under the pointer, overlays on the price
/// scale, panes underneath, and a crosshair that snaps to a bar and says where it is in both axes.
///
/// <para><b>Why this exists as a widget.</b> The library could already draw candles — and a candle
/// series on its own is not a chart. Everything that makes one readable was left to each unit to
/// invent: where the prices are written, what time a column is, how far the wheel zooms, which bar the
/// pointer is over. Every unit that tried got some of it wrong, and the ones that skipped it produced
/// a picture nobody could act on. This is that furniture, once, with the arithmetic in one place.</para>
///
/// <para><b>It answers the gestures</b>, which no other widget in the library does: the wheel changes
/// how many bars are on screen and the drag moves through the history, both via
/// <see cref="ChartWindow.Of"/>, so a chart drawn by one call is one a viewer can actually explore.</para>
///
/// <para>Overlays are <see cref="SeriesData"/>, markers are <see cref="Signal"/>, reference lines are
/// <see cref="Level"/> and a lower pane is a <see cref="ChartPane"/> around a series — the library's
/// own vocabulary rather than a private one, so what you know about drawing a series beside a chart is
/// what you need to draw one on it. Every overlay is indexed like the bars.</para>
///
/// <example><code>
/// var view = PriceChart.Draw(surface, _bars,
///     PriceChartOptions.Default with { Symbol = "ES", Style = ChartStyle.Candles },
///     overlays: [SeriesData.Line("EMA 20", _ema20), SeriesData.Line("EMA 50", _ema50, RenderThemeColor.Warning)],
///     markers: _fills,
///     levels: [new Level(_stop, "stop", RenderThemeColor.Bearish)]);
/// </code></example>
/// </summary>
public static class PriceChart
{
    /// <summary>Height of the legend row.</summary>
    public const double LegendHeight = 16d;

    /// <summary>The price pane is never squeezed below this to make room for panes. A chart that has
    /// given all its height away to its indicators has stopped being a chart.</summary>
    public const double MinimumPriceHeight = 60d;

    /// <summary>A pane thinner than this cannot be read, so it is dropped rather than drawn.</summary>
    public const double MinimumPaneHeight = 22d;

    /// <summary>
    /// Draws the chart and returns the mapping back into it.
    /// </summary>
    /// <param name="surface">The surface to draw onto.</param>
    /// <param name="bars">The history, oldest first. Every index below is an index into this.</param>
    /// <param name="options">Style and which furniture to draw.</param>
    /// <param name="overlays">Series drawn on the price scale — averages, bands, VWAP, a forecast.
    /// Indexed like <paramref name="bars"/>; non-finite values are gaps, so an average may start late.</param>
    /// <param name="markers">Entries, exits and events. <c>Signal.Index</c> is a bar index and
    /// <c>Signal.Value</c> the price; a non-finite value is drawn at the bar's close.</param>
    /// <param name="levels">Horizontal reference lines, each also tagged in the gutter.</param>
    /// <param name="panes">Panes stacked under the price, the first nearest to it.</param>
    /// <param name="area">Where the whole control goes. Omitted, the whole panel.</param>
    public static ChartView Draw(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar>? bars,
        PriceChartOptions options = default,
        IReadOnlyList<SeriesData>? overlays = null,
        IReadOnlyList<Signal>? markers = null,
        IReadOnlyList<Level>? levels = null,
        IReadOnlyList<ChartPane>? panes = null,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (options.MaximumBars <= 0 || options.ScaleWidth <= 0d) options = PriceChartOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return ChartView.None;

        if (bars is null || bars.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for bars…");
            return ChartView.None;
        }

        var scaleOptions = new PriceScaleOptions(options.ScaleWidth, Format: options.PriceFormat);
        var axisOptions = TimeAxisOptions.Default;

        // ── the furniture, outside in ───────────────────────────────────────────────────────────
        var body = area;
        var axisStrip = PlotArea.None;
        if (options.ShowTimeAxis && body.Height > axisOptions.Height * 3d)
            (axisStrip, body) = body.SplitBottom(axisOptions.Height);

        var (scale, inner) = body.SplitRight(Math.Min(options.ScaleWidth, body.Width));

        var legendStrip = PlotArea.None;
        if (options.ShowLegend && inner.Height > LegendHeight * 3d)
            (legendStrip, inner) = inner.SplitTop(LegendHeight);

        var wanted = 0d;
        if (panes is not null)
        {
            for (var index = 0; index < panes.Count; index++)
                wanted += Math.Max(panes[index].Height, MinimumPaneHeight);
        }

        var (paneStack, plot) = inner.SplitBottom(
            Math.Clamp(wanted, 0d, Math.Max(0d, inner.Height - MinimumPriceHeight)));

        if (!plot.IsValid) return ChartView.None;

        // ── what is on screen ───────────────────────────────────────────────────────────────────
        var window = ChartWindow.Of(surface, bars.Count, options.MaximumBars, plot);
        if (window.IsEmpty) return ChartView.None;

        var range = Fold(ChartSeries.RangeOf(bars, window, options.Style), overlays, window).Padded();
        if (!range.IsValid) return ChartView.None;

        var column = plot.Width / window.Count;
        var priceRegion = new PlotArea(plot.X, plot.Y, plot.Width + scale.Width, plot.Height);
        var bottom = paneStack.IsValid ? paneStack.Bottom : plot.Bottom;
        var timeRegion = new PlotArea(plot.X, plot.Y, plot.Width, Math.Max(0d, area.Bottom - plot.Y));

        var cursor = surface.Cursor;
        var hovered = cursor.IsInside && plot.Contains(cursor.X, cursor.Y) && column > 0d
            ? Math.Clamp(window.First + (int)Math.Floor((cursor.X - plot.X) / column), window.First, window.Last)
            : -1;

        var volume = options.ShowVolume && options.VolumeShare > 0d
            ? plot.SplitBottom(plot.Height * Math.Clamp(options.VolumeShare, 0.05d, 0.5d)).Taken
            : PlotArea.None;

        // ── the picture, back to front ──────────────────────────────────────────────────────────
        PriceScale.Draw(surface, range, scaleOptions, priceRegion);

        if (axisStrip.IsValid) TimeAxis.Draw(surface, bars, window, axisOptions, timeRegion);
        if (options.ShowWatermark) Watermark(surface, plot, options, bars);
        if (volume.IsValid) Volume(surface, bars, window, volume);

        ChartSeries.Draw(surface, bars, window, new ChartSeriesOptions(options.Style), range, plot);

        if (overlays is not null)
        {
            for (var index = 0; index < overlays.Count; index++)
            {
                var overlay = overlays[index];
                ChartSeries.Overlay(surface, overlay.Name, overlay.Values, window, range, overlay.Options, plot);
            }
        }

        if (levels is not null)
        {
            Levels.Draw(surface, levels, range, plot);
            for (var index = 0; index < levels.Count; index++)
            {
                var level = levels[index];
                PriceScale.Tag(surface, range, level.Value, null, level.Color, scaleOptions, priceRegion);
            }
        }

        Markers(surface, bars, markers, window, range, plot, column);

        if (options.ShowLastPrice) LastPrice(surface, bars[window.Last], range, plot, scaleOptions, priceRegion);
        if (paneStack.IsValid) Panes(surface, panes, window, paneStack, scale.Width, scaleOptions, wanted);
        if (legendStrip.IsValid) LegendRow(surface, bars, window, hovered, overlays, options, legendStrip);

        if (options.ShowCrosshair && hovered >= 0)
        {
            Crosshair(
                surface, bars, window, range, plot, bottom, hovered, column,
                scaleOptions, priceRegion, axisStrip.IsValid ? axisOptions : default, timeRegion, axisStrip.IsValid);
        }

        return new ChartView(window, range, plot, volume, scale, axisStrip, hovered);
    }

    /// <summary>Widens the price range to hold the overlays, so an average cannot run off the top of
    /// the chart it is drawn on. Only the visible slice counts — an old spike outside the window must
    /// not flatten everything on screen.</summary>
    private static PlotRange Fold(PlotRange range, IReadOnlyList<SeriesData>? overlays, ChartWindow window)
    {
        if (overlays is null) return range;

        for (var index = 0; index < overlays.Count; index++)
        {
            var values = overlays[index].Values;
            if (values is null) continue;

            for (var at = Math.Max(0, window.First); at <= window.Last && at < values.Count; at++)
                range = range.Include(values[at]);
        }

        return range;
    }

    /// <summary>
    /// Volume along the floor of the price pane, translucent, coloured by the bar's direction.
    ///
    /// <para>Overlaid rather than given a pane of its own. Volume is read <i>against</i> the price that
    /// made it — the tall bar matters because of the candle above it — and taking a fifth of the height
    /// away from the price to say so is a bad trade.</para>
    /// </summary>
    private static void Volume(IRenderSurface surface, IReadOnlyList<OhlcvBar> bars, ChartWindow window, PlotArea area)
    {
        var peak = 0d;
        for (var index = window.First; index <= window.Last; index++)
            peak = Math.Max(peak, bars[index].Volume);

        if (peak <= 0d) return;

        var column = area.Width / window.Count;
        var body = Math.Max(column * 0.72d, 1d);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var bearish = surface.Theme(RenderThemeColor.Bearish);

        for (var index = window.First; index <= window.Last; index++)
        {
            var bar = bars[index];
            if (bar.Volume <= 0L) continue;

            var height = bar.Volume / peak * area.Height;
            if (!double.IsFinite(height) || height <= 0d) continue;

            surface.SetStyle(new RenderStyle(bar.Close >= bar.Open ? bullish : bearish, Alpha: 0.4d));
            surface.Rect(
                ChartSeries.Center(area, window, index, column) - (body / 2d),
                area.Bottom - height,
                body,
                height);
        }
    }

    /// <summary>Entry and exit glyphs, offset off the bar rather than drawn on it — a triangle on top of
    /// the candle it refers to hides the bar the reader is checking it against.</summary>
    private static void Markers(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<Signal>? markers,
        ChartWindow window,
        PlotRange range,
        PlotArea area,
        double column)
    {
        if (markers is null || markers.Count == 0) return;

        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            if (!window.Contains(marker.Index) || marker.Index >= bars.Count) continue;

            var value = double.IsFinite(marker.Value) ? marker.Value : bars[marker.Index].Close;
            if (value < range.Minimum || value > range.Maximum) continue;

            var x = ChartSeries.Center(area, window, marker.Index, column);
            var offset = marker.Kind switch
            {
                SignalKind.Buy => 10d,
                SignalKind.Sell => -10d,
                _ => 0d,
            };

            var y = Math.Clamp(area.ToY(value, range) + offset, area.Y, area.Bottom);

            surface.SetStyle(new RenderStyle(surface.Theme(Signals.ColorOf(marker.Kind)), 7d));
            surface.Marker(x, y, Signals.ShapeOf(marker.Kind));

            if (string.IsNullOrEmpty(marker.Label)) continue;

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));
            surface.Text(x + 7d, y - 4d, marker.Label!);
        }
    }

    /// <summary>The last close, dashed across the chart and tagged in the gutter — the one number a
    /// glance at a chart is usually looking for.</summary>
    private static void LastPrice(
        IRenderSurface surface,
        OhlcvBar last,
        PlotRange range,
        PlotArea area,
        PriceScaleOptions scaleOptions,
        PlotArea priceRegion)
    {
        if (!double.IsFinite(last.Close) || last.Close < range.Minimum || last.Close > range.Maximum) return;

        var color = last.Close >= last.Open ? RenderThemeColor.Bullish : RenderThemeColor.Bearish;
        var y = area.ToY(last.Close, range);

        surface.SetStyle(new RenderStyle(surface.Theme(color), Thickness: 1d, Alpha: 0.55d, Dashed: true));
        surface.Line(area.X, y, area.Right, y);

        PriceScale.Tag(surface, range, last.Close, null, color, scaleOptions, priceRegion);
    }

    /// <summary>The panes, stacked under the price on the chart's own columns and window.</summary>
    private static void Panes(
        IRenderSurface surface,
        IReadOnlyList<ChartPane>? panes,
        ChartWindow window,
        PlotArea stack,
        double gutterWidth,
        PriceScaleOptions scaleOptions,
        double wanted)
    {
        if (panes is null || panes.Count == 0 || wanted <= 0d) return;

        var factor = Math.Min(1d, stack.Height / wanted);
        var top = stack.Y;

        for (var index = 0; index < panes.Count; index++)
        {
            var pane = panes[index];
            var height = Math.Max(pane.Height, MinimumPaneHeight) * factor;
            if (height < MinimumPaneHeight || top + height > stack.Bottom + 0.5d) return;

            var row = new PlotArea(stack.X, top, stack.Width, height);
            top += height;

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.5d));
            surface.Line(row.X, row.Y, row.Right, row.Y);

            var values = pane.Series.Values;
            if (values is null || values.Count == 0) continue;

            var range = PlotRange.Empty;
            var last = double.NaN;
            for (var at = window.First; at <= window.Last && at < values.Count; at++)
            {
                range = range.Include(values[at]);
                if (double.IsFinite(values[at])) last = values[at];
            }

            if (double.IsFinite(pane.Reference)) range = range.Include(pane.Reference);
            range = range.Padded(0.08d);
            if (!range.IsValid) continue;

            var inner = row.Inset(0d, 3d);
            if (double.IsFinite(pane.Reference))
                Levels.Draw(surface, [new Level(pane.Reference)], range, inner, alpha: 0.5d);

            if (pane.Series.Options.Kind == RenderSeriesKind.Bars)
                PaneHistogram(surface, values, window, range, inner);
            else
                ChartSeries.Overlay(surface, pane.Series.Name, values, window, range, pane.Series.Options, inner);

            Plot.Caption(surface, row, pane.Series.Name);

            PriceScale.Tag(
                surface, range, last, null, pane.Series.Options.Color, scaleOptions,
                new PlotArea(row.X, row.Y, row.Width + gutterWidth, row.Height));
        }
    }

    /// <summary>A pane's histogram, on the chart's columns and coloured by sign — the shape a MACD or a
    /// delta is read in, where the side of the baseline is the whole message.</summary>
    private static void PaneHistogram(
        IRenderSurface surface,
        IReadOnlyList<double> values,
        ChartWindow window,
        PlotRange range,
        PlotArea area)
    {
        var column = area.Width / window.Count;
        var body = Math.Max(column * 0.7d, 1d);
        var zero = Math.Clamp(0d, range.Minimum, range.Maximum);
        var baseline = area.ToY(zero, range);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var bearish = surface.Theme(RenderThemeColor.Bearish);

        for (var index = window.First; index <= window.Last && index < values.Count; index++)
        {
            var value = values[index];
            if (!double.IsFinite(value)) continue;

            var y = area.ToY(value, range);
            surface.SetStyle(new RenderStyle(value >= zero ? bullish : bearish, Alpha: 0.85d));
            surface.Rect(
                ChartSeries.Center(area, window, index, column) - (body / 2d),
                Math.Min(y, baseline),
                body,
                Math.Max(Math.Abs(y - baseline), 1d));
        }
    }

    /// <summary>
    /// The header: what this is, then the bar's own numbers.
    ///
    /// <para><b>It reads the hovered bar, not the last one.</b> That is the behaviour that makes a
    /// chart inspectable rather than merely current — the pointer becomes a readout, and the reader
    /// stops having to guess a value off the gutter.</para>
    /// </summary>
    private static void LegendRow(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar> bars,
        ChartWindow window,
        int hovered,
        IReadOnlyList<SeriesData>? overlays,
        PriceChartOptions options,
        PlotArea strip)
    {
        var bar = bars[hovered >= 0 ? hovered : window.Last];
        var up = bar.Close >= bar.Open;
        var baseline = strip.Y + strip.Height - 4d;
        var x = strip.X + 4d;

        var head = Head(options, bar);
        if (head.Length > 0)
        {
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 11d));
            surface.Text(x, baseline, head);
            x += (head.Length * 11d * 0.62d) + 10d;
        }

        var ohlc =
            $"O {Format(bar.Open, options.PriceFormat)}  H {Format(bar.High, options.PriceFormat)}  "
            + $"L {Format(bar.Low, options.PriceFormat)}  C {Format(bar.Close, options.PriceFormat)}";

        surface.SetStyle(new RenderStyle(
            surface.Theme(up ? RenderThemeColor.Bullish : RenderThemeColor.Bearish), FontSize: 10.5d));
        surface.Text(x, baseline, ohlc);
        x += (ohlc.Length * 10.5d * 0.62d) + 10d;

        var change = bar.Close - bar.Open;
        var percent = Math.Abs(bar.Open) > 0d ? change / bar.Open * 100d : 0d;
        if (double.IsFinite(change) && double.IsFinite(percent))
        {
            var text = change.ToString("+0.####;-0.####;0", CultureInfo.InvariantCulture)
                + " (" + percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%)";

            surface.SetStyle(new RenderStyle(
                surface.Theme(up ? RenderThemeColor.Bullish : RenderThemeColor.Bearish), FontSize: 10.5d));
            surface.Text(x, baseline, text);
            x += (text.Length * 10.5d * 0.62d) + 12d;
        }

        // The overlay key, from the same SeriesData the lines were drawn from, so the swatches cannot
        // disagree with them.
        if (overlays is not null && x < strip.Right - 40d)
            Legend.Draw(surface, overlays, new PlotArea(x - 6d, strip.Y, strip.Right - x + 6d, strip.Height));
    }

    /// <summary>The symbol and the interval, the interval read off the bars when nobody said.</summary>
    private static string Head(PriceChartOptions options, OhlcvBar bar)
    {
        var interval = options.Interval ?? Interval(bar.Size);
        if (string.IsNullOrEmpty(options.Symbol)) return interval;

        return $"{options.Symbol}  ·  {interval}";
    }

    private static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1H",
        BarSize.OneDay => "1D",
        _ => string.Empty,
    };

    /// <summary>The symbol behind the chart, faint enough to read the candles through. Off by default.</summary>
    private static void Watermark(
        IRenderSurface surface, PlotArea area, PriceChartOptions options, IReadOnlyList<OhlcvBar> bars)
    {
        var text = options.Symbol ?? Interval(bars[^1].Size);
        if (string.IsNullOrEmpty(text)) return;

        const double size = 30d;
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), Alpha: 0.07d, FontSize: size));
        surface.Text(
            Math.Max(area.X + 4d, area.CenterX - (text.Length * size * 0.62d / 2d)),
            area.CenterY,
            text);
    }

    /// <summary>
    /// The crosshair, snapped to the bar under the pointer.
    ///
    /// <para><b>Snapped, and that is the difference between a crosshair and a pair of lines.</b> A
    /// vertical rule at the raw pointer X sits between two candles and reads a time that no bar has;
    /// on the column centre it names the bar the legend is simultaneously reporting.</para>
    ///
    /// <para>The horizontal line stays where the pointer is, because that one is asking "what price is
    /// here" rather than "which bar is this" — and it is tagged in the gutter, where a price belongs.</para>
    /// </summary>
    private static void Crosshair(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar> bars,
        ChartWindow window,
        PlotRange range,
        PlotArea plot,
        double bottom,
        int hovered,
        double column,
        PriceScaleOptions scaleOptions,
        PlotArea priceRegion,
        TimeAxisOptions axisOptions,
        PlotArea timeRegion,
        bool hasAxis)
    {
        var cursor = surface.Cursor;
        var x = ChartSeries.Center(plot, window, hovered, column);

        surface.SetStyle(new RenderStyle(
            surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.85d, Dashed: true));
        surface.Line(x, plot.Y, x, bottom);
        surface.Line(plot.X, cursor.Y, plot.Right, cursor.Y);

        PriceScale.Tag(
            surface, range, Plot.FromY(cursor.Y - plot.Y, range, plot.Height), null,
            RenderThemeColor.Neutral, scaleOptions, priceRegion);

        if (!hasAxis) return;

        var stamp = bars[hovered].OpenTimeUtc;
        TimeAxis.Tag(
            surface, x, stamp.ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
            RenderThemeColor.Neutral, axisOptions, timeRegion);
    }

    private static string Format(double value, string? format) =>
        value.ToString(format ?? "0.####", CultureInfo.InvariantCulture);
}
