using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.Controls;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// Renders the Order Flow Pressure Map matrix (ticker rows × 1-minute columns) onto a <see cref="Canvas"/>
/// in response to <see cref="OrderFlowPressureMapViewModel.PressureMapChanged"/> — the same
/// render-in-code-behind convention the Volume Footprint window uses. Pure presentation: it owns no
/// data and computes nothing about signals; it only colours cells, shows the hover tooltip, and routes
/// a row click back to the VM's <see cref="OrderFlowPressureMapViewModel.Pin"/>.
/// </summary>
public partial class OrderFlowPressureMapWindow : MetroWindow
{
    private const double LabelWidth = 72;
    private const double HeaderHeight = 20;
    private const double RowHeight = 15;
    private const double CellWidth = 16;

    private static readonly Color NoDataColor = (Color)ColorConverter.ConvertFromString("#0F1620");
    private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#1C2530");
    private static readonly Color GridColor = (Color)ColorConverter.ConvertFromString("#263241");
    private static readonly Color TextColor = (Color)ColorConverter.ConvertFromString("#E6EDF3");
    private static readonly Color DimColor = (Color)ColorConverter.ConvertFromString("#8A94A6");
    private static readonly Color PinColor = (Color)ColorConverter.ConvertFromString("#FFD600");

    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(TextColor));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(DimColor));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(GridColor));
    private static readonly Brush PinBrush = Freeze(new SolidColorBrush(PinColor));

    private readonly Dictionary<uint, Brush> _brushCache = new();

    private OrderFlowPressureMapViewModel? _vm;
    private IReadOnlyList<PressureRowSnapshot> _snapshot = Array.Empty<PressureRowSnapshot>();

    public OrderFlowPressureMapWindow()
    {
        InitializeComponent();
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
            Redraw();
        }
    }

    private void OnPressureMapChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        PressureCanvas.Children.Clear();
        if (_vm is null) return;

        _snapshot = _vm.Snapshot;
        var rows = _snapshot;
        var cols = _vm.Columns;

        if (rows.Count == 0)
        {
            PressureCanvas.Width = 0;
            PressureCanvas.Height = 0;
            return;
        }

        PressureCanvas.Width = LabelWidth + cols * CellWidth;
        PressureCanvas.Height = HeaderHeight + rows.Count * RowHeight;

        DrawTimeAxis(cols, _vm.LatestColumnTime);

        for (var r = 0; r < rows.Count; r++)
            DrawRow(rows[r], r, cols);
    }

    private void DrawTimeAxis(int cols, DateTime latestUtc)
    {
        if (latestUtc == DateTime.MinValue) return;
        // Label every 10th column with its local HH:mm (column c = latest - (cols-1-c) minutes).
        for (var c = 0; c < cols; c += 10)
        {
            var t = latestUtc.AddMinutes(-(cols - 1 - c)).ToLocalTime();
            AddText(t.ToString("HH:mm"), LabelWidth + c * CellWidth, 0, CellWidth * 6, HeaderHeight,
                DimBrush, 10, TextAlignment.Left);
        }
    }

    private void DrawRow(PressureRowSnapshot row, int rowIndex, int cols)
    {
        var y = HeaderHeight + rowIndex * RowHeight;
        var pinned = _vm is not null && string.Equals(_vm.PinnedSymbol, row.Symbol, StringComparison.Ordinal);

        // Row label (ticker). Highlighted when pinned.
        AddText(row.Symbol, 2, y, LabelWidth - 6, RowHeight, pinned ? PinBrush : TextBrush, 10.5, TextAlignment.Left);

        for (var c = 0; c < cols; c++)
        {
            var cell = row.Cells[c];
            var color = cell is null ? NoDataColor : ColorFor(cell.Signal, cell.Intensity);
            var rect = new Rectangle
            {
                Width = CellWidth - 1,
                Height = RowHeight - 1,
                Fill = BrushFor(color),
            };
            Canvas.SetLeft(rect, LabelWidth + c * CellWidth);
            Canvas.SetTop(rect, y);
            PressureCanvas.Children.Add(rect);
        }

        if (pinned)
        {
            var hl = new Rectangle
            {
                Width = LabelWidth + cols * CellWidth - 1,
                Height = RowHeight - 1,
                Stroke = PinBrush,
                StrokeThickness = 1,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(hl, 0);
            Canvas.SetTop(hl, y);
            PressureCanvas.Children.Add(hl);
        }
    }

    // ── Hover tooltip + click-to-pin ────────────────────────────────────────────────────────────

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var (row, col) = HitTest(e.GetPosition(PressureCanvas));
        if (row < 0 || col < 0 || row >= _snapshot.Count)
        {
            TooltipBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var snap = _snapshot[row];
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
        var (row, _) = HitTest(e.GetPosition(PressureCanvas));
        if (row < 0 || row >= _snapshot.Count) return;
        _vm?.Pin(_snapshot[row].Symbol);
        Redraw();
    }

    private static (int row, int col) HitTest(Point p)
    {
        if (p.Y < HeaderHeight) return (-1, -1);
        var row = (int)((p.Y - HeaderHeight) / RowHeight);
        var col = p.X < LabelWidth ? 0 : (int)((p.X - LabelWidth) / CellWidth);
        return (row, col);
    }

    // ── Colour helpers ──────────────────────────────────────────────────────────────────────────

    private static Color ColorFor(PressureSignal signal, double intensity)
    {
        var (baseC, highC) = signal switch
        {
            PressureSignal.BullishAbsorption => ("#00E5FF", "#84F5FF"),
            PressureSignal.BearishAbsorption => ("#B388FF", "#D7B8FF"),
            PressureSignal.BullishBreakthrough => ("#00C853", "#69F0AE"),
            PressureSignal.BearishBreakdown => ("#FF1744", "#FF8A80"),
            _ => (string.Empty, string.Empty),
        };
        if (baseC.Length == 0) return NeutralColor;
        return Lerp((Color)ColorConverter.ConvertFromString(baseC),
                    (Color)ColorConverter.ConvertFromString(highC),
                    Math.Clamp(intensity, 0, 1));
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private Brush BrushFor(Color c)
    {
        var key = (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
        if (_brushCache.TryGetValue(key, out var b)) return b;
        var brush = Freeze(new SolidColorBrush(c));
        _brushCache[key] = brush;
        return brush;
    }

    private void AddText(string text, double x, double y, double w, double h, Brush brush, double size, TextAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontFamily = new FontFamily("Consolas"),
            Width = w,
            Height = h,
            TextAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y + (h - size - 3) / 2.0);
        PressureCanvas.Children.Add(tb);
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.PressureMapChanged -= OnPressureMapChanged;
        _vm.Dispose();
    }
}
