using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// High-throughput renderer for the pressure-map matrix (ticker rows × 1-minute columns). Draws the
/// whole grid in a single retained <see cref="OnRender"/> pass — issuing <c>DrawRectangle</c>/
/// <c>DrawText</c> instructions straight into the <see cref="DrawingContext"/> — instead of
/// materialising one <c>Rectangle</c>/<c>TextBlock</c> UIElement per cell.
///
/// <para>The previous Canvas approach rebuilt several thousand framework elements (≈ rows × columns,
/// e.g. 100 × 60 = 6 000) on every render tick, which saturated the layout pass and the GC and made
/// the window crawl. A retained draw has no per-cell elements and no child layout, so the same grid
/// repaints in a fraction of the time.</para>
/// </summary>
internal sealed class PressureMatrixView : FrameworkElement
{
    public const double LabelWidth = 72;
    public const double HeaderHeight = 20;
    public const double RowHeight = 15;
    public const double CellWidth = 16;

    private static readonly Color NoDataColor = (Color)ColorConverter.ConvertFromString("#0F1620");
    private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#1C2530");
    private static readonly Color TextColor = (Color)ColorConverter.ConvertFromString("#E6EDF3");
    private static readonly Color DimColor = (Color)ColorConverter.ConvertFromString("#8A94A6");
    private static readonly Color PinColor = (Color)ColorConverter.ConvertFromString("#FFD600");

    private static readonly Brush BackdropBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0F14")));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(TextColor));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(DimColor));
    private static readonly Brush PinBrush = Freeze(new SolidColorBrush(PinColor));
    private static readonly Pen PinPen = FreezePen(new Pen(PinBrush, 1));

    private static readonly Typeface Mono =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly Dictionary<uint, Brush> _brushCache = new();

    private IReadOnlyList<PressureRowSnapshot> _rows = Array.Empty<PressureRowSnapshot>();
    private int _cols;
    private DateTime _latest;
    private string? _pinned;

    /// <summary>Swaps in the latest snapshot and repaints. Re-measures only when the grid dimensions
    /// change (so the ScrollViewer's extent stays correct without a layout pass every tick).</summary>
    public void SetData(IReadOnlyList<PressureRowSnapshot> rows, int cols, DateTime latest, string? pinned)
    {
        var sizeChanged = rows.Count != _rows.Count || cols != _cols;
        _rows = rows;
        _cols = cols;
        _latest = latest;
        _pinned = pinned;
        if (sizeChanged) InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Maps a point (in this element's coordinates) to a (row, col) matrix index.</summary>
    public (int row, int col) HitTest(Point p)
    {
        if (p.Y < HeaderHeight) return (-1, -1);
        var row = (int)((p.Y - HeaderHeight) / RowHeight);
        var col = p.X < LabelWidth ? 0 : (int)((p.X - LabelWidth) / CellWidth);
        return (row, col);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_rows.Count == 0 || _cols == 0) return new Size(0, 0);
        return new Size(LabelWidth + _cols * CellWidth, HeaderHeight + _rows.Count * RowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_rows.Count == 0 || _cols == 0) return;

        var width = LabelWidth + _cols * CellWidth;
        var height = HeaderHeight + _rows.Count * RowHeight;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Opaque backdrop — also makes the whole element hit-testable (hover/click span the gaps).
        dc.DrawRectangle(BackdropBrush, null, new Rect(0, 0, width, height));

        // Time axis — every 10th column labelled with local HH:mm.
        if (_latest != DateTime.MinValue)
            for (var c = 0; c < _cols; c += 10)
            {
                var t = _latest.AddMinutes(-(_cols - 1 - c)).ToLocalTime();
                DrawText(dc, t.ToString("HH:mm"), LabelWidth + c * CellWidth, (HeaderHeight - 10 - 3) / 2.0,
                    CellWidth * 6, DimBrush, 10, dpi);
            }

        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            var y = HeaderHeight + r * RowHeight;
            var pinned = _pinned is not null && string.Equals(_pinned, row.Symbol, StringComparison.Ordinal);

            DrawText(dc, row.Symbol, 2, y + (RowHeight - 10.5 - 3) / 2.0, LabelWidth - 6,
                pinned ? PinBrush : TextBrush, 10.5, dpi);

            var cells = row.Cells;
            var count = cells.Count;
            for (var c = 0; c < _cols; c++)
            {
                var cell = c < count ? cells[c] : null;
                var color = cell is null ? NoDataColor : ColorFor(cell.Signal, cell.Intensity);
                dc.DrawRectangle(BrushFor(color), null,
                    new Rect(LabelWidth + c * CellWidth, y, CellWidth - 1, RowHeight - 1));
            }

            if (pinned)
                dc.DrawRectangle(null, PinPen, new Rect(0.5, y + 0.5, width - 1, RowHeight - 1));
        }
    }

    private void DrawText(DrawingContext dc, string s, double x, double top, double maxWidth, Brush brush, double size, double dpi)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(x, top));
    }

    // ── Colour helpers (moved off the window — same palette/maths) ────────────────────────────────

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

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
