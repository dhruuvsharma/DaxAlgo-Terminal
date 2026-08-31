using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DaxAlgo.Sdk;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The gestures a chart needs and the surface could not express: a pinned level, zoom, and a pan.
///
/// <para><b>Why they are state and not events.</b> A click, a wheel notch and a drag are transitions,
/// and <c>OnRender</c> invokes the draw callback twice — a discovery pass, then the real one — so
/// <c>Draw</c> must be pure and cannot consume a transition without firing twice. The host therefore
/// accumulates each gesture into a value that stays put, and a unit reads it. Everything below asserts
/// that accumulation, and that it arrives at the surface the unit actually draws through.</para>
///
/// <para>The first test is the one that mattered: <c>IsPressed</c> was sampled on <c>MouseMove</c>
/// alone, so a press that did not move was invisible and a release that did not move never cleared.
/// It was not coarse, it was wrong, and a unit had no way to tell.</para>
/// </summary>
public sealed class RenderSurfaceGestureTests
{
    [WpfFact]
    public void APressThatDoesNotMoveIsStillAPress()
    {
        // The defect, directly. Sampling the button on MouseMove meant press-and-hold-still read as
        // not-pressed, and release-and-hold-still read as still-pressed.
        var view = Arrange(_ => { });

        view.PressAt(new Point(40d, 20d));
        Assert.True(view.CurrentCursor.IsPressed);

        view.ReleaseAt(new Point(40d, 20d));
        Assert.False(view.CurrentCursor.IsPressed);
    }

    [WpfFact]
    public void AClickPinsThePointItLandedOn()
    {
        var view = Arrange(_ => { });

        view.PressAt(new Point(40d, 20d));
        view.ReleaseAt(new Point(40d, 20d));

        var cursor = view.CurrentCursor;
        Assert.True(cursor.HasSelection);
        Assert.Equal(40d, cursor.SelectionX);
        Assert.Equal(20d, cursor.SelectionY);
    }

    [WpfFact]
    public void ADragPansAndPinsNothing()
    {
        // Pinning a level every time someone panned the chart would make the highlight noise.
        var view = Arrange(_ => { });

        view.PressAt(new Point(40d, 20d));
        view.MoveTo(new Point(70d, 35d));
        view.ReleaseAt(new Point(70d, 35d));

        Assert.False(view.CurrentCursor.HasSelection);
        Assert.Equal(30d, view.CurrentTransform.PanX);
        Assert.Equal(15d, view.CurrentTransform.PanY);
    }

    [WpfFact]
    public void AMouseThatTwitchesUnderTheFingerIsStillAClick()
    {
        // A book is clicked with a mouse that moves a pixel or two on the way down.
        var view = Arrange(_ => { });

        view.PressAt(new Point(40d, 20d));
        view.MoveTo(new Point(41d, 21d));
        view.ReleaseAt(new Point(41d, 21d));

        Assert.True(view.CurrentCursor.HasSelection);
    }

    [WpfFact]
    public void TheWheelZoomsBothWaysAndIsBounded()
    {
        // Unbounded zoom lets a viewer reach a range of 1e-300 and then a NaN, in a unit that did
        // nothing wrong.
        var view = Arrange(_ => { });

        view.Wheel(120);
        Assert.True(view.CurrentTransform.Zoom > 1d);

        view.Wheel(-240);
        Assert.True(view.CurrentTransform.Zoom < 1d);

        for (var i = 0; i < 200; i++) view.Wheel(120);
        Assert.True(view.CurrentTransform.Zoom <= 32d);

        for (var i = 0; i < 400; i++) view.Wheel(-120);
        Assert.True(view.CurrentTransform.Zoom >= 0.25d);
    }

    [WpfFact]
    public void ResetPutsTheViewBackBecauseAUnitCannot()
    {
        // A unit reads the transform and never writes it, so returning to unzoomed has to be the
        // host's to offer.
        var view = Arrange(_ => { });

        view.Wheel(240);
        view.PressAt(new Point(10d, 10d));
        view.MoveTo(new Point(60d, 40d));
        view.ReleaseAt(new Point(60d, 40d));

        view.ResetView();

        Assert.Equal(1d, view.CurrentTransform.Zoom);
        Assert.Equal(0d, view.CurrentTransform.PanX);
        Assert.False(view.CurrentCursor.HasSelection);
    }

    // ── reaching the unit ───────────────────────────────────────────────────────────────────────

    [WpfFact]
    public void EveryGestureReachesTheSurfaceTheUnitDrawsThrough()
    {
        // The assertion that matters. Accumulating state the draw callback never sees would be the
        // same defect in a new place.
        RenderCursor cursor = default;
        RenderViewport viewport = default;

        var view = Arrange(surface =>
        {
            using var panel = surface.Panel("p", RenderPanelKind.Chart);
            cursor = surface.Cursor;
            viewport = surface.Viewport;
        });

        view.Wheel(120);
        view.PressAt(new Point(40d, 20d));
        view.ReleaseAt(new Point(40d, 20d));
        Render(view);

        Assert.True(cursor.HasSelection, "the pinned point must reach Draw");
        Assert.Equal(40d, cursor.SelectionX);
        Assert.True(viewport.Zoom > 1d, "the zoom must reach Draw");
    }

    [WpfFact]
    public void AnUntouchedSurfaceIsUnzoomedRatherThanDegenerate()
    {
        // default(RenderViewport) would carry Zoom 0, and every unit dividing its window by it.
        RenderViewport viewport = default;
        var view = Arrange(surface =>
        {
            using var panel = surface.Panel("p", RenderPanelKind.Chart);
            viewport = surface.Viewport;
        });

        Render(view);

        Assert.Equal(1d, viewport.Zoom);
        Assert.False(view.CurrentCursor.HasSelection);
    }

    [WpfFact]
    public void APinnedLevelBelongsToItsOwnPanel()
    {
        // Two panels must never both think they hold the selection — the same rule the hover state
        // already followed, and the reason it is mapped panel-by-panel rather than raised globally.
        // Tagged by panel rather than counted: OnRender draws twice per frame and arranging the
        // control can produce a frame of its own, so the number of reads is not fixed. Which panel
        // holds the pin is.
        var seen = new List<(string Panel, RenderCursor Cursor)>();
        var view = Arrange(surface =>
        {
            using (surface.Panel("top", RenderPanelKind.Chart)) seen.Add(("top", surface.Cursor));
            using (surface.Panel("bottom", RenderPanelKind.Chart)) seen.Add(("bottom", surface.Cursor));
        });

        // Panels stack and share the height, so with a 100px control this lands in the lower one.
        view.PressAt(new Point(30d, 80d));
        view.ReleaseAt(new Point(30d, 80d));
        seen.Clear();
        Render(view);

        Assert.Contains(seen, s => s.Panel == "bottom" && s.Cursor.HasSelection);
        Assert.DoesNotContain(seen, s => s.Panel == "top" && s.Cursor.HasSelection);

        // And it is mapped into that panel: two panels share a 100px control, so the lower one starts
        // at y = 50 and a click at y = 80 is 30 from its own top.
        Assert.All(
            seen.Where(s => s.Cursor.HasSelection), s => Assert.Equal(30d, s.Cursor.SelectionY));
    }

    [WpfFact]
    public void APinnedLevelSurvivesThePointerLeaving()
    {
        // Which is exactly when someone is looking at what they pinned. Mapping the selection
        // alongside the hover state would have cleared it on the way out.
        RenderCursor cursor = default;
        var view = Arrange(surface =>
        {
            using var panel = surface.Panel("p", RenderPanelKind.Chart);
            cursor = surface.Cursor;
        });

        view.PressAt(new Point(40d, 20d));
        view.ReleaseAt(new Point(40d, 20d));
        view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        Render(view);

        Assert.False(cursor.IsInside, "the pointer has left");
        Assert.True(cursor.HasSelection, "but the pin stands");
    }

    // ── the handlers are actually subscribed ────────────────────────────────────────────────────

    [WpfFact]
    public void TheControlSubscribesTheButtonAndWheelEvents()
    {
        // The rules above are internal methods, so they would pass just as well on a control that
        // never listened for a button. These raise the real routed events. The POSITION cannot be
        // asserted here — GetPosition reads the physical device — but the state transition is
        // position-independent, and it is the subscription being proven.
        var view = Arrange(_ => { });

        view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent,
        });
        Assert.True(view.CurrentCursor.IsPressed, "MouseDown is not wired");

        view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseUpEvent,
        });
        Assert.False(view.CurrentCursor.IsPressed, "MouseUp is not wired");
        Assert.True(view.CurrentCursor.HasSelection, "MouseUp does not pin");

        view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
        });
        Assert.True(view.CurrentTransform.Zoom > 1d, "MouseWheel is not wired");
    }

    [WpfFact]
    public void ARightButtonIsNotATrade_OrASelection()
    {
        // Only the primary button pins. A context menu must not move the pinned level under it.
        var view = Arrange(_ => { });

        view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = Mouse.MouseDownEvent,
        });
        view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = Mouse.MouseUpEvent,
        });

        Assert.False(view.CurrentCursor.IsPressed);
        Assert.False(view.CurrentCursor.HasSelection);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static RenderSurfaceView Arrange(Action<IRenderSurface> draw) => new() { Draw = draw };

    private static void Render(RenderSurfaceView view, double width = 200d, double height = 100d)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0d, 0d, width, height));

        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        typeof(RenderSurfaceView)
            .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, [context]);
    }
}
