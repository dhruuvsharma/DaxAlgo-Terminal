using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.IndexKScoreSurface;

public partial class IndexKScoreSurfaceWindow : MetroWindow
{
    private IndexKScoreSurfaceViewModel? _vm;

    /// <summary>Vertical exaggeration of the K-axis. K_final ∈ [-1.5, +1.5] is mapped to
    /// world-Z in [-0.5, +0.5] × HeightScale so the surface looks pronounced next to the
    /// unit-square X/Y footprint. Matches the OrderFlowSurfaceSpike / ImbalanceHeatFront
    /// convention so the three cube/surface windows feel consistent side-by-side.</summary>
    private const double HeightScale = 1.6;

    /// <summary>Maximum |K| we map to the renderer (anything beyond clamps to the edge of the
    /// gradient). K_final ∈ [-1.5, +1.5], so 1.5 is the natural ceiling.</summary>
    private const double KMaxClamp = 1.5;

    public IndexKScoreSurfaceWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SurfaceChanged -= OnSurfaceChanged;
            ((INotifyCollectionChanged)_vm.LogEntries).CollectionChanged -= OnLogChanged;
        }
        _vm = e.NewValue as IndexKScoreSurfaceViewModel;
        if (_vm is not null)
        {
            _vm.SurfaceChanged += OnSurfaceChanged;
            ((INotifyCollectionChanged)_vm.LogEntries).CollectionChanged += OnLogChanged;
            Redraw();
        }
    }

    // LogEntries are newest-first (VM Insert(0, ...)), so scroll to top on add.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) LogScroller.ScrollToTop();
    }

    private void OnSurfaceChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm is null) return;
        View3D.Children.Clear();
        View3D.Children.Add(new DefaultLights());

        AddAxis(new Point3D(0, 0, 0), new Point3D(1, 0, 0), Colors.IndianRed,
            "Component →", "light", "heavy");
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 1, 0), Colors.LimeGreen,
            "Time →", $"-{IndexKScoreSurfaceViewModel.KHistoryLength}", "now");
        AddAxis(new Point3D(0, 0, -0.5 * HeightScale), new Point3D(0, 0, 0.5 * HeightScale), Colors.DeepSkyBlue,
            "K (±1.5)", "−1.5", "+1.5");
        AddBoundsWireframe();

        var surface = _vm.Surface;
        var thresholds = _vm.Thresholds;
        if (surface is null || thresholds is null) return;

        var rows = surface.GetLength(0);
        var cols = surface.GetLength(1);
        if (rows < 2 || cols < 2) return;

        var mesh = BuildSurfaceMesh(surface, thresholds, rows, cols);
        var material = BuildHeatmapMaterial();
        var model = new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material };
        View3D.Children.Add(new ModelVisual3D { Content = model });

        // Threshold ridge — draw the per-column threshold curtain at the latest time row so the
        // user sees where the pivot lives. Thin translucent green band on the +side, red on −.
        AddThresholdCurtain(thresholds, cols);
    }

    /// <summary>
    /// Builds the heightmap surface mesh. Per-vertex texture U is the key trick:
    /// <list type="bullet">
    /// <item>If <c>|K| &lt; threshold[c]</c>: U is mapped to a cool-blue region of the gradient,
    /// proportional to <c>|K| / threshold</c> so cells inside the band still show a subtle
    /// "approaching the limit" hint.</item>
    /// <item>If <c>K &gt;= threshold</c>: U ramps into the warm half of the gradient (orange/red),
    /// scaled by <c>(K - threshold) / (1.5 - threshold)</c>.</item>
    /// <item>If <c>K &lt;= -threshold</c>: U ramps into the deepest-cool half (icy cyan),
    /// scaled symmetrically.</item>
    /// </list>
    /// The Z-axis height carries the signed K value directly, so direction is unambiguous from
    /// geometry even when the user can't read the color.
    /// </summary>
    private static MeshGeometry3D BuildSurfaceMesh(double[,] surface, double[] thresholds, int rows, int cols)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection(rows * cols);
        var textureCoords = new PointCollection(rows * cols);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var k = Math.Clamp(surface[r, c], -KMaxClamp, KMaxClamp);
                var thr = thresholds[c];

                positions.Add(new Point3D(
                    c / (double)(cols - 1),
                    r / (double)(rows - 1),
                    k / KMaxClamp * 0.5 * HeightScale));

                var u = MapTextureU(k, thr);
                textureCoords.Add(new Point(u, 0.5));
            }
        }

        var indices = new Int32Collection((rows - 1) * (cols - 1) * 6);
        for (var r = 0; r < rows - 1; r++)
        {
            for (var c = 0; c < cols - 1; c++)
            {
                var i00 = r * cols + c;
                var i01 = r * cols + (c + 1);
                var i10 = (r + 1) * cols + c;
                var i11 = (r + 1) * cols + (c + 1);
                indices.Add(i00); indices.Add(i10); indices.Add(i01);
                indices.Add(i01); indices.Add(i10); indices.Add(i11);
            }
        }

        mesh.Positions = positions;
        mesh.TextureCoordinates = textureCoords;
        mesh.TriangleIndices = indices;
        return mesh;
    }

    /// <summary>
    /// Maps a signed K value to texture U in [0, 1] given the per-column threshold. Cool band
    /// (blue/cyan) for |K| &lt; threshold; warm half (orange/red) once positive K crosses up;
    /// icy half (deep blue) once negative K crosses down. The "no-signal" middle compresses to
    /// a narrow blue strip at U = 0.45–0.55 so the heat zones dominate visually.
    /// </summary>
    private static double MapTextureU(double k, double threshold)
    {
        if (threshold <= 0) threshold = 0.01;
        if (Math.Abs(k) < threshold)
        {
            // Below threshold — stay in the blue plateau. Slight tilt so values approaching the
            // band edge are subtly distinguishable from dead-zero.
            var t = k / threshold; // ∈ (-1, +1)
            return 0.5 - 0.05 * t;  // 0.45 .. 0.55 (very narrow blue strip)
        }

        if (k > 0)
        {
            // Warm half: linear ramp from 0.55 (just above threshold) → 1.0 (deep red at K_max).
            var span = Math.Max(KMaxClamp - threshold, 1e-3);
            var t = Math.Clamp((k - threshold) / span, 0, 1);
            return 0.55 + 0.45 * t;
        }
        else
        {
            // Icy half: linear ramp from 0.45 → 0.0 (deepest blue at K_min).
            var span = Math.Max(KMaxClamp - threshold, 1e-3);
            var t = Math.Clamp((-k - threshold) / span, 0, 1);
            return 0.45 - 0.45 * t;
        }
    }

    private static Material? _cachedHeatmap;
    private static Material BuildHeatmapMaterial()
    {
        if (_cachedHeatmap is not null) return _cachedHeatmap;

        // Bipolar heat gradient with a wide neutral-blue band centered on 0.5:
        // 0.00 (deep ice blue, K = -1.5) → 0.30 (cool blue) → 0.45 (blue plateau, sub-threshold) →
        // 0.55 (blue plateau) → 0.70 (orange) → 1.00 (deep red, K = +1.5).
        // The plateau range (0.45-0.55) is where MapTextureU compresses the below-threshold zone,
        // so the mesh appears uniformly blue when no component is piercing — matches the user
        // brief: blue below the threshold surface, full heat colors once crossed.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x06, 0x2E, 0x6C), 0.00)); // deep ice blue
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0x4D, 0xA8), 0.20));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x2C, 0x6F, 0xBF), 0.40));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x7C, 0xC8), 0.45)); // plateau start
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x7C, 0xC8), 0.55)); // plateau end (same color)
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC6, 0x8A, 0x2E), 0.70));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0x5C, 0x12), 0.85));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0x3C, 0x12), 1.00)); // deep red
        brush.Freeze();

        _cachedHeatmap = new DiffuseMaterial(brush);
        ((DiffuseMaterial)_cachedHeatmap).Freeze();
        return _cachedHeatmap;
    }

    /// <summary>
    /// Draws the per-column +threshold and -threshold lines at the latest time row, so the user
    /// can see where the pivot lives on the surface. Translucent so the underlying mesh shows.
    /// </summary>
    private void AddThresholdCurtain(double[] thresholds, int cols)
    {
        var ptsUp = new Point3DCollection();
        var ptsDown = new Point3DCollection();
        for (var c = 0; c < cols - 1; c++)
        {
            var x0 = c / (double)(cols - 1);
            var x1 = (c + 1) / (double)(cols - 1);
            var zUp0 = thresholds[c] / KMaxClamp * 0.5 * HeightScale;
            var zUp1 = thresholds[c + 1] / KMaxClamp * 0.5 * HeightScale;
            var zDn0 = -zUp0;
            var zDn1 = -zUp1;
            ptsUp.Add(new Point3D(x0, 1.0, zUp0));
            ptsUp.Add(new Point3D(x1, 1.0, zUp1));
            ptsDown.Add(new Point3D(x0, 1.0, zDn0));
            ptsDown.Add(new Point3D(x1, 1.0, zDn1));
        }
        View3D.Children.Add(new LinesVisual3D
        {
            Points = ptsUp,
            Color = Color.FromArgb(0xCC, 0xE6, 0x5C, 0x12),
            Thickness = 2,
        });
        View3D.Children.Add(new LinesVisual3D
        {
            Points = ptsDown,
            Color = Color.FromArgb(0xCC, 0x12, 0x6F, 0xBF),
            Thickness = 2,
        });
    }

    private void AddAxis(Point3D from, Point3D to, Color color, string label, string lowTick, string highTick)
    {
        View3D.Children.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { from, to },
            Color = color,
            Thickness = 2,
        });
        var offset = new Vector3D(0.04, 0.04, 0.04);
        View3D.Children.Add(MakeLabel(to + offset, $"{label}  {highTick}", color));
        View3D.Children.Add(MakeLabel(from - offset, lowTick, color));
    }

    private void AddBoundsWireframe()
    {
        var zMin = -0.5 * HeightScale;
        var zMax = 0.5 * HeightScale;
        Point3D[] c =
        {
            new(0,0,zMin), new(1,0,zMin), new(1,1,zMin), new(0,1,zMin),
            new(0,0,zMax), new(1,0,zMax), new(1,1,zMax), new(0,1,zMax),
        };
        (int, int)[] edges =
        {
            (0,1),(1,2),(2,3),(3,0),
            (4,5),(5,6),(6,7),(7,4),
            (0,4),(1,5),(2,6),(3,7),
        };
        var pts = new Point3DCollection();
        foreach (var (a, b) in edges) { pts.Add(c[a]); pts.Add(c[b]); }
        View3D.Children.Add(new LinesVisual3D
        {
            Points = pts,
            Color = Color.FromArgb(0x55, 0x55, 0x55, 0x55),
            Thickness = 1,
        });
    }

    private static BillboardTextVisual3D MakeLabel(Point3D at, string text, Color color) => new()
    {
        Position = at,
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(color),
    };

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.SurfaceChanged -= OnSurfaceChanged;
        await _vm.StopStreamAsync();
        _vm.Dispose();
    }
}
