namespace DaxAlgo.Sdk;

/// <summary>One primitive call, as it was made.</summary>
/// <param name="Kind">Which surface member was called — <c>Panel</c>, <c>Push</c>, <c>Rect</c> and so on.</param>
/// <param name="Label">The panel title, series name or text, where the call carried one.</param>
public readonly record struct RenderCall(string Kind, string? Label = null, double X = 0d, double Y = 0d);

/// <summary>A rectangle, with the style that was active when it was drawn — shading and alpha are the
/// substance of a heat map or a footprint cell, not decoration on top of one.</summary>
public readonly record struct RecordedRect(
    double X, double Y, double Width, double Height, bool Filled, RenderStyle Style);

/// <summary>A line, with the style active when it was stroked.</summary>
public readonly record struct RecordedLine(
    double X1, double Y1, double X2, double Y2, RenderStyle Style);

/// <summary>Text, with where it was placed — so layout is testable and not only content.</summary>
public readonly record struct RecordedText(double X, double Y, string Text, RenderStyle Style);

/// <summary>
/// A render surface that keeps what was drawn instead of painting it.
///
/// <para><b>This is how you check your own unit.</b> <see cref="IRenderSurface"/> is an interface, not a
/// control, so testing a picture needs no window, no dispatcher and no running host: construct one of
/// these, call <c>Draw</c>, and assert on what came back.</para>
///
/// <para>It exists because a unit that compiles and paints nothing is the easiest mistake to ship and the
/// hardest to notice — a blank panel is indistinguishable from a broken host, so nobody reports it as a
/// bug in the visualizer. The host uses the same class to verify authored units before a user ever sees
/// one.</para>
///
/// <para>Not thread-safe, and deliberately so: <c>Draw</c> is called on one thread at a time, and a lock
/// here would hide a unit that violated that.</para>
/// </summary>
public sealed class RecordingRenderSurface : IRenderSurface
{
    private readonly List<RenderCall> _calls = [];
    private readonly List<string> _panels = [];
    private readonly List<string> _series = [];
    private readonly List<(double X, double Y)> _points = [];
    private readonly List<RecordedText> _texts = [];
    private readonly List<RecordedRect> _rects = [];
    private readonly List<RecordedLine> _lines = [];
    private RenderStyle _style = new(new RenderColor(0, 0, 0));
    private readonly List<(double X, double Y, RenderMarkerShape Shape)> _markers = [];
    private readonly List<RenderThemeColor> _themeTokens = [];

    /// <param name="viewport">The area to report. Pass a degenerate one — zero width or height — to check
    /// that a unit scaling to the viewport degrades gracefully instead of dividing by it.</param>
    /// <param name="cursor">Pointer state. Defaults to outside, which is what a crosshair routine should
    /// treat as "draw nothing".</param>
    public RecordingRenderSurface(RenderViewport? viewport = null, RenderCursor? cursor = null)
    {
        Viewport = viewport ?? new RenderViewport(800d, 400d, 1d);
        Cursor = cursor ?? new RenderCursor(0d, 0d, IsInside: false, IsPressed: false);
    }

    public RenderViewport Viewport { get; }

    public RenderCursor Cursor { get; }

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<RenderCall> Calls => _calls;

    /// <summary>Panel titles, in the order they were opened.</summary>
    public IReadOnlyList<string> Panels => _panels;

    /// <summary>Series names, in the order they were opened. Named for the names rather than for
    /// the member, because <c>Series</c> itself is the interface method that opens one.</summary>
    public IReadOnlyList<string> SeriesNames => _series;

    /// <summary>Every point pushed into any series.</summary>
    public IReadOnlyList<(double X, double Y)> Points => _points;

    /// <summary>Text drawn, with the point it was placed at.</summary>
    public IReadOnlyList<RecordedText> Texts => _texts;

    /// <summary>Rectangles drawn, with geometry and the style in force.</summary>
    public IReadOnlyList<RecordedRect> Rectangles => _rects;

    /// <summary>Lines drawn, with both endpoints and the style in force.</summary>
    public IReadOnlyList<RecordedLine> Lines => _lines;

    /// <summary>Markers drawn, in order.</summary>
    public IReadOnlyList<(double X, double Y, RenderMarkerShape Shape)> Markers => _markers;

    /// <summary>Theme roles resolved. Empty means the unit used literal colours, which will be
    /// unreadable in one theme or the other.</summary>
    public IReadOnlyList<RenderThemeColor> ThemeTokens => _themeTokens;

    /// <summary>Total primitives emitted — what a per-frame budget is measured against.</summary>
    public int PrimitiveCount { get; private set; }

    /// <summary>True when any coordinate was NaN or infinite. A single one of these can take out a whole
    /// frame in a real renderer, and it usually means an average over an empty window.</summary>
    public bool HasNonFiniteCoordinate { get; private set; }

    /// <summary>True when nothing at all was drawn — no primitive, no text, no marker.</summary>
    public bool IsBlank => PrimitiveCount == 0;

    /// <summary>
    /// A distinct colour per role, in the mid range.
    ///
    /// <para>Distinct matters. This used to return one mid grey for every token, which meant no test
    /// could tell a widget that drew its losses in the bullish colour from one that got it right — the
    /// two produced byte-identical output. Mid-range keeps the original property that a recorded colour
    /// is never accidentally invisible, and is still obviously not a literal anyone would pick.</para>
    /// </summary>
    public RenderColor Theme(RenderThemeColor token)
    {
        _themeTokens.Add(token);
        _calls.Add(new RenderCall("Theme", token.ToString()));

        var index = (byte)((int)token * 17);
        return new RenderColor((byte)(80 + (index % 120)), (byte)(96 + (index % 96)), (byte)(112 + (index % 80)));
    }

    public void SetStyle(RenderStyle style)
    {
        _style = style;
        _calls.Add(new RenderCall("SetStyle"));
    }

    public IDisposable Panel(string title, RenderPanelKind kind)
    {
        _panels.Add(title);
        _calls.Add(new RenderCall("Panel", title));
        return Scope.Instance;
    }

    public void AxisX(double minimum, double maximum, string? format = null)
    {
        Track(minimum, maximum);
        _calls.Add(new RenderCall("AxisX", format, minimum, maximum));
    }

    public void AxisY(double minimum, double maximum, string? format = null)
    {
        Track(minimum, maximum);
        _calls.Add(new RenderCall("AxisY", format, minimum, maximum));
    }

    public IDisposable Series(string name, RenderSeriesKind kind)
    {
        _series.Add(name);
        _calls.Add(new RenderCall("Series", name));
        return Scope.Instance;
    }

    public void Push(double x, double y)
    {
        Track(x, y);
        _points.Add((x, y));
        _calls.Add(new RenderCall("Push", null, x, y));
        PrimitiveCount++;
    }

    public void Line(double x1, double y1, double x2, double y2)
    {
        Track(x1, y1);
        Track(x2, y2);
        _lines.Add(new RecordedLine(x1, y1, x2, y2, _style));
        _calls.Add(new RenderCall("Line", null, x1, y1));
        PrimitiveCount++;
    }

    public void Rect(double x, double y, double width, double height, bool filled = true)
    {
        Track(x, y);
        Track(width, height);
        _rects.Add(new RecordedRect(x, y, width, height, filled, _style));
        _calls.Add(new RenderCall("Rect", null, x, y));
        PrimitiveCount++;
    }

    public void Text(double x, double y, string text)
    {
        Track(x, y);
        _texts.Add(new RecordedText(x, y, text, _style));
        _calls.Add(new RenderCall("Text", text, x, y));
        PrimitiveCount++;
    }

    public void Marker(double x, double y, RenderMarkerShape shape)
    {
        Track(x, y);
        _markers.Add((x, y, shape));
        _calls.Add(new RenderCall("Marker", shape.ToString(), x, y));
        PrimitiveCount++;
    }

    private void Track(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) HasNonFiniteCoordinate = true;
    }

    private sealed class Scope : IDisposable
    {
        internal static readonly Scope Instance = new();

        public void Dispose()
        {
        }
    }
}
