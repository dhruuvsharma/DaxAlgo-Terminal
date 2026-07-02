using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using TradingTerminal.Core.Quant.Surfaces;
using TradingTerminal.UI;

namespace TradingTerminal.SurfaceLab;

/// <summary>
/// Code-behind for the Surface Lab. View-only rendering (all math lives in Core/Quant/Surfaces):
/// on <see cref="SurfaceLabViewModel.SurfaceUpdated"/> it rebuilds the Helix mesh, axes, and
/// peak pin; on <see cref="SurfaceLabViewModel.SliceChanged"/> it only swaps the two translucent
/// cutting planes and refreshes the 2D slice charts — never the mesh, so slider drags stay cheap.
/// Brushes/materials are cached statics (no per-redraw allocation of frozen resources).
/// </summary>
public partial class SurfaceLabView : UserControl
{
    private const double SurfaceHeight = 0.55;

    private SurfaceLabViewModel? _vm;
    private ModelVisual3D? _slicePlaneX;
    private ModelVisual3D? _slicePlaneY;
    private double _zMin, _zMax;

    private static readonly ScottPlot.Color SliceLineColor = new(38, 198, 218);
    private static readonly ScottPlot.Color SliceMarkerColor = new(255, 82, 82);

    public SurfaceLabView()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(SliceXPlot);
        StrategyChartHelpers.ConfigureDarkPlot(SliceYPlot);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SurfaceLabViewModel oldVm)
        {
            oldVm.SurfaceUpdated -= OnSurfaceUpdated;
            oldVm.SliceChanged -= OnSliceChanged;
        }
        _vm = e.NewValue as SurfaceLabViewModel;
        if (_vm is not null)
        {
            _vm.SurfaceUpdated += OnSurfaceUpdated;
            _vm.SliceChanged += OnSliceChanged;
            if (_vm.HasResult) OnSurfaceUpdated(_vm, EventArgs.Empty);
        }
    }

    private void OnSurfaceUpdated(object? sender, EventArgs e)
    {
        RedrawSurface();
        UpdateSlicePlanes();
        UpdateSliceCharts();
    }

    private void OnSliceChanged(object? sender, EventArgs e)
    {
        UpdateSlicePlanes();
        UpdateSliceCharts();
    }

    // ── 3D surface ────────────────────────────────────────────────────────────────────────────

    private void RedrawSurface()
    {
        View3D.Children.Clear();
        _slicePlaneX = null;
        _slicePlaneY = null;
        View3D.Children.Add(new DefaultLights());

        if (_vm?.Result is not { } r) return;
        if (r.Columns < 2 || r.Rows < 2) return;

        (_zMin, _zMax) = ZRange(r.Z);
        if (double.IsNaN(_zMin)) return;

        var heightScale = _vm.HeightScale;
        var zTop = SurfaceHeight * heightScale;

        AddAxis(new Point3D(0, 0, 0), new Point3D(1, 0, 0), Colors.IndianRed,
            r.XName, r.XLabels[0], r.XLabels[^1]);
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 1, 0), Colors.LimeGreen,
            r.YName, r.YLabels[0], r.YLabels[^1]);
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 0, zTop), Colors.DeepSkyBlue,
            r.ZName,
            SurfaceAxisFormats.Format(_zMin, r.ZFormat),
            SurfaceAxisFormats.Format(_zMax, r.ZFormat));
        AddBoundsWireframe(zTop);

        var colorGrid = _vm.RobustnessColorMode ? r.Robustness : r.W;
        var mesh = BuildSurfaceMesh(r.Z, colorGrid, _vm.RobustnessColorMode, heightScale);
        var material = _vm.RobustnessColorMode ? RobustnessMaterial() : ValueMaterial();
        View3D.Children.Add(new ModelVisual3D
        {
            Content = new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material },
        });

        if (_vm.ShowPeakMarker)
            AddPeakMarker(r, heightScale);
    }

    /// <summary>Heightmap mesh over the unit square. Z is min-max normalized; per-vertex texture
    /// U carries the color variable (W metric min-max normalized, or the robustness score which
    /// is already in [0,1]). Triangles touching a NaN cell are skipped, so degenerate parameter
    /// combos and empty buckets render as honest holes.</summary>
    private MeshGeometry3D BuildSurfaceMesh(double[,] z, double[,] colorGrid, bool colorIsUnit, double heightScale)
    {
        var rows = z.GetLength(0);
        var cols = z.GetLength(1);
        var zSpan = Math.Max(_zMax - _zMin, 1e-12);
        var (wMin, wMax) = colorIsUnit ? (0.0, 1.0) : ZRange(colorGrid);
        var wSpan = Math.Max(wMax - wMin, 1e-12);

        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection(rows * cols);
        var textureCoords = new PointCollection(rows * cols);

        for (var rIdx = 0; rIdx < rows; rIdx++)
        {
            for (var c = 0; c < cols; c++)
            {
                var v = z[rIdx, c];
                var zNorm = double.IsNaN(v) ? 0 : (v - _zMin) / zSpan;
                positions.Add(new Point3D(
                    c / (double)(cols - 1),
                    rIdx / (double)(rows - 1),
                    zNorm * SurfaceHeight * heightScale));

                var w = colorGrid[rIdx, c];
                var u = double.IsNaN(w) || double.IsNaN(wMin) ? 0 : Math.Clamp((w - wMin) / wSpan, 0, 1);
                textureCoords.Add(new Point(u, 0.5));
            }
        }

        var indices = new Int32Collection((rows - 1) * (cols - 1) * 6);
        for (var rIdx = 0; rIdx < rows - 1; rIdx++)
        {
            for (var c = 0; c < cols - 1; c++)
            {
                if (double.IsNaN(z[rIdx, c]) || double.IsNaN(z[rIdx, c + 1]) ||
                    double.IsNaN(z[rIdx + 1, c]) || double.IsNaN(z[rIdx + 1, c + 1]))
                    continue;
                var i00 = rIdx * cols + c;
                var i01 = rIdx * cols + (c + 1);
                var i10 = (rIdx + 1) * cols + c;
                var i11 = (rIdx + 1) * cols + (c + 1);
                indices.Add(i00); indices.Add(i10); indices.Add(i01);
                indices.Add(i01); indices.Add(i10); indices.Add(i11);
            }
        }

        mesh.Positions = positions;
        mesh.TextureCoordinates = textureCoords;
        mesh.TriangleIndices = indices;
        return mesh;
    }

    private void AddPeakMarker(SurfaceGridResult r, double heightScale)
    {
        var peak = SurfaceGridAnalysis.FindMax(r.Z);
        if (!peak.IsValid) return;

        var zSpan = Math.Max(_zMax - _zMin, 1e-12);
        var p = new Point3D(
            peak.Col / (double)(r.Columns - 1),
            peak.Row / (double)(r.Rows - 1),
            (peak.Value - _zMin) / zSpan * SurfaceHeight * heightScale);

        // Red pin: needle from above down to the peak vertex, ball on top.
        View3D.Children.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { p, new Point3D(p.X, p.Y, p.Z + 0.14) },
            Color = Colors.Red,
            Thickness = 3,
        });
        View3D.Children.Add(new SphereVisual3D
        {
            Center = new Point3D(p.X, p.Y, p.Z + 0.14),
            Radius = 0.018,
            Material = PeakMaterial(),
        });
        View3D.Children.Add(new BillboardTextVisual3D
        {
            Position = new Point3D(p.X, p.Y, p.Z + 0.20),
            Text = $"▼ {r.XName} = {r.XLabels[peak.Col]} · {r.YName} = {r.YLabels[peak.Row]}\n{r.ZName} = {SurfaceAxisFormats.Format(peak.Value, r.ZFormat)}",
            FontSize = 12,
            Foreground = PeakLabelBrush,
            Background = PeakLabelBackground,
            Padding = new Thickness(4, 2, 4, 2),
        });
    }

    // ── Cutting planes + 2D slice charts ─────────────────────────────────────────────────────

    private void UpdateSlicePlanes()
    {
        if (_vm?.Result is not { } r || double.IsNaN(_zMin)) return;
        if (_slicePlaneX is not null) View3D.Children.Remove(_slicePlaneX);
        if (_slicePlaneY is not null) View3D.Children.Remove(_slicePlaneY);
        if (r.Columns < 2 || r.Rows < 2) return;

        var zTop = SurfaceHeight * _vm.HeightScale;
        var x = Math.Clamp(_vm.SliceXIndex, 0, r.Columns - 1) / (double)(r.Columns - 1);
        var y = Math.Clamp(_vm.SliceYIndex, 0, r.Rows - 1) / (double)(r.Rows - 1);

        _slicePlaneX = BuildPlane(
            new Point3D(x, 0, 0), new Point3D(x, 1, 0), new Point3D(x, 1, zTop), new Point3D(x, 0, zTop),
            SlicePlaneXMaterial());
        _slicePlaneY = BuildPlane(
            new Point3D(0, y, 0), new Point3D(1, y, 0), new Point3D(1, y, zTop), new Point3D(0, y, zTop),
            SlicePlaneYMaterial());
        View3D.Children.Add(_slicePlaneX);
        View3D.Children.Add(_slicePlaneY);
    }

    private void UpdateSliceCharts()
    {
        if (_vm?.Result is not { } r) return;

        var xIdx = Math.Clamp(_vm.SliceXIndex, 0, r.Columns - 1);
        var yIdx = Math.Clamp(_vm.SliceYIndex, 0, r.Rows - 1);

        // X slice: fix X, walk Y.
        DrawSlice(SliceXPlot,
            r.YValues, SurfaceGridAnalysis.SliceAtColumn(r.Z, xIdx),
            $"{r.ZName} along {r.YName}  @  {r.XName} = {r.XLabels[xIdx]}");

        // Y slice: fix Y, walk X.
        DrawSlice(SliceYPlot,
            r.XValues, SurfaceGridAnalysis.SliceAtRow(r.Z, yIdx),
            $"{r.ZName} along {r.XName}  @  {r.YName} = {r.YLabels[yIdx]}");
    }

    private static void DrawSlice(ScottPlot.WPF.WpfPlot plot, double[] axis, double[] values, string title)
    {
        plot.Plot.Clear();

        // NaN cells are gaps — plot only the valid points.
        var xs = new List<double>(axis.Length);
        var ys = new List<double>(axis.Length);
        for (var i = 0; i < axis.Length && i < values.Length; i++)
        {
            if (double.IsNaN(values[i])) continue;
            xs.Add(axis[i]);
            ys.Add(values[i]);
        }

        if (xs.Count >= 2)
        {
            var line = plot.Plot.Add.Scatter(xs.ToArray(), ys.ToArray());
            line.LineWidth = 1.6f;
            line.MarkerSize = 4;
            line.Color = SliceLineColor;

            // Mark the slice's own maximum.
            var maxIdx = 0;
            for (var i = 1; i < ys.Count; i++)
                if (ys[i] > ys[maxIdx]) maxIdx = i;
            var marker = plot.Plot.Add.Marker(xs[maxIdx], ys[maxIdx]);
            marker.Color = SliceMarkerColor;
            marker.Size = 8;
        }

        plot.Plot.Title(title, 11);
        plot.Plot.Axes.AutoScale();
        plot.Refresh();
    }

    // ── Static 3D helpers ─────────────────────────────────────────────────────────────────────

    private static (double Min, double Max) ZRange(double[,] grid)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in grid)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return min <= max ? (min, max) : (double.NaN, double.NaN);
    }

    private static ModelVisual3D BuildPlane(Point3D p0, Point3D p1, Point3D p2, Point3D p3, Material material)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { p0, p1, p2, p3 },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        return new ModelVisual3D
        {
            Content = new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material },
        };
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

    private void AddBoundsWireframe(double zTop)
    {
        Point3D[] c =
        {
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
            new(0, 0, zTop), new(1, 0, zTop), new(1, 1, zTop), new(0, 1, zTop),
        };
        (int, int)[] edges =
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
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

    // ── Cached materials (built once, frozen) ─────────────────────────────────────────────────

    private static Material? _valueMaterial;
    private static Material? _robustnessMaterial;
    private static Material? _peakMaterial;
    private static Material? _slicePlaneXMaterial;
    private static Material? _slicePlaneYMaterial;

    private static readonly Brush PeakLabelBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush PeakLabelBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x30, 0x10, 0x10)));

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>Cold-to-hot gradient for the W metric: deep blue → cyan → green → yellow → red.</summary>
    private static Material ValueMaterial() => _valueMaterial ??= MakeGradientMaterial(
        (0.00, Color.FromRgb(0x0D, 0x2B, 0x6B)),
        (0.25, Color.FromRgb(0x12, 0x74, 0xB8)),
        (0.50, Color.FromRgb(0x19, 0xB5, 0x8A)),
        (0.75, Color.FromRgb(0xE8, 0xC5, 0x2A)),
        (1.00, Color.FromRgb(0xE6, 0x3C, 0x12)));

    /// <summary>Robustness gradient: green (flat plateau, robust) → yellow → red (spike, overfit).</summary>
    private static Material RobustnessMaterial() => _robustnessMaterial ??= MakeGradientMaterial(
        (0.00, Color.FromRgb(0x14, 0xA0, 0x44)),
        (0.45, Color.FromRgb(0x9A, 0xC1, 0x2C)),
        (0.70, Color.FromRgb(0xE8, 0xA8, 0x1E)),
        (1.00, Color.FromRgb(0xD8, 0x22, 0x18)));

    private static Material PeakMaterial()
    {
        if (_peakMaterial is null)
        {
            _peakMaterial = new DiffuseMaterial(Frozen(new SolidColorBrush(Colors.Red)));
            _peakMaterial.Freeze();
        }
        return _peakMaterial;
    }

    private static Material SlicePlaneXMaterial() => _slicePlaneXMaterial ??= MakePlaneMaterial(Color.FromArgb(0x30, 0xE0, 0x50, 0x50));
    private static Material SlicePlaneYMaterial() => _slicePlaneYMaterial ??= MakePlaneMaterial(Color.FromArgb(0x30, 0x50, 0xC0, 0x50));

    private static Material MakePlaneMaterial(Color color)
    {
        var m = new DiffuseMaterial(Frozen(new SolidColorBrush(color)));
        m.Freeze();
        return m;
    }

    private static Material MakeGradientMaterial(params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop(color, offset));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }
}
