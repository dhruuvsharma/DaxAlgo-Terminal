using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

/// <summary>
/// View for the Index Regime Graph. Everything here is <em>pure view interaction</em> over the
/// feed-forward node canvas — fit-to-view, pan (drag), zoom (wheel / buttons / +/-), node drag and
/// keyboard navigation — driving a single <see cref="MatrixTransform"/>. No scoring logic lives
/// here; selection/focus is delegated to view-model commands.
/// </summary>
public partial class IndexRegimeGraphWindow : MetroWindow
{
    private const double MinScale = 0.15;
    private const double MaxScale = 4.0;
    private const double KeyPanStep = 50;
    private const double WheelZoom = 1.12;

    private IndexRegimeGraphViewModel? _vm;
    private bool _needFit;

    // Background pan state.
    private bool _panning;
    private Point _panStartScreen;
    private Matrix _matrixAtPanStart;

    // Node drag state.
    private GraphNode? _dragNode;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragLastScreen;

    public IndexRegimeGraphWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => { _needFit = true; Fit(); Focus(); };
        KeyDown += OnKeyDown;
        Closed += OnClosed;
        GraphViewport.SizeChanged += (_, _) => { if (_needFit) Fit(); };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ResetViewRequested -= OnResetViewRequested;
            _vm.GraphRebuilt -= OnGraphRebuilt;
        }
        _vm = e.NewValue as IndexRegimeGraphViewModel;
        if (_vm is not null)
        {
            _vm.ResetViewRequested += OnResetViewRequested;
            _vm.GraphRebuilt += OnGraphRebuilt;
        }
    }

    private void OnResetViewRequested(object? sender, EventArgs e) => Fit();

    private void OnGraphRebuilt(object? sender, EventArgs e)
    {
        _needFit = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Fit));
    }

    // ── Fit / zoom helpers ────────────────────────────────────────────────────────────────────

    private void ApplyMatrix(Matrix m)
    {
        GraphTransform.Matrix = m;
        ZoomReadout.Text = $"{m.M11:P0}";
    }

    private void Fit()
    {
        if (_vm is null || _vm.GraphNodes.Count == 0) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in _vm.GraphNodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Size);
            maxY = Math.Max(maxY, n.Y + n.Size);
        }

        var cw = maxX - minX;
        var ch = maxY - minY;
        var vw = GraphViewport.ActualWidth;
        var vh = GraphViewport.ActualHeight;
        if (vw < 10 || vh < 10 || cw < 1 || ch < 1) { _needFit = true; return; }

        var scale = Math.Clamp(Math.Min(vw / cw, vh / ch) * 0.9, MinScale, MaxScale);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;

        var m = Matrix.Identity;
        m.Scale(scale, scale);
        m.OffsetX = vw / 2 - cx * scale;
        m.OffsetY = vh / 2 - cy * scale;
        ApplyMatrix(m);
        _needFit = false;
    }

    private void Zoom(double factor, double centerX, double centerY)
    {
        var m = GraphTransform.Matrix;
        var newScale = m.M11 * factor;
        if (newScale < MinScale || newScale > MaxScale) return;
        m.ScaleAt(factor, factor, centerX, centerY);
        ApplyMatrix(m);
    }

    private void Pan(double dx, double dy)
    {
        var m = GraphTransform.Matrix;
        m.OffsetX += dx;
        m.OffsetY += dy;
        ApplyMatrix(m);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(WheelZoom, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / WheelZoom, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2);
    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    // ── Background pan / wheel zoom ───────────────────────────────────────────────────────────

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return; // a node handled it
        Focus();
        _panning = true;
        _panStartScreen = e.GetPosition(GraphViewport);
        _matrixAtPanStart = GraphTransform.Matrix;
        GraphViewport.CaptureMouse();
        GraphViewport.Cursor = Cursors.ScrollAll;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(GraphViewport);
        var m = _matrixAtPanStart;
        m.OffsetX += p.X - _panStartScreen.X;
        m.OffsetY += p.Y - _panStartScreen.Y;
        ApplyMatrix(m);
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        GraphViewport.ReleaseMouseCapture();
        GraphViewport.Cursor = Cursors.Arrow;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(GraphViewport);
        Zoom(e.Delta > 0 ? WheelZoom : 1 / WheelZoom, pos.X, pos.Y);
        e.Handled = true;
    }

    // ── Node drag / select ────────────────────────────────────────────────────────────────────

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GraphNode node } fe) return;
        _dragNode = node;
        _dragging = true;
        _dragMoved = false;
        _dragLastScreen = e.GetPosition(GraphViewport);
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragNode is null) return;
        var p = e.GetPosition(GraphViewport);
        var scale = GraphTransform.Matrix.M11;
        if (scale <= 1e-6) return;
        var dx = (p.X - _dragLastScreen.X) / scale;
        var dy = (p.Y - _dragLastScreen.Y) / scale;
        if (!_dragMoved && Math.Abs(p.X - _dragLastScreen.X) < 3 && Math.Abs(p.Y - _dragLastScreen.Y) < 3) return;
        _dragMoved = true;
        _dragNode.X += dx;
        _dragNode.Y += dy;
        _dragLastScreen = p;
        e.Handled = true;
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        var node = _dragNode;
        var moved = _dragMoved;
        _dragging = false;
        _dragNode = null;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        // A click without a drag = select/focus.
        if (!moved && node is not null) _vm?.SelectNodeCommand.Execute(node);
        e.Handled = true;
    }

    // ── Keyboard navigation ──────────────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:  Pan(KeyPanStep, 0); e.Handled = true; break;
            case Key.Right: Pan(-KeyPanStep, 0); e.Handled = true; break;
            case Key.Up:    Pan(0, KeyPanStep); e.Handled = true; break;
            case Key.Down:  Pan(0, -KeyPanStep); e.Handled = true; break;
            case Key.Add:
            case Key.OemPlus:  Zoom(WheelZoom, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2); e.Handled = true; break;
            case Key.Subtract:
            case Key.OemMinus: Zoom(1 / WheelZoom, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2); e.Handled = true; break;
            case Key.Home: Fit(); e.Handled = true; break;
            case Key.Escape: _vm?.ClearFocusCommand.Execute(null); e.Handled = true; break;
            case Key.Tab: CycleCompanies(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; break;
        }
    }

    private void CycleCompanies(int direction)
    {
        if (_vm is null) return;
        var companies = _vm.GraphNodes.Where(n => n.Kind == GraphNodeKind.Company).ToList();
        if (companies.Count == 0) return;
        var current = _vm.SelectedNode;
        var idx = current is null ? -1 : companies.IndexOf(current);
        var next = ((idx + direction) % companies.Count + companies.Count) % companies.Count;
        _vm.SelectNodeCommand.Execute(companies[next]);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.ResetViewRequested -= OnResetViewRequested;
        _vm.GraphRebuilt -= OnGraphRebuilt;
        try { await _vm.StopCommand.ExecuteAsync(null); } catch { /* shutting down */ }
        _vm.Dispose();
    }
}
