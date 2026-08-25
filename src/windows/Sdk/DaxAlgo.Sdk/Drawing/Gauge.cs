namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a bounded meter is drawn.</summary>
/// <param name="Minimum">Left end of the scale.</param>
/// <param name="Maximum">Right end.</param>
/// <param name="Diverging">Whether the bar grows from the centre rather than the left. True for a
/// signed quantity — an imbalance at −0.4 and one at +0.4 should not look alike.</param>
/// <param name="Label">Caption above the bar.</param>
/// <param name="Format">Numeric format for the readout.</param>
/// <param name="ShowScale">Whether to print the end values.</param>
public readonly record struct GaugeOptions(
    double Minimum = -1d,
    double Maximum = 1d,
    bool Diverging = true,
    string? Label = null,
    string? Format = null,
    bool ShowScale = true)
{
    /// <summary>The intended defaults: a signed meter from −1 to 1.</summary>
    public static GaugeOptions Default { get; } = new(Minimum: -1d, Maximum: 1d);

    /// <summary>A 0…1 meter that grows from the left — for a confidence, a fill ratio, a share.</summary>
    public static GaugeOptions Ratio(string? label = null) =>
        new(Minimum: 0d, Maximum: 1d, Diverging: false, Label: label);

    /// <summary>A 0…100 meter, for an oscillator reading.</summary>
    public static GaugeOptions Percent(string? label = null) =>
        new(Minimum: 0d, Maximum: 100d, Diverging: false, Label: label);
}

/// <summary>
/// A horizontal meter for one bounded number — order-book imbalance, VPIN, a regime score, a model's
/// confidence, how much of a position is filled.
///
/// <para>The picture for a quantity whose <b>position within its range</b> is the point. Printed as a
/// number, 0.62 means nothing without knowing what the scale is; drawn as a meter, it is legible in the
/// time it takes to glance at it, and a strategy's live panel is read in glances.</para>
/// </summary>
public static class Gauge
{
    /// <summary>Draws the meter into an area.</summary>
    public static void Draw(
        IRenderSurface surface,
        double value,
        GaugeOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (options.Maximum <= options.Minimum) options = GaugeOptions.Default with
        {
            Label = options.Label, Format = options.Format,
        };

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var inner = area.Inset(6d, 4d);
        if (!inner.IsValid) return;

        var track = inner;
        if (!string.IsNullOrEmpty(options.Label))
        {
            var (caption, rest) = inner.SplitTop(14d);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));
            surface.Text(caption.X, caption.Y + 10d, options.Label!);
            track = rest;
        }

        if (options.ShowScale)
        {
            var (bar, scale) = track.SplitBottom(11d);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 8.5d, Alpha: 0.8d));
            surface.Text(scale.X, scale.Bottom - 1d, Format(options.Minimum, options.Format));
            surface.Text(Math.Max(scale.X, scale.Right - 28d), scale.Bottom - 1d, Format(options.Maximum, options.Format));
            track = bar;
        }

        track = track.Inset(0d, Math.Max(0d, (track.Height - 12d) / 2d));
        if (!track.IsValid) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Surface), Alpha: 0.7d));
        surface.Rect(track.X, track.Y, track.Width, track.Height);

        var clamped = double.IsFinite(value) ? Math.Clamp(value, options.Minimum, options.Maximum) : options.Minimum;
        var span = options.Maximum - options.Minimum;
        var fraction = (clamped - options.Minimum) / span;

        if (options.Diverging)
        {
            // Zero is wherever it falls in the range, which is not always the middle: a gauge from −1 to
            // 3 with its origin drawn at the centre would report the wrong sign for a third of its scale.
            var origin = (0d - options.Minimum) / span;
            var originX = track.X + (Math.Clamp(origin, 0d, 1d) * track.Width);
            var valueX = track.X + (fraction * track.Width);

            surface.SetStyle(new RenderStyle(
                surface.Theme(clamped >= 0d ? RenderThemeColor.Bullish : RenderThemeColor.Bearish), Alpha: 0.9d));
            surface.Rect(Math.Min(originX, valueX), track.Y, Math.Max(1d, Math.Abs(valueX - originX)), track.Height);

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d));
            surface.Line(originX, track.Y, originX, track.Bottom);
        }
        else
        {
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), Alpha: 0.9d));
            surface.Rect(track.X, track.Y, Math.Max(1d, fraction * track.Width), track.Height);
        }

        Plot.Frame(surface, track, 0.4d);

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 10d));
        surface.Text(track.X + 4d, track.Y + track.Height - 2d, Format(value, options.Format));
    }

    private static string Format(double value, string? format) =>
        double.IsFinite(value)
            ? value.ToString(format ?? "0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
}
