using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DaxAlgo.Sdk;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// The control that turns <see cref="IRenderSurface"/> calls into pixels.
///
/// <para>This is the substrate the whole control library stands on. A visualizer draws through the
/// SDK surface and never touches WPF; the richer controls (ladder, footprint, candles) are drawing
/// routines over the same surface hosted by this control, so there is exactly ONE implementation of
/// each picture rather than one for sandboxed visualizers and another for host windows.</para>
///
/// <para>Immediate mode maps directly onto <see cref="OnRender"/>: the callback describes the whole
/// frame, WPF composites it, and nothing is retained. That is why a visualizer holds no visual state
/// the host has to reconcile.</para>
///
/// <para><b>Drawing happens in data space.</b> A panel that declares axes gets its coordinates
/// transformed from data units to pixels, so a visualizer plots in price and time rather than doing
/// its own arithmetic. A panel with no axes is in pixel space, which is the right default for free
/// layout.</para>
/// </summary>
public sealed class RenderSurfaceView : FrameworkElement
{
    /// <summary>
    /// Hard cap on primitives per frame. A visualizer that draws unreasonably is truncated rather
    /// than allowed to wedge the UI thread — untrusted code gets bounded, not trusted.
    /// </summary>
    public const int MaximumOperationsPerFrame = 20_000;

    public static readonly DependencyProperty DrawProperty = DependencyProperty.Register(
        nameof(Draw),
        typeof(Action<IRenderSurface>),
        typeof(RenderSurfaceView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The frame callback. Invoked on every render pass; it must not block.</summary>
    public Action<IRenderSurface>? Draw
    {
        get => (Action<IRenderSurface>?)GetValue(DrawProperty);
        set => SetValue(DrawProperty, value);
    }

    /// <summary>Resolves theme roles to brushes, so a visualizer never names a literal colour.</summary>
    public Func<RenderThemeColor, Color>? ThemeResolver { get; set; }

    /// <summary>Primitives drawn in the last frame, for diagnostics and for spotting a runaway visualizer.</summary>
    public int LastFrameOperationCount { get; private set; }

    /// <summary>True when the last frame hit <see cref="MaximumOperationsPerFrame"/>.</summary>
    public bool LastFrameWasTruncated { get; private set; }

    /// <summary>How far one wheel notch zooms. Compounding, so three notches is 1.2³.</summary>
    private const double ZoomPerNotch = 1.2d;

    /// <summary>Zoom bounds. Unbounded zoom lets a viewer reach a range of 1e-300 and then a NaN, in
    /// a unit that did nothing wrong.</summary>
    private const double MinimumZoom = 0.25d;
    private const double MaximumZoom = 32d;

    /// <summary>How far the pointer may travel between press and release and still be a click rather
    /// than a drag. A book is clicked with a mouse that moves a pixel or two on the way down.</summary>
    private const double ClickSlop = 4d;

    public RenderSurfaceView()
    {
        ClipToBounds = true;

        // Pointer state is a read on the surface rather than an event, so the control only has to keep
        // the latest position and invalidate — visualizers that ignore the cursor cost nothing.
        //
        // Every gesture below is accumulated into STATE rather than dispatched as an event, and that
        // is forced by the contract rather than chosen: OnRender invokes the draw callback twice (a
        // discovery pass, then the real one) so Draw must be pure, and a pure Draw cannot consume a
        // click, a notch or a delta. Sticky state is the only shape a unit can read safely.
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => ClearCursor();

        // IsPressed used to be sampled on MouseMove ALONE, so a press that did not move was invisible
        // and a release that did not move never cleared. Reporting the button state wrongly until the
        // pointer next moves is worse than not reporting it, because a unit cannot tell the two apart.
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
    }

    private Point _cursor;
    private bool _cursorInside;
    private bool _cursorPressed;

    private Point _pressOrigin;
    private Point _dragAnchor;
    private bool _dragged;

    private Point _selection;
    private bool _hasSelection;

    private double _zoom = 1d;
    private Vector _pan;

    /// <summary>The pointer state a frame is drawn with — the raw, control-space values that
    /// <c>DrawingContextSurface</c> maps into whichever panel is open.</summary>
    internal RenderCursor CurrentCursor => new(_cursor.X, _cursor.Y, _cursorInside, _cursorPressed)
    {
        HasSelection = _hasSelection,
        SelectionX = _selection.X,
        SelectionY = _selection.Y,
    };

    /// <summary>The accumulated view transform a frame is drawn with.</summary>
    internal (double Zoom, double PanX, double PanY) CurrentTransform => (_zoom, _pan.X, _pan.Y);

    /// <summary>Puts the view back where it started. The host offers this because a unit cannot: it
    /// reads the transform and never writes it.</summary>
    public void ResetView()
    {
        _zoom = 1d;
        _pan = default;
        _hasSelection = false;
        InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => MoveTo(e.GetPosition(this));

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        PressAt(e.GetPosition(this));

        // Without capture a drag that leaves the control never gets its MouseUp, and the surface stays
        // pressed forever — the same class of bug as the one this replaced.
        CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        ReleaseAt(e.GetPosition(this));
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        Wheel(e.Delta);

        // Marked handled so an ancestor ScrollViewer cannot scroll the pane out from under a chart
        // the viewer is zooming. No such ancestor exists today — the authored-unit body and the
        // authoring preview both sit in a plain Grid row, checked — so this is a guard rather than a
        // fix for an observed bug. It costs nothing and the surface is the substrate every tool window
        // draws on, which is exactly the kind of place a scrolling ancestor arrives later.
        e.Handled = true;
    }

    // The four below carry every rule; the handlers above only turn an event into a position. Split
    // that way because a routed mouse event cannot be given a position from a test — GetPosition reads
    // the real device — so the rules would otherwise be assertable only by hand.

    internal void MoveTo(Point position)
    {
        if (_cursorPressed)
        {
            // Held and moved: a pan. Tracked from the last position rather than from the origin, so
            // the picture follows the pointer instead of accelerating away from it.
            _pan += position - _dragAnchor;
            _dragAnchor = position;
            if ((position - _pressOrigin).Length > ClickSlop) _dragged = true;
        }

        _cursor = position;
        _cursorInside = true;
        InvalidateVisual();
    }

    internal void PressAt(Point position)
    {
        _cursor = _pressOrigin = _dragAnchor = position;
        _cursorInside = true;
        _cursorPressed = true;
        _dragged = false;
        InvalidateVisual();
    }

    internal void ReleaseAt(Point position)
    {
        _cursorPressed = false;

        // A press and release that did not travel is a click, and a click is what pins a level. A drag
        // is not: pinning a level every time someone panned the chart would make the highlight noise.
        if (!_dragged)
        {
            _selection = position;
            _hasSelection = true;
        }

        InvalidateVisual();
    }

    internal void Wheel(int delta)
    {
        var notches = delta / 120d;
        _zoom = Math.Clamp(_zoom * Math.Pow(ZoomPerNotch, notches), MinimumZoom, MaximumZoom);
        InvalidateVisual();
    }

    private void ClearCursor()
    {
        // The pointer leaving does not release the button — a captured drag is still a drag — and it
        // does not clear the selection either. A pinned level that vanished when you moved away to
        // read it would be useless.
        if (IsMouseCaptured) return;

        _cursorInside = false;
        _cursorPressed = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Draw is not { } draw || ActualWidth <= 0d || ActualHeight <= 0d)
        {
            LastFrameOperationCount = 0;
            LastFrameWasTruncated = false;
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var size = new Size(ActualWidth, ActualHeight);
        var cursor = CurrentCursor;
        var theme = ThemeResolver ?? DefaultTheme;

        // A transparent ground over the whole control, so the pointer is over SOMETHING everywhere.
        // A FrameworkElement is hit-tested against what it drew, so without this a click that landed
        // between two strokes reached nothing and no gesture was ever recorded there — the picture
        // would appear to ignore clicks in its empty regions, which is most of it.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        // Two passes. The first draws nothing and only counts panels, so the second can give every
        // panel its correct share of the height — otherwise the first panel opened is laid out
        // before the frame knows how many follow it, and is always drawn wrong.
        //
        // This is why Draw MUST BE PURE: it is invoked more than once per frame. Computation belongs
        // in the visualizer's data callbacks; Draw only describes the picture. It is also why every
        // gesture arrives as accumulated state rather than as an event.
        // The discovery pass is pointer-BLIND, deliberately.
        //
        // Its only job is to count panels, and a count that varies with the pointer is a layout that
        // rearranges as the mouse moves: a unit branching on Cursor.IsInside or HasSelection would
        // open a different number of panels on the two passes of the same frame, and every panel would
        // get the wrong share of the height. Panel structure must not depend on pointer state, and
        // handing discovery a blank cursor is what makes that true rather than merely advised.
        //
        // It also removes an answer that was wrong anyway: during discovery no panel has its final
        // bounds, so every panel contains every point and each would have reported the same click.
        var discovery = new DrawingContextSurface(
            drawingContext, size, scale, default, theme, discovering: true, transform: CurrentTransform);
        try
        {
            draw(discovery);
        }
        catch (Exception)
        {
            // A visualizer that throws during discovery still gets a drawing pass; it will most
            // likely throw there too, and that path already degrades to a partial frame.
        }

        var surface = new DrawingContextSurface(
            drawingContext,
            size,
            scale,
            cursor,
            theme,
            expectedPanels: discovery.PanelCount,
            transform: CurrentTransform);

        try
        {
            draw(surface);
        }
        catch (Exception)
        {
            // A visualizer that throws mid-frame must not take the window down with it. The partial
            // frame stays on screen; the runtime that owns the visualizer is what reports the fault.
        }
        finally
        {
            surface.Close();
            LastFrameOperationCount = surface.OperationCount;
            LastFrameWasTruncated = surface.WasTruncated;
        }
    }

    /// <summary>A readable fallback palette for a host that supplies no resolver.</summary>
    private static Color DefaultTheme(RenderThemeColor token) => token switch
    {
        RenderThemeColor.Text => Color.FromRgb(0xE6, 0xED, 0xF3),
        RenderThemeColor.TextSecondary => Color.FromRgb(0x8C, 0x9A, 0xB3),
        RenderThemeColor.Background => Color.FromRgb(0x0D, 0x11, 0x17),
        RenderThemeColor.Surface => Color.FromRgb(0x16, 0x1B, 0x22),
        RenderThemeColor.Grid => Color.FromRgb(0x22, 0x2A, 0x35),
        RenderThemeColor.Border => Color.FromRgb(0x30, 0x3A, 0x48),
        RenderThemeColor.Accent => Color.FromRgb(0x2F, 0x6B, 0xD4),
        RenderThemeColor.Bullish => Color.FromRgb(0x26, 0xA6, 0x69),
        RenderThemeColor.Bearish => Color.FromRgb(0xC0, 0x26, 0x26),
        RenderThemeColor.Warning => Color.FromRgb(0xD9, 0x8A, 0x0B),
        _ => Color.FromRgb(0x8C, 0x9A, 0xB3),
    };
}
