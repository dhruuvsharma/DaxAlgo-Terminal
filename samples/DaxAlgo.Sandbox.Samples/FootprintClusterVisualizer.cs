using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// Traded volume split by price inside each bar — the footprint cluster, drawn cell by cell.
///
/// <para><b>This is the exemplar for BUILDING a picture rather than calling one.</b> The other samples
/// answer their brief with a widget: one <c>Series.Chart</c>, one <c>Ladder.Draw</c>, one
/// <c>Heatmap.Draw</c>. That is the right answer when a widget fits, and it is why generated units
/// look like the widget library instead of like a trading window — a model copies the SHAPE of the
/// worked example far more strongly than it reads the prose, which is measured rather than assumed.
/// Nothing below is a widget. It is <c>Rect</c>, <c>Line</c> and <c>Text</c>.</para>
///
/// <para><b>The skeleton is the lesson</b>, and it is the same one the hand-written
/// <c>VolumeFootprint</c> and <c>OrderBook</c> windows use:</para>
///
/// <list type="number">
/// <item>guard the empty and the degenerate case, and say which it is;</item>
/// <item>choose the VISIBLE WINDOW — the most recent columns that fit, not all of them;</item>
/// <item>derive a SHARED AXIS from the data in that window, so every column lines up;</item>
/// <item>take ONE global scale over the same window, so colour means the same thing everywhere;</item>
/// <item>infer the tick and the decimals from the prices rather than hard-coding them;</item>
/// <item>build local <c>X()</c> / <c>Y()</c> mappers once, and draw through them;</item>
/// <item>lay the passes down in order — field, then marks, then overlays — because the last one wins;</item>
/// <item>end with a line of text stating what is on screen.</item>
/// </list>
///
/// <para>Follow that and a brief nothing in the library resembles is still drawable. Skip step 3 and
/// the columns do not line up; skip step 4 and each column is scaled to itself, which makes a quiet
/// bar look identical to a violent one.</para>
/// </summary>
public sealed class FootprintClusterVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string TickSizeParameter = "tickSize";
    public const string BarsVisibleParameter = "barsVisible";
    public const string ImbalanceRatioParameter = "imbalanceRatio";

    /// <summary>Bars kept in memory. Bounded, because a visualizer that keeps every bar of a session
    /// is a leak with a chart on top.</summary>
    private const int Capacity = 240;

    /// <summary>Below this a row is unreadable, so the cluster shows a price WINDOW around the point
    /// of control instead of squeezing every level into the panel.</summary>
    private const double MinimumRowHeight = 3d;

    private const double AxisWidth = 62d;

    private InstrumentId _instrument;
    private double _tick = 0.01d;
    private int _barsVisible;
    private double _imbalanceRatio;

    private readonly List<Bar> _bars = [];
    private Quote? _quote;
    private long _prints;
    private long _sessionDelta;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Number(
            TickSizeParameter, "Tick size", 0.01d, min: 0.000001d, max: 1000d, group: "Market",
            unit: "price"),
        StrategyParameter.Int(
            BarsVisibleParameter, "Bars visible", 24, min: 4, max: 120, group: "Cluster",
            unit: "bars"),
        StrategyParameter.Number(
            ImbalanceRatioParameter, "Imbalance ratio", 3d, min: 1.2d, max: 20d, group: "Cluster"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;

    /// <summary>
    /// Two panels: the cluster, and a readout under it.
    ///
    /// <para>Declared here, so the host frames and titles each region and calls the callback for it.
    /// A callback must NOT open a panel of its own — the header is already drawn, and a second one
    /// prints the title twice.</para>
    /// </summary>
    public UnitLayout Layout => UnitLayout.Rows(
        UnitLayout.Panel("Footprint", DrawCluster).Star(5),
        UnitLayout.Panel("Session", DrawReadout).Pixels(64));

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _tick = Math.Max(1e-9d, context.Parameters.GetDouble(TickSizeParameter));
        _barsVisible = context.Parameters.GetInt(BarsVisibleParameter);
        _imbalanceRatio = context.Parameters.GetDouble(ImbalanceRatioParameter);
        return Task.CompletedTask;
    }

    public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.InstrumentId == _instrument) _quote = quote;
        return Task.CompletedTask;
    }

    /// <summary>Opens a bar. The tape fills it; this only marks the boundary, so a bar with no prints
    /// still occupies a column and the gap is visible rather than silently closed up.</summary>
    public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (bar.InstrumentId != _instrument) return Task.CompletedTask;

        if (_bars.Count == 0 || _bars[^1].OpenTimeUtc != bar.OpenTimeUtc)
        {
            _bars.Add(new Bar(bar.OpenTimeUtc));
            if (_bars.Count > Capacity) _bars.RemoveAt(0);
        }

        return Task.CompletedTask;
    }

    public Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trade);
        if (trade.InstrumentId != _instrument || _bars.Count == 0) return Task.CompletedTask;
        if (!double.IsFinite(trade.Price) || trade.Size <= 0) return Task.CompletedTask;

        // The venue's own flag when it has one, the quote rule when it does not. Signing a print by
        // guesswork corrupts every number built on top of it, delta first.
        var side = TradeClassifier.Classify(trade, _quote);
        if (side == TradeSide.Unknown) return Task.CompletedTask;

        // Bucketed to the tick grid, so a price landing between levels joins one rather than opening
        // a row of its own and shearing the shared axis.
        var level = (long)Math.Round(trade.Price / _tick, MidpointRounding.AwayFromZero);
        _bars[^1].Add(level, trade.Size, side);

        _prints++;
        _sessionDelta += side == TradeSide.Buy ? trade.Size : -trade.Size;
        return Task.CompletedTask;
    }

    // ── the picture ─────────────────────────────────────────────────────────────────────────────

    private void DrawCluster(IRenderSurface surface)
    {
        var area = PlotArea.Of(surface);

        // 1 · The empty case and the degenerate one, told apart. A panel collapsed to nothing is not
        // the same as a feed that has sent nothing, and reporting either as the other sends somebody
        // looking in the wrong place.
        if (area.Width <= AxisWidth + 8d || area.Height <= 24d) return;
        if (_bars.Count == 0 || _prints == 0)
        {
            Plot.Waiting(surface, "Waiting for prints…");
            return;
        }

        // 2 · The visible window: the most recent bars that fit, never all of them. The wheel divides
        // the DATA range rather than scaling coordinates, so text and strokes keep their size.
        var wanted = Math.Max(4, (int)(_barsVisible / Math.Max(0.05d, surface.Viewport.Zoom)));
        var visible = Math.Min(_bars.Count, wanted);
        var first = _bars.Count - visible;
        var columnWidth = (area.Width - AxisWidth) / visible;

        // 3 · The shared axis, derived from the data in the window — the union of every level traded
        // in it, high to low. Built once and used by every column, which is what makes the rows line
        // up; a per-column axis is the commonest way a cluster comes out looking like noise.
        var levels = new SortedSet<long>();
        long peak = 1;
        for (var i = first; i < _bars.Count; i++)
        {
            foreach (var (level, cell) in _bars[i].Cells)
            {
                levels.Add(level);
                // 4 · ONE scale across the window. Per-column scaling makes a quiet bar look exactly
                // like a violent one, which is the opposite of what this picture is for.
                peak = Math.Max(peak, Math.Max(cell.Buy, cell.Sell));
            }
        }

        if (levels.Count == 0)
        {
            Plot.Waiting(surface, "Bars opened, no prints in them yet.");
            return;
        }

        // 5 · Rows that fit. When there are more levels than the panel can show, the window narrows
        // around the point of control rather than shrinking rows below legibility — an unreadable
        // full picture is worth less than a readable part of one.
        var rows = levels.Reverse().ToArray();
        var maxRows = Math.Max(1, (int)(area.Height / MinimumRowHeight));
        var poc = PointOfControl(first);
        rows = Window(rows, poc, maxRows);

        var rowHeight = area.Height / rows.Length;
        var decimals = Decimals(_tick);

        // 6 · The mappers, built once. Every draw below goes through these, so the layout lives in one
        // place and a change of margin cannot leave one pass behind.
        var index = new Dictionary<long, int>(rows.Length);
        for (var r = 0; r < rows.Length; r++) index[rows[r]] = r;

        double Y(int row) => area.Y + row * rowHeight;
        double X(int column) => area.X + AxisWidth + (column - first) * columnWidth;

        DrawPriceAxis(surface, rows, area, rowHeight, decimals);

        // 7 · The passes, in order, because the last one drawn wins the pixel.
        DrawCells(surface, first, index, X, Y, columnWidth, rowHeight, peak);
        DrawImbalances(surface, first, index, X, Y, columnWidth, rowHeight);
        DrawPointOfControl(surface, index, poc, area, X, Y, rowHeight, decimals);
        DrawHover(surface, area, rows, first, columnWidth, rowHeight, decimals);
    }

    /// <summary>
    /// What the pointer is over: the price, both sides, and the delta of that one cell.
    ///
    /// <para><b>A picture a viewer cannot read a value off is half a window.</b> A cluster is the
    /// clearest case of it — the colour says "a lot" and the number is the reason anyone is looking.
    /// The hand-written window puts this in a tooltip; here it is two lines of text beside the
    /// pointer, which needs no host support at all.</para>
    ///
    /// <para>The cursor is a READ. The host accumulates the movement and this only looks at where it
    /// ended up, which is what lets <c>Draw</c> stay pure — it is invoked more than once per frame,
    /// so anything latched here would advance twice.</para>
    ///
    /// <para>Inverting the mappers rather than searching: the same arithmetic that placed the cell,
    /// run backwards. A hit test that walks every cell looking for a match is the version that gets
    /// slow exactly when the picture gets interesting.</para>
    /// </summary>
    private void DrawHover(
        IRenderSurface surface, PlotArea area, long[] rows, int first,
        double columnWidth, double rowHeight, int decimals)
    {
        var cursor = surface.Cursor;
        if (!cursor.IsInside || cursor.X < area.X + AxisWidth) return;

        var column = first + (int)((cursor.X - area.X - AxisWidth) / Math.Max(1e-6d, columnWidth));
        var row = (int)((cursor.Y - area.Y) / Math.Max(1e-6d, rowHeight));
        if (column < first || column >= _bars.Count || row < 0 || row >= rows.Length) return;

        var level = rows[row];
        var cell = _bars[column].Cells.TryGetValue(level, out var found) ? found : default;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), Thickness: 1.2d));
        surface.Rect(
            area.X + AxisWidth + (column - first) * columnWidth, area.Y + row * rowHeight,
            columnWidth, rowHeight, filled: false);

        var delta = cell.Buy - cell.Sell;
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 10.5d));
        surface.Text(
            Math.Min(cursor.X + 10d, area.Right - 150d), Math.Max(area.Y + 2d, cursor.Y - 24d),
            (level * _tick).ToString("N" + decimals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture));

        surface.SetStyle(new RenderStyle(
            surface.Theme(delta >= 0 ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
            FontSize: 10.5d));
        surface.Text(
            Math.Min(cursor.X + 10d, area.Right - 150d), Math.Max(area.Y + 14d, cursor.Y - 11d),
            $"{cell.Buy:N0} × {cell.Sell:N0}   Δ {delta:+#,0;-#,0;0}");
    }

    /// <summary>The price ladder down the left edge. Labelled every few rows rather than every row:
    /// at three pixels a row the text would overlap itself into a grey band.</summary>
    private void DrawPriceAxis(
        IRenderSurface surface, long[] rows, PlotArea area, double rowHeight, int decimals)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));

        var every = Math.Max(1, (int)Math.Ceiling(12d / Math.Max(1d, rowHeight)));
        for (var r = 0; r < rows.Length; r += every)
        {
            surface.Text(
                area.X + 2d,
                area.Y + r * rowHeight + rowHeight * 0.5d - 5d,
                (rows[r] * _tick).ToString("N" + decimals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The field: one cell per traded level per bar, split bid-left / ask-right.
    ///
    /// <para>Width is the side's share of the global peak, so a cell's size is comparable with every
    /// other cell on screen. Alpha carries the same number again — colour alone is hard to rank, and
    /// two encodings of one value is what makes a cluster readable at a glance.</para>
    /// </summary>
    private void DrawCells(
        IRenderSurface surface, int first, Dictionary<long, int> index,
        Func<int, double> x, Func<int, double> y, double columnWidth, double rowHeight, long peak)
    {
        var half = columnWidth * 0.5d - 1d;
        if (half <= 0.5d) return;

        for (var i = first; i < _bars.Count; i++)
        {
            var left = x(i);
            foreach (var (level, cell) in _bars[i].Cells)
            {
                if (!index.TryGetValue(level, out var row)) continue;   // outside the price window
                var top = y(row);

                Fill(surface, RenderThemeColor.Bullish, cell.Buy, peak,
                    left + half - Share(cell.Buy, peak) * half, top, Share(cell.Buy, peak) * half, rowHeight);

                Fill(surface, RenderThemeColor.Bearish, cell.Sell, peak,
                    left + half + 1d, top, Share(cell.Sell, peak) * half, rowHeight);
            }
        }
    }

    private static double Share(long size, long peak) => peak <= 0 ? 0d : Math.Clamp(size / (double)peak, 0d, 1d);

    private static void Fill(
        IRenderSurface surface, RenderThemeColor tone, long size, long peak,
        double x, double y, double width, double height)
    {
        if (size <= 0 || width <= 0.4d || height <= 0.4d) return;

        // Floored well above zero: a cell that exists must be visible, or a thin tape reads as an
        // empty bar and the picture lies about there being no trade there.
        surface.SetStyle(new RenderStyle(surface.Theme(tone), Alpha: 0.25d + 0.75d * Share(size, peak)));
        surface.Rect(x, y, width, height);
    }

    /// <summary>
    /// Stacked imbalance: a bid resting under an ask it dwarfs, compared DIAGONALLY.
    ///
    /// <para>Diagonal because that is how the two sides actually meet — the bid at a price trades
    /// against the ask one tick above it. Comparing a level with itself is the usual mistake and it
    /// marks almost everything, which is the same as marking nothing.</para>
    /// </summary>
    private void DrawImbalances(
        IRenderSurface surface, int first, Dictionary<long, int> index,
        Func<int, double> x, Func<int, double> y, double columnWidth, double rowHeight)
    {
        for (var i = first; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var left = x(i);

            foreach (var (level, cell) in bar.Cells)
            {
                if (!index.TryGetValue(level, out var row)) continue;

                var above = bar.SellAt(level + 1);
                var below = bar.BuyAt(level - 1);

                var buyDominates = above > 0 && cell.Buy >= above * _imbalanceRatio;
                var sellDominates = below > 0 && cell.Sell >= below * _imbalanceRatio;
                if (!buyDominates && !sellDominates) continue;

                surface.SetStyle(new RenderStyle(
                    surface.Theme(buyDominates ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
                    Thickness: 1.2d));
                surface.Rect(left + 1d, y(row), columnWidth - 2d, rowHeight, filled: false);
            }
        }
    }

    /// <summary>The point of control, drawn last so it survives the field beneath it.</summary>
    private void DrawPointOfControl(
        IRenderSurface surface, Dictionary<long, int> index, long poc, PlotArea area,
        Func<int, double> x, Func<int, double> y, double rowHeight, int decimals)
    {
        if (!index.TryGetValue(poc, out var row)) return;

        var mid = y(row) + rowHeight * 0.5d;
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), Thickness: 1.4d, Dashed: true));
        surface.Line(area.X + AxisWidth, mid, area.Right, mid);

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), FontSize: 10d));
        surface.Text(
            area.X + AxisWidth + 4d, mid - 12d,
            "POC " + (poc * _tick).ToString("N" + decimals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>8 · What is on screen, stated. A picture nobody can read a number off is a decoration.</summary>
    private void DrawReadout(IRenderSurface surface)
    {
        var visible = Math.Min(_bars.Count, Math.Max(4, _barsVisible));
        var poc = _bars.Count == 0 ? 0L : PointOfControl(_bars.Count - visible);

        Tiles.Draw(surface,
        [
            new Tile("Session Δ", _sessionDelta.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                "buy − sell", _sessionDelta >= 0 ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
            new Tile("Prints", _prints.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), "classified"),
            new Tile("Bars", $"{visible} / {_bars.Count}", "shown / kept"),
            new Tile("POC", poc == 0 ? "—" : (poc * _tick).ToString(
                    "N" + Decimals(_tick).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture),
                "most traded"),
        ]);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The most-traded level across the visible window.</summary>
    private long PointOfControl(int first)
    {
        long best = 0, most = 0;
        for (var i = Math.Max(0, first); i < _bars.Count; i++)
        {
            foreach (var (level, cell) in _bars[i].Cells)
            {
                var total = cell.Buy + cell.Sell;
                if (total <= most) continue;
                most = total;
                best = level;
            }
        }

        return best;
    }

    /// <summary>The rows that fit, centred on the point of control rather than truncated from one
    /// end — a cluster clipped at the top hides exactly the half a breakout happens in.</summary>
    private static long[] Window(long[] rowsDescending, long centre, int maxRows)
    {
        if (rowsDescending.Length <= maxRows) return rowsDescending;

        var at = Array.IndexOf(rowsDescending, centre);
        if (at < 0) at = rowsDescending.Length / 2;

        var start = Math.Clamp(at - maxRows / 2, 0, rowsDescending.Length - maxRows);
        return [.. rowsDescending.Skip(start).Take(maxRows)];
    }

    /// <summary>Decimals implied by the tick, so 0.25 shows two and 0.00001 shows five. Hard-coding
    /// this is how a crypto pair comes out rounded to whole numbers.</summary>
    private static int Decimals(double tick)
    {
        var places = 0;
        while (places < 8 && Math.Abs(tick * Math.Pow(10d, places) % 1d) > 1e-9d) places++;
        return places;
    }

    /// <summary>One bar's cells, keyed by tick level. A dictionary rather than an array because a bar
    /// touches a handful of levels out of the session's range.</summary>
    private sealed class Bar(DateTime openTimeUtc)
    {
        public DateTime OpenTimeUtc { get; } = openTimeUtc;

        public Dictionary<long, Cell> Cells { get; } = [];

        public void Add(long level, long size, TradeSide side)
        {
            Cells.TryGetValue(level, out var cell);
            Cells[level] = side == TradeSide.Buy
                ? cell with { Buy = cell.Buy + size }
                : cell with { Sell = cell.Sell + size };
        }

        public long BuyAt(long level) => Cells.TryGetValue(level, out var c) ? c.Buy : 0L;

        public long SellAt(long level) => Cells.TryGetValue(level, out var c) ? c.Sell : 0L;
    }

    private readonly record struct Cell(long Buy, long Sell);
}
