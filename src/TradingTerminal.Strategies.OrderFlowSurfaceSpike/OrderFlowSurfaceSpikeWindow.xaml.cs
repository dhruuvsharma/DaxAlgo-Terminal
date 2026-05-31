using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public partial class OrderFlowSurfaceSpikeWindow : MetroWindow
{
    private OrderFlowSurfaceSpikeViewModel? _vm;

    /// <summary>Z-score clamp for visualization: anything beyond ±Zclamp pegs at the heatmap end.
    /// 3.5σ catches every meaningful spike without losing detail on smaller cells.</summary>
    private const double ZClamp = 3.5;

    /// <summary>Vertical exaggeration. Without it, |Z|=3 lifts ~0.43 of the bin-axis span — fine
    /// in absolute terms but the surface looks flat next to the bin/slice dimensions. Multiply
    /// to make spikes pop visually.</summary>
    private const double HeightScale = 1.6;

    public OrderFlowSurfaceSpikeWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.SurfaceChanged -= OnSurfaceChanged;
        _vm = e.NewValue as OrderFlowSurfaceSpikeViewModel;
        if (_vm is not null)
        {
            _vm.SurfaceChanged += OnSurfaceChanged;
            Redraw();
        }
    }

    private void OnSurfaceChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm is null) return;
        View3D.Children.Clear();

        View3D.Children.Add(new DefaultLights());

        // Scene bounds: the surface lives in [0,1] × [0,1] × [-0.5, 0.5] world coords.
        // Axes anchored at the back-left corner so they don't intersect the mesh.
        AddAxis(new Point3D(0, 0, 0), new Point3D(1, 0, 0), Colors.IndianRed,   "Price bin →", "low", "high");
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 1, 0), Colors.LimeGreen,   "Time slice →", "old", "now");
        AddAxis(new Point3D(0, 0, -0.5), new Point3D(0, 0, 0.5), Colors.DeepSkyBlue, $"Z-score (±{ZClamp:F1})", $"−{ZClamp:F1}", $"+{ZClamp:F1}");
        AddBoundsWireframe();

        var surface = _vm.Surface;
        if (surface is null) return;

        var rows = surface.GetLength(0);  // slices (0 = oldest, last = current)
        var cols = surface.GetLength(1);  // bins
        if (rows < 2 || cols < 2) return;

        var mesh = BuildSurfaceMesh(surface, rows, cols);
        var material = BuildHeatmapMaterial();
        var model = new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material };
        View3D.Children.Add(new ModelVisual3D { Content = model });

        // Spike highlight — small bright sphere over the spike cell in the latest slice.
        if (Math.Abs(_vm.CurrentSpikeZ) >= _vm.SpikeThreshold && _vm.Surface is not null)
        {
            var latestRow = rows - 1;
            // Find the spike's column in the centered window: spike_bin - (latestBin - half).
            var half = cols / 2;
            var spikeCol = (int)(_vm.CurrentSpikeBin - (_vm.LatestBin - half));
            if (spikeCol >= 0 && spikeCol < cols)
            {
                var z = Math.Clamp(_vm.CurrentSpikeZ, -ZClamp, ZClamp);
                var pos = new Point3D(
                    spikeCol / (double)(cols - 1),
                    1.0,
                    z / (2 * ZClamp) * HeightScale + 0.04 * Math.Sign(z));
                var color = z >= 0 ? Colors.OrangeRed : Colors.DeepSkyBlue;
                View3D.Children.Add(new SphereVisual3D
                {
                    Center = pos,
                    Radius = 0.025,
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
                var z = Math.Clamp(surface[r, c], -ZClamp, ZClamp);
                positions.Add(new Point3D(
                    c / (double)(cols - 1),
                    r / (double)(rows - 1),
                    z / (2 * ZClamp) * HeightScale));
                textureCoords.Add(new Point((z + ZClamp) / (2 * ZClamp), 0.5));
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
                // CCW from above so the diffuse face is the top — BackMaterial covers the underside.
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

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0x69, 0xA8), 0.00)); // strong blue (−Zclamp)
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x8C, 0xC2), 0.30));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x44, 0x44, 0x44), 0.50)); // neutral mid
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0x8A, 0x2E), 0.70));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0x3C, 0x12), 1.00)); // strong red (+Zclamp)
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
        // Bounding box: bins on X [0,1], slices on Y [0,1], Z-score on Z [-0.5, 0.5] * HeightScale.
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
