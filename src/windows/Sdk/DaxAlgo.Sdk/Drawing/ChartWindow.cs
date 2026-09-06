namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// Which slice of a bar history is on screen: the first index drawn, and how many.
///
/// <para><b>This is the piece the library was missing, and it is why every generated chart showed
/// whatever happened to be in the buffer.</b> The gesture contract says to apply
/// <see cref="RenderViewport.Zoom"/> to your data range and <see cref="RenderViewport.PanX"/> to
/// which slice you show — but no widget did it, so a unit either ignored the wheel entirely or
/// invented its own arithmetic and got the clamping wrong at the ends. <see cref="Of"/> is that
/// arithmetic, once.</para>
///
/// <para>Indices are into the caller's bar list, not into the window, so a window travels with the
/// data it came from: <c>bars[window.First]</c> is the leftmost bar drawn and
/// <c>bars[window.Last]</c> the rightmost. The right edge is the newest bar until somebody drags,
/// which is what makes a live chart follow the market on its own.</para>
/// </summary>
/// <param name="First">Index of the leftmost bar drawn.</param>
/// <param name="Count">How many bars are drawn.</param>
public readonly record struct ChartWindow(int First, int Count)
{
    /// <summary>The fewest bars a zoom is allowed to reach. Below this a chart stops being a chart:
    /// three fat candles say nothing about a market, and the wheel becomes a way to destroy the
    /// picture rather than to inspect it.</summary>
    public const int MinimumBars = 8;

    /// <summary>How many bars an unzoomed chart shows when nobody says otherwise.</summary>
    public const int DefaultBars = 240;

    /// <summary>No bars. What every routine here returns rather than drawing an empty picture.</summary>
    public static ChartWindow None { get; }

    /// <summary>True when there is nothing to draw.</summary>
    public bool IsEmpty => Count <= 0;

    /// <summary>Index of the rightmost bar drawn — the newest one, unless the viewer has panned back.</summary>
    public int Last => First + Count - 1;

    /// <summary>True when an index falls inside the window, which is the guard before mapping one to
    /// an X coordinate: a marker on a bar that scrolled off must not be drawn at the edge as though
    /// it belonged there.</summary>
    public bool Contains(int index) => Count > 0 && index >= First && index <= Last;

    /// <summary>The whole history — the window a widget uses when the caller has not asked for one.</summary>
    public static ChartWindow All(int barCount) => barCount <= 0 ? None : new ChartWindow(0, barCount);

    /// <summary>
    /// The slice the viewer has asked for, from the wheel and the drag.
    ///
    /// <para><b>Zoom divides the count, never the coordinates.</b> 240 bars at zoom 2 is 120 bars at
    /// the same size, which is what zooming in means on a chart; scaling the drawing instead
    /// magnifies the candles, the text and the line widths together and reads as a bug.</para>
    ///
    /// <para><b>Pan is measured in bars, not pixels</b>, by dividing the accumulated drag by the width
    /// one column happens to have. So a drag moves the data under the pointer by the amount the
    /// pointer moved, at every zoom level — which is the difference between a chart that feels
    /// attached to the mouse and one that crawls when you zoom in.</para>
    ///
    /// <para>Clamped at both ends: the newest bar cannot be dragged off the right, and there is
    /// nothing to the left of the first bar. A viewer who flings the chart lands on the end of the
    /// history rather than on an empty panel they cannot recover from.</para>
    /// </summary>
    /// <param name="surface">The surface whose viewport carries the gestures.</param>
    /// <param name="barCount">How many bars the caller holds.</param>
    /// <param name="maximumBars">How many to show unzoomed.</param>
    /// <param name="area">Where the chart is drawn, so a drag can be converted into bars. Omitted,
    /// the whole panel is assumed.</param>
    public static ChartWindow Of(
        IRenderSurface surface,
        int barCount,
        int maximumBars = DefaultBars,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (barCount <= 0) return None;
        if (maximumBars <= 0) maximumBars = DefaultBars;

        var viewport = surface.Viewport;
        var zoom = double.IsFinite(viewport.Zoom) && viewport.Zoom > 0d ? viewport.Zoom : 1d;

        var visible = (int)Math.Round(maximumBars / zoom);
        visible = Math.Clamp(visible, Math.Min(MinimumBars, barCount), barCount);

        if (!area.IsValid) area = PlotArea.Of(surface);
        var column = area.IsValid ? area.Width / visible : 0d;

        // Positive PanX means the viewer dragged right, and dragging right on a chart pulls older
        // bars into view — so the first index moves BACK, which is why this subtracts.
        var pan = double.IsFinite(viewport.PanX) ? viewport.PanX : 0d;
        var shift = column > 0d ? (int)Math.Round(pan / column) : 0;

        return new ChartWindow(Math.Clamp(barCount - visible - shift, 0, barCount - visible), visible);
    }

    /// <summary>
    /// The same window, made safe for a list of <paramref name="barCount"/> bars.
    ///
    /// <para>A window is held across frames by anything that keeps one, and the list it indexes grows
    /// on every bar and is trimmed when the buffer is bounded. This is the guard that turns that into
    /// a shorter window rather than an <c>IndexOutOfRangeException</c> on the render thread.</para>
    /// </summary>
    public ChartWindow ClampedTo(int barCount)
    {
        if (barCount <= 0 || Count <= 0) return None;

        var first = Math.Clamp(First, 0, barCount - 1);
        return new ChartWindow(first, Math.Clamp(Count, 0, barCount - first));
    }
}
