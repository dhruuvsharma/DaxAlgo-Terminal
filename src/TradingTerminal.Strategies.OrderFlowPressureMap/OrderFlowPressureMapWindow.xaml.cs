using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// Renders the Order Flow Pressure Map matrix (ticker rows × 1-minute columns) via the retained-draw
/// <see cref="PressureMatrixView"/>, fed from <see cref="OrderFlowPressureMapViewModel.PressureMapChanged"/>.
/// Pure presentation: it owns no data and computes nothing about signals; it pushes the snapshot to the
/// matrix view, shows the hover tooltip, and routes a row click back to the VM's
/// <see cref="OrderFlowPressureMapViewModel.Pin"/>.
/// </summary>
public partial class OrderFlowPressureMapWindow : MetroWindow
{
    private readonly PressureMatrixView _matrix = new();

    private OrderFlowPressureMapViewModel? _vm;
    private IReadOnlyList<PressureRowSnapshot> _snapshot = Array.Empty<PressureRowSnapshot>();

    public OrderFlowPressureMapWindow()
    {
        InitializeComponent();
        _matrix.MouseMove += OnCanvasMouseMove;
        _matrix.MouseLeave += OnCanvasMouseLeave;
        _matrix.MouseLeftButtonDown += OnCanvasMouseDown;
        Scroll.Content = _matrix;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PressureMapChanged -= OnPressureMapChanged;
        _vm = e.NewValue as OrderFlowPressureMapViewModel;
        if (_vm is not null)
        {
            _vm.PressureMapChanged += OnPressureMapChanged;
            RefreshFromVm();
        }
    }

    private void OnPressureMapChanged(object? sender, EventArgs e) => RefreshFromVm();

    private void RefreshFromVm()
    {
        if (_vm is null) return;
        _snapshot = _vm.Snapshot;
        _matrix.SetData(_snapshot, _vm.Columns, _vm.LatestColumnTime, _vm.PinnedSymbol);
    }

    // ── Hover tooltip + click-to-pin ────────────────────────────────────────────────────────────

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var (row, col) = _matrix.HitTest(e.GetPosition(_matrix));
        if (row < 0 || col < 0 || row >= _snapshot.Count)
        {
            TooltipBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var snap = _snapshot[row];
        if (col >= snap.Cells.Count)
        {
            TooltipBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var cell = snap.Cells[col];
        if (cell is null)
        {
            TooltipBorder.Visibility = Visibility.Collapsed;
            return;
        }

        TooltipText.Text =
            $"{snap.Symbol}  {cell.OpenTimeUtc.ToLocalTime():HH:mm}\n" +
            $"O {cell.Open:N2}  H {cell.High:N2}  L {cell.Low:N2}  C {cell.Close:N2}\n" +
            $"Vol {cell.Volume:N0}   RelVol {cell.RelativeVolume:N2}×\n" +
            $"Pos {cell.CandlePosition:N2}  Impact {cell.PriceImpact:N2}  Imb {cell.BookImbalance:+0.00;-0.00;0.00}\n" +
            OrderFlowPressureMapViewModel.Describe(cell.Signal);

        var p = e.GetPosition(Scroll);
        TooltipBorder.Margin = new Thickness(Math.Min(p.X + 14, Scroll.ActualWidth - 220), Math.Min(p.Y + 14, Scroll.ActualHeight - 90), 0, 0);
        TooltipBorder.Visibility = Visibility.Visible;
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e) => TooltipBorder.Visibility = Visibility.Collapsed;

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var (row, _) = _matrix.HitTest(e.GetPosition(_matrix));
        if (row < 0 || row >= _snapshot.Count) return;
        _vm?.Pin(_snapshot[row].Symbol);
        RefreshFromVm();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.PressureMapChanged -= OnPressureMapChanged;
        _vm.Dispose();
    }
}
