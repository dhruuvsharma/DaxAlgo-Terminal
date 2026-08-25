namespace DaxAlgo.Sdk.Drawing;

/// <summary>Volume traded at one price bucket.</summary>
/// <param name="Price">The bucket's price.</param>
/// <param name="BuyVolume">Buy-initiated volume at that price.</param>
/// <param name="SellVolume">Sell-initiated volume at that price.</param>
public readonly record struct ProfileRow(double Price, double BuyVolume, double SellVolume)
{
    public double Total => BuyVolume + SellVolume;

    /// <summary>A row with no side breakdown, for a profile built from bar volume rather than tape.</summary>
    public static ProfileRow At(double price, double volume) => new(price, volume / 2d, volume / 2d);
}

/// <summary>How a volume profile is drawn.</summary>
/// <param name="Width">Horizontal space the bars may use, as a fraction of the area. The rest is left
/// for whatever the profile is overlaid on.</param>
/// <param name="FromRight">Whether bars grow leftward from the right edge — the convention when a
/// profile sits beside a price chart.</param>
/// <param name="ShowPoc">Whether to mark the point of control.</param>
/// <param name="ValueAreaShare">Share of total volume that defines the value area, conventionally 0.7.
/// Zero disables the shading.</param>
/// <param name="SplitSides">Whether to show the buy/sell split within each bar.</param>
/// <param name="Alpha">Bar opacity.</param>
public readonly record struct ProfileOptions(
    double Width = 0.32d,
    bool FromRight = true,
    bool ShowPoc = true,
    double ValueAreaShare = 0.7d,
    bool SplitSides = true,
    double Alpha = 0.75d)
{
    /// <summary>The intended defaults. Explicit argument because <c>new()</c> on a record struct lands
    /// every field on zero, which here means zero-width bars.</summary>
    public static ProfileOptions Default { get; } = new(Width: 0.32d, Alpha: 0.75d);
}

/// <summary>
/// Volume at price: a horizontal histogram with the point of control and the value area.
///
/// <para>Distinct from every other picture here in that price is the <b>vertical</b> axis and volume is
/// the bar length, which is what makes it overlay a price chart. It answers the question a price series
/// cannot — where trade actually happened, rather than where price passed through.</para>
///
/// <para>The point of control and the value area are the reason to draw one at all. A profile without
/// them is a shape; with them it is a set of levels a strategy can reference, and
/// <see cref="ValueArea"/> returns them so the strategy can use the same numbers the picture shows.</para>
/// </summary>
public static class VolumeProfile
{
    /// <summary>Draws the profile against a shared price range and returns the value area it found.</summary>
    public static (double Low, double High, double Poc) Draw(
        IRenderSurface surface,
        IReadOnlyList<ProfileRow>? rows,
        PlotRange priceRange = default,
        ProfileOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (rows is null || rows.Count == 0) return (double.NaN, double.NaN, double.NaN);

        if (options.Width <= 0d) options = ProfileOptions.Default;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return (double.NaN, double.NaN, double.NaN);

        if (!priceRange.IsValid)
        {
            priceRange = PlotRange.Empty;
            for (var index = 0; index < rows.Count; index++) priceRange = priceRange.Include(rows[index].Price);
            priceRange = priceRange.Padded();
        }

        if (!priceRange.IsValid) return (double.NaN, double.NaN, double.NaN);

        var peak = 0d;
        for (var index = 0; index < rows.Count; index++) peak = Math.Max(peak, rows[index].Total);
        if (peak <= 0d) return (double.NaN, double.NaN, double.NaN);

        var (low, high, poc) = ValueArea(rows, options.ValueAreaShare);
        var span = area.Width * Math.Clamp(options.Width, 0.05d, 1d);

        // One row's height. Derived from the price spacing rather than the area, so a profile with gaps
        // in it shows the gaps instead of silently stretching to fill.
        var rowHeight = RowHeight(rows, priceRange, area);

        if (options.ValueAreaShare > 0d && double.IsFinite(low) && double.IsFinite(high))
        {
            var top = area.ToY(high, priceRange);
            var bottom = area.ToY(low, priceRange);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), Alpha: 0.08d));
            surface.Rect(
                options.FromRight ? area.Right - span : area.X,
                Math.Min(top, bottom), span, Math.Abs(bottom - top));
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!double.IsFinite(row.Price) || row.Total <= 0d) continue;
            if (row.Price < priceRange.Minimum || row.Price > priceRange.Maximum) continue;

            var length = row.Total / peak * span;
            var y = area.ToY(row.Price, priceRange) - (rowHeight / 2d);

            if (options.SplitSides && row.Total > 0d)
            {
                var buyLength = length * (row.BuyVolume / row.Total);
                Bar(surface, area, options, y, rowHeight, buyLength, RenderThemeColor.Bullish, 0d);
                Bar(surface, area, options, y, rowHeight, length - buyLength, RenderThemeColor.Bearish, buyLength);
            }
            else
            {
                Bar(surface, area, options, y, rowHeight, length, RenderThemeColor.Neutral, 0d);
            }
        }

        if (options.ShowPoc && double.IsFinite(poc))
        {
            var y = area.ToY(poc, priceRange);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Warning), Thickness: 1.5d, Alpha: 0.9d));
            surface.Line(area.X, y, area.Right, y);

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Warning), FontSize: 9.5d));
            surface.Text(area.X + 4d, y - 3d, "POC");
        }

        return (low, high, poc);

        static void Bar(
            IRenderSurface surface, PlotArea area, ProfileOptions options,
            double y, double height, double length, RenderThemeColor color, double offset)
        {
            if (length <= 0d) return;

            surface.SetStyle(new RenderStyle(surface.Theme(color), Alpha: options.Alpha));
            var x = options.FromRight ? area.Right - offset - length : area.X + offset;
            surface.Rect(x, y, length, Math.Max(1d, height));
        }
    }

    /// <summary>
    /// The value area and the point of control: the narrowest contiguous price span holding
    /// <paramref name="share"/> of the volume, grown outward from the busiest row.
    ///
    /// <para>Exposed separately because a strategy usually wants the numbers, not the picture — and it
    /// must be the same calculation, or the levels it trades will differ from the ones it shows.</para>
    /// </summary>
    public static (double Low, double High, double Poc) ValueArea(
        IReadOnlyList<ProfileRow> rows, double share = 0.7d)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return (double.NaN, double.NaN, double.NaN);

        var total = 0d;
        var pocIndex = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            total += rows[index].Total;
            if (rows[index].Total > rows[pocIndex].Total) pocIndex = index;
        }

        var poc = rows[pocIndex].Price;
        if (total <= 0d || share <= 0d) return (double.NaN, double.NaN, poc);

        var target = total * Math.Clamp(share, 0d, 1d);
        var accumulated = rows[pocIndex].Total;
        int low = pocIndex, high = pocIndex;

        // Grow towards whichever neighbour holds more volume — the standard construction, and the reason
        // a value area is usually not centred on the point of control.
        while (accumulated < target && (low > 0 || high < rows.Count - 1))
        {
            var below = low > 0 ? rows[low - 1].Total : -1d;
            var above = high < rows.Count - 1 ? rows[high + 1].Total : -1d;

            if (above >= below) accumulated += rows[++high].Total;
            else accumulated += rows[--low].Total;
        }

        var first = rows[low].Price;
        var last = rows[high].Price;
        return (Math.Min(first, last), Math.Max(first, last), poc);
    }

    private static double RowHeight(IReadOnlyList<ProfileRow> rows, PlotRange range, PlotArea area)
    {
        if (rows.Count < 2) return Math.Max(2d, area.Height / 20d);

        var smallest = double.PositiveInfinity;
        for (var index = 1; index < rows.Count; index++)
        {
            var gap = Math.Abs(rows[index].Price - rows[index - 1].Price);
            if (gap > 0d) smallest = Math.Min(smallest, gap);
        }

        if (!double.IsFinite(smallest) || range.Span <= 0d) return Math.Max(2d, area.Height / rows.Count);

        return Math.Max(1d, smallest / range.Span * area.Height * 0.9d);
    }
}
