using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The clock a unit animates from, driven through the real controls.
///
/// <para>Two properties carry the whole design, and both are the kind that look obviously true and are
/// obviously false the moment the implementation is naive: one frame is one instant, and one unit is
/// one clock.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class AuthoredWindowClockTests
{
    [WpfFact]
    public void Both_passes_of_one_frame_see_the_same_instant()
    {
        // OnRender invokes the draw callback TWICE — a discovery pass to count panels, then the real
        // one. A surface that read the clock itself would hand those two passes different instants, and
        // a unit whose panel count depended on time would be laid out against a frame it never drew.
        // The same failure the pointer-blind discovery pass exists to prevent, with a different input.
        var ticks = 0;
        var seen = new List<DateTime>();

        var view = new RenderSurfaceView
        {
            Clock = () => Origin.AddSeconds(ticks++),
            Draw = surface => seen.Add(surface.Now),
        };

        Render(view);

        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0], seen[1]);
    }

    [WpfFact]
    public void Every_panel_of_one_unit_shares_the_clock()
    {
        // Per-panel clocks are the obvious implementation and they drift: the views are built
        // milliseconds apart as the tree is assembled, so two panels animating the same thing would sit
        // permanently out of phase and look like an authoring mistake.
        var seen = new Dictionary<string, DateTime>();

        var host = new AuthoredUnitLayoutHost
        {
            Clock = () => Origin,
            Layout = UnitLayout.Rows(
                UnitLayout.Panel("Top", s => seen["Top"] = s.Now).Star(1),
                UnitLayout.Panel("Bottom", s => seen["Bottom"] = s.Now).Star(1)),
        };

        Render(host);

        Assert.Equal(Origin, seen["Top"]);
        Assert.Equal(Origin, seen["Bottom"]);
    }

    [WpfFact]
    public void A_clock_set_after_the_panels_were_built_still_reaches_them()
    {
        // The order the window is assembled in: the host is constructed and its layout applied, and the
        // clock arrives later when the presenter is attached. Applying it only in NewSurface would
        // leave every already-built panel frozen at DateTime.MinValue — the same shape as a theme
        // resolver that only reached the first surface.
        var seen = new List<DateTime>();

        var host = new AuthoredUnitLayoutHost
        {
            Layout = UnitLayout.Rows(UnitLayout.Panel("Only", s => seen.Add(s.Now)).Star(1)),
        };

        host.Clock = () => Origin;
        Render(host);

        Assert.NotEmpty(seen);
        Assert.All(seen, instant => Assert.Equal(Origin, instant));
    }

    [WpfFact]
    public void With_no_clock_a_frame_is_drawn_at_MinValue()
    {
        // A preview and a still have no clock. A unit must look sensible there, so the honest answer is
        // "no time", not an invented one.
        DateTime? seen = null;
        var view = new RenderSurfaceView { Draw = surface => seen = surface.Now };

        Render(view);

        Assert.Equal(DateTime.MinValue, seen);
    }

    private static readonly DateTime Origin = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private static void Render(FrameworkElement element, double width = 320d, double height = 200d)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0d, 0d, width, height));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(element);
    }
}
