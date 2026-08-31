using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// The book and the tape, read together: where the resting size is, who is crossing the spread, and
/// whether the flow has turned one-sided.
///
/// <para><b>The order-flow exemplar.</b> The other two samples take bars; this one takes
/// <see cref="StrategyDataRequirement.Depth"/> and <see cref="StrategyDataRequirement.TradeTape"/>,
/// which is what an imbalance, footprint or book brief actually needs — and what a model has no
/// worked example of otherwise.</para>
///
/// <para>Everything quantitative here is one call. <c>TradeClassifier</c> signs the prints,
/// <c>OrderFlowImbalance</c> accumulates them, <c>Vpin</c> buckets by volume, and <c>Book</c> answers
/// the microprice, the queue imbalance and what a sweep would cost. The hand-written part is the
/// history the picture is drawn from, which is bookkeeping rather than statistics.</para>
/// </summary>
public sealed class BookPressureVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string LevelsParameter = "levels";
    public const string BucketVolumeParameter = "bucketVolume";
    public const string SweepSizeParameter = "sweepSize";

    private InstrumentId _instrument;
    private int _levels;
    private double _sweepSize;

    private OrderFlowImbalance? _flow;
    private Vpin? _vpin;
    private readonly SpreadStats _spread = new(200);

    /// <summary>The last quote, kept because signing a print needs the book as it stood when the print
    /// landed — not as it stands now, a few events later.</summary>
    private Quote? _quote;

    private DepthSnapshot? _depth;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Int(
            LevelsParameter, "Book levels", 5, min: 1, max: 25, group: "Book", unit: "levels"),
        StrategyParameter.Number(
            BucketVolumeParameter, "VPIN bucket", 500d, min: 1d, max: 1_000_000d, group: "Flow",
            unit: "contracts"),
        StrategyParameter.Number(
            SweepSizeParameter, "Sweep size", 50d, min: 1d, max: 1_000_000d, group: "Book",
            unit: "contracts"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;

    /// <summary>
    /// One verb. Flow statistics accumulate from the moment the unit starts, so after a news print or
    /// a session roll the reading describes a market that is no longer there — and there is no value
    /// to set that means "forget it". That is what an action is for.
    /// </summary>
    public IReadOnlyList<UnitAction> Actions =>
    [
        new(ResetFlowAction, "Reset flow", "Forgets the accumulated imbalance, toxicity and history."),
        new(CopyBookAction, "Copy book", "Puts the visible ladder on the clipboard as CSV."),
    ];

    public const string ResetFlowAction = "reset-flow";
    public const string CopyBookAction = "copy-book";

    /// <summary>
    /// Runs the verb. The runtime calls this under the same gate as the data callbacks, so touching
    /// the same fields they touch needs no lock of its own.
    /// </summary>
    public Task OnActionAsync(string id, IVisualizerContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (id == ResetFlowAction)
        {
            _flow?.Reset();
            _vpin?.Reset();
            _spread.Reset();
            _history.Clear();
        }
        else if (id == CopyBookAction && _depth is { } depth)
        {
            // The unit produces the CONTENT; where it goes is the host's business. Offers are honoured
            // only inside an action, which is what ties a take-away to the button that was pressed.
            context.Export.Offer("Book (CSV)", Csv(depth));
        }

        // An id you do not recognise is not an error.
        return Task.CompletedTask;
    }

    /// <summary>Three panels: the pressure history, the live ladder beside it, and the numbers under
    /// both. A book is read as a column, so the ladder gets a fixed width and the history takes the
    /// rest.</summary>
    public UnitLayout Layout => UnitLayout.Rows(
        UnitLayout.Columns(
            UnitLayout.Panel("Pressure", DrawPressure).Star(3),
            UnitLayout.Panel("Book", DrawLadder).Pixels(230)).Star(4),
        UnitLayout.Panel("Flow", DrawStats).Pixels(64));

    private const int HistoryCapacity = 240;

    private readonly List<Sample> _history = new(HistoryCapacity);

    private readonly record struct Sample(double Microprice, double Mid, double Queue, double Delta);

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _levels = context.Parameters.GetInt(LevelsParameter);
        _sweepSize = context.Parameters.GetDouble(SweepSizeParameter);

        _flow = new OrderFlowImbalance(200);
        _vpin = new Vpin(context.Parameters.GetDouble(BucketVolumeParameter), buckets: 50);

        _history.Clear();
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

        // The venue's own aggressor flag when it has one, the quote rule when it does not. Signing a
        // print wrongly corrupts every statistic built on top of it, so it is worth the one call
        // rather than a comparison written from memory.
        var side = TradeClassifier.Classify(trade, _quote);
        _flow.Update(trade.Size, side);
        _vpin.Update(trade.Size, side);
        return Task.CompletedTask;
    }

    public Task OnDepthAsync(
        InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ct.ThrowIfCancellationRequested();

        if (instrument != _instrument || _flow is null) return Task.CompletedTask;

        _depth = depth;

        // A snapshot is the whole book, so everything here is recomputed rather than diffed.
        Record(new Sample(
            Book.Microprice(depth),
            (depth.BestBid + depth.BestAsk) * 0.5d,
            Book.Imbalance(depth, _levels),
            _flow.Value));

        return Task.CompletedTask;
    }

    /// <summary>The visible ladder as CSV. Built here rather than in <c>Draw</c>: this runs once, when
    /// the button is pressed, and Draw runs on the render thread every frame.</summary>
    private string Csv(DepthSnapshot depth)
    {
        var rows = new System.Text.StringBuilder();
        rows.AppendLine("side,price,size");

        foreach (var level in depth.Asks.Take(_levels))
            rows.AppendLine($"ask,{level.Price},{level.Size}");

        foreach (var level in depth.Bids.Take(_levels))
            rows.AppendLine($"bid,{level.Price},{level.Size}");

        return rows.ToString();
    }

    private void Record(Sample sample)
    {
        if (_history.Count == HistoryCapacity) _history.RemoveAt(0);
        _history.Add(sample);
    }

    /// <summary>The whole picture on one surface — the fallback for a host that does not build
    /// <see cref="Layout"/>, and what the authoring preview renders.</summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var panel = surface.Panel("Book pressure", RenderPanelKind.Chart);
        if (_history.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for depth…");
            return;
        }

        // SplitBottom and SplitRight both return (Taken, Remainder) — the strip FIRST. Named the other
        // way round the chart gets the strip and the strip gets the panel.
        var (stats, upper) = PlotArea.Of(surface).SplitBottom(58d);
        var (ladder, pressure) = upper.SplitRight(230d);

        DrawPressure(surface, pressure);
        DrawLadder(surface, ladder);
        DrawStats(surface, stats);
    }

    private void DrawPressure(IRenderSurface surface) => DrawPressure(surface, PlotArea.Of(surface));

    private void DrawPressure(IRenderSurface surface, PlotArea area)
    {
        if (_history.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for depth…");
            return;
        }

        // The microprice against the plain mid. They separate exactly when the book is lopsided, which
        // is the moment the next print is predictable — and the reason to measure edge from the
        // microprice rather than the mid.
        var (queue, price) = area.SplitBottom(area.Height * 0.35d);

        // The wheel chooses how much history is on screen. Zoom divides the DATA RANGE, never the
        // coordinates: scaling what is drawn would magnify the text and the line widths with it.
        var shown = Math.Clamp((int)(HistoryCapacity / surface.Viewport.Zoom), 8, _history.Count);

        Series.Chart(
            surface,
            [
                SeriesData.Dashed("Mid", Column(static s => s.Mid, shown), RenderThemeColor.Neutral),
                SeriesData.Line("Microprice", Column(static s => s.Microprice, shown), RenderThemeColor.Accent),
            ],
            area: price);

        // A clicked row stays clicked, so the viewer can read it after moving the pointer away. The
        // cursor is a READ — the host accumulates the gesture and this only looks at the result, which
        // is what lets Draw stay pure.
        var cursor = surface.Cursor;
        if (cursor.HasSelection && price.Contains(cursor.SelectionX, cursor.SelectionY))
        {
            var index = _history.Count - shown
                + (int)((cursor.SelectionX - price.X) / Math.Max(1d, price.Width) * shown);

            if (index >= 0 && index < _history.Count)
            {
                var pinned = _history[index];
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
                surface.Line(cursor.SelectionX, price.Y, cursor.SelectionX, price.Bottom);
                surface.Text(
                    cursor.SelectionX + 5d, price.Y + 12d,
                    $"micro {pinned.Microprice:F2}  queue {pinned.Queue:F2}");
            }
        }

        // Queue imbalance is already in [-1, 1], so it is a signed histogram about zero rather than a
        // line needing a scale of its own.
        Plot.Caption(surface, queue, "Queue imbalance");
        Histogram.Draw(surface, Column(static s => s.Queue, shown), area: queue);
    }

    private void DrawLadder(IRenderSurface surface) => DrawLadder(surface, PlotArea.Of(surface));

    private void DrawLadder(IRenderSurface surface, PlotArea area)
    {
        if (_depth is null)
        {
            Plot.Caption(surface, area, "No depth yet.");
            return;
        }

        Ladder.Draw(surface, _depth, LadderOptions.Default with { Levels = _levels }, area);
    }

    private void DrawStats(IRenderSurface surface) => DrawStats(surface, PlotArea.Of(surface));

    private void DrawStats(IRenderSurface surface, PlotArea area)
    {
        if (_flow is null || _vpin is null || _history.Count == 0)
        {
            Plot.Caption(surface, area, "Flow statistics appear once trades arrive.");
            return;
        }

        var last = _history[^1];
        var edge = last.Microprice - last.Mid;
        var sweep = _depth is null ? 0d : Book.SweepSlippage(_depth.Asks, _sweepSize, last.Microprice);

        // Every tile normalised or in price units the instrument itself supplies — nothing here is a
        // contract count that means something different on the next symbol.
        Tiles.Draw(
            surface,
            [
                Tile.Signed("Queue", last.Queue, last.Queue.ToString("F2"), $"{_levels} levels"),
                Tile.Signed("Flow", _flow.Value, _flow.Value.ToString("F2"), "signed / gross"),
                new Tile(
                    "Toxicity",
                    _vpin.IsReady ? _vpin.Value.ToString("F2") : "—",
                    _vpin.IsReady && _vpin.Value > 0.7d ? "one-sided" : "balanced",
                    _vpin.IsReady && _vpin.Value > 0.7d ? RenderThemeColor.Warning : RenderThemeColor.Text),
                Tile.Signed("Edge", edge, edge.ToString("F4"), "micro − mid"),
                new Tile(
                    "Sweep",
                    sweep > 0d ? sweep.ToString("F4") : "—",
                    sweep > 0d ? $"{_sweepSize:N0} lots" : "book too thin"),
                new Tile(
                    "Spread",
                    _spread.IsReady ? _spread.ZScore.ToString("F1") : "—",
                    _spread.IsWide() ? "unusually wide" : "normal",
                    _spread.IsWide() ? RenderThemeColor.Warning : RenderThemeColor.Text),
            ],
            area: area);
    }

    /// <summary>The last <paramref name="count"/> samples of one field, oldest first.</summary>
    private double[] Column(Func<Sample, double> select, int count)
    {
        var take = Math.Clamp(count, 0, _history.Count);
        var first = _history.Count - take;
        var values = new double[take];
        for (var index = 0; index < take; index++) values[index] = select(_history[first + index]);
        return values;
    }
}
