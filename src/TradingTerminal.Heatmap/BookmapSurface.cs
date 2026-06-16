using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Heatmap;

/// <summary>
/// The combined <b>Bookmap + VolBook</b> drawing surface. A single immediate-mode
/// <see cref="FrameworkElement"/> that renders the whole view through one <see cref="DrawingContext"/>
/// — the liquidity heatmap (a <see cref="WriteableBitmap"/> blit), volume profile, VWAP, POC / value
/// area, trade dots (large-lot / iceberg styling), mid track, axes, the CVD sub-panel, the live DOM,
/// and the hover crosshair.
///
/// <para>Performance: the data layer (everything except the crosshair) is rendered once per feed tick
/// into a cached <see cref="DrawingGroup"/>; <see cref="OnRender"/> just blits that drawing and adds
/// the crosshair, so hovering/scrubbing is cheap. This replaces the old approach of adding thousands
/// of <c>Shape</c>/<c>TextBlock</c> objects to a <c>Canvas</c> every frame, which is the WPF
/// retained-mode perf trap that made the window laggy.</para>
/// </summary>
public sealed class BookmapSurface : FrameworkElement
{
    private const double LeftAxisW = 58;
    private const double TopPad = 8;
    private const double RightGutterW = 156;
    private const double BottomAxisH = 26;
    private const double CvdPanelH = 92;
    private const double CvdGap = 8;
    private const double CavPanelH = 64;
    private const double CavGap = 8;
    private const int PriceRows = 120;

    // Fixed dark palette (this is a "lights-off" microstructure view, theme-independent).
    private static readonly Color EmptyCell = Color.FromRgb(0x0A, 0x0A, 0x0F);
    private static readonly Brush SurfaceBack = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0F)));
    private static readonly Brush BuyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x9A)));
    private static readonly Brush SellBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)));
    private static readonly Brush BuyDot = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x4D, 0xD0, 0xC4)));
    private static readonly Brush SellDot = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x6E, 0x6B)));
    private static readonly Brush NeutralDot = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xBD, 0xBD, 0xBD)));
    private static readonly Brush AxisText = Frozen(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));
    private static readonly Brush AxisFaint = Frozen(new SolidColorBrush(Color.FromArgb(0xAA, 0x9E, 0x9E, 0x9E)));
    private static readonly Brush PriceText = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
    private static readonly Brush GutterBack = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x18, 0x18, 0x20)));
    private static readonly Brush PanelBack = Frozen(new SolidColorBrush(Color.FromArgb(0x44, 0x16, 0x18, 0x20)));
    private static readonly Brush ProfileBuy = Frozen(new SolidColorBrush(Color.FromArgb(0x72, 0x26, 0xA6, 0x9A)));
    private static readonly Brush ProfileSell = Frozen(new SolidColorBrush(Color.FromArgb(0x72, 0xEF, 0x53, 0x50)));
    private static readonly Brush ProfileBuyDim = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0x26, 0xA6, 0x9A)));
    private static readonly Brush ProfileSellDim = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xEF, 0x53, 0x50)));
    private static readonly Brush PocFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0x8F, 0x00)));
    private static readonly Brush VolBar = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x6A, 0x8E, 0xC9)));
    private static readonly Brush CavTextBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x82, 0xB1, 0xFF)));
    private static readonly Brush DeltaUp = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x26, 0xA6, 0x9A)));
    private static readonly Brush DeltaDown = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x53, 0x50)));
    private static readonly Brush VwapText = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xC1, 0x07)));
    private static readonly Brush CvdText = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x4D, 0xD0, 0xE1)));
    private static readonly Brush TooltipBack = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x1B, 0x1E, 0x26)));

    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x33, 0x88, 0x88, 0x88), 0.5);
    private static readonly Pen GutterPen = FrozenPen(Color.FromArgb(0x33, 0x88, 0x88, 0x88), 1.0);
    private static readonly Pen ZeroPen = FrozenPen(Color.FromArgb(0x33, 0x88, 0x88, 0x88), 0.5, 3, 3);
    private static readonly Pen MidPen = FrozenPen(Color.FromArgb(0xCC, 0xEC, 0xEC, 0xEC), 1.1);
    private static readonly Pen VwapPen = FrozenPen(Color.FromArgb(0xE6, 0xFF, 0xC1, 0x07), 1.3);
    private static readonly Pen PocPen = FrozenPen(Color.FromArgb(0xE6, 0xFF, 0x8F, 0x00), 1.0, 5, 4);
    private static readonly Pen ValueAreaPen = FrozenPen(Color.FromArgb(0xC0, 0x42, 0xA5, 0xF5), 1.0, 5, 4);
    private static readonly Pen CvdPen = FrozenPen(Color.FromArgb(0xF0, 0x4D, 0xD0, 0xE1), 1.3);
    private static readonly Pen CavPen = FrozenPen(Color.FromArgb(0xF0, 0x82, 0xB1, 0xFF), 1.3);
    private static readonly Pen LargePen = FrozenPen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 1.3);
    private static readonly Pen IcebergPen = FrozenPen(Color.FromArgb(0xF0, 0xFF, 0xE0, 0x54), 1.2);
    private static readonly Pen CrosshairPen = FrozenPen(Color.FromArgb(0xC0, 0xCF, 0xCF, 0xCF), 0.7);
    private static readonly Pen TooltipBorderPen = FrozenPen(Color.FromArgb(0xFF, 0x55, 0x5A, 0x60), 1.0);

    private static readonly Brush PocBrush = FrozenBrush(Color.FromArgb(0xE6, 0xFF, 0x8F, 0x00));
    private static readonly Brush ValueAreaBrush = FrozenBrush(Color.FromArgb(0xC0, 0x42, 0xA5, 0xF5));

    private static readonly (double T, Color C)[] Magma =
    {
        (0.00, Color.FromRgb(0x05, 0x01, 0x0A)),
        (0.18, Color.FromRgb(0x33, 0x0A, 0x5C)),
        (0.38, Color.FromRgb(0x78, 0x1C, 0x6D)),
        (0.58, Color.FromRgb(0xBA, 0x36, 0x55)),
        (0.78, Color.FromRgb(0xF1, 0x70, 0x5C)),
        (0.92, Color.FromRgb(0xFE, 0xB1, 0x77)),
        (1.00, Color.FromRgb(0xFC, 0xFD, 0xBF)),
    };

    private static readonly Typeface MonoFace = new("Consolas");

    private BookmapHeatmapViewModel? _vm;
    private readonly DrawingGroup _dataLayer = new();

    // Layout + mapping captured each Recompute so the crosshair handler can map cursor → price.
    private double _pMin, _pMax;
    private double _hmLeft, _hmTop, _hmRight, _hmBottom;
    private double _cvdTop, _cvdBottom, _cavTop, _cavBottom;
    private int _start, _vis;
    private DepthSnapshot? _current;
    private int _decimals = 2;
    private double _dpi = 1.0;

    private double _zoom = 1.0;
    private double _centerPrice = double.NaN;

    private Point _mouse;
    private bool _mouseInside;

    public BookmapSurface()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Focusable = false;
    }

    public BookmapHeatmapViewModel? ViewModel
    {
        get => _vm;
        set
        {
            _vm = value;
            _zoom = 1.0; _centerPrice = double.NaN;
            Recompute();
            InvalidateVisual();
        }
    }

    /// <summary>Called when the VM's buffers change — rebuild the cached data layer.</summary>
    public void OnDataUpdated()
    {
        Recompute();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Recompute();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawDrawing(_dataLayer);
        if (_mouseInside) DrawCrosshair(dc);
    }

    // ── Data layer ───────────────────────────────────────────────────────────────────────────────

    private void Recompute()
    {
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        using var dc = _dataLayer.Open();

        double w = ActualWidth, h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        dc.DrawRectangle(SurfaceBack, null, new Rect(0, 0, w, h)); // full-bounds fill → hit-testable

        if (_vm is null) return;

        // Stack the optional bottom sub-panels (CVD over CAV) above the time axis, heatmap fills the rest.
        bool showCvd = _vm.ShowCvdPanel;
        bool showCav = _vm.ShowCav;
        _hmLeft = LeftAxisW; _hmTop = TopPad; _hmRight = w - RightGutterW;
        double avail = h - BottomAxisH;
        if (showCav) { _cavBottom = avail; _cavTop = avail - CavPanelH; avail = _cavTop - CavGap; }
        if (showCvd) { _cvdBottom = avail; _cvdTop = avail - CvdPanelH; avail = _cvdTop - CvdGap; }
        _hmBottom = avail;
        double hmW = _hmRight - _hmLeft, hmH = _hmBottom - _hmTop;
        _decimals = _vm.PriceDecimals;

        var all = _vm.AllColumns;
        var stats = _vm.AllStats;
        int total = all.Count;
        if (total == 0 || hmW <= 4 || hmH <= 4) return;

        _vis = Math.Min(BookmapHeatmapViewModel.VisibleColumns, total);
        int maxStart = Math.Max(0, total - _vis);
        _start = _vm.IsPaused ? Math.Clamp((int)Math.Round(_vm.ScrubPosition * maxStart), 0, maxStart) : maxStart;
        _current = all[_start + _vis - 1];

        double pMin = double.MaxValue, pMax = double.MinValue;
        for (int c = 0; c < _vis; c++)
        {
            var snap = all[_start + c];
            foreach (var l in snap.Bids) { if (l.Price < pMin) pMin = l.Price; if (l.Price > pMax) pMax = l.Price; }
            foreach (var l in snap.Asks) { if (l.Price < pMin) pMin = l.Price; if (l.Price > pMax) pMax = l.Price; }
        }
        if (pMin > pMax) return;
        if (pMax - pMin < 1e-9) { pMax += 0.5; pMin -= 0.5; }

        double fullSpan = pMax - pMin;
        if (_zoom > 1.0 && !double.IsNaN(_centerPrice))
        {
            double span = fullSpan / _zoom;
            double centre = Math.Clamp(_centerPrice, pMin + span / 2, pMax - span / 2);
            _pMin = centre - span / 2; _pMax = centre + span / 2;
        }
        else { _pMin = pMin; _pMax = pMax; }

        DrawHeatmap(dc, all, hmW, hmH);
        if (_vm.ShowVolumeProfile) DrawSvp(dc, hmW, hmH);
        DrawGridAndAxes(dc, all, hmH);
        DrawMidTrack(dc, all, hmW);
        if (_vm.ShowVwap) DrawVwapTrack(dc, stats, hmW);
        if (_vm.ShowValueArea) DrawValueArea(dc);
        if (_vm.ShowTradeDots) DrawTradeDots(dc, all, hmW);
        if (showCvd) DrawCvdPanel(dc, stats, _cvdTop, _cvdBottom);
        if (showCav) DrawCavPanel(dc, stats, _cavTop, _cavBottom);
        if (_vm.ShowOrderBook) DrawDom(dc, w);
        if (_vm.IsPaused) DrawText(dc, "⏸ PAUSED — scrubbing history", _hmLeft + 104, _hmTop + 1, 240, AxisFaint, 10, TextAlignment.Left);
    }

    private void DrawHeatmap(DrawingContext dc, IReadOnlyList<DepthSnapshot> all, double hmW, double hmH)
    {
        int cols = _vis;
        var size = new double[PriceRows * cols];
        double maxSize = 0;
        double span = _pMax - _pMin;
        for (int c = 0; c < cols; c++)
        {
            var snap = all[_start + c];
            foreach (var l in snap.Bids) Accumulate(l, c);
            foreach (var l in snap.Asks) Accumulate(l, c);
        }
        if (maxSize <= 0) maxSize = 1;

        var pixels = new byte[PriceRows * cols * 4];
        for (int r = 0; r < PriceRows; r++)
            for (int c = 0; c < cols; c++)
            {
                double s = size[r * cols + c];
                Color col = s <= 0 ? EmptyCell : Sample(Math.Sqrt(s / maxSize));
                int i = (r * cols + c) * 4;
                pixels[i + 0] = col.B;
                pixels[i + 1] = col.G;
                pixels[i + 2] = col.R;
                pixels[i + 3] = 0xFF;
            }

        var bmp = new WriteableBitmap(cols, PriceRows, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, cols, PriceRows), pixels, cols * 4, 0);
        bmp.Freeze();
        dc.DrawImage(bmp, new Rect(_hmLeft, _hmTop, hmW, hmH));

        void Accumulate(DepthLevel l, int c)
        {
            if (l.Price < _pMin || l.Price > _pMax) return;
            int r = (int)Math.Round((_pMax - l.Price) / span * (PriceRows - 1));
            if (r < 0 || r >= PriceRows) return;
            int idx = r * cols + c;
            size[idx] += l.Size;
            if (size[idx] > maxSize) maxSize = size[idx];
        }
    }

    /// <summary>SVP — Session Range Volume Profile: a left-edge volume-at-price histogram for the whole
    /// session (buy/sell split). Buckets inside the 70 % value area are drawn at full strength and those
    /// outside it dimmed; the POC bucket is filled gold.</summary>
    private void DrawSvp(DrawingContext dc, double hmW, double hmH)
    {
        var buckets = _vm!.VolumeProfileSnapshot();
        if (buckets.Length == 0) return;

        long maxVol = 1;
        double pocPrice = double.NaN;
        foreach (var b in buckets)
        {
            long t = b.BuyVolume + b.SellVolume;
            if (t > maxVol) { maxVol = t; pocPrice = b.Price; }
        }

        var va = _vm.ComputeValueArea();
        bool hasVa = va.TotalVolume > 0;
        double maxW = Math.Min(hmW * 0.22, 190);
        double thickness = Math.Max(2.0, hmH / PriceRows);

        foreach (var b in buckets)
        {
            if (b.Price < _pMin || b.Price > _pMax) continue;
            long tot = b.BuyVolume + b.SellVolume;
            if (tot <= 0) continue;
            double wpx = maxW * tot / maxVol;
            double y = PriceToY(b.Price) - thickness / 2;

            if (b.Price.Equals(pocPrice))
            {
                dc.DrawRectangle(PocFill, null, new Rect(_hmLeft, y, wpx, thickness));
                continue;
            }

            bool inVa = hasVa && b.Price >= va.ValueAreaLow - 1e-9 && b.Price <= va.ValueAreaHigh + 1e-9;
            double buyW = wpx * ((double)b.BuyVolume / tot);
            double sellW = wpx - buyW;
            if (buyW >= 0.5) dc.DrawRectangle(inVa ? ProfileBuy : ProfileBuyDim, null, new Rect(_hmLeft, y, buyW, thickness));
            if (sellW >= 0.5) dc.DrawRectangle(inVa ? ProfileSell : ProfileSellDim, null, new Rect(_hmLeft + buyW, y, sellW, thickness));
        }

        DrawText(dc, "SVP", _hmLeft + 3, _hmTop + 2, 60, AxisFaint, 9, TextAlignment.Left);
    }

    private void DrawGridAndAxes(DrawingContext dc, IReadOnlyList<DepthSnapshot> all, double hmH)
    {
        int priceTicks = Math.Clamp((int)(hmH / 30), 3, 12);
        for (int i = 0; i <= priceTicks; i++)
        {
            double frac = (double)i / priceTicks;
            double price = _pMax - frac * (_pMax - _pMin);
            double y = _hmTop + frac * hmH;
            dc.DrawLine(GridPen, new Point(_hmLeft, y), new Point(_hmRight, y));
            DrawText(dc, price.ToString("N" + _decimals, CultureInfo.InvariantCulture), 0, y - 7, LeftAxisW - 6, PriceText, 10.5, TextAlignment.Right);
        }

        double hmW = _hmRight - _hmLeft;
        int timeTicks = Math.Min(6, _vis);
        for (int i = 0; i < timeTicks; i++)
        {
            int c = timeTicks == 1 ? _vis - 1 : (int)Math.Round((double)i * (_vis - 1) / (timeTicks - 1));
            double x = _hmLeft + (c + 0.5) / _vis * hmW;
            DrawText(dc, all[_start + c].TimestampUtc.ToLocalTime().ToString("HH:mm:ss"), x - 34, _hmBottom + 5, 68, AxisText, 10, TextAlignment.Center);
        }
    }

    private void DrawMidTrack(DrawingContext dc, IReadOnlyList<DepthSnapshot> all, double hmW)
    {
        var geo = BuildPolyline(c =>
        {
            var snap = all[_start + c];
            if (snap.BestBid <= 0 || snap.BestAsk <= 0) return null;
            double mid = (snap.BestBid + snap.BestAsk) * 0.5;
            if (mid < _pMin || mid > _pMax) return null;
            return new Point(_hmLeft + (c + 0.5) / _vis * hmW, PriceToY(mid));
        });
        if (geo is not null) dc.DrawGeometry(null, MidPen, geo);
    }

    private void DrawVwapTrack(DrawingContext dc, IReadOnlyList<ColumnStat> stats, double hmW)
    {
        var geo = BuildPolyline(c =>
        {
            double v = stats[_start + c].Vwap;
            if (double.IsNaN(v) || v < _pMin || v > _pMax) return null;
            return new Point(_hmLeft + (c + 0.5) / _vis * hmW, PriceToY(v));
        });
        if (geo is not null) dc.DrawGeometry(null, VwapPen, geo);
    }

    private void DrawValueArea(DrawingContext dc)
    {
        var va = _vm!.ComputeValueArea();
        if (va.TotalVolume <= 0) return;
        DrawLevel(dc, va.ValueAreaHigh, ValueAreaPen, ValueAreaBrush, "VAH");
        DrawLevel(dc, va.ValueAreaLow, ValueAreaPen, ValueAreaBrush, "VAL");
        DrawLevel(dc, va.Poc, PocPen, PocBrush, "POC");
    }

    private void DrawLevel(DrawingContext dc, double price, Pen pen, Brush textBrush, string label)
    {
        if (price < _pMin || price > _pMax) return;
        double y = PriceToY(price);
        dc.DrawLine(pen, new Point(_hmLeft, y), new Point(_hmRight, y));
        DrawText(dc, label, _hmRight - 40, y - 13, 38, textBrush, 9.5, TextAlignment.Right);
    }

    private void DrawTradeDots(DrawingContext dc, IReadOnlyList<DepthSnapshot> all, double hmW)
    {
        var trades = _vm!.RecentTrades();
        if (trades.Length == 0) return;

        double tMin = all[_start].TimestampUtc.ToOADate();
        double tMax = all[_start + _vis - 1].TimestampUtc.ToOADate();
        double tSpan = tMax - tMin;
        bool highlight = _vm.HighlightLargeLots;

        long maxSize = 1;
        foreach (var t in trades) if (t.Size > maxSize) maxSize = t.Size;

        foreach (var t in trades)
        {
            if (t.Price < _pMin || t.Price > _pMax) continue;
            double to = t.Time.ToOADate();
            if (to < tMin || to > tMax) continue;
            double frac = tSpan > 0 ? (to - tMin) / tSpan : 1.0;
            double x = _hmLeft + Math.Clamp(frac, 0, 1) * hmW;
            double y = PriceToY(t.Price);
            double r = 2.5 + 9.0 * Math.Sqrt((double)t.Size / maxSize);
            var fill = t.Side switch { BubbleSide.Buy => BuyDot, BubbleSide.Sell => SellDot, _ => NeutralDot };

            if (highlight && t.Large)
            {
                r = Math.Max(r, 6.5);
                dc.DrawEllipse(fill, LargePen, new Point(x, y), r, r);
            }
            else
            {
                dc.DrawEllipse(fill, null, new Point(x, y), r, r);
            }

            if (highlight && t.Iceberg)
                dc.DrawEllipse(null, IcebergPen, new Point(x, y), r + 3, r + 3);
        }
    }

    private void DrawCvdPanel(DrawingContext dc, IReadOnlyList<ColumnStat> stats, double top, double bottom)
    {
        double ph = bottom - top;
        double left = _hmLeft, right = _hmRight, pw = right - left;
        if (ph < 12 || pw < 12) return;

        dc.DrawRectangle(PanelBack, null, new Rect(left, top, pw, ph));
        dc.DrawLine(GridPen, new Point(left, top), new Point(right, top));

        double cvdMin = 0, cvdMax = 0;
        long maxAbsDelta = 1;
        for (int c = 0; c < _vis; c++)
        {
            var s = stats[_start + c];
            if (s.Cvd < cvdMin) cvdMin = s.Cvd;
            if (s.Cvd > cvdMax) cvdMax = s.Cvd;
            long net = s.BuyVolume - s.SellVolume;
            if (Math.Abs(net) > maxAbsDelta) maxAbsDelta = Math.Abs(net);
        }
        if (cvdMax - cvdMin < 1e-9) cvdMax = cvdMin + 1;

        double mid = (top + bottom) / 2;
        double barW = Math.Max(1.0, pw / _vis * 0.7);
        for (int c = 0; c < _vis; c++)
        {
            long net = stats[_start + c].BuyVolume - stats[_start + c].SellVolume;
            if (net == 0) continue;
            double x = left + (c + 0.5) / _vis * pw;
            double bh = (ph / 2 - 2) * Math.Min(1.0, (double)Math.Abs(net) / maxAbsDelta);
            if (net > 0) dc.DrawRectangle(DeltaUp, null, new Rect(x - barW / 2, mid - bh, barW, bh));
            else dc.DrawRectangle(DeltaDown, null, new Rect(x - barW / 2, mid, barW, bh));
        }

        if (cvdMin <= 0 && cvdMax >= 0)
        {
            double y0 = bottom - (0 - cvdMin) / (cvdMax - cvdMin) * ph;
            dc.DrawLine(ZeroPen, new Point(left, y0), new Point(right, y0));
        }

        var geo = BuildPolyline(c =>
            new Point(left + (c + 0.5) / _vis * pw, bottom - (stats[_start + c].Cvd - cvdMin) / (cvdMax - cvdMin) * ph));
        if (geo is not null) dc.DrawGeometry(null, CvdPen, geo);

        DrawText(dc, "CVD", left + 3, top + 1, 60, CvdText, 9.5, TextAlignment.Left);
    }

    /// <summary>COB — the Current Order Book column down the right edge: a horizontal resting-size
    /// histogram per price (bid green / ask red) with numeric sizes at every visible level (spaced so a
    /// dense ladder doesn't overlap), plus the cumulative bid/ask depth totals in the footer.</summary>
    private void DrawDom(DrawingContext dc, double w)
    {
        var snap = _current;
        double gx = _hmRight;
        double gw = w - _hmRight;
        if (snap is null || gw <= 8) return;

        dc.DrawRectangle(GutterBack, null, new Rect(gx, _hmTop, gw, _hmBottom - _hmTop));
        dc.DrawLine(GutterPen, new Point(gx, _hmTop), new Point(gx, _hmBottom));

        long maxSize = 1, totBid = 0, totAsk = 0;
        foreach (var l in snap.Bids) { if (l.Price < _pMin || l.Price > _pMax) continue; if (l.Size > maxSize) maxSize = l.Size; totBid += l.Size; }
        foreach (var l in snap.Asks) { if (l.Price < _pMin || l.Price > _pMax) continue; if (l.Size > maxSize) maxSize = l.Size; totAsk += l.Size; }

        const double labelW = 46;
        const double labelGap = 12; // min vertical px between numeric labels so they don't overlap
        double barMax = Math.Max(8, gw - labelW - 8);
        double rowH = Math.Max(2, (_hmBottom - _hmTop) / PriceRows);

        DrawLevels(snap.Bids, BuyBrush);
        DrawLevels(snap.Asks, SellBrush);

        double lastBidLabel = double.NegativeInfinity, lastAskLabel = double.NegativeInfinity;
        foreach (var l in snap.Bids) AddSizeLabel(l, BuyBrush, ref lastBidLabel);
        foreach (var l in snap.Asks) AddSizeLabel(l, SellBrush, ref lastAskLabel);

        DrawText(dc, "COB", gx + 4, _hmTop + 1, 60, AxisFaint, 9, TextAlignment.Left);
        double footY = _hmBottom - 13;
        dc.DrawRectangle(GutterBack, null, new Rect(gx + 1, footY - 1, gw - 1, 14));
        DrawText(dc, "Σ " + totBid.ToString("N0", CultureInfo.InvariantCulture), gx + 4, footY, gw / 2 - 4, BuyBrush, 9.5, TextAlignment.Left);
        DrawText(dc, totAsk.ToString("N0", CultureInfo.InvariantCulture) + " Σ", gx + gw / 2, footY, gw / 2 - 4, SellBrush, 9.5, TextAlignment.Right);

        void DrawLevels(IReadOnlyList<DepthLevel> levels, Brush brush)
        {
            foreach (var l in levels)
            {
                if (l.Price < _pMin || l.Price > _pMax) continue;
                double bw = barMax * Math.Min(1.0, (double)l.Size / maxSize);
                double y = PriceToY(l.Price) - rowH / 2;
                dc.DrawRectangle(brush, null, new Rect(gx + 4, y, Math.Max(1, bw), rowH));
            }
        }

        void AddSizeLabel(DepthLevel l, Brush brush, ref double lastY)
        {
            if (l.Price < _pMin || l.Price > _pMax) return;
            double y = PriceToY(l.Price);
            if (Math.Abs(y - lastY) < labelGap) return; // skip overlapping labels
            lastY = y;
            DrawText(dc, l.Size.ToString("N0", CultureInfo.InvariantCulture), w - labelW - 2, y - 7, labelW, brush, 10, TextAlignment.Right);
        }
    }

    /// <summary>CAV — Cumulative Average Volume: a compact strip showing per-column traded volume bars
    /// with the running session-average-volume line over them, so spikes above/below the session
    /// average pop out.</summary>
    private void DrawCavPanel(DrawingContext dc, IReadOnlyList<ColumnStat> stats, double top, double bottom)
    {
        double ph = bottom - top;
        double left = _hmLeft, right = _hmRight, pw = right - left;
        if (ph < 12 || pw < 12) return;

        dc.DrawRectangle(PanelBack, null, new Rect(left, top, pw, ph));
        dc.DrawLine(GridPen, new Point(left, top), new Point(right, top));

        long maxVol = 1;
        for (int c = 0; c < _vis; c++)
        {
            long tot = stats[_start + c].BuyVolume + stats[_start + c].SellVolume;
            if (tot > maxVol) maxVol = tot;
        }

        double barW = Math.Max(1.0, pw / _vis * 0.7);
        for (int c = 0; c < _vis; c++)
        {
            long tot = stats[_start + c].BuyVolume + stats[_start + c].SellVolume;
            if (tot <= 0) continue;
            double x = left + (c + 0.5) / _vis * pw;
            double bh = (ph - 4) * Math.Min(1.0, (double)tot / maxVol);
            dc.DrawRectangle(VolBar, null, new Rect(x - barW / 2, bottom - bh, barW, bh));
        }

        var geo = BuildPolyline(c =>
            new Point(left + (c + 0.5) / _vis * pw, bottom - (ph - 4) * Math.Min(1.0, stats[_start + c].AvgVolume / maxVol)));
        if (geo is not null) dc.DrawGeometry(null, CavPen, geo);

        DrawText(dc, "CAV", left + 3, top + 1, 60, CavTextBrush, 9.5, TextAlignment.Left);
    }

    // ── Crosshair (drawn live, on top of the cached data layer) ──────────────────────────────────

    private void DrawCrosshair(DrawingContext dc)
    {
        if (_vm is null || _current is null) return;
        var pos = _mouse;
        if (pos.X < _hmLeft || pos.X > _hmRight || pos.Y < _hmTop || pos.Y > _hmBottom) return;

        double frac = (pos.Y - _hmTop) / (_hmBottom - _hmTop);
        double price = _pMax - frac * (_pMax - _pMin);

        dc.DrawLine(CrosshairPen, new Point(_hmLeft, pos.Y), new Point(_hmRight, pos.Y));
        dc.DrawLine(CrosshairPen, new Point(pos.X, _hmTop), new Point(pos.X, _hmBottom));

        var lines = new List<(string Text, Brush Brush)>
        {
            ($"Price  {price.ToString("N" + _decimals, CultureInfo.InvariantCulture)}", PriceText),
        };
        long resting = NearestLevelSize(price, out bool isBid);
        if (resting > 0) lines.Add(($"{(isBid ? "Bid" : "Ask")}    {resting:N0}", isBid ? BuyBrush : SellBrush));
        long profVol = ProfileVolumeAt(price);
        if (profVol > 0) lines.Add(($"Vol@   {profVol:N0}", VwapText));

        DrawTooltip(dc, pos, lines);
    }

    private void DrawTooltip(DrawingContext dc, Point cursor, List<(string Text, Brush Brush)> lines)
    {
        const double pad = 6, lineH = 15;
        double tw = 0;
        var fts = new List<FormattedText>(lines.Count);
        foreach (var (text, brush) in lines)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 11, brush, _dpi);
            fts.Add(ft);
            if (ft.Width > tw) tw = ft.Width;
        }
        double bw = tw + pad * 2, bh = lines.Count * lineH + pad * 2;
        double bx = cursor.X + 16, by = cursor.Y + 14;
        if (bx + bw > _hmRight) bx = cursor.X - bw - 8;
        if (by + bh > _hmBottom) by = cursor.Y - bh - 8;
        bx = Math.Max(0, bx); by = Math.Max(0, by);

        dc.DrawRoundedRectangle(TooltipBack, TooltipBorderPen, new Rect(bx, by, bw, bh), 3, 3);
        for (int i = 0; i < fts.Count; i++)
            dc.DrawText(fts[i], new Point(bx + pad, by + pad + i * lineH));
    }

    private long NearestLevelSize(double price, out bool isBid)
    {
        isBid = true;
        if (_current is null) return 0;
        long best = 0;
        double bestDist = double.MaxValue;
        foreach (var l in _current.Bids)
        {
            double d = Math.Abs(l.Price - price);
            if (d < bestDist) { bestDist = d; best = l.Size; isBid = true; }
        }
        foreach (var l in _current.Asks)
        {
            double d = Math.Abs(l.Price - price);
            if (d < bestDist) { bestDist = d; best = l.Size; isBid = false; }
        }
        return best;
    }

    private long ProfileVolumeAt(double price)
    {
        if (_vm is null) return 0;
        var buckets = _vm.VolumeProfileSnapshot();
        long best = 0;
        double bestDist = double.MaxValue;
        foreach (var b in buckets)
        {
            double d = Math.Abs(b.Price - price);
            if (d < bestDist) { bestDist = d; best = b.BuyVolume + b.SellVolume; }
        }
        return best;
    }

    // ── Mouse: hover crosshair + price zoom ──────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        InvalidateVisual(); // cheap — redraws the cached data layer + the crosshair only
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouseInside = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm is null) return;
        var pos = e.GetPosition(this);
        if (pos.X < _hmLeft || pos.X > _hmRight || pos.Y < _hmTop || pos.Y > _hmBottom) return;

        double frac = (pos.Y - _hmTop) / (_hmBottom - _hmTop);
        _centerPrice = _pMax - frac * (_pMax - _pMin);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1.0, 50.0);
        if (_zoom <= 1.0001) { _zoom = 1.0; _centerPrice = double.NaN; }
        e.Handled = true;
        Recompute();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) // double-click resets the price zoom
        {
            _zoom = 1.0; _centerPrice = double.NaN;
            Recompute();
            InvalidateVisual();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private StreamGeometry? BuildPolyline(Func<int, Point?> map)
    {
        var pts = new List<Point>(_vis);
        for (int c = 0; c < _vis; c++)
            if (map(c) is { } p) pts.Add(p);
        if (pts.Count < 2) return null;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, false);
        }
        geo.Freeze();
        return geo;
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double w, Brush brush, double size, TextAlignment align)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, size, brush, _dpi);
        if (w > 0) { ft.MaxTextWidth = Math.Max(1, w); ft.TextAlignment = align; }
        dc.DrawText(ft, new Point(x, y));
    }

    private double PriceToY(double price)
    {
        double frac = (_pMax - price) / (_pMax - _pMin);
        return _hmTop + Math.Clamp(frac, 0, 1) * (_hmBottom - _hmTop);
    }

    private Color Sample(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (int i = 1; i < Magma.Length; i++)
        {
            if (t <= Magma[i].T)
            {
                var (t0, c0) = Magma[i - 1];
                var (t1, c1) = Magma[i];
                double f = (t - t0) / (t1 - t0);
                return Color.FromRgb(Lerp(c0.R, c1.R, f), Lerp(c0.G, c1.G, f), Lerp(c0.B, c1.B, f));
            }
        }
        return Magma[^1].C;
    }

    private static byte Lerp(byte a, byte b, double f) => (byte)(a + (b - a) * Math.Clamp(f, 0, 1));

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    private static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double thickness, params double[] dashes)
    {
        var pen = new Pen(new SolidColorBrush(c), thickness);
        if (dashes is { Length: > 0 }) pen.DashStyle = new DashStyle(dashes, 0);
        pen.Freeze();
        return pen;
    }
}
