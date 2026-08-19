using System.Globalization;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>An inclusive numeric range, and the arithmetic every drawing routine repeats.</summary>
/// <param name="Minimum">Lower bound.</param>
/// <param name="Maximum">Upper bound.</param>
public readonly record struct PlotRange(double Minimum, double Maximum)
{
    /// <summary>A range that has not been given any values yet.</summary>
    public static PlotRange Empty { get; } = new(double.PositiveInfinity, double.NegativeInfinity);

    /// <summary>True once at least one finite value has been folded in and the bounds are usable.</summary>
    public bool IsValid => double.IsFinite(Minimum) && double.IsFinite(Maximum) && Maximum > Minimum;

    public double Span => Maximum - Minimum;

    /// <summary>Widens to include a value, ignoring non-finite input rather than poisoning the range.</summary>
    public PlotRange Include(double value) => double.IsFinite(value)
        ? new PlotRange(Math.Min(Minimum, value), Math.Max(Maximum, value))
        : this;

    /// <summary>
    /// Pads by a fraction of the span so data does not sit flush against the panel edge, and gives a
    /// flat range a usable width — a series of identical prices would otherwise be a zero-height
    /// range that nothing can be plotted against.
    /// </summary>
    public PlotRange Padded(double fraction = 0.05d)
    {
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum))
            return this;

        var span = Maximum - Minimum;
        if (span <= 0d)
        {
            var flat = Math.Abs(Maximum) is var magnitude and > 0d ? magnitude * 0.001d : 1d;
            return new PlotRange(Maximum - flat, Maximum + flat);
        }

        var pad = span * fraction;
        return new PlotRange(Minimum - pad, Maximum + pad);
    }
}

/// <summary>
/// The shared bits of plotting: ranges, gridlines, axis labels and a crosshair.
///
/// <para>These are pure functions over <see cref="IRenderSurface"/> — no WPF, no host types — so a
/// sandboxed visualizer, a strategy's own picture and a host-composed panel all draw the same
/// furniture from the same code.</para>
/// </summary>
public static class Plot
{
    /// <summary>Folds a sequence into a range, skipping non-finite values.</summary>
    public static PlotRange RangeOf<T>(IReadOnlyList<T> items, Func<T, double> select)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(select);

        var range = PlotRange.Empty;
        for (var index = 0; index < items.Count; index++)
            range = range.Include(select(items[index]));
        return range;
    }

    /// <summary>
    /// Draws horizontal gridlines with right-aligned value labels, and declares the Y axis.
    ///
    /// <para>Ticks are chosen on a 1/2/5 progression so the labels land on numbers a human would
    /// have picked, rather than on whatever the span divided by a fixed count happens to be.</para>
    /// </summary>
    public static void HorizontalGrid(
        IRenderSurface surface,
        PlotRange range,
        int approximateLines = 5,
        string? format = null,
        double labelWidth = 56d)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!range.IsValid || approximateLines <= 0)
            return;

        var viewport = surface.Viewport;
        if (viewport.Width <= 0d || viewport.Height <= 0d)
            return;

        var step = NiceStep(range.Span / approximateLines);
        if (!double.IsFinite(step) || step <= 0d)
            return;

        var grid = surface.Theme(RenderThemeColor.Grid);
        var text = surface.Theme(RenderThemeColor.TextSecondary);
        var first = Math.Ceiling(range.Minimum / step) * step;

        for (var value = first; value <= range.Maximum; value += step)
        {
            var y = ToY(value, range, viewport.Height);
            surface.SetStyle(new RenderStyle(grid, Thickness: 1d, Alpha: 0.55d));
            surface.Line(0d, y, viewport.Width - labelWidth, y);

            surface.SetStyle(new RenderStyle(text, FontSize: 10d));
            surface.Text(viewport.Width - labelWidth + 4d, y + 4d, Format(value, format));
        }
    }

    /// <summary>
    /// Draws a crosshair at the pointer with a value readout, and does nothing when the pointer is
    /// elsewhere — so a visualizer can call it unconditionally.
    /// </summary>
    public static void Crosshair(
        IRenderSurface surface,
        PlotRange verticalRange,
        string? format = null)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var cursor = surface.Cursor;
        if (!cursor.IsInside)
            return;

        var viewport = surface.Viewport;
        if (viewport.Width <= 0d || viewport.Height <= 0d)
            return;

        var line = surface.Theme(RenderThemeColor.Border);
        surface.SetStyle(new RenderStyle(line, Thickness: 1d, Alpha: 0.8d, Dashed: true));
        surface.Line(cursor.X, 0d, cursor.X, viewport.Height);
        surface.Line(0d, cursor.Y, viewport.Width, cursor.Y);

        if (!verticalRange.IsValid)
            return;

        var value = FromY(cursor.Y, verticalRange, viewport.Height);
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 10d));
        surface.Text(4d, cursor.Y - 3d, Format(value, format));
    }

    /// <summary>Pixel Y for a value, top-down, for routines drawing in panel pixel space.</summary>
    public static double ToY(double value, PlotRange range, double height) =>
        range.IsValid && height > 0d
            ? height - ((value - range.Minimum) / range.Span * height)
            : 0d;

    /// <summary>The inverse of <see cref="ToY"/>, for turning a cursor position back into a value.</summary>
    public static double FromY(double y, PlotRange range, double height) =>
        range.IsValid && height > 0d
            ? range.Minimum + ((height - y) / height * range.Span)
            : double.NaN;

    /// <summary>
    /// Rounds a raw step up to the nearest 1, 2 or 5 times a power of ten — the progression that
    /// produces axis labels a person would have chosen.
    /// </summary>
    public static double NiceStep(double rawStep)
    {
        if (!double.IsFinite(rawStep) || rawStep <= 0d)
            return 0d;

        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(rawStep)));
        var normalized = rawStep / magnitude;
        var nice = normalized switch
        {
            <= 1d => 1d,
            <= 2d => 2d,
            <= 5d => 5d,
            _ => 10d,
        };
        return nice * magnitude;
    }

    private static string Format(double value, string? format) =>
        value.ToString(format ?? "0.####", CultureInfo.InvariantCulture);
}
