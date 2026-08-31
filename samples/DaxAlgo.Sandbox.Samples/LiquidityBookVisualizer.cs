using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// The benchmark unit: an authored answer to the same brief the hand-written
/// <c>TradingTerminal.OrderBook</c> window answers — a depth ladder, a liquidity heatmap over time
/// with trade dots and a microprice line, an imbalance lane, and a microstructure strip.
///
/// <para><b>This exists to be compared, not to be shipped.</b> Its purpose is the goal loop's
/// benchmark: drive Hyperion with a one-line brief and measure the result against a hand-written
/// panel. Half of any shortfall would otherwise be unattributable — is the SDK unable to express the
/// window, or did the model simply not write it? This is the control. It is written by hand with full
/// knowledge of the SDK, so everything missing from it is missing from the <b>SDK</b>, and a model
/// cannot be blamed for it.</para>
///
/// <para>What it establishes, by being as close as the contract allows: the picture is reachable.
/// Ladder, heatmap, overlaid prints, a signed lane and a tile strip are all one call each. What is
/// NOT reachable is recorded in <c>docs/authored-unit-gaps.md</c> and summarised here:</para>
///
/// <list type="bullet">
/// <item><b>Actions.</b> The hand-written window has Export ladder CSV, Export series CSV, Save PNG,
/// Save preset, Delete preset and a help popup. A unit can declare parameters; it cannot declare a
/// verb. There is no affordance in the SDK for "a button that does something".</item>
/// <item><b>Scrolling.</b> The hand-written ladder is a <c>ScrollViewer</c> over every level. An
/// immediate-mode panel draws what fits; <see cref="LevelsParameter"/> is the workaround.</item>
/// <item><b>A time axis on a captured series.</b> A heat column is a capture tick, not a clock
/// interval, so the trade dots are placed by index rather than by their own timestamp.</item>
/// </list>
///
/// <para><b>Closed on 2026-08-31:</b> selection and zoom/pan. The host now accumulates each gesture
/// into state a pure <c>Draw</c> can read — <c>Cursor.HasSelection</c> pins a price row here, and
/// <c>Viewport.Zoom</c> and <c>PanX</c> choose the visible window that used to need a parameter.</para>
///
/// <para>Everything the SDK <i>can</i> do is used here deliberately, so the comparison is fair: the
/// gaps are the contract's, not the author's.</para>
/// </summary>
public sealed class LiquidityBookVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string LevelsParameter = "levels";
    public const string SweepSizeParameter = "sweepSize";
    public const string ShowTradesParameter = "showTrades";
    public const string ShowMicropriceParameter = "showMicroprice";
    public const string ShowImbalanceLaneParameter = "showImbalanceLane";

    /// <summary>Heat columns kept. Bounded because the ring is the unit's whole memory footprint and
    /// a book can tick thousands of times a minute.</summary>
    private const int MaximumHeatColumns = 480;

    /// <summary>Columns shown at zoom 1. Deliberately NOT a parameter: the wheel already chooses how
    /// much history is on screen, and a spinner beside it would be two controls fighting over one
    /// number.</summary>
    private const int DefaultHeatWindow = 180;

    /// <summary>Price rows in the heatmap. Odd, so the mid sits on a row rather than between two.</summary>
    private const int HeatRows = 41;

    private const int TapeCapacity = 256;

    private InstrumentId _instrument;
    private int _levels;
    private double _sweepSize;
    private bool _showTrades;
    private bool _showMicroprice;
    private bool _showLane;

    private OrderFlowImbalance? _flow;
    private Vpin? _vpin;
    private readonly SpreadStats _spread = new(200);

    private Quote? _quote;
    private DepthSnapshot? _depth;

    /// <summary>One captured column of the heatmap: the book as it stood, reduced to a price ladder of
    /// resting size plus the scalars the overlays need.</summary>
    private readonly List<Column> _columns = new(MaximumHeatColumns);

    private readonly List<TradePrint> _tape = new(TapeCapacity);

    private sealed record Column(
        double[] Sizes, double Low, double High, double Microprice, double Mid, double Queue);

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Int(
            LevelsParameter, "Ladder levels", 12, min: 1, max: 30, group: "Book", unit: "levels"),
        StrategyParameter.Number(
            SweepSizeParameter, "Sweep size", 50d, min: 1d, max: 1_000_000d, group: "Book",
            unit: "contracts"),
        StrategyParameter.Bool(ShowTradesParameter, "Trade dots", true, group: "Heatmap"),
        StrategyParameter.Bool(ShowMicropriceParameter, "Microprice line", true, group: "Heatmap"),
        StrategyParameter.Bool(ShowImbalanceLaneParameter, "Imbalance lane", true, group: "Heatmap"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;

    /// <summary>The hand-written window's arrangement: heatmap dominant, ladder as a fixed column
    /// beside it, numbers underneath both.</summary>
    public UnitLayout Layout => UnitLayout.Rows(
        UnitLayout.Columns(
            UnitLayout.Panel("Liquidity", DrawHeat).Star(3),
            UnitLayout.Panel("Book", DrawLadder).Pixels(240)).Star(4),
        UnitLayout.Panel("Microstructure", DrawStrip).Pixels(64));

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _levels = context.Parameters.GetInt(LevelsParameter);
        _sweepSize = context.Parameters.GetDouble(SweepSizeParameter);
        _showTrades = context.Parameters.GetBool(ShowTradesParameter);
        _showMicroprice = context.Parameters.GetBool(ShowMicropriceParameter);
        _showLane = context.Parameters.GetBool(ShowImbalanceLaneParameter);

        _flow = new OrderFlowImbalance(200);
        _vpin = new Vpin(500d, buckets: 50);

        _columns.Clear();
        _tape.Clear();
        _spread.Reset();
        _quote = null;
        _depth = null;
        return Task.CompletedTask;
    }

    public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ct.ThrowIfCancellationRequested();

        if (quote.InstrumentId != _instrument) return Task.CompletedTask;

        _quote = quote;
        _spread.Update(quote);
        return Task.CompletedTask;
    }

    public Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ct.ThrowIfCancellationRequested();

        if (trade.InstrumentId != _instrument || _flow is null || _vpin is null)
            return Task.CompletedTask;

        var side = TradeClassifier.Classify(trade, _quote);
        _flow.Update(trade.Size, side);
        _vpin.Update(trade.Size, side);

        if (_tape.Count == TapeCapacity) _tape.RemoveAt(0);
        _tape.Add(trade);
        return Task.CompletedTask;
    }

    public Task OnDepthAsync(
        InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ct.ThrowIfCancellationRequested();

        if (instrument != _instrument || _flow is null) return Task.CompletedTask;

        _depth = depth;
        Capture(depth);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reduces the book to one heatmap column: resting size bucketed into <see cref="HeatRows"/>
    /// price rows spanning the depth actually quoted.
    ///
    /// <para>Done here rather than in <c>Draw</c> on purpose. Draw runs on the render thread and is
    /// invoked more than once per frame; this runs once per snapshot on the pump thread, which is the
    /// rule the whole contract rests on.</para>
    /// </summary>
    private void Capture(DepthSnapshot depth)
    {
        var low = depth.Bids.Count > 0 ? depth.Bids[^1].Price : depth.BestBid;
        var high = depth.Asks.Count > 0 ? depth.Asks[^1].Price : depth.BestAsk;
        if (!(high > low)) return;

        var sizes = new double[HeatRows];
        var span = high - low;

        foreach (var level in depth.Bids) Deposit(sizes, level, low, span);
        foreach (var level in depth.Asks) Deposit(sizes, level, low, span);

        if (_columns.Count == MaximumHeatColumns) _columns.RemoveAt(0);
        _columns.Add(new Column(
            sizes, low, high,
            Book.Microprice(depth),
            (depth.BestBid + depth.BestAsk) * 0.5d,
            Book.Imbalance(depth, _levels)));
    }

    private static void Deposit(double[] sizes, DepthLevel level, double low, double span)
    {
        var row = (int)((level.Price - low) / span * (HeatRows - 1));
        if (row >= 0 && row < sizes.Length) sizes[row] += level.Size;
    }

    /// <summary>The whole picture on one surface — the preview's path, and the fallback for a host
    /// that does not build <see cref="Layout"/>.</summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var panel = surface.Panel("Liquidity book", RenderPanelKind.Chart);
        if (_columns.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for depth…");
            return;
        }

        var (strip, upper) = PlotArea.Of(surface).SplitBottom(60d);
        var (ladder, heat) = upper.SplitRight(240d);

        DrawHeat(surface, heat);
        DrawLadder(surface, ladder);
        DrawStrip(surface, strip);
    }

    private void DrawHeat(IRenderSurface surface) => DrawHeat(surface, PlotArea.Of(surface));

    private void DrawHeat(IRenderSurface surface, PlotArea area)
    {
        if (_columns.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for depth…");
            return;
        }

        var (lane, plot) = _showLane ? area.SplitBottom(56d) : (PlotArea.None, area);

        // The visible window, oldest at the left, chosen by the WHEEL and the DRAG rather than by a
        // parameter. Zoom divides the window — showing fewer columns is what zooming in means on a
        // chart, and scaling the drawing instead would magnify the text with it. Pan then slides that
        // window back through the history.
        var view = surface.Viewport;
        var window = Math.Clamp((int)(DefaultHeatWindow / view.Zoom), 8, MaximumHeatColumns);

        // Pan is in pixels, so it becomes columns at the current column width. Clamped so a viewer
        // cannot drag past the end of what was captured and be shown an empty panel with no way back.
        var perColumn = Math.Max(1d, plot.Width / Math.Max(1, window));
        var back = (int)Math.Clamp(view.PanX / perColumn, 0d, Math.Max(0, _columns.Count - window));

        var first = Math.Max(0, _columns.Count - window - back);
        var visible = Math.Min(window, _columns.Count - first);
        if (visible <= 0) return;

        // Rows are shared across the window so the picture does not shimmer as the book drifts; the
        // extremes of the window set the scale.
        var low = double.MaxValue;
        var high = double.MinValue;
        for (var i = first; i < _columns.Count; i++)
        {
            low = Math.Min(low, _columns[i].Low);
            high = Math.Max(high, _columns[i].High);
        }
        if (!(high > low)) return;

        var range = new PlotRange(low, high);
        surface.AxisY(low, high, "F2");

        Heatmap.Draw(
            surface, visible, HeatRows,
            (column, row) => Resample(_columns[first + column], row, low, high),
            HeatmapOptions.Default,
            plot);

        // The microprice path over the liquidity, which is the read the window exists for: the price
        // leaning into whichever side of the book is thinning.
        if (_showMicroprice)
        {
            using var series = surface.Series("Microprice", RenderSeriesKind.Line);
            for (var i = first; i < _columns.Count; i++)
                surface.Push(plot.ToX(i - first, visible), plot.ToY(_columns[i].Microprice, range));
        }

        // Prints as dots at their own price. Their X is the right edge rather than a true time axis:
        // a column is a capture tick, not a clock, and a unit has no way to ask the host what time a
        // column happened at.
        if (_showTrades && _tape.Count > 0)
        {
            var shown = Math.Min(_tape.Count, visible);
            for (var i = 0; i < shown; i++)
            {
                var print = _tape[^(shown - i)];
                if (print.Price < low || print.Price > high) continue;

                var side = TradeClassifier.Classify(print, _quote);
                surface.SetStyle(new RenderStyle(surface.Theme(
                    side == TradeSide.Buy ? RenderThemeColor.Bullish : RenderThemeColor.Bearish)));
                surface.Marker(
                    plot.ToX(visible - shown + i, visible), plot.ToY(print.Price, range),
                    RenderMarkerShape.Circle);
            }
        }

        if (_showLane)
        {
            Plot.Caption(surface, lane, "Queue imbalance");
            var queue = new double[visible];
            for (var i = 0; i < visible; i++) queue[i] = _columns[first + i].Queue;
            Histogram.Draw(surface, queue, area: lane);
        }

        DrawHoverReadout(surface, plot, range, first, visible);
    }

    /// <summary>
    /// The crosshair, the hover readout, and the PINNED row.
    ///
    /// <para>Both are pure reads of host-accumulated state, which is what makes them legal here:
    /// <c>Draw</c> is invoked more than once per frame, so a unit could not consume a click even if
    /// one were delivered. The host turns the click into a point that stays put and this reads it.</para>
    ///
    /// <para>The pin is drawn before the crosshair so the moving line sits over the standing one, and
    /// it is drawn whether or not the pointer is present — someone who pinned a level then moved away
    /// to read it should still see what they pinned.</para>
    /// </summary>
    private void DrawHoverReadout(
        IRenderSurface surface, PlotArea plot, PlotRange range, int first, int visible)
    {
        var cursor = surface.Cursor;

        if (cursor.HasSelection && plot.Contains(cursor.SelectionX, cursor.SelectionY))
        {
            var pinned = PriceAt(cursor.SelectionY, plot, range);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), Thickness: 1.5d));
            surface.Line(plot.X, cursor.SelectionY, plot.Right, cursor.SelectionY);
            surface.Text(plot.X + 4d, cursor.SelectionY - 6d, $"pinned {pinned:F2}");
        }

        if (!cursor.IsInside || !plot.Contains(cursor.X, cursor.Y)) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Grid)));
        surface.Line(plot.X, cursor.Y, plot.Right, cursor.Y);
        surface.Line(cursor.X, plot.Y, cursor.X, plot.Bottom);

        var price = PriceAt(cursor.Y, plot, range);
        var column = (int)((cursor.X - plot.X) / Math.Max(1d, plot.Width) * visible);
        var index = first + Math.Clamp(column, 0, Math.Max(0, visible - 1));
        var resting = index < _columns.Count
            ? Resample(_columns[index], RowOf(cursor.Y, plot), range.Minimum, range.Maximum)
            : 0d;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text)));
        surface.Text(cursor.X + 8d, cursor.Y - 8d, $"{price:F2}  ·  {resting:N0}");
    }

    private static double PriceAt(double y, PlotArea plot, PlotRange range) =>
        range.Minimum + (plot.Bottom - y) / Math.Max(1d, plot.Height) * (range.Maximum - range.Minimum);

    private static int RowOf(double y, PlotArea plot) =>
        Math.Clamp((int)((plot.Bottom - y) / Math.Max(1d, plot.Height) * (HeatRows - 1)), 0, HeatRows - 1);

    /// <summary>A column's size at a shared row, since each column was bucketed over its own span.</summary>
    private static double Resample(Column column, int row, double low, double high)
    {
        var price = low + (high - low) * row / (HeatRows - 1);
        if (price < column.Low || price > column.High) return 0d;

        var span = column.High - column.Low;
        if (!(span > 0d)) return 0d;

        var index = (int)((price - column.Low) / span * (HeatRows - 1));
        return index >= 0 && index < column.Sizes.Length ? column.Sizes[index] : 0d;
    }

    private void DrawLadder(IRenderSurface surface) => DrawLadder(surface, PlotArea.Of(surface));

    private void DrawLadder(IRenderSurface surface, PlotArea area)
    {
        if (_depth is null)
        {
            Plot.Caption(surface, area, "No depth yet.");
            return;
        }

        // Levels is a parameter because the panel cannot scroll: an immediate-mode panel draws what
        // fits, and the hand-written window puts the same ladder in a ScrollViewer.
        Ladder.Draw(surface, _depth, LadderOptions.Default with { Levels = _levels }, area);
    }

    private void DrawStrip(IRenderSurface surface) => DrawStrip(surface, PlotArea.Of(surface));

    private void DrawStrip(IRenderSurface surface, PlotArea area)
    {
        if (_flow is null || _vpin is null || _columns.Count == 0)
        {
            Plot.Caption(surface, area, "Microstructure appears once depth and trades arrive.");
            return;
        }

        var last = _columns[^1];
        var buy = _depth is null ? 0d : Book.SweepSlippage(_depth.Asks, _sweepSize, last.Microprice);
        var sell = _depth is null ? 0d : Book.SweepSlippage(_depth.Bids, _sweepSize, last.Microprice);

        Tiles.Draw(
            surface,
            [
                new Tile("Mid", last.Mid.ToString("F2"), "best bid/ask"),
                Tile.Signed("Edge", last.Microprice - last.Mid,
                    (last.Microprice - last.Mid).ToString("F4"), "micro − mid"),
                Tile.Signed("Queue", last.Queue, last.Queue.ToString("F2"), $"{_levels} levels"),
                Tile.Signed("Flow", _flow.Value, _flow.Value.ToString("F2"), "signed / gross"),
                new Tile(
                    "Toxicity",
                    _vpin.IsReady ? _vpin.Value.ToString("F2") : "—",
                    _vpin.IsReady && _vpin.Value > 0.7d ? "one-sided" : "balanced",
                    _vpin.IsReady && _vpin.Value > 0.7d ? RenderThemeColor.Warning : RenderThemeColor.Text),
                new Tile(
                    "Sweep",
                    buy > 0d ? buy.ToString("F4") : "—",
                    sell > 0d ? $"sell {sell:F4}" : $"{_sweepSize:N0} lots"),
                new Tile(
                    "Spread",
                    _spread.IsReady ? _spread.ZScore.ToString("F1") : "—",
                    _spread.IsWide() ? "unusually wide" : "normal",
                    _spread.IsWide() ? RenderThemeColor.Warning : RenderThemeColor.Text),
            ],
            area: area);
    }
}
