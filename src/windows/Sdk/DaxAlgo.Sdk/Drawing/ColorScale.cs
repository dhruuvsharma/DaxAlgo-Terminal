namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// Value-to-colour ramps, anchored on the host's theme.
///
/// <para>The one place a picture is allowed to compute a colour rather than name a role, because a
/// heatmap's whole content <i>is</i> the colour and eleven named roles cannot express a gradient. The
/// ramps still start from theme colours, so a scale stays legible against whichever background is
/// active instead of being tuned for one of them.</para>
///
/// <para><b>Sequential for magnitude, diverging for sign.</b> A quantity with a meaningful zero —
/// delta, correlation, PnL, imbalance — drawn on a sequential ramp hides which side of zero it is on,
/// which is usually the only thing the reader wanted to know.</para>
/// </summary>
public static class ColorScale
{
    /// <summary>
    /// Sequential: a single hue fading from the surface colour to full strength.
    ///
    /// <para>For a magnitude with no sign — volume, liquidity, trade count, dwell time.</para>
    /// </summary>
    /// <param name="surface">The surface, for its theme.</param>
    /// <param name="t">Position along the ramp, clamped to 0…1.</param>
    /// <param name="color">The role the ramp runs towards.</param>
    public static RenderColor Sequential(
        IRenderSurface surface, double t, RenderThemeColor color = RenderThemeColor.Accent)
    {
        ArgumentNullException.ThrowIfNull(surface);

        return Mix(surface.Theme(RenderThemeColor.Surface), surface.Theme(color), Clamp01(t));
    }

    /// <summary>
    /// Diverging: bearish through neutral to bullish, with zero at the midpoint.
    ///
    /// <para>For anything signed. <paramref name="value"/> is divided by <paramref name="extent"/>, so
    /// pass the largest magnitude in view — a scale fitted to the whole history washes out the frame the
    /// reader is actually looking at.</para>
    /// </summary>
    public static RenderColor Diverging(IRenderSurface surface, double value, double extent)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var neutral = surface.Theme(RenderThemeColor.Surface);
        if (!double.IsFinite(value) || !double.IsFinite(extent) || extent <= 0d) return neutral;

        var t = Clamp01(Math.Abs(value) / extent);
        var end = surface.Theme(value >= 0d ? RenderThemeColor.Bullish : RenderThemeColor.Bearish);
        return Mix(neutral, end, t);
    }

    /// <summary>
    /// Buy/sell shading for a footprint or tape cell: the side that dominated, at a strength given by how
    /// much it dominated.
    /// </summary>
    /// <param name="surface">The surface, for its theme.</param>
    /// <param name="buy">Buy-initiated quantity.</param>
    /// <param name="sell">Sell-initiated quantity.</param>
    /// <param name="scale">The quantity that counts as full strength — the largest cell total in view.</param>
    public static RenderColor Flow(IRenderSurface surface, double buy, double sell, double scale)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var total = buy + sell;
        if (total <= 0d || scale <= 0d) return surface.Theme(RenderThemeColor.Surface);

        // Strength from the total, hue from the side. Using the imbalance for both would make a huge
        // balanced cell look identical to an empty one.
        var strength = Clamp01(total / scale);
        var end = surface.Theme(buy >= sell ? RenderThemeColor.Bullish : RenderThemeColor.Bearish);
        return Mix(surface.Theme(RenderThemeColor.Surface), end, 0.15d + (strength * 0.85d));
    }

    /// <summary>Linear interpolation between two colours.</summary>
    public static RenderColor Mix(RenderColor from, RenderColor to, double t)
    {
        var amount = Clamp01(t);
        return new RenderColor(
            Channel(from.R, to.R, amount),
            Channel(from.G, to.G, amount),
            Channel(from.B, to.B, amount));
    }

    private static byte Channel(byte from, byte to, double t) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * t)), 0d, 255d);

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;
}
