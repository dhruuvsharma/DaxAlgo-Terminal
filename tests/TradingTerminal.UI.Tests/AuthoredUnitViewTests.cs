using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The window body shared by authored strategies and visualizers.
///
/// <para>These are the tests that close the vertical slice. Everything up to here drew into a
/// recording surface, which proves the routines emit the right primitives but not that anything
/// reaches a screen. Here the control tree is really built from its XAML, really laid out, and really
/// rasterised — <see cref="RenderTargetBitmap"/> drives <c>OnRender</c> the way the compositor does,
/// so a broken template, an unresolved binding or a mis-sized panel shows up as missing pixels.</para>
/// </summary>
public sealed class AuthoredUnitViewTests
{
    private const double Width = 640d;
    private const double Height = 480d;

    /// <summary>A colour no theme brush uses, so finding it in the raster can only mean the unit drew it.</summary>
    private static readonly Color Sentinel = Color.FromRgb(0xFF, 0x00, 0xFF);

    [WpfFact]
    public void WhatAUnitDrawsReachesTheScreen()
    {
        var presenter = new AuthoredUnitPresenter
        {
            Title = "Sentinel",
            Draw = surface =>
            {
                using (surface.Panel("p", RenderPanelKind.Canvas))
                {
                    surface.SetStyle(new RenderStyle(new RenderColor(0xFF, 0x00, 0xFF)));
                    surface.Rect(10d, 10d, 120d, 80d);
                }
            },
        };

        var pixels = Rasterise(presenter, out _);

        Assert.Contains(Sentinel, pixels);
    }

    [WpfFact]
    public void ADrawingRoutinePaintsRealPixels()
    {
        // The end-to-end claim: an author calls one of the shipped routines and the picture appears.
        // Asserted against a blank frame rather than on an exact colour, because the ladder composites
        // its bars at 32% over the window background and an exact match would be asserting the blend.
        var blank = Rasterise(new AuthoredUnitPresenter(), out _);
        var drawn = Rasterise(
            new AuthoredUnitPresenter
            {
                Draw = surface =>
                {
                    using (surface.Panel("Book", RenderPanelKind.Ladder))
                        Ladder.Draw(surface, Book());
                },
            },
            out _);

        // Ask bars are drawn in the bearish role, which is the only red-dominant thing on screen —
        // every other brush in the palette is grey, blue or teal.
        Assert.DoesNotContain(blank, IsRedDominant);
        Assert.Contains(drawn, IsRedDominant);
    }

    [WpfFact]
    public void ThePictureGetsTheSpaceLeftOverFromTheChrome()
    {
        // The middle row is the author's, and it must be the row that absorbs the window: chrome that
        // grew at the picture's expense would silently shrink every visualizer in the product.
        var presenter = new AuthoredUnitPresenter { Title = "Depth", HasBook = true };
        presenter.Append(new AuthoredUnitLogLine(DateTime.UnixEpoch, "unit", "hello"));

        Rasterise(presenter, out var view);
        var surface = Find<RenderSurfaceView>(view);

        Assert.True(
            surface.ActualHeight > Height / 2d,
            $"the author's panel got {surface.ActualHeight:N0}px of {Height:N0}px");
        Assert.Equal(Width, surface.ActualWidth);
    }

    [WpfFact]
    public void AVisualizerHidesTheBookAndAStrategyShowsIt()
    {
        // The ONLY structural difference between the two kinds. A strategy is a visualizer that can
        // also trade, so the book is a row that appears — not a different window.
        Rasterise(new AuthoredUnitPresenter { HasBook = false }, out var withoutBook);
        Rasterise(new AuthoredUnitPresenter { HasBook = true }, out var withBook);

        Assert.True(
            Find<RenderSurfaceView>(withoutBook).ActualHeight > Find<RenderSurfaceView>(withBook).ActualHeight,
            "hiding the book row should hand its height back to the picture");
    }

    [WpfFact]
    public void ThemeRolesResolveToTheApplicationsOwnBrushes()
    {
        // An author names a role and gets whatever the theme is using. This is what stops a visualizer
        // hard-coding a colour that vanishes against the next theme — and the values asserted here are
        // the theme's, NOT the renderer's built-in fallback, so a resolver that silently stopped
        // working would fail this test rather than quietly pass it.
        RenderColor bullish = default;
        RenderColor text = default;
        var presenter = new AuthoredUnitPresenter
        {
            Draw = surface =>
            {
                using (surface.Panel("p", RenderPanelKind.Canvas))
                {
                    bullish = surface.Theme(RenderThemeColor.Bullish);
                    text = surface.Theme(RenderThemeColor.Text);
                }
            },
        };

        Rasterise(presenter, out _);

        Assert.Equal(new RenderColor(0x08, 0x99, 0x81), bullish);
        Assert.Equal(new RenderColor(0xD1, 0xD4, 0xDC), text);
    }

    [WpfFact]
    public void SwappingTheDrawCallbackRepaints()
    {
        // Hyperion recompiles a unit while its window is open, and the window has to follow. Binding
        // the callback rather than rebuilding the control is what makes that a property change instead
        // of a window teardown.
        var presenter = new AuthoredUnitPresenter();
        Assert.DoesNotContain(Sentinel, Rasterise(presenter, out var view));

        presenter.Draw = surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Canvas))
            {
                surface.SetStyle(new RenderStyle(new RenderColor(0xFF, 0x00, 0xFF)));
                surface.Rect(0d, 0d, 200d, 200d);
            }
        };

        Assert.Contains(Sentinel, Rasterise(view));
    }

    [WpfFact]
    public void AUnitThatThrowsWhileDrawingStillLeavesAUsableWindow()
    {
        // The chrome belongs to the host, so a broken unit costs its picture and nothing else — and the
        // log, which still renders, is exactly where the user then goes to find out why.
        var presenter = new AuthoredUnitPresenter
        {
            Draw = _ => throw new InvalidOperationException("unit blew up"),
        };
        presenter.Append(new AuthoredUnitLogLine(DateTime.UnixEpoch, "runtime", "unit blew up"));

        var fault = Record.Exception(() => Rasterise(presenter, out var view));

        Assert.Null(fault);
    }

    [WpfFact]
    public void DisposingTheViewStopsItListeningToThePresenter()
    {
        // Windows are opened and closed all day; a presenter that outlives its view must not keep the
        // view alive through an event subscription.
        var presenter = new AuthoredUnitPresenter();
        Rasterise(presenter, out var view);

        view.Dispose();
        presenter.Draw = _ => throw new InvalidOperationException("must not be called");

        Assert.Null(Find<RenderSurfaceView>(view).Draw);
    }

    [WpfFact]
    public void TheLogIsBoundedSoAUnitLeftRunningOvernightCannotGrowWithoutBound()
    {
        var presenter = new AuthoredUnitPresenter();

        for (var index = 0; index < AuthoredUnitPresenter.MaximumLogLines + 250; index++)
            presenter.Append(new AuthoredUnitLogLine(DateTime.UnixEpoch.AddSeconds(index), "unit", $"line {index}"));

        Assert.Equal(AuthoredUnitPresenter.MaximumLogLines, presenter.Log.Count);
        // The OLDEST lines are the ones dropped: the newest line is the one being watched.
        Assert.Equal("line 749", presenter.Log[^1].Message);
        Assert.Equal("line 250", presenter.Log[0].Message);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsRedDominant(Color colour) =>
        colour.R > colour.G + 24 && colour.R > colour.B + 24;

    private static DepthSnapshot Book() => new(
        DateTime.UnixEpoch,
        [new DepthLevel(99.9d, 40L), new DepthLevel(99.8d, 30L), new DepthLevel(99.7d, 90L)],
        [new DepthLevel(100.1d, 55L), new DepthLevel(100.2d, 20L), new DepthLevel(100.3d, 70L)]);

    /// <summary>
    /// Builds the view, gives it the brushes it would find in a running shell, lays it out and
    /// rasterises it — the same path the compositor takes.
    /// </summary>
    private static IReadOnlyCollection<Color> Rasterise(AuthoredUnitPresenter presenter, out AuthoredUnitView view)
    {
        view = new AuthoredUnitView();
        foreach (var (key, colour) in Palette)
            view.Resources[key] = new SolidColorBrush(colour);

        view.DataContext = presenter;
        return Rasterise(view);
    }

    private static IReadOnlyCollection<Color> Rasterise(AuthoredUnitView view)
    {
        view.Measure(new Size(Width, Height));
        view.Arrange(new Rect(0d, 0d, Width, Height));
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var stride = bitmap.PixelWidth * 4;
        var buffer = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(buffer, stride, 0);

        var colours = new HashSet<Color>();
        for (var offset = 0; offset < buffer.Length; offset += 4)
        {
            // Pbgra32 is premultiplied; a fully transparent pixel carries no colour worth recording.
            if (buffer[offset + 3] == 0)
                continue;

            colours.Add(Color.FromRgb(buffer[offset + 2], buffer[offset + 1], buffer[offset]));
        }

        return colours;
    }

    /// <summary>
    /// The subset of <c>Themes/TvDark.xaml</c> the view names, with that theme's real values. Supplied
    /// directly rather than loaded from the pack URI so these tests need no <see cref="Application"/>,
    /// which is process-wide and would give the suite an ordering dependency.
    /// </summary>
    private static readonly (string Key, Color Colour)[] Palette =
    [
        ("Text.Primary", Color.FromRgb(0xD1, 0xD4, 0xDC)),
        ("Text.Secondary", Color.FromRgb(0x78, 0x7B, 0x86)),
        ("Background.Primary", Color.FromRgb(0x13, 0x17, 0x22)),
        ("Background.Elevated", Color.FromRgb(0x26, 0x2B, 0x38)),
        ("Border.Brush", Color.FromRgb(0x2A, 0x2E, 0x39)),
        ("Border.Strong", Color.FromRgb(0x3A, 0x40, 0x4E)),
        ("Accent.Brush", Color.FromRgb(0x29, 0x62, 0xFF)),
        ("Bullish.Brush", Color.FromRgb(0x08, 0x99, 0x81)),
        ("Bearish.Brush", Color.FromRgb(0xF2, 0x36, 0x45)),
        ("Warning.Brush", Color.FromRgb(0xF7, 0xA6, 0x00)),
    ];

    private static T Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
            return match;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        }

        return null!;
    }
}
