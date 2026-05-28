using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

public partial class ImbalanceHeatFrontWindow : MetroWindow
{
    private ImbalanceHeatFrontViewModel? _vm;

    /// <summary>Vertical exaggeration. Imbalance is naturally bounded to [-1, +1] so the raw
    /// height range is small relative to the X/Y unit-square footprint — without scaling, ridges
    /// look flat next to the bin/slice dimensions. 1.6 matches the OrderFlowSurfaceSpike window
    /// so the two surfaces feel consistent side-by-side.</summary>
    private const double HeightScale = 1.6;

    public ImbalanceHeatFrontWindow()
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
            ((INotifyCollectionChanged)_vm.DataLog).CollectionChanged -= OnLogChanged;
        }
        _vm = e.NewValue as ImbalanceHeatFrontViewModel;
        if (_vm is not null)
        {
            _vm.SurfaceChanged += OnSurfaceChanged;
            ((INotifyCollectionChanged)_vm.DataLog).CollectionChanged += OnLogChanged;
            Redraw();
        }
    }

    // DataLog is newest-first (base class Insert(0, ...)), so a new entry shows up at the TOP —
    // scroll to the start, not the end.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogScroller.ScrollToTop();
    }

    private void OnSurfaceChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm is null) return;
        View3D.Children.Clear();
        View3D.Children.Add(new DefaultLights());

        AddAxis(new Point3D(0, 0, 0), new Point3D(1, 0, 0), Colors.IndianRed,   "Distance from touch →", "touch", "deep");
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 1, 0), Colors.LimeGreen,   "Time →", "30s ago", "now");
        AddAxis(new Point3D(0, 0, -0.5 * HeightScale), new Point3D(0, 0, 0.5 * HeightScale), Colors.DeepSkyBlue, "Imbalance (±1.0)", "−1 ask", "+1 bid");
        AddBoundsWireframe();

        var surface = _vm.Surface;
        if (surface is null) return;

        var rows = surface.GetLength(0);
        var cols = surface.GetLength(1);
        if (rows < 2 || cols < 2) return;

        var mesh = BuildSurfaceMesh(surface, rows, cols);
        var material = BuildHeatmapMaterial();
        var model = new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material };
        View3D.Children.Add(new ModelVisual3D { Content = model });

        // Ridge highlight: draw a band of spheres on the latest slice across the ridge's columns.
        if (_vm.RidgeSide != 0 && _vm.RidgeWidthDisplay >= 1)
        {
            var color = _vm.RidgeSide > 0 ? Colors.OrangeRed : Colors.DeepSkyBlue;
            var latestY = 1.0;
            for (var i = 0; i < _vm.RidgeWidthDisplay; i++)
            {
                var col = _vm.RidgeStartLevel + i;
                if (col < 0 || col >= cols) continue;
                var z = Math.Clamp(surface[rows - 1, col], -1.0, 1.0);
                var pos = new Point3D(
                    col / (double)(cols - 1),
                    latestY,
                    z * 0.5 * HeightScale + 0.04 * Math.Sign(z));
                View3D.Children.Add(new SphereVisual3D
                {
                    Center = pos,
                    Radius = 0.022,
                    Material = MaterialHelper.CreateMaterial(new SolidColorBrush(color)),
                });
            }
        }
    }

    private static MeshGeometry3D BuildSurfaceMesh(double[,] surface, int rows, int cols)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection(rows * cols);
        var textureCoords = new PointCollection(rows * cols);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var v = Math.Clamp(surface[r, c], -1.0, 1.0);
                positions.Add(new Point3D(
                    c / (double)(cols - 1),
                    r / (double)(rows - 1),
                    v * 0.5 * HeightScale));
                // Texture U maps [-1, +1] → [0, 1] for the horizontal gradient brush.
                textureCoords.Add(new Point((v + 1.0) * 0.5, 0.5));
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

    private static Material? _cachedHeatmap;
    private static Material BuildHeatmapMaterial()
    {
        if (_cachedHeatmap is not null) return _cachedHeatmap;

        // User spec: blue valley (-1, ask dominance) → green/neutral (0) → red peak (+1, bid dominance).
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0x4D, 0xA8), 0.00)); // deep blue (−1)
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x8C, 0xC2), 0.30));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x99, 0x5E), 0.50)); // neutral green (0)
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0x8A, 0x2E), 0.70));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0x3C, 0x12), 1.00)); // deep red (+1)
        brush.Freeze();

        _cachedHeatmap = new DiffuseMaterial(brush);
        ((DiffuseMaterial)_cachedHeatmap).Freeze();
        return _cachedHeatmap;
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
        await _vm.StopCommand.ExecuteAsync(null);
        _vm.Dispose();
    }
}
