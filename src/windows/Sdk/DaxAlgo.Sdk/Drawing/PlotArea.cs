namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// A rectangle inside a panel, in panel pixels.
///
/// <para>The composition primitive. Every widget that can be placed rather than filling the panel takes
/// one of these, so a dashboard is a handful of <see cref="Row"/> and <see cref="Column"/> calls rather
/// than arithmetic repeated at every call site — and getting that arithmetic subtly wrong is how panels
/// end up with widgets drawn on top of each other.</para>
///
/// <para>Splits return the strip <b>and what is left</b>, so a layout reads top to bottom without any
/// running offset to keep straight:
/// <c>var (header, body) = area.SplitTop(20d);</c></para>
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct PlotArea(double X, double Y, double Width, double Height)
{
    /// <summary>The whole panel.</summary>
    public static PlotArea Of(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var viewport = surface.Viewport;
        return new PlotArea(0d, 0d, viewport.Width, viewport.Height);
    }

    /// <summary>Nothing. What a split or a cell returns when there was no room for it.</summary>
    public static PlotArea None { get; }

    /// <summary>True when there is room to draw. Every widget checks this and returns rather than
    /// emitting primitives with negative extents.</summary>
    public bool IsValid => Width > 0d && Height > 0d;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2d);

    public double CenterY => Y + (Height / 2d);

    /// <summary>Shrinks by the same padding on every side, clamped so it can never invert.</summary>
    public PlotArea Inset(double padding) => Inset(padding, padding);

    /// <summary>Shrinks horizontally and vertically, clamped so it can never invert.</summary>
    public PlotArea Inset(double horizontal, double vertical) => new(
        X + horizontal,
        Y + vertical,
        Math.Max(0d, Width - (horizontal * 2d)),
        Math.Max(0d, Height - (vertical * 2d)));

    /// <summary>One of <paramref name="count"/> equal horizontal strips, top to bottom.</summary>
    public PlotArea Row(int index, int count, double gap = 0d)
    {
        if (count <= 0 || index < 0 || index >= count) return None;

        var each = (Height - (gap * (count - 1))) / count;
        return each <= 0d ? None : new PlotArea(X, Y + (index * (each + gap)), Width, each);
    }

    /// <summary>One of <paramref name="count"/> equal vertical strips, left to right.</summary>
    public PlotArea Column(int index, int count, double gap = 0d)
    {
        if (count <= 0 || index < 0 || index >= count) return None;

        var each = (Width - (gap * (count - 1))) / count;
        return each <= 0d ? None : new PlotArea(X + (index * (each + gap)), Y, each, Height);
    }

    /// <summary>Splits off the top <paramref name="height"/> pixels: the strip, then the rest below it.</summary>
    public (PlotArea Taken, PlotArea Remainder) SplitTop(double height)
    {
        var taken = Math.Clamp(height, 0d, Height);
        return (new PlotArea(X, Y, Width, taken),
                new PlotArea(X, Y + taken, Width, Height - taken));
    }

    /// <summary>Splits off the bottom <paramref name="height"/> pixels: the strip, then the rest above it.</summary>
    public (PlotArea Taken, PlotArea Remainder) SplitBottom(double height)
    {
        var taken = Math.Clamp(height, 0d, Height);
        return (new PlotArea(X, Bottom - taken, Width, taken),
                new PlotArea(X, Y, Width, Height - taken));
    }

    /// <summary>Splits off the left <paramref name="width"/> pixels.</summary>
    public (PlotArea Taken, PlotArea Remainder) SplitLeft(double width)
    {
        var taken = Math.Clamp(width, 0d, Width);
        return (new PlotArea(X, Y, taken, Height),
                new PlotArea(X + taken, Y, Width - taken, Height));
    }

    /// <summary>Splits off the right <paramref name="width"/> pixels — where a price gutter goes.</summary>
    public (PlotArea Taken, PlotArea Remainder) SplitRight(double width)
    {
        var taken = Math.Clamp(width, 0d, Width);
        return (new PlotArea(Right - taken, Y, taken, Height),
                new PlotArea(X, Y, Width - taken, Height));
    }

    /// <summary>Pixel Y for a value within this area, top-down. An invalid range collapses to the middle
    /// rather than to NaN, so a widget fed a flat series still draws a line somebody can see.</summary>
    public double ToY(double value, PlotRange range) =>
        range.IsValid && Height > 0d
            ? Bottom - ((value - range.Minimum) / range.Span * Height)
            : CenterY;

    /// <summary>Pixel X for item <paramref name="index"/> of <paramref name="count"/>, spread across the
    /// area. A single item sits in the middle rather than on the left edge.</summary>
    public double ToX(int index, int count) => count switch
    {
        <= 0 => X,
        1 => CenterX,
        _ => X + (Math.Clamp(index, 0, count - 1) / (double)(count - 1) * Width),
    };

    /// <summary>Column width when <paramref name="count"/> items are laid side by side.</summary>
    public double StepX(int count) => count <= 1 ? Width : Width / count;

    /// <summary>True when a point is inside — for hit-testing the cursor against a placed widget.</summary>
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}
