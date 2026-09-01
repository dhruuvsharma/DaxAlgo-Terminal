using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// Turns a unit's <see cref="UnitLayout"/> into real panels.
///
/// <para>The body of an authored window used to be a single <see cref="RenderSurfaceView"/>: one
/// surface, one picture. That is still the default and still right for most units, but it made the
/// layouts issue #42 asks for impossible to express — two charts, an order book beside a chart, two
/// books with an arbitrage strip between them. An author could subdivide their one surface with
/// <c>PlotArea</c>, but the result is one panel wearing a drawn grid: no separate viewports, no
/// headers, and nothing the user can drag.</para>
///
/// <para>Each <see cref="PanelNode"/> here gets its own surface, so it gets its own viewport and
/// cursor, and neighbours get a <see cref="GridSplitter"/> between them. The host builds every bit of
/// this; the author supplies a tree of data and draw callbacks and never touches a WPF type.</para>
///
/// <para><b>Rebuilt wholesale when the layout changes, never mutated in place.</b> A unit's layout
/// changes when a different unit is shown, which is rare; diffing a visual tree to save that is how
/// stale panels end up bound to a previous unit's callbacks.</para>
/// </summary>
public sealed class AuthoredUnitLayoutHost : ContentControl
{
    /// <summary>Thickness of the draggable separator between neighbouring panels.</summary>
    private const double SplitterExtent = 4d;

    /// <summary>Header height for a titled panel. Zero-height for untitled ones — a single full-bleed
    /// chart should not lose six pixels to an empty bar.</summary>
    private const double HeaderExtent = 20d;

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(UnitLayout),
        typeof(AuthoredUnitLayoutHost),
        new FrameworkPropertyMetadata(null, OnLayoutChanged));

    /// <summary>The tree to build. Null or <see cref="UnitLayout.Single"/> renders one panel using
    /// <see cref="Draw"/>.</summary>
    public UnitLayout? Layout
    {
        get => (UnitLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static readonly DependencyProperty DrawProperty = DependencyProperty.Register(
        nameof(Draw),
        typeof(Action<IRenderSurface>),
        typeof(AuthoredUnitLayoutHost),
        new FrameworkPropertyMetadata(null, OnLayoutChanged));

    /// <summary>The unit's own frame callback, used for the single-panel default.</summary>
    public Action<IRenderSurface>? Draw
    {
        get => (Action<IRenderSurface>?)GetValue(DrawProperty);
        set => SetValue(DrawProperty, value);
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AuthoredUnitLayoutHost)d).Rebuild();

    /// <summary>Every surface this host built, in visual order.</summary>
    private readonly List<RenderSurfaceView> _surfaces = [];

    private Func<RenderThemeColor, Color>? _themeResolver;

    /// <summary>
    /// How the unit's palette tokens become colours. Applied to <b>every</b> surface, not just the
    /// first — a multi-panel window whose second panel resolved its own defaults would render two
    /// different themes side by side, and it would look like an authoring mistake rather than a
    /// missing assignment here.
    /// </summary>
    public Func<RenderThemeColor, Color>? ThemeResolver
    {
        get => _themeResolver;
        set
        {
            _themeResolver = value;
            foreach (var surface in _surfaces) surface.ThemeResolver = value;
        }
    }

    /// <summary>Repaints every panel. The runtime asks for one frame, and a frame is the whole
    /// window — invalidating only one surface would freeze the others on a stale picture.</summary>
    public void InvalidateSurfaces()
    {
        foreach (var surface in _surfaces) surface.InvalidateVisual();
    }

    private Func<DateTime>? _clock;

    /// <summary>
    /// The host clock every panel draws against, and it is deliberately ONE for the whole unit.
    ///
    /// <para>Per-panel clocks would be the obvious implementation and would be wrong twice over. The
    /// views are constructed milliseconds apart as the tree is built and a rebuild replaces only some
    /// of them, so two panels animating the same thing would sit permanently out of phase. And it has
    /// to be the same clock the unit reads in its data callbacks: a unit stamps an event as it arrives
    /// and computes the age while drawing, and two origins make that subtraction meaningless.</para>
    ///
    /// <para>Applied to every surface for the same reason <see cref="ThemeResolver"/> is — including
    /// the ones already built, since the host is assembled before the clock is handed over.</para>
    /// </summary>
    public Func<DateTime>? Clock
    {
        get => _clock;
        set
        {
            _clock = value;
            foreach (var surface in _surfaces) surface.Clock = value;
        }
    }

    private RenderSurfaceView NewSurface(Action<IRenderSurface>? draw)
    {
        var surface = new RenderSurfaceView
        {
            Draw = draw,
            ThemeResolver = _themeResolver,
            Clock = _clock,
        };
        _surfaces.Add(surface);
        return surface;
    }

    private void Rebuild()
    {
        var layout = Layout;
        _surfaces.Clear();

        // The default, and the path almost every unit takes: one surface filling the body.
        if (layout is null || layout.IsSingle || layout.Root is null)
        {
            Content = NewSurface(Draw);
            return;
        }

        Content = Build(layout.Root);
    }

    private UIElement Build(LayoutNode node) => node switch
    {
        PanelNode panel => BuildPanel(panel),
        SplitNode split => BuildSplit(split),

        // Unreachable: the vocabulary is a closed hierarchy with a private-protected base, so no
        // third node type can exist outside the SDK. An empty surface beats throwing on a render pass.
        _ => NewSurface(null),
    };

    private UIElement BuildPanel(PanelNode panel)
    {
        var surface = NewSurface(panel.Draw);
        if (string.IsNullOrWhiteSpace(panel.Title)) return surface;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderExtent) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });

        var header = new TextBlock
        {
            Text = panel.Title,
            FontSize = 9.5d,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(8d, 3d, 8d, 0d),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        header.SetResourceReference(TextBlock.BackgroundProperty, "Background.Elevated");

        Grid.SetRow(header, 0);
        Grid.SetRow(surface, 1);
        grid.Children.Add(header);
        grid.Children.Add(surface);
        return grid;
    }

    private UIElement BuildSplit(SplitNode split)
    {
        var rows = split.Orientation == SplitOrientation.Rows;
        var grid = new Grid();

        // Two definitions per child after the first — the child and the splitter before it — so a
        // definition index is not the same as a child index. Tracked explicitly rather than computed,
        // because off-by-one here puts a panel under a splitter and is invisible until someone drags.
        var index = 0;
        for (var i = 0; i < split.Children.Count; i++)
        {
            if (i > 0)
            {
                AddDefinition(grid, rows, new GridLength(SplitterExtent));
                var splitter = new GridSplitter
                {
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    HorizontalAlignment = rows ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                    VerticalAlignment = rows ? VerticalAlignment.Center : VerticalAlignment.Stretch,
                    Width = rows ? double.NaN : SplitterExtent,
                    Height = rows ? SplitterExtent : double.NaN,
                };
                splitter.SetResourceReference(BackgroundProperty, "Border.Brush");
                Place(grid, splitter, rows, index++);
                grid.Children.Add(splitter);
            }

            var child = split.Children[i];
            AddDefinition(grid, rows, Length(child.Size));
            var element = Build(child);
            Place(grid, element, rows, index++);
            grid.Children.Add(element);
        }

        return grid;
    }

    private static GridLength Length(PanelSize size) => size.Unit switch
    {
        PanelSizeUnit.Pixels => new GridLength(size.Value),
        _ => new GridLength(size.Value, GridUnitType.Star),
    };

    private static void AddDefinition(Grid grid, bool rows, GridLength length)
    {
        if (rows) grid.RowDefinitions.Add(new RowDefinition { Height = length });
        else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
    }

    private static void Place(Grid grid, UIElement element, bool rows, int index)
    {
        if (rows) Grid.SetRow(element, index);
        else Grid.SetColumn(element, index);
    }
}
