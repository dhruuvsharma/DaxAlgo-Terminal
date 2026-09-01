using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DaxAlgo.Sdk;

// The IRenderSurface method is named Rect, which shadows the WPF type inside this class.
using WpfRect = System.Windows.Rect;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// <see cref="IRenderSurface"/> implemented straight onto a live <see cref="DrawingContext"/>.
///
/// <para>No intermediate op list: immediate mode in, immediate mode out. The visualizer's calls
/// become WPF drawing calls as they happen, which is why a frame costs no allocation beyond the
/// brushes and pens it actually uses.</para>
///
/// <para>Two things it is responsible for that the caller must not have to think about:</para>
/// <list type="bullet">
///   <item><b>Data space.</b> A panel that declared axes has its coordinates mapped from data units
///     to pixels here, so a visualizer plots in price and time. Y is flipped, because data grows
///     upward and pixels grow downward.</item>
///   <item><b>Bounds.</b> Primitives are counted and cut off at a cap. Untrusted code gets truncated
///     rather than allowed to wedge the render thread.</item>
/// </list>
/// </summary>
internal sealed class DrawingContextSurface : IRenderSurface
{
    private readonly DrawingContext _context;
    private readonly Size _size;
    private readonly double _scale;
    private readonly RenderCursor _rawCursor;
    private readonly Func<RenderThemeColor, Color> _theme;

    private readonly List<PanelSlot> _panels = [];
    private PanelSlot? _panel;
    private int _clipDepth;

    private RenderStyle _style = new(new RenderColor(0xE6, 0xED, 0xF3));
    private Pen? _pen;
    private Brush? _brush;

    private SeriesScope? _series;

    private readonly int _expectedPanels;
    private readonly bool _discovering;

    internal DrawingContextSurface(
        DrawingContext context,
        Size size,
        double scale,
        RenderCursor cursor,
        Func<RenderThemeColor, Color> theme,
        int expectedPanels = 0,
        bool discovering = false,
        (double Zoom, double PanX, double PanY) transform = default,
        DateTime now = default)
    {
        _context = context;
        _size = size;
        _scale = scale <= 0d ? 1d : scale;
        _rawCursor = cursor;
        _theme = theme;
        _expectedPanels = expectedPanels;
        _discovering = discovering;
        // default(ValueTuple) is a zero zoom, which would have every unit dividing its window by 0.
        // A surface built without a transform is unzoomed, not degenerate.
        _transform = transform.Zoom > 0d ? transform : (1d, 0d, 0d);
        Now = now;
    }

    /// <summary>The frame instant, sampled ONCE per frame by the view and handed to both passes — a
    /// clock read here would give the discovery pass and the drawing pass different instants, and a
    /// unit whose panel count depended on time would then be laid out against a frame it never
    /// drew.</summary>
    public DateTime Now { get; }

    private readonly (double Zoom, double PanX, double PanY) _transform;

    /// <summary>Panels opened during this pass — what the discovery pass exists to find out.</summary>
    internal int PanelCount => _panels.Count;

    internal int OperationCount { get; private set; }

    internal bool WasTruncated { get; private set; }

    /// <summary>
    /// The area a visualizer may draw into: the current panel's, or the whole control before any
    /// panel is opened.
    /// </summary>
    public RenderViewport Viewport
    {
        get
        {
            var bounds = _panel is { } panel
                ? new RenderViewport(panel.Bounds.Width, panel.Bounds.Height, _scale)
                : new RenderViewport(_size.Width, _size.Height, _scale);

            return bounds with
            {
                Zoom = _transform.Zoom,
                PanX = _transform.PanX,
                PanY = _transform.PanY,
            };
        }
    }

    /// <summary>
    /// Cursor in the current panel's coordinates, reported outside when it is over a different panel
    /// — so two panels never both think they are hovered.
    ///
    /// <para>The standing selection is mapped the same way and <b>independently</b>: a pinned level
    /// survives the pointer leaving the window, which is exactly when someone is looking at what they
    /// pinned. Mapping it alongside the hover state would have cleared it on the way out.</para>
    /// </summary>
    public RenderCursor Cursor
    {
        get
        {
            var hover = Hover();
            return hover with
            {
                HasSelection = Selection(out var x, out var y),
                SelectionX = x,
                SelectionY = y,
            };
        }
    }

    private RenderCursor Hover()
    {
        if (!_rawCursor.IsInside) return new RenderCursor(0d, 0d, IsInside: false, _rawCursor.IsPressed);
        if (_panel is not { } panel)
            return new RenderCursor(_rawCursor.X, _rawCursor.Y, IsInside: true, _rawCursor.IsPressed);
        if (!panel.Bounds.Contains(_rawCursor.X, _rawCursor.Y))
            return new RenderCursor(0d, 0d, IsInside: false, IsPressed: false);

        return new RenderCursor(
            _rawCursor.X - panel.Bounds.X,
            _rawCursor.Y - panel.Bounds.Y,
            IsInside: true,
            _rawCursor.IsPressed);
    }

    /// <summary>The standing click in the current panel's coordinates, or false when there is none or
    /// it belongs to another panel.</summary>
    private bool Selection(out double x, out double y)
    {
        x = y = 0d;
        if (!_rawCursor.HasSelection) return false;

        if (_panel is not { } panel)
        {
            x = _rawCursor.SelectionX;
            y = _rawCursor.SelectionY;
            return true;
        }

        if (!panel.Bounds.Contains(_rawCursor.SelectionX, _rawCursor.SelectionY)) return false;

        x = _rawCursor.SelectionX - panel.Bounds.X;
        y = _rawCursor.SelectionY - panel.Bounds.Y;
        return true;
    }

    public RenderColor Theme(RenderThemeColor token)
    {
        var color = _theme(token);
        return new RenderColor(color.R, color.G, color.B);
    }

    public void SetStyle(RenderStyle style)
    {
        _style = style;
        _pen = null;
        _brush = null;
    }

    public IDisposable Panel(string title, RenderPanelKind kind)
    {
        // Panels stack vertically and share the height evenly. Reserving the slot on open means the
        // first panel cannot know how many follow, so the split is applied as each one is entered
        // and re-entering is what an animated frame does anyway.
        var index = _panels.Count;
        var slot = new PanelSlot(index, title, kind, WpfRect.Empty);
        _panels.Add(slot);
        LayoutPanels();
        _panel = _panels[index];

        if (_discovering)
            return new PanelScope(this, index);

        _context.PushClip(new RectangleGeometry(_panel.Bounds));
        _clipDepth++;
        Count();
        DrawPanelTitle(_panel);
        return new PanelScope(this, index);
    }

    /// <summary>Height reserved above a titled panel, matching the header an
    /// <see cref="AuthoredUnitLayoutHost"/> panel gets.</summary>
    private const double TitleExtent = 14d;

    /// <summary>
    /// Writes the panel's own title into its top-left corner.
    ///
    /// <para><b>It was passed, stored, and drawn by nothing.</b> A body declared as a
    /// <c>UnitLayout</c> gets a real header per panel from the layout host, but a unit that divides one
    /// surface with several <c>Panel</c> scopes — which is what both exemplars do — got unlabelled
    /// regions, and the title argument it had dutifully supplied went nowhere.</para>
    ///
    /// <para><b>Not counted against the frame budget.</b> That budget bounds what UNTRUSTED code may
    /// emit; charging a unit for the host's own decoration would let a chrome change push a
    /// well-behaved visualizer over the limit.</para>
    /// </summary>
    private void DrawPanelTitle(PanelSlot panel)
    {
        if (string.IsNullOrWhiteSpace(panel.Title) || panel.Bounds.Height < TitleExtent * 2d)
            return;

        var formatted = new FormattedText(
            panel.Title,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface,
            9.5d,
            new SolidColorBrush(_theme(RenderThemeColor.TextSecondary)),
            _scale)
        {
            MaxTextWidth = Math.Max(1d, panel.Bounds.Width - 8d),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        _context.DrawText(formatted, new Point(panel.Bounds.X + 4d, panel.Bounds.Y + 1d));
    }

    private void LayoutPanels()
    {
        if (_panels.Count == 0)
            return;

        // Against the expected total, not the count so far: laying out against "panels opened up to
        // now" gave the first panel the full height and only split once a second one opened, so the
        // first panel was always drawn wrong. The discovery pass is what supplies the total.
        var total = Math.Max(_expectedPanels, _panels.Count);
        var height = _size.Height / total;
        for (var index = 0; index < _panels.Count; index++)
        {
            _panels[index] = _panels[index] with
            {
                Bounds = new WpfRect(0d, index * height, _size.Width, height),
            };
        }
    }

    private void ClosePanel(int index)
    {
        if (_discovering)
        {
            _panel = null;
            return;
        }

        if (_clipDepth > 0)
        {
            _context.Pop();
            _clipDepth--;
        }

        _panel = null;
        _ = index;
    }

    public void AxisX(double minimum, double maximum, string? format = null)
    {
        if (_panel is { } panel && maximum > minimum)
            Replace(panel with { XMinimum = minimum, XMaximum = maximum, XFormat = format });
    }

    public void AxisY(double minimum, double maximum, string? format = null)
    {
        if (_panel is { } panel && maximum > minimum)
            Replace(panel with { YMinimum = minimum, YMaximum = maximum, YFormat = format });
    }

    private void Replace(PanelSlot updated)
    {
        _panels[updated.Index] = updated;
        _panel = updated;
    }

    public IDisposable Series(string name, RenderSeriesKind kind)
    {
        _series = new SeriesScope(kind);
        return new SeriesCloser(this);
    }

    public void Push(double x, double y)
    {
        if (_series is null || !Finite(x, y) || !Count())
            return;

        _series.Points.Add(ToPixels(x, y));
    }

    private void CloseSeries()
    {
        if (_series is not { } series)
            return;

        _series = null;
        if (_discovering)
            return;

        if (series.Points.Count < 1)
            return;

        var pen = CurrentPen();
        switch (series.Kind)
        {
            case RenderSeriesKind.Scatter:
                foreach (var point in series.Points)
                    _context.DrawEllipse(CurrentBrush(), null, point, 1.5, 1.5);
                break;

            case RenderSeriesKind.Bars:
                foreach (var point in series.Points)
                    _context.DrawLine(pen, new Point(point.X, BaselineY()), point);
                break;

            default:
                DrawJoined(series, pen);
                break;
        }
    }

    private void DrawJoined(SeriesScope series, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var open = geometry.Open())
        {
            open.BeginFigure(series.Points[0], isFilled: series.Kind == RenderSeriesKind.Area, isClosed: false);
            for (var index = 1; index < series.Points.Count; index++)
            {
                var point = series.Points[index];
                if (series.Kind == RenderSeriesKind.Steps)
                    open.LineTo(new Point(point.X, series.Points[index - 1].Y), isStroked: true, isSmoothJoin: false);
                open.LineTo(point, isStroked: true, isSmoothJoin: false);
            }

            if (series.Kind == RenderSeriesKind.Area)
            {
                var baseline = BaselineY();
                open.LineTo(new Point(series.Points[^1].X, baseline), isStroked: false, isSmoothJoin: false);
                open.LineTo(new Point(series.Points[0].X, baseline), isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        _context.DrawGeometry(series.Kind == RenderSeriesKind.Area ? CurrentBrush() : null, pen, geometry);
    }

    /// <summary>Where an area fills down to, or bars rise from: the panel floor.</summary>
    private double BaselineY() => _panel is { } panel ? panel.Bounds.Bottom : _size.Height;

    public void Line(double x1, double y1, double x2, double y2)
    {
        if (!Finite(x1, y1, x2, y2) || !Count())
            return;

        var a = ToPixels(x1, y1);
        var b = ToPixels(x2, y2);
        if (!Finite(a.X, a.Y, b.X, b.Y))
            return;

        _context.DrawLine(CurrentPen(), a, b);
    }

    public void Rect(double x, double y, double width, double height, bool filled = true)
    {
        if (!Finite(x, y, width, height) || !Count())
            return;

        var a = ToPixels(x, y);
        var b = ToPixels(x + width, y + height);
        var rect = new WpfRect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X),
            Math.Abs(b.Y - a.Y));
        _context.DrawRectangle(filled ? CurrentBrush() : null, filled ? null : CurrentPen(), rect);
    }

    public void Text(double x, double y, string text)
    {
        if (string.IsNullOrEmpty(text) || !Finite(x, y) || !Count())
            return;

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface,
            double.IsFinite(_style.FontSize) && _style.FontSize > 0d ? _style.FontSize : 11d,
            CurrentBrush(),
            _scale);
        var origin = ToPixels(x, y);
        _context.DrawText(formatted, new Point(origin.X, origin.Y - formatted.Height));
    }

    public void Marker(double x, double y, RenderMarkerShape shape)
    {
        if (!Finite(x, y) || !Count())
            return;

        var point = ToPixels(x, y);
        const double radius = 3.5d;
        var brush = CurrentBrush();
        switch (shape)
        {
            case RenderMarkerShape.Square:
                _context.DrawRectangle(brush, null,
                    new WpfRect(point.X - radius, point.Y - radius, radius * 2d, radius * 2d));
                break;

            case RenderMarkerShape.Cross:
                var pen = CurrentPen();
                _context.DrawLine(pen, new Point(point.X - radius, point.Y - radius), new Point(point.X + radius, point.Y + radius));
                _context.DrawLine(pen, new Point(point.X - radius, point.Y + radius), new Point(point.X + radius, point.Y - radius));
                break;

            case RenderMarkerShape.Triangle:
            case RenderMarkerShape.Diamond:
                _context.DrawGeometry(brush, null, Polygon(point, radius, shape));
                break;

            default:
                _context.DrawEllipse(brush, null, point, radius, radius);
                break;
        }
    }

    private static StreamGeometry Polygon(Point centre, double radius, RenderMarkerShape shape)
    {
        var geometry = new StreamGeometry();
        using (var open = geometry.Open())
        {
            if (shape == RenderMarkerShape.Triangle)
            {
                open.BeginFigure(new Point(centre.X, centre.Y - radius), isFilled: true, isClosed: true);
                open.LineTo(new Point(centre.X + radius, centre.Y + radius), true, false);
                open.LineTo(new Point(centre.X - radius, centre.Y + radius), true, false);
            }
            else
            {
                open.BeginFigure(new Point(centre.X, centre.Y - radius), isFilled: true, isClosed: true);
                open.LineTo(new Point(centre.X + radius, centre.Y), true, false);
                open.LineTo(new Point(centre.X, centre.Y + radius), true, false);
                open.LineTo(new Point(centre.X - radius, centre.Y), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Data units to device pixels for the current panel. Without declared axes this is the identity
    /// plus the panel offset, which makes a panel with no axes a plain pixel canvas.
    /// </summary>
    private Point ToPixels(double x, double y)
    {
        if (_panel is not { } panel)
            return new Point(x, y);

        var bounds = panel.Bounds;
        var px = panel.HasXAxis
            ? bounds.X + ((x - panel.XMinimum) / (panel.XMaximum - panel.XMinimum) * bounds.Width)
            : bounds.X + x;
        // Y is flipped: data grows upward, pixels grow downward.
        var py = panel.HasYAxis
            ? bounds.Bottom - ((y - panel.YMinimum) / (panel.YMaximum - panel.YMinimum) * bounds.Height)
            : bounds.Y + y;
        return new Point(px, py);
    }

    /// <summary>
    /// Refuses non-finite coordinates.
    ///
    /// <para>NaN arrives the moment a visualizer divides by a zero volume or an empty lookback, and
    /// WPF accepts it without complaint — the primitive is simply never painted correctly and the
    /// visual is corrupt from then on. Dropping the primitive is the only outcome that stays
    /// diagnosable.</para>
    /// </summary>
    private static bool Finite(params double[] values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
                return false;
        }

        return true;
    }

    private bool Count()
    {
        // Discovery draws nothing: it exists only to learn how many panels the frame has.
        if (_discovering)
            return false;

        if (OperationCount >= RenderSurfaceView.MaximumOperationsPerFrame)
        {
            WasTruncated = true;
            return false;
        }

        OperationCount++;
        return true;
    }

    private static readonly Typeface Typeface = new("Segoe UI");

    /// <summary>
    /// Alpha, clamped and NaN-safe. <c>(byte)Math.Clamp(NaN, 0, 255)</c> is undefined behaviour in
    /// the cast, so a visualizer computing an opacity from empty data could produce any colour at all.
    /// </summary>
    private Color StyleColor()
    {
        var alpha = double.IsFinite(_style.Alpha) ? Math.Clamp(_style.Alpha, 0d, 1d) : 1d;
        return Color.FromArgb(
            (byte)Math.Round(alpha * 255d),
            _style.Color.R,
            _style.Color.G,
            _style.Color.B);
    }

    private Brush CurrentBrush()
    {
        if (_brush is not null)
            return _brush;

        var brush = new SolidColorBrush(StyleColor());
        brush.Freeze();
        _brush = brush;
        return brush;
    }

    private Pen CurrentPen()
    {
        if (_pen is not null)
            return _pen;

        var thickness = double.IsFinite(_style.Thickness) && _style.Thickness > 0d
            ? _style.Thickness
            : 1d;
        var pen = new Pen(CurrentBrush(), thickness);
        if (_style.Dashed)
            pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        _pen = pen;
        return pen;
    }

    /// <summary>Balances anything the frame left open, so a sloppy visualizer cannot corrupt the context.</summary>
    internal void Close()
    {
        CloseSeries();
        while (_clipDepth > 0)
        {
            _context.Pop();
            _clipDepth--;
        }
    }

    private sealed record PanelSlot(
        int Index,
        string Title,
        RenderPanelKind Kind,
        WpfRect Bounds,
        double XMinimum = 0d,
        double XMaximum = 0d,
        string? XFormat = null,
        double YMinimum = 0d,
        double YMaximum = 0d,
        string? YFormat = null)
    {
        internal bool HasXAxis => XMaximum > XMinimum;

        internal bool HasYAxis => YMaximum > YMinimum;
    }

    private sealed class SeriesScope(RenderSeriesKind kind)
    {
        internal RenderSeriesKind Kind { get; } = kind;

        internal List<Point> Points { get; } = [];
    }

    private sealed class PanelScope(DrawingContextSurface surface, int index) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
                return;

            _closed = true;
            surface.ClosePanel(index);
        }
    }

    private sealed class SeriesCloser(DrawingContextSurface surface) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
                return;

            _closed = true;
            surface.CloseSeries();
        }
    }
}
