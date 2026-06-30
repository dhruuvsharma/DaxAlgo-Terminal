using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.BubbleChart;

/// <summary>
/// The Bookmap-style drawing surface for the experimental bubble heatmap. An immediate-mode
/// <see cref="FrameworkElement"/> that paints, through one <see cref="DrawingContext"/>:
/// <list type="bullet">
///   <item>the <b>liquidity heatmap</b> — resting L2 size per (price, time) cell on a sqrt-compressed
///   blue→yellow→red ramp (brighter = heavier resting orders), the Bookmap core;</item>
///   <item><b>trade volume bubbles</b> — every recent print as a circle, area ∝ volume, green = buy /
///   red = sell, large prints white-rimmed;</item>
///   <item>the <b>mid-price track</b>, and the price + time axes.</item>
/// </list>
///
/// <para>Memory-safety: every brush/pen/typeface is a cached, frozen <c>static readonly</c>; nothing
/// heavy is allocated in the draw loop. It redraws only when the VM raises
/// <see cref="BubbleChartViewModel.SurfaceChanged"/> (timer-coalesced) or on resize — never per feed
/// event. The VM owns the data and the subscriptions.</para>
/// </summary>
public sealed class HeatmapBubbleSurface : FrameworkElement
{
    private const double LeftAxisW = 60;
    private const double TopPad = 8;
    private const double BottomAxisH = 24;
    private const double RightPad = 10;

    private static readonly Typeface MonoFace = new("Consolas");

    private static readonly Brush BackBrush = Frozen(0xFF, 0x0A, 0x0A, 0x0F);
    private static readonly Brush AxisText = Frozen(0xFF, 0x9E, 0x9E, 0x9E);
    private static readonly Brush PriceText = Frozen(0xFF, 0xC8, 0xC8, 0xC8);
    private static readonly Brush HintText = Frozen(0xFF, 0x77, 0x7D, 0x85);

    private static readonly Brush BuyDot = Frozen(0xE6, 0x3D, 0xD9, 0x8A);   // green = net buying
    private static readonly Brush SellDot = Frozen(0xE6, 0xFF, 0x5A, 0x57);  // red = net selling
    private static readonly Brush NeutralDot = Frozen(0xC0, 0xCF, 0xCF, 0xCF);

    private static readonly Pen GridPen = FrozenPen(0x22, 0x88, 0x88, 0x88, 0.5);
    private static readonly Pen MidPen = FrozenPen(0xCC, 0xEC, 0xEC, 0xEC, 1.1);
    private static readonly Pen DotOutline = FrozenPen(0x55, 0xFF, 0xFF, 0xFF, 0.6);
    private static readonly Pen LargePen = FrozenPen(0xF0, 0xFF, 0xFF, 0xFF, 1.4);

    // 64-step heat ramp (low → high resting size): dark navy → blue → cyan → yellow → orange → red.
    private const int HeatSteps = 64;
    private static readonly Brush[] HeatPalette = BuildHeatPalette();

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    private static Pen FrozenPen(byte a, byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(Frozen(a, r, g, b), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush[] BuildHeatPalette()
    {
        // (position, R, G, B) stops.
        (double p, byte r, byte g, byte b)[] stops =
        {
            (0.00, 0x0A, 0x0C, 0x1A),
            (0.16, 0x12, 0x26, 0x6E),
            (0.36, 0x16, 0x6E, 0xC8),
            (0.56, 0x16, 0xC0, 0xB0),
            (0.72, 0xD8, 0xCC, 0x28),
            (0.86, 0xF0, 0x8C, 0x1E),
            (1.00, 0xF6, 0x3B, 0x32),
        };
        var palette = new Brush[HeatSteps];
        for (var i = 0; i < HeatSteps; i++)
        {
            var t = (double)i / (HeatSteps - 1);
            var s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].p) s++;
            var (p0, r0, g0, b0) = stops[s];
            var (p1, r1, g1, b1) = stops[s + 1];
            var f = p1 > p0 ? (t - p0) / (p1 - p0) : 0;
            byte Lerp(byte a, byte c) => (byte)(a + (c - a) * f);
            palette[i] = Frozen(0xFF, Lerp(r0, r1), Lerp(g0, g1), Lerp(b0, b1));
        }
        return palette;
    }

    private BubbleChartViewModel? _vm;

    public HeatmapBubbleSurface()
    {
        ClipToBounds = true;
    }

    public BubbleChartViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null) _vm.SurfaceChanged -= OnSurfaceChanged;
            _vm = value;
            if (_vm is not null) _vm.SurfaceChanged += OnSurfaceChanged;
            InvalidateVisual();
        }
    }

    public void Detach()
    {
        if (_vm is not null) _vm.SurfaceChanged -= OnSurfaceChanged;
        _vm = null;
    }

    private void OnSurfaceChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));
        if (_vm is null || w < 80 || h < 80) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var columns = _vm.Columns;
        var total = columns.Count;
        if (total == 0)
        {
            DrawText(dc, _vm.NoDepth ? "No L2 depth from this broker — pick a depth-capable one (e.g. Binance)." : "Waiting for the order book…",
                w / 2 - 200, h / 2 - 8, AxisText, 12, dpi, 400, TextAlignment.Center);
            return;
        }

        var plotL = LeftAxisW;
        var plotT = TopPad;
        var plotB = h - BottomAxisH;
        var plotR = w - RightPad;
        var plotW = plotR - plotL;
        var plotH = plotB - plotT;
        if (plotW < 40 || plotH < 40) return;

        var vis = total;
        var start = 0;
        var colW = plotW / vis;

        var last = columns[total - 1];
        var mid = last.BestBid > 0 && last.BestAsk > 0 ? (last.BestBid + last.BestAsk) * 0.5
            : last.BestAsk > 0 ? last.BestAsk : last.BestBid;
        var step = EstimateStep(last, mid);

        // Price window from the actual resting book across visible columns; clamp if absurdly deep.
        double pMin = double.MaxValue, pMax = double.MinValue;
        long maxSize = 1;
        for (var c = start; c < total; c++)
        {
            var col = columns[c];
            ScanLevels(col.Bids, ref pMin, ref pMax, ref maxSize);
            ScanLevels(col.Asks, ref pMin, ref pMax, ref maxSize);
        }
        if (pMax <= pMin)
        {
            DrawText(dc, "Order book is empty…", w / 2 - 120, h / 2 - 8, AxisText, 12, dpi, 240, TextAlignment.Center);
            return;
        }
        if (step > 0 && mid > 0 && (pMax - pMin) / step > 200)
        {
            pMin = Math.Max(pMin, mid - 100 * step);
            pMax = Math.Min(pMax, mid + 100 * step);
        }
        var vpad = step > 0 ? step : (pMax - pMin) * 0.02;
        pMin -= vpad; pMax += vpad;
        var pSpan = pMax - pMin;

        double Y(double price) => plotT + (pMax - price) / pSpan * plotH;
        var rowH = Math.Max(1.5, step > 0 ? step / pSpan * plotH : plotH / 40.0);

        // ── Liquidity heatmap ───────────────────────────────────────────────────────────────────
        for (var c = start; c < total; c++)
        {
            var col = columns[c];
            var x = plotL + (c - start) * colW;
            DrawColumnLevels(dc, col.Bids, x, colW, rowH, pMin, pMax, pSpan, plotT, plotH, maxSize);
            DrawColumnLevels(dc, col.Asks, x, colW, rowH, pMin, pMax, pSpan, plotT, plotH, maxSize);
        }

        // ── Mid-price track ─────────────────────────────────────────────────────────────────────
        var midGeo = new StreamGeometry();
        using (var g = midGeo.Open())
        {
            var started = false;
            for (var c = start; c < total; c++)
            {
                var col = columns[c];
                if (col.BestBid <= 0 || col.BestAsk <= 0) { started = false; continue; }
                var m = (col.BestBid + col.BestAsk) * 0.5;
                if (m < pMin || m > pMax) { started = false; continue; }
                var pt = new Point(plotL + (c - start + 0.5) * colW, Y(m));
                if (!started) { g.BeginFigure(pt, false, false); started = true; }
                else g.LineTo(pt, true, false);
            }
        }
        midGeo.Freeze();
        dc.DrawGeometry(null, MidPen, midGeo);

        // ── Trade volume bubbles ────────────────────────────────────────────────────────────────
        var trades = _vm.RecentTrades();
        if (trades.Length > 0)
        {
            var tMin = columns[start].TimestampUtc.ToOADate();
            var tMax = columns[total - 1].TimestampUtc.ToOADate();
            var tSpan = tMax - tMin;
            long maxTrade = 1;
            foreach (var t in trades) if (t.Size > maxTrade) maxTrade = t.Size;

            foreach (var t in trades)
            {
                if (t.Price < pMin || t.Price > pMax) continue;
                var to = t.Time.ToOADate();
                if (to < tMin || to > tMax) continue;
                var frac = tSpan > 0 ? (to - tMin) / tSpan : 1.0;
                var x = plotL + Math.Clamp(frac, 0, 1) * plotW;
                var y = Y(t.Price);
                var r = 2.5 + 9.0 * Math.Sqrt((double)t.Size / maxTrade);
                var fill = t.Side > 0 ? BuyDot : t.Side < 0 ? SellDot : NeutralDot;
                if (t.Large) { r = Math.Max(r, 6.5); dc.DrawEllipse(fill, LargePen, new Point(x, y), r, r); }
                else dc.DrawEllipse(fill, DotOutline, new Point(x, y), r, r);
            }
        }

        // ── Axes ─────────────────────────────────────────────────────────────────────────────────
        var decimals = _vm.PriceDecimals;
        var priceTicks = Math.Clamp((int)(plotH / 34), 3, 12);
        for (var i = 0; i <= priceTicks; i++)
        {
            var frac = (double)i / priceTicks;
            var price = pMax - frac * pSpan;
            var y = plotT + frac * plotH;
            dc.DrawLine(GridPen, new Point(plotL, y), new Point(plotR, y));
            DrawText(dc, price.ToString("N" + decimals, CultureInfo.InvariantCulture), 0, y - 7, PriceText, 10.5, dpi, LeftAxisW - 6, TextAlignment.Right);
        }
        var timeTicks = Math.Min(6, vis);
        for (var i = 0; i < timeTicks; i++)
        {
            var c = timeTicks == 1 ? vis - 1 : (int)Math.Round((double)i * (vis - 1) / (timeTicks - 1));
            var x = plotL + (c + 0.5) * colW;
            DrawText(dc, columns[start + c].TimestampUtc.ToLocalTime().ToString("HH:mm:ss"), x - 34, plotB + 4, AxisText, 10, dpi, 68, TextAlignment.Center);
        }
    }

    private static void ScanLevels(IReadOnlyList<DepthLevel> levels, ref double pMin, ref double pMax, ref long maxSize)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            var lv = levels[i];
            if (lv.Size <= 0) continue;
            if (lv.Price < pMin) pMin = lv.Price;
            if (lv.Price > pMax) pMax = lv.Price;
            if (lv.Size > maxSize) maxSize = lv.Size;
        }
    }

    private static void DrawColumnLevels(DrawingContext dc, IReadOnlyList<DepthLevel> levels, double x, double colW,
        double rowH, double pMin, double pMax, double pSpan, double plotT, double plotH, long maxSize)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            var lv = levels[i];
            if (lv.Size <= 0 || lv.Price < pMin || lv.Price > pMax) continue;
            var frac = Math.Sqrt((double)lv.Size / maxSize);          // sqrt-compress the heavy tail
            var brush = HeatPalette[Math.Clamp((int)(frac * (HeatSteps - 1)), 0, HeatSteps - 1)];
            var y = plotT + (pMax - lv.Price) / pSpan * plotH;
            dc.DrawRectangle(brush, null, new Rect(x, y - rowH / 2, colW + 0.6, rowH + 0.6));
        }
    }

    private static double EstimateStep(DepthSnapshot s, double mid)
    {
        var step = double.MaxValue;
        StepFrom(s.Asks, ref step);
        StepFrom(s.Bids, ref step);
        if (step == double.MaxValue || step <= 0)
            step = mid > 0 ? Math.Pow(10, Math.Floor(Math.Log10(mid)) - 3) : 0.0;
        return step;

        static void StepFrom(IReadOnlyList<DepthLevel> levels, ref double step)
        {
            for (var i = 1; i < levels.Count; i++)
            {
                var g = Math.Abs(levels[i].Price - levels[i - 1].Price);
                if (g > 1e-12 && g < step) step = g;
            }
        }
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size, double dpi, double width, TextAlignment align)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, size, brush, dpi);
        var ox = align switch
        {
            TextAlignment.Right => x + width - ft.Width,
            TextAlignment.Center => x + (width - ft.Width) / 2,
            _ => x,
        };
        dc.DrawText(ft, new Point(ox, y));
    }
}
