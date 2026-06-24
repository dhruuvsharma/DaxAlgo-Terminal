using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.OrderFlowCube;

public partial class OrderFlowCubeWindow : MetroWindow
{
    private OrderFlowCubeViewModel? _vm;

    public OrderFlowCubeWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.TrailChanged -= OnTrailChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as OrderFlowCubeViewModel;

        if (_vm is not null)
        {
            _vm.TrailChanged += OnTrailChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
            RedrawChart();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrderFlowCubeViewModel.CvdThreshold)
                           or nameof(OrderFlowCubeViewModel.AggressorBuyThreshold)
                           or nameof(OrderFlowCubeViewModel.SizeRatioThreshold)
                           or nameof(OrderFlowCubeViewModel.SizeAxisCeiling))
            RedrawChart();
    }

    private void OnTrailChanged(object? sender, EventArgs e) => RedrawChart();

    // World-coordinate cube: each axis normalized to [0, 1].
    // X: CVD imbalance, mapped from [-1, +1] to [0, 1].
    // Y: Aggressor ratio, already in [0, 1].
    // Z: Size ratio, mapped from [0, ceiling] (clamped) to [0, 1] — typical baseline is ~1,
    // institutional surges 1.5-3x. The ceiling is driven live from the VM's Size× axis slider.
    private static Point3D ToWorld(double cvd, double aggressor, double sizeRatio, double sizeCeiling) => new(
        (cvd + 1.0) * 0.5,
        aggressor,
        Math.Clamp(sizeRatio / sizeCeiling, 0, 1));

    private void RedrawChart()
    {
        if (_vm is null) return;
        View3D.Children.Clear();

        // Lighting + scene props.
        View3D.Children.Add(new DefaultLights());

        // Axes + tick labels. X=CVD (red), Y=Aggressor (green), Z=Size× (blue).
        AddAxis(new Point3D(0, 0, 0), new Point3D(1, 0, 0), Colors.IndianRed,    "CVD",      "−1", "+1");
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 1, 0), Colors.LimeGreen,    "Aggressor", "0",  "1");
        var sizeCeiling = _vm.SizeAxisCeiling;
        AddAxis(new Point3D(0, 0, 0), new Point3D(0, 0, 1), Colors.DeepSkyBlue,  "Size×",    "0",  $"{sizeCeiling:F0}+");

        // Wire-frame box outlining the unit cube so depth perception works at any rotation.
        AddCubeWireframe(Color.FromArgb(0x55, 0x55, 0x55, 0x55));

        // Regime threshold reference markers — short axis-aligned bars at each threshold so
        // the user can SEE when a point enters the accumulation / distribution octant.
        var cvdHiX = (_vm.CvdThreshold + 1.0) * 0.5;
        var cvdLoX = (-_vm.CvdThreshold + 1.0) * 0.5;
        var aggHiY = _vm.AggressorBuyThreshold;
        var aggLoY = 1.0 - _vm.AggressorBuyThreshold;
        var sizeHiZ = Math.Clamp(_vm.SizeRatioThreshold / sizeCeiling, 0, 1);
        AddThresholdMarker(cvdHiX, Axis.X, Colors.LightGreen);
        AddThresholdMarker(cvdLoX, Axis.X, Colors.LightCoral);
        AddThresholdMarker(aggHiY, Axis.Y, Colors.LightGreen);
        AddThresholdMarker(aggLoY, Axis.Y, Colors.LightCoral);
        AddThresholdMarker(sizeHiZ, Axis.Z, Colors.LightSkyBlue);

        var trail = _vm.TrailPoints;
        if (trail.Count == 0) return;

        // Trail spheres — older points dimmer and smaller, newer brighter and larger.
        var lastIdx = trail.Count - 1;
        for (var i = 0; i < trail.Count; i++)
        {
            var p = trail[i];
            var age = lastIdx == 0 ? 1.0 : (double)i / lastIdx; // 0 = oldest, 1 = newest
            var pos = ToWorld(p.Cvd, p.Aggressor, p.SizeRatio, sizeCeiling);

            var brightness = (byte)(80 + 175 * age);
            var color = Color.FromRgb(brightness, (byte)(brightness * 0.7), 0); // amber
            var radius = 0.012 + 0.014 * age;

            View3D.Children.Add(new SphereVisual3D
            {
                Center = pos,
                Radius = radius,
                Material = MaterialHelper.CreateMaterial(new SolidColorBrush(color)),
            });
        }

        // Latest point: bigger, white. The eye locks onto "where we are right now."
        var last = trail[lastIdx];
        var latestPos = ToWorld(last.Cvd, last.Aggressor, last.SizeRatio, sizeCeiling);
        View3D.Children.Add(new SphereVisual3D
        {
            Center = latestPos,
            Radius = 0.04,
            Material = MaterialHelper.CreateMaterial(Brushes.White),
        });

        // Trail line: connect successive trail points so the trajectory through phase space reads as a path.
        var linePts = new Point3DCollection(trail.Count);
        for (var i = 0; i < trail.Count; i++)
            linePts.Add(ToWorld(trail[i].Cvd, trail[i].Aggressor, trail[i].SizeRatio, sizeCeiling));
        View3D.Children.Add(new LinesVisual3D
        {
            Points = linePts,
            Color = Color.FromArgb(0x80, 0x00, 0x7A, 0xCC),
            Thickness = 1.5,
        });
    }

    private enum Axis { X, Y, Z }

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

    private void AddThresholdMarker(double value, Axis axis, Color color)
    {
        // Render a small tick along the axis at the threshold value. Subtle on purpose —
        // they're guide rails, not the main signal.
        Point3D a, b;
        const double w = 0.04;
        switch (axis)
        {
            case Axis.X: a = new Point3D(value, -w, -w); b = new Point3D(value, w, w); break;
            case Axis.Y: a = new Point3D(-w, value, -w); b = new Point3D(w, value, w); break;
            default:     a = new Point3D(-w, -w, value); b = new Point3D(w, w, value); break;
        }
        View3D.Children.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { a, b },
            Color = Color.FromArgb(0xC0, color.R, color.G, color.B),
            Thickness = 1.5,
        });
    }

    private void AddCubeWireframe(Color color)
    {
        // 12 edges of the unit cube [0,1]^3.
        Point3D[] c =
        {
            new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0),
            new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1),
        };
        (int, int)[] edges =
        {
            (0,1),(1,2),(2,3),(3,0), // bottom
            (4,5),(5,6),(6,7),(7,4), // top
            (0,4),(1,5),(2,6),(3,7), // verticals
        };
        var pts = new Point3DCollection();
        foreach (var (a, b) in edges) { pts.Add(c[a]); pts.Add(c[b]); }
        View3D.Children.Add(new LinesVisual3D { Points = pts, Color = color, Thickness = 1 });
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
        _vm.TrailChanged -= OnTrailChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        await _vm.StopStreamAsync();
        _vm.Dispose();
    }
}
