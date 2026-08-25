using System.Globalization;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How the tape is drawn.</summary>
/// <param name="RowHeight">Pixels per print.</param>
/// <param name="PriceFormat">Numeric format for the price column.</param>
/// <param name="ShowTime">Whether to show the time column.</param>
/// <param name="HighlightFrom">Size at or above which a print is emphasised. Zero disables it.</param>
/// <param name="Newest">Whether the newest print is at the top. True matches every trading platform's
/// tape, and getting it backwards makes the panel read as frozen.</param>
public readonly record struct TapeOptions(
    double RowHeight = 15d,
    string? PriceFormat = null,
    bool ShowTime = true,
    long HighlightFrom = 0L,
    bool Newest = true)
{
    /// <summary>The intended defaults.</summary>
    public static TapeOptions Default { get; } = new(RowHeight: 15d);
}

/// <summary>
/// Time and sales: the printed trades, newest first, coloured by which side crossed the spread.
///
/// <para>The rawest view of the market there is, and the one an order-flow strategy is reasoning about
/// whether or not it shows it. A cumulative-delta line says what the aggression added up to; the tape
/// says whether it arrived as one block or four hundred lots — which is the difference between a
/// participant and noise, and it is invisible in every aggregate.</para>
///
/// <para>Bounded by the area rather than by the list: the caller keeps a rolling buffer, and this draws
/// as much of the newest end of it as fits.</para>
/// </summary>
public static class Tape
{
    /// <summary>Draws the prints and returns how many were shown.</summary>
    public static int Draw(
        IRenderSurface surface,
        IReadOnlyList<TradePrint>? prints,
        TapeOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (prints is null || prints.Count == 0) return 0;

        if (options.RowHeight <= 0d) options = TapeOptions.Default with
        {
            PriceFormat = options.PriceFormat, HighlightFrom = options.HighlightFrom,
        };

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return 0;

        var (header, body) = area.SplitTop(options.RowHeight);
        var timeWidth = options.ShowTime ? Math.Min(58d, body.Width * 0.3d) : 0d;
        var sizeWidth = Math.Min(56d, body.Width * 0.28d);

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9d));
        if (options.ShowTime) surface.Text(header.X + 3d, header.Bottom - 4d, "TIME");
        surface.Text(header.X + timeWidth + 3d, header.Bottom - 4d, "PRICE");
        surface.Text(header.Right - sizeWidth + 3d, header.Bottom - 4d, "SIZE");

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.6d));
        surface.Line(header.X, header.Bottom, header.Right, header.Bottom);

        var fits = (int)Math.Floor(body.Height / options.RowHeight);
        var count = Math.Min(prints.Count, Math.Max(0, fits));

        for (var row = 0; row < count; row++)
        {
            // Newest first means walking the caller's buffer backwards — the buffer is append-ordered
            // because that is what a rolling window is, and reversing it every frame would be work on
            // the render thread.
            var print = options.Newest ? prints[prints.Count - 1 - row] : prints[row];
            var y = body.Y + (row * options.RowHeight);
            var big = options.HighlightFrom > 0L && print.Size >= options.HighlightFrom;

            var tone = print.Aggressor switch
            {
                AggressorSide.Buy => RenderThemeColor.Bullish,
                AggressorSide.Sell => RenderThemeColor.Bearish,
                _ => RenderThemeColor.Neutral,
            };

            if (big)
            {
                surface.SetStyle(new RenderStyle(surface.Theme(tone), Alpha: 0.18d));
                surface.Rect(body.X, y, body.Width, options.RowHeight);
            }

            if (options.ShowTime)
            {
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));
                surface.Text(body.X + 3d, y + options.RowHeight - 4d,
                    print.EventTimeUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }

            surface.SetStyle(new RenderStyle(surface.Theme(tone), FontSize: 10d));
            surface.Text(body.X + timeWidth + 3d, y + options.RowHeight - 4d,
                print.Price.ToString(options.PriceFormat ?? "0.####", CultureInfo.InvariantCulture));

            // Size right-aligned so a block stands out by its width in the column, without needing the
            // reader to compare digit counts.
            var size = print.Size.ToString("N0", CultureInfo.InvariantCulture);
            surface.SetStyle(new RenderStyle(surface.Theme(big ? tone : RenderThemeColor.Text),
                FontSize: 10d, Alpha: big ? 1d : 0.9d));
            surface.Text(
                body.Right - Math.Max(4d, (size.Length * 6.1d) + 4d), y + options.RowHeight - 4d, size);
        }

        return count;
    }
}
