namespace DaxAlgo.Sdk;

/// <summary>What a panel is for. The host picks chrome, gutters and default axes from this.</summary>
public enum RenderPanelKind
{
    /// <summary>Time on X, price or value on Y.</summary>
    Chart = 0,

    /// <summary>Price rows stacked vertically — an order-book ladder.</summary>
    Ladder = 1,

    /// <summary>Cells addressed by column and price row — a volume footprint.</summary>
    Matrix = 2,

    /// <summary>Free coordinates; the visualizer owns the whole space.</summary>
    Canvas = 3,
}

/// <summary>How a pushed point sequence is joined.</summary>
public enum RenderSeriesKind
{
    Line = 0,
    Area = 1,
    Bars = 2,
    Steps = 3,
    Scatter = 4,
}

/// <summary>Marker glyph for a single point.</summary>
public enum RenderMarkerShape
{
    Circle = 0,
    Square = 1,
    Triangle = 2,
    Cross = 3,
    Diamond = 4,
}

/// <summary>
/// A colour from the host's theme rather than a literal.
///
/// <para>Visualizers name roles, not RGB, so one visualizer looks right in every theme and cannot
/// paint itself invisible against the current background.</para>
/// </summary>
public enum RenderThemeColor
{
    Text = 0,
    TextSecondary = 1,
    Background = 2,
    Surface = 3,
    Grid = 4,
    Border = 5,
    Accent = 6,
    Bullish = 7,
    Bearish = 8,
    Neutral = 9,
    Warning = 10,
}

/// <summary>An exact colour. Prefer <see cref="RenderThemeColor"/>; this exists for data-driven scales.</summary>
/// <param name="R">Red channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
public readonly record struct RenderColor(byte R, byte G, byte B);

/// <summary>
/// Stroke and fill state applied to subsequent draw calls, in the immediate-mode sense: set it, then
/// draw, then set it again. Nothing is retained between frames.
/// </summary>
/// <param name="Color">Stroke and fill colour.</param>
/// <param name="Thickness">Stroke width in device-independent pixels.</param>
/// <param name="Alpha">0 transparent to 1 opaque.</param>
/// <param name="Dashed">Whether strokes are dashed.</param>
/// <param name="FontSize">Point size used by <see cref="IRenderSurface.Text"/>.</param>
public readonly record struct RenderStyle(
    RenderColor Color,
    double Thickness = 1d,
    double Alpha = 1d,
    bool Dashed = false,
    double FontSize = 11d);

/// <summary>The drawable area of the current panel, in device-independent pixels.</summary>
/// <param name="Width">Panel width.</param>
/// <param name="Height">Panel height.</param>
/// <param name="Scale">Device pixel ratio, for hairline-accurate strokes.</param>
public readonly record struct RenderViewport(double Width, double Height, double Scale);

/// <summary>
/// Pointer state for the current panel. Present so a visualizer can draw a crosshair or a hover
/// readout — the volume footprint's tooltip is exactly this — without the host needing to know what
/// the visualizer considers hoverable.
/// </summary>
/// <param name="X">Pointer X in panel coordinates.</param>
/// <param name="Y">Pointer Y in panel coordinates.</param>
/// <param name="IsInside">Whether the pointer is over the panel at all.</param>
/// <param name="IsPressed">Whether the primary button is down.</param>
public readonly record struct RenderCursor(double X, double Y, bool IsInside, bool IsPressed);

/// <summary>
/// The visualizer's drawing output.
///
/// <para>Immediate mode: a visualizer describes the whole frame each time it is asked to draw, and
/// the host retains nothing between frames. That suits streaming market data, where most of the
/// picture changes on every tick, and it means a visualizer holds no visual state the host has to
/// reconcile.</para>
///
/// <para><b>This is a data contract, not a UI toolkit.</b> A visualizer never touches WPF, never
/// receives a control, and cannot reach the window it is drawn into — which is what keeps it
/// sandboxable. The host translates these calls into pixels and is free to bound, batch, clip or
/// refuse them; a visualizer that draws unreasonably is throttled, not trusted.</para>
///
/// <para>The primitive set is deliberately the same one the sealed-format renderer uses, so both
/// kinds of visualizer can be drawn by a single host renderer and an author learns one API.</para>
/// </summary>
public interface IRenderSurface
{
    /// <summary>The current panel's drawable area.</summary>
    RenderViewport Viewport { get; }

    /// <summary>Pointer state for the current panel.</summary>
    RenderCursor Cursor { get; }

    /// <summary>Resolves a theme role to the colour the host is currently using for it.</summary>
    RenderColor Theme(RenderThemeColor token);

    /// <summary>Sets stroke and fill state for subsequent draw calls.</summary>
    void SetStyle(RenderStyle style);

    /// <summary>
    /// Opens a panel and returns a scope that closes it. Panels may be opened in sequence to stack
    /// several regions in one visualizer — a ladder beside a chart, say.
    /// </summary>
    /// <example><code>using (surface.Panel("Depth", RenderPanelKind.Ladder)) { ... }</code></example>
    IDisposable Panel(string title, RenderPanelKind kind);

    /// <summary>Declares the X range and optional numeric/date format for the current panel.</summary>
    void AxisX(double minimum, double maximum, string? format = null);

    /// <summary>Declares the Y range and optional numeric format for the current panel.</summary>
    void AxisY(double minimum, double maximum, string? format = null);

    /// <summary>
    /// Opens a point series and returns a scope that closes it. Push points inside the scope with
    /// <see cref="Push"/>.
    /// </summary>
    IDisposable Series(string name, RenderSeriesKind kind);

    /// <summary>Adds one point to the open series.</summary>
    void Push(double x, double y);

    /// <summary>Strokes a line between two points.</summary>
    void Line(double x1, double y1, double x2, double y2);

    /// <summary>Draws a rectangle — filled by default, since cells and bars are the common case.</summary>
    void Rect(double x, double y, double width, double height, bool filled = true);

    /// <summary>Draws text with its baseline start at the given point.</summary>
    void Text(double x, double y, string text);

    /// <summary>Draws a single marker glyph.</summary>
    void Marker(double x, double y, RenderMarkerShape shape);
}
