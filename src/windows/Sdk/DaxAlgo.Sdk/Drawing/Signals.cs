namespace DaxAlgo.Sdk.Drawing;

/// <summary>What a signal marker means. The direction, not the drawing.</summary>
public enum SignalKind
{
    /// <summary>Entered long, or a bullish cross.</summary>
    Buy = 0,

    /// <summary>Entered short, or a bearish cross.</summary>
    Sell = 1,

    /// <summary>Closed a position, either way.</summary>
    Exit = 2,

    /// <summary>Something worth marking that is neither a buy nor a sell — a regime change, a session
    /// boundary, a rejected setup.</summary>
    Note = 3,
}

/// <summary>One marked event.</summary>
/// <param name="Index">Position along the series.</param>
/// <param name="Value">Where on the value axis it sits — usually the price it happened at.</param>
/// <param name="Kind">Which direction it was.</param>
/// <param name="Label">Optional short text drawn beside it.</param>
public readonly record struct Signal(int Index, double Value, SignalKind Kind, string? Label = null);

/// <summary>How signal markers are drawn.</summary>
/// <param name="Size">Marker size hint passed through to the surface's own glyph.</param>
/// <param name="ShowLabels">Whether to draw the labels beside the markers.</param>
/// <param name="Alpha">0 transparent to 1 opaque.</param>
public readonly record struct SignalOptions(
    double Size = 7d,
    bool ShowLabels = true,
    double Alpha = 1d)
{
    /// <summary>The intended defaults.</summary>
    public static SignalOptions Default { get; } = new(Size: 7d, Alpha: 1d);
}

/// <summary>
/// Entry, exit and cross markers.
///
/// <para><b>Shape carries the meaning, colour only reinforces it.</b> Roughly one man in twelve cannot
/// separate the bullish and bearish roles reliably, and the signals are the one thing on a strategy's
/// chart that has to read at a glance — so buys are triangles, sells are diamonds, exits are crosses,
/// whatever the theme does with the colours. Getting that convention right at every call site is exactly
/// what a shared widget is for.</para>
///
/// <para>It is also what keeps a picture honest: a chart that shows a strategy's trades where the
/// strategy actually took them is the difference between a reviewable strategy and a plausible one.</para>
/// </summary>
public static class Signals
{
    /// <summary>Draws markers for each signal, positioned against a shared range.</summary>
    public static void Draw(
        IRenderSurface surface,
        IReadOnlyList<Signal>? signals,
        int count,
        PlotRange range,
        SignalOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (signals is null || signals.Count == 0 || count <= 0 || !range.IsValid) return;

        if (options.Size <= 0d) options = SignalOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i];
            if (!double.IsFinite(signal.Value)) continue;

            var x = area.ToX(signal.Index, count);
            var y = area.ToY(signal.Value, range);

            surface.SetStyle(new RenderStyle(
                surface.Theme(ColorOf(signal.Kind)), options.Size, options.Alpha));
            surface.Marker(x, y, ShapeOf(signal.Kind));

            if (options.ShowLabels && !string.IsNullOrEmpty(signal.Label))
            {
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));
                surface.Text(x + 6d, y - 4d, signal.Label!);
            }
        }
    }

    /// <summary>
    /// The convention, exposed so a caller drawing its own marker still gets the right glyph.
    ///
    /// <para>Triangle up for a buy, diamond for a sell, cross for an exit, circle for a note. Distinct in
    /// silhouette, not merely in colour.</para>
    /// </summary>
    public static RenderMarkerShape ShapeOf(SignalKind kind) => kind switch
    {
        SignalKind.Buy => RenderMarkerShape.Triangle,
        SignalKind.Sell => RenderMarkerShape.Diamond,
        SignalKind.Exit => RenderMarkerShape.Cross,
        _ => RenderMarkerShape.Circle,
    };

    /// <summary>The theme role for a signal kind.</summary>
    public static RenderThemeColor ColorOf(SignalKind kind) => kind switch
    {
        SignalKind.Buy => RenderThemeColor.Bullish,
        SignalKind.Sell => RenderThemeColor.Bearish,
        SignalKind.Exit => RenderThemeColor.Warning,
        _ => RenderThemeColor.Neutral,
    };
}
