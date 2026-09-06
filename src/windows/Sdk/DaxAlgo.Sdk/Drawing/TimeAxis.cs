using System.Globalization;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How the time strip is drawn.</summary>
/// <param name="Height">Strip height in pixels, taken off the bottom of the area it is given.</param>
/// <param name="ApproximateTicks">Roughly how many labels to write across the strip.</param>
/// <param name="Format">Label format. Null adapts to how much time is on screen, which is almost always
/// what you want — an intraday chart wants the clock and a yearly one wants the month.</param>
/// <param name="ShowGrid">Whether to rule a vertical gridline up through the plot at each label.</param>
/// <param name="FontSize">Label size.</param>
public readonly record struct TimeAxisOptions(
    double Height = 18d,
    int ApproximateTicks = 6,
    string? Format = null,
    bool ShowGrid = true,
    double FontSize = 10d)
{
    /// <summary>The intended defaults. Written with explicit arguments because <c>new()</c> on a record
    /// struct binds the implicit parameterless constructor and lands every field on zero — a strip with
    /// no height and no labels in it.</summary>
    public static TimeAxisOptions Default { get; } =
        new(Height: 18d, ApproximateTicks: 6, ShowGrid: true, FontSize: 10d);
}

/// <summary>
/// The time strip along the bottom, labelled on round times.
///
/// <para><b>Labels land on bar boundaries, not on even divisions of the panel.</b> Bars are drawn one
/// per column whatever the clock did between them — which is how a chart survives a weekend, a
/// halt or an illiquid hour without a gap the width of the screen — so the axis has to say which
/// column is 09:30 rather than assume the columns are evenly spaced in time. Dividing the span by six
/// and labelling six positions produces an axis that is confidently wrong across every gap in the
/// session, and it looks right.</para>
/// </summary>
public static class TimeAxis
{
    /// <summary>Height of a tag pill in the strip.</summary>
    public const double TagHeight = 15d;

    /// <summary>
    /// The steps an axis is allowed to label on: the round intervals a person reads a clock and a
    /// calendar in. Anything else — 7 minutes, 3 hours 20 — is a correct division of the span and an
    /// unreadable axis.
    /// </summary>
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
        TimeSpan.FromDays(90), TimeSpan.FromDays(365),
    ];

    /// <summary>
    /// Draws the strip and returns <b>the plot area above it</b>, so a caller lays a chart out by
    /// assignment rather than by arithmetic.
    /// </summary>
    /// <param name="surface">The surface to draw onto.</param>
    /// <param name="bars">The history the timestamps are read from.</param>
    /// <param name="window">Which bars are on screen. Omitted, all of them.</param>
    /// <param name="options">Height, tick count, format.</param>
    /// <param name="area">The chart region <b>including</b> the strip. Omitted, the whole panel.</param>
    public static PlotArea Draw(
        IRenderSurface surface,
        IReadOnlyList<OhlcvBar>? bars,
        ChartWindow window = default,
        TimeAxisOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (options.Height <= 0d || options.ApproximateTicks <= 0) options = TimeAxisOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotArea.None;

        var (strip, plot) = area.SplitBottom(Math.Min(options.Height, area.Height));
        if (bars is null || bars.Count == 0 || !plot.IsValid) return plot;

        window = window.IsEmpty ? ChartWindow.All(bars.Count) : window.ClampedTo(bars.Count);
        if (window.IsEmpty) return plot;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.7d));
        surface.Line(area.X, strip.Y, area.Right, strip.Y);

        surface.AxisX(window.First, window.Last);

        var step = StepFor(bars[window.Last].OpenTimeUtc - bars[window.First].OpenTimeUtc, options.ApproximateTicks);
        var column = plot.Width / window.Count;
        var written = 0;
        var occupied = double.NegativeInfinity;

        for (var index = window.First + 1; index <= window.Last; index++)
        {
            var stamp = bars[index].OpenTimeUtc;
            var previous = bars[index - 1].OpenTimeUtc;
            if (!Crossed(previous, stamp, step)) continue;

            var x = ChartSeries.Center(plot, window, index, column);
            var text = options.Format is null
                ? Label(stamp, step, previous.Date != stamp.Date)
                : stamp.ToString(options.Format, CultureInfo.InvariantCulture);

            // Estimated advance rather than a measured one: the surface has no text metrics by design.
            // Skipping a label that would collide is what keeps a zoomed-out axis readable instead of
            // turning it into a smear.
            var width = (text.Length * options.FontSize * 0.62d) + 8d;
            if (x - (width / 2d) < occupied) continue;
            occupied = x + (width / 2d);

            if (options.ShowGrid)
            {
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Grid), Thickness: 1d, Alpha: 0.4d));
                surface.Line(x, plot.Y, x, plot.Bottom);
            }

            Write(surface, strip, x, width, text, options);
            written++;
        }

        // A window narrower than one step of the ladder crosses nothing, and an unlabelled axis is a
        // strip of empty grey. Say where the ends are instead.
        if (written == 0 && strip.IsValid)
        {
            var first = bars[window.First].OpenTimeUtc;
            var text = Label(first, TimeSpan.Zero, dayChanged: true) + " " + first.ToString("HH:mm", CultureInfo.InvariantCulture);
            Write(surface, strip, plot.X + (strip.Width / 4d), (text.Length * options.FontSize * 0.62d) + 8d, text, options);
        }

        return plot;
    }

    /// <summary>
    /// A filled pill in the strip at one X — where the crosshair is, in words.
    /// </summary>
    /// <param name="surface">The surface to draw onto.</param>
    /// <param name="x">Where the pill is centred.</param>
    /// <param name="text">What it says.</param>
    /// <param name="color">Theme role for the fill.</param>
    /// <param name="options">Height and font, matching the strip it is drawn in.</param>
    /// <param name="area">The chart region including the strip, exactly as given to <see cref="Draw"/>.</param>
    public static void Tag(
        IRenderSurface surface,
        double x,
        string text,
        RenderThemeColor color = RenderThemeColor.Neutral,
        TimeAxisOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (string.IsNullOrEmpty(text) || !double.IsFinite(x)) return;

        if (options.Height <= 0d || options.ApproximateTicks <= 0) options = TimeAxisOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var (strip, _) = area.SplitBottom(Math.Min(options.Height, area.Height));
        if (!strip.IsValid) return;

        var width = Math.Min((text.Length * options.FontSize * 0.62d) + 10d, strip.Width);
        var left = Math.Clamp(x - (width / 2d), strip.X, Math.Max(strip.X, strip.Right - width));

        surface.SetStyle(new RenderStyle(surface.Theme(color), Alpha: 0.95d));
        surface.Rect(left, strip.Y + 1d, width, Math.Min(TagHeight, strip.Height));

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Background), FontSize: options.FontSize));
        surface.Text(left + 5d, strip.Y + Math.Min(TagHeight, strip.Height) - 3d, text);
    }

    /// <summary>
    /// The round interval to label on for a span of time — the smallest rung of the ladder that keeps
    /// the label count near <paramref name="approximateTicks"/>.
    /// </summary>
    public static TimeSpan StepFor(TimeSpan span, int approximateTicks = 6)
    {
        if (approximateTicks <= 0) approximateTicks = TimeAxisOptions.Default.ApproximateTicks;

        var target = span.Ticks <= 0L ? TimeSpan.Zero : new TimeSpan(span.Ticks / approximateTicks);
        foreach (var step in Ladder)
        {
            if (step >= target) return step;
        }

        return Ladder[^1];
    }

    /// <summary>
    /// One label, at the resolution the step implies: the year for a decade, the month for a year, the
    /// date for a week, the clock for a session — and the date wherever the day changed, because that
    /// is the one place an intraday axis has to say more than the time.
    /// </summary>
    public static string Label(DateTime stamp, TimeSpan step, bool dayChanged = false)
    {
        if (step >= TimeSpan.FromDays(300)) return stamp.ToString("yyyy", CultureInfo.InvariantCulture);
        if (step >= TimeSpan.FromDays(28)) return stamp.ToString("MMM yy", CultureInfo.InvariantCulture);
        if (step >= TimeSpan.FromDays(1) || dayChanged) return stamp.ToString("d MMM", CultureInfo.InvariantCulture);

        return stamp.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a label boundary falls between two consecutive bars. A day change always counts,
    /// however fine the step: an intraday axis that runs into tomorrow without saying so is the one
    /// mistake on this strip that misleads rather than merely reads badly.</summary>
    private static bool Crossed(DateTime previous, DateTime current, TimeSpan step)
    {
        if (step >= TimeSpan.FromDays(28)) return previous.Year != current.Year || previous.Month != current.Month;
        if (step >= TimeSpan.FromDays(1)) return previous.Date != current.Date;
        if (previous.Date != current.Date) return true;
        if (step <= TimeSpan.Zero) return false;

        return previous.TimeOfDay.Ticks / step.Ticks != current.TimeOfDay.Ticks / step.Ticks;
    }

    private static void Write(
        IRenderSurface surface, PlotArea strip, double x, double width, string text, TimeAxisOptions options)
    {
        if (!strip.IsValid) return;

        var left = Math.Clamp(x - (width / 2d), strip.X, Math.Max(strip.X, strip.Right - width));

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: options.FontSize));
        surface.Text(left, strip.Y + Math.Min(options.FontSize + 3d, strip.Height), text);
    }
}
