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

    public RenderSurfaceView()
    {
        ClipToBounds = true;
        // Pointer state is a read on the surface rather than an event, so the control only has to
        // keep the latest position and invalidate — visualizers that ignore the cursor cost nothing.
        MouseMove += (_, e) => UpdateCursor(e.GetPosition(this), pressed: e.LeftButton == MouseButtonState.Pressed);
        MouseLeave += (_, _) => ClearCursor();
    }

    private Point _cursor;
    private bool _cursorInside;
    private bool _cursorPressed;

    private void UpdateCursor(Point position, bool pressed)
    {
        _cursor = position;
        _cursorInside = true;
        _cursorPressed = pressed;
        InvalidateVisual();
    }

    private void ClearCursor()
    {
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
        var cursor = new RenderCursor(_cursor.X, _cursor.Y, _cursorInside, _cursorPressed);
        var theme = ThemeResolver ?? DefaultTheme;

        // Two passes. The first draws nothing and only counts panels, so the second can give every
        // panel its correct share of the height — otherwise the first panel opened is laid out
        // before the frame knows how many follow it, and is always drawn wrong.
        //
        // This is why Draw MUST BE PURE: it is invoked more than once per frame. Computation belongs
        // in the visualizer's data callbacks; Draw only describes the picture.
        var discovery = new DrawingContextSurface(drawingContext, size, scale, cursor, theme, discovering: true);
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
            expectedPanels: discovery.PanelCount);

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
