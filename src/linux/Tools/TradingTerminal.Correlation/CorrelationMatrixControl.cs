using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingTerminal.Core.Analytics;
using TradingTerminal.UI.Converters;

namespace TradingTerminal.Correlation;

/// <summary>
/// Immediate-mode renderer for an NxN correlation matrix. Replaces the previous nested
/// <c>ItemsControl</c> approach, which re-templated N² framework elements (each with a border,
/// text block and tooltip multibinding) on every matrix update — at the live tool's 1–2 s sample
/// cadence with a few dozen instruments that meant thousands of element teardowns per second and
/// visible UI stutter. Here the whole grid (rotated column headers, row headers, heat cells and
/// values) is drawn in a single <see cref="OnRender"/> pass with cached frozen brushes and cached
/// <see cref="FormattedText"/> runs, so an update costs one invalidation instead of a layout storm.
/// Cell tooltips are synthesized on mouse-move from the hit cell.
/// </summary>
public sealed class CorrelationMatrixControl : FrameworkElement
{
    private const double RowHeaderWidth = 92;
    private const double ColHeaderHeight = 74;
    private const double CellWidth = 40;
    private const double CellHeight = 30;

    /// <summary>Single source of the diverging red/grey/green heat colours (cached, frozen).</summary>
    private static readonly CorrelationToBrushConverter ValueBrush = new();

    private static readonly Typeface CellTypeface = new("Segoe UI");

    // FormattedText is expensive to construct, but the cell display strings are quantized to two
    // decimals (≤ 201 distinct values) and the header label set is small and stable — so caching
    // by string turns each render pass into pure draw calls. Cleared when the DPI changes.
    private readonly Dictionary<string, FormattedText> _cellTextCache = new();
    private readonly Dictionary<string, FormattedText> _rowHeaderCache = new();
    private readonly Dictionary<string, FormattedText> _colHeaderCache = new();

    private int _tipRow = -1, _tipCol = -1;

    public static readonly DependencyProperty MatrixProperty = DependencyProperty.Register(
        nameof(Matrix), typeof(CorrelationMatrix), typeof(CorrelationMatrixControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public CorrelationMatrix? Matrix
    {
        get => (CorrelationMatrix?)GetValue(MatrixProperty);
        set => SetValue(MatrixProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var n = Matrix?.Size ?? 0;
        return n == 0
            ? new Size(0, 0)
            : new Size(RowHeaderWidth + n * CellWidth, ColHeaderHeight + n * CellHeight);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _cellTextCache.Clear();
        _rowHeaderCache.Clear();
        _colHeaderCache.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var matrix = Matrix;
        var n = matrix?.Size ?? 0;
        if (matrix is null || n == 0) return;

        // Transparent backdrop so the whole grid (headers included) is mouse-hit-testable.
        dc.DrawRectangle(Brushes.Transparent, null,
            new Rect(0, 0, RowHeaderWidth + n * CellWidth, ColHeaderHeight + n * CellHeight));

        var headerBrush = TryFindResource("Text.Primary") as Brush ?? Brushes.Gainsboro;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Rotated column headers, bottom-anchored above each column.
        for (var j = 0; j < n; j++)
        {
            var ft = ColHeaderText(matrix.Labels[j], headerBrush, dip);
            var cx = RowHeaderWidth + j * CellWidth + (CellWidth - ft.Height) / 2;
            var by = ColHeaderHeight - 4;
            dc.PushTransform(new RotateTransform(-90, cx, by));
            dc.DrawText(ft, new Point(cx, by));
            dc.Pop();
        }

        // Right-aligned row headers.
        for (var i = 0; i < n; i++)
        {
            var ft = RowHeaderText(matrix.Labels[i], headerBrush, dip);
            dc.DrawText(ft, new Point(0, ColHeaderHeight + i * CellHeight + (CellHeight - ft.Height) / 2));
        }

        // Heat cells with centred values. A 1px gap between cells stands in for the old border.
        for (var i = 0; i < n; i++)
        {
            var y = ColHeaderHeight + i * CellHeight;
            for (var j = 0; j < n; j++)
            {
                var x = RowHeaderWidth + j * CellWidth;
                var v = matrix.At(i, j);
                var brush = (Brush)ValueBrush.Convert(v, typeof(Brush), null, CultureInfo.InvariantCulture);
                dc.DrawRectangle(brush, null, new Rect(x + 0.5, y + 0.5, CellWidth - 1, CellHeight - 1));

                var ft = CellText(Display(v), dip);
                dc.DrawText(ft, new Point(x + (CellWidth - ft.Width) / 2, y + (CellHeight - ft.Height) / 2));
            }
        }
    }

    // ── Hover tooltip (one ToolTip for the whole grid, retargeted per cell) ─────────────────────

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var matrix = Matrix;
        if (matrix is null || matrix.Size == 0) { ClearTip(); return; }

        var p = e.GetPosition(this);
        var col = (int)Math.Floor((p.X - RowHeaderWidth) / CellWidth);
        var row = (int)Math.Floor((p.Y - ColHeaderHeight) / CellHeight);
        if (col < 0 || row < 0 || col >= matrix.Size || row >= matrix.Size) { ClearTip(); return; }

        if (row == _tipRow && col == _tipCol) return;
        _tipRow = row; _tipCol = col;
        ToolTip = $"{matrix.Labels[row]} ↔ {matrix.Labels[col]}: {Display(matrix.At(row, col))}";
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearTip();
    }

    private void ClearTip()
    {
        if (_tipRow == -1 && _tipCol == -1) return;
        _tipRow = _tipCol = -1;
        ToolTip = null;
    }

    // ── Text caches ──────────────────────────────────────────────────────────────────────────────

    private static string Display(double v) =>
        double.IsNaN(v) ? "—" : v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private FormattedText CellText(string text, double dip)
    {
        if (_cellTextCache.TryGetValue(text, out var ft)) return ft;
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CellTypeface, 10, Brushes.White, dip);
        _cellTextCache[text] = ft;
        return ft;
    }

    private FormattedText RowHeaderText(string text, Brush brush, double dip)
    {
        if (_rowHeaderCache.TryGetValue(text, out var ft)) return ft;
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CellTypeface, 11, brush, dip)
        {
            MaxTextWidth = RowHeaderWidth - 8,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
        };
        _rowHeaderCache[text] = ft;
        return ft;
    }

    private FormattedText ColHeaderText(string text, Brush brush, double dip)
    {
        if (_colHeaderCache.TryGetValue(text, out var ft)) return ft;
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CellTypeface, 11, brush, dip)
        {
            MaxTextWidth = ColHeaderHeight - 8,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        _colHeaderCache[text] = ft;
        return ft;
    }
}
