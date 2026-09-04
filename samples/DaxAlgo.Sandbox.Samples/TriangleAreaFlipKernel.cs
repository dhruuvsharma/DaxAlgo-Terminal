using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// Triangle-Area Flip — the curvature of the last three highs against the curvature of the last three
/// lows, larger side wins, always in the market.
///
/// <para><b>Recovered rather than generated.</b> The unit a live run produced was never persisted (the
/// agent path did not push its files into the editor, so the session banked the empty scaffold), but the
/// Interviewer's final specification survived in full. This is that specification implemented by hand,
/// the way <c>LiquidityBookVisualizer</c> is a hand-written control for the order-book brief: what the
/// contract can express, with no model involved.</para>
///
/// <para><b>The maths.</b> Three highs at x = 0, 1, 2 form a triangle whose shoelace area collapses to
/// <c>½·|h₁ − 2h₂ + h₃|</c> — half the absolute second difference, which is curvature. Both triangles
/// share the same x spacing, so the comparison is unaffected by it, and the units (price × bar index)
/// matter only for the comparison. Recomputed from the host's own bar history every bar rather than
/// accumulated, so it stays correct across a feed gap or a parameter change.</para>
/// </summary>
public sealed class TriangleAreaFlipKernel : IStrategyKernel
{
    public const string InstrumentParameter = "instrument";
    public const string TimeframeParameter = "timeframe";
    public const string PositionSizeParameter = "positionSize";

    /// <summary>The chart shows five candles; the triangles sit on the most recent three.</summary>
    private const int VisibleBars = 5;

    private const int TriangleBars = 3;
    private const int HistoryCapacity = 50;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        // Enum<BarSize>, NOT a string choice, and this is the part worth copying.
        //
        // SyntheticDrive.DeclaredBarSize reads the schema for a parameter whose DEFAULT IS A BarSize and
        // feeds that size; anything else and it falls back to one minute. Declared as "5m" in a string
        // choice, this unit filtered every bar the drive sent and did nothing at all — the ladder caught
        // it as 'declared positionSize but never read it', because the line that reads it was never
        // reached. A timeframe is a typed thing; spelling it as text hides it from the host.
        StrategyParameter.Enum(
            TimeframeParameter, "Timeframe", BarSize.OneMinute, group: "Signal"),
        StrategyParameter.Number(
            PositionSizeParameter, "Position size", 1d,
            min: 0.01d, max: 1_000_000d, step: 0.01d, group: "Risk", unit: "units"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

    /// <summary>
    /// The window the brief asked for: chart beside a readout, signal log beneath.
    ///
    /// <para>Spelled <c>UnitLayout.*</c> and never <c>Layout.*</c> — inside a class whose property is
    /// called <c>Layout</c> the identifier binds to the property, and the natural spelling stops
    /// compiling. That trap cost a live run its whole session.</para>
    /// </summary>
    public UnitLayout Layout => UnitLayout.Rows(
        UnitLayout.Columns(
            UnitLayout.Panel("Price", DrawChart).Star(3),
            UnitLayout.Panel("Signal", DrawInfo).Pixels(260)),
        UnitLayout.Panel("Signal history", DrawHistory).Pixels(170));

    public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _stance = Stance.Flat;
        _bars = [];
        _history.Clear();
        _highArea = _lowArea = 0d;
        _barsSinceFlip = 0;
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var instrument = context.Parameters.GetInstrument(InstrumentParameter);
        var size = context.Parameters.GetEnum<BarSize>(TimeframeParameter);

        // Completed bars of the chosen instrument and size only. Everything else — in-progress bars,
        // other sizes, other instruments — is somebody else's event.
        if (bar.InstrumentId != instrument || bar.Size != size || !bar.IsFinal)
            return Task.CompletedTask;

        // Re-read every bar, not inside the flip branch below. The specification says a parameter change
        // takes effect from the next completed candle, and rung 5 says the same thing from the other
        // side: a control the unit only reads on the rare path is a control that mostly does nothing.
        _size = context.Parameters.GetDouble(PositionSizeParameter);

        var recent = context.Data.RecentBars(instrument, size, VisibleBars);
        _bars = recent;
        _barsSinceFlip++;

        if (recent.Count < TriangleBars) return Task.CompletedTask;

        var a = recent[^3];
        var b = recent[^2];
        var c = recent[^1];

        // A bar carrying a non-finite or non-positive price produces no signal rather than a NaN that
        // would propagate into the picture and the book alike.
        if (!Sane(a) || !Sane(b) || !Sane(c)) return Task.CompletedTask;

        _highArea = Area(a.High, b.High, c.High);
        _lowArea = Area(a.Low, b.Low, c.Low);

        // An exact tie holds the current stance: with doubles it is vanishingly rare, and flipping on
        // it would be a coin toss dressed as a signal.
        if (_highArea == _lowArea) return Task.CompletedTask;

        var wanted = _highArea > _lowArea ? Stance.Long : Stance.Short;
        if (wanted == _stance) return Task.CompletedTask;   // same signal: hold, no write, no row

        var target = wanted == Stance.Long ? _size : -_size;

        context.Book.SetTargetPosition(instrument, target);

        var action = _stance == Stance.Flat
            ? (wanted == Stance.Long ? "entered long" : "entered short")
            : (wanted == Stance.Long ? "flipped to long" : "flipped to short");

        Record(new SignalRow(bar.OpenTimeUtc, wanted, _highArea, _lowArea, action));

        context.Alerts.Alert(
            $"{action}: high area {_highArea:F5} against low area {_lowArea:F5}.",
            AlertLevel.Information,
            $"triangle-area-flip:{wanted}");

        _stance = wanted;
        _barsSinceFlip = 0;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The single-surface fallback, for a host that does not use the layout — the authoring preview is
    /// one. It opens its own panel HERE rather than delegating to a wrapper, because a generated unit
    /// copies the method with the work in it and three in a row dropped the scope that way.
    /// </summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var panel = surface.Panel("Triangle-Area Flip", RenderPanelKind.Chart);

        var (history, top) = PlotArea.Of(surface).SplitBottom(120d);
        var (info, chart) = top.SplitRight(240d);

        DrawChart(surface, chart);
        DrawInfo(surface, info);
        DrawHistory(surface, history);
    }

    // ── The picture ─────────────────────────────────────────────────────────────────────────────

    private void DrawChart(IRenderSurface surface) => DrawChart(surface, PlotArea.Of(surface));

    private void DrawChart(IRenderSurface surface, PlotArea area)
    {
        var bars = _bars;
        if (bars.Count < TriangleBars)
        {
            Plot.Waiting(surface, "Waiting for three completed candles…");
            return;
        }

        // The candles own the scale, and hand it back so the triangles land exactly on the wicks.
        var range = Candles.Draw(surface, bars, area: area);
        if (!range.IsValid) return;

        // The triangles sit on the LAST three candles, at the same x positions the candles occupy.
        var first = bars.Count - TriangleBars;
        var xs = new double[TriangleBars];
        for (var i = 0; i < TriangleBars; i++) xs[i] = area.ToX(first + i, bars.Count);

        Triangle(surface, area, range, xs,
            [bars[^3].High, bars[^2].High, bars[^1].High], RenderThemeColor.Bullish);

        Triangle(surface, area, range, xs,
            [bars[^3].Low, bars[^2].Low, bars[^1].Low], RenderThemeColor.Bearish);

        // Where the stance changed, on any bar still visible.
        var marks = new List<Signal>();
        for (var index = 0; index < bars.Count; index++)
        {
            var at = bars[index].OpenTimeUtc;
            foreach (var row in _history)
            {
                if (row.At != at) continue;
                marks.Add(new Signal(
                    index,
                    row.Signal == Stance.Long ? bars[index].Low : bars[index].High,
                    row.Signal == Stance.Long ? SignalKind.Buy : SignalKind.Sell));
            }
        }

        if (marks.Count > 0) Signals.Draw(surface, marks, bars.Count, range, area: area);

        // What am I pointing at — a hover readout, not a click.
        Plot.Crosshair(surface, range, area: area);
    }

    /// <summary>Three closed segments through the three vertices. Lines only, no fill.</summary>
    private static void Triangle(
        IRenderSurface surface,
        PlotArea area,
        PlotRange range,
        IReadOnlyList<double> xs,
        IReadOnlyList<double> prices,
        RenderThemeColor role)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(role), Thickness: 1.6d));

        for (var i = 0; i < TriangleBars; i++)
        {
            var j = (i + 1) % TriangleBars;
            surface.Line(
                xs[i], area.ToY(prices[i], range),
                xs[j], area.ToY(prices[j], range));
        }
    }

    private void DrawInfo(IRenderSurface surface) => DrawInfo(surface, PlotArea.Of(surface));

    private void DrawInfo(IRenderSurface surface, PlotArea area)
    {
        if (_bars.Count < TriangleBars)
        {
            Plot.Caption(surface, area, "The readout appears once three candles have closed.");
            return;
        }

        var (meter, tiles) = area.SplitBottom(38d);

        var dominant = _highArea > _lowArea ? "Highs" : _highArea < _lowArea ? "Lows" : "Level";

        Tiles.Draw(
            surface,
            [
                new Tile(
                    "Stance",
                    _stance switch { Stance.Long => "LONG", Stance.Short => "SHORT", _ => "FLAT" },
                    dominant + " dominant",
                    _stance switch
                    {
                        Stance.Long => RenderThemeColor.Bullish,
                        Stance.Short => RenderThemeColor.Bearish,
                        _ => RenderThemeColor.Neutral,
                    }),
                new Tile("High area", _highArea.ToString("F5"), "curvature of highs", RenderThemeColor.Bullish),
                new Tile("Low area", _lowArea.ToString("F5"), "curvature of lows", RenderThemeColor.Bearish),
                new Tile("Since flip", _barsSinceFlip.ToString(), "bars"),
            ],
            area: tiles);

        // Which triangle is winning, at a glance. Centred at 0.5: equal areas sit in the middle.
        var total = _highArea + _lowArea;
        Gauge.Draw(
            surface,
            total > 0d ? Num.SafeDiv(_highArea, total) : 0.5d,
            GaugeOptions.Ratio("Highs vs lows"),
            area: meter);
    }

    private void DrawHistory(IRenderSurface surface) => DrawHistory(surface, PlotArea.Of(surface));

    private void DrawHistory(IRenderSurface surface, PlotArea area)
    {
        if (_history.Count == 0)
        {
            // A kernel cannot read its own book's fills, so this is the SIGNAL log — the decisions the
            // strategy made and the numbers behind them, which is what "trade history" can honestly
            // mean from inside a write-only book. The empty state says so.
            Plot.Caption(surface, area, "No signals yet — the log fills as the triangles flip.");
            return;
        }

        // Newest first.
        var rows = new List<IReadOnlyList<string>>(_history.Count);
        var tones = new List<RenderThemeColor>(_history.Count);
        for (var index = _history.Count - 1; index >= 0; index--)
        {
            var row = _history[index];
            rows.Add([
                row.At.ToString("HH:mm:ss"),
                row.Signal == Stance.Long ? "BUY" : "SELL",
                row.HighArea.ToString("F5"),
                row.LowArea.ToString("F5"),
                row.Action,
            ]);
            tones.Add(row.Signal == Stance.Long ? RenderThemeColor.Bullish : RenderThemeColor.Bearish);
        }

        Table.Draw(
            surface,
            [
                new TableColumn("Time (UTC)", 1.1d),
                new TableColumn("Signal", 0.7d),
                TableColumn.Number("High area", 1d),
                TableColumn.Number("Low area", 1d),
                new TableColumn("Action", 1.4d),
            ],
            rows,
            area: area,
            toneOf: index => tones[index]);
    }

    // ── State ───────────────────────────────────────────────────────────────────────────────────

    private enum Stance { Flat, Long, Short }

    private readonly record struct SignalRow(
        DateTime At, Stance Signal, double HighArea, double LowArea, string Action);

    private Stance _stance = Stance.Flat;
    private IReadOnlyList<OhlcvBar> _bars = [];
    private double _highArea;
    private double _lowArea;
    private int _barsSinceFlip;
    private double _size = 1d;

    /// <summary>Bounded, because a strategy runs for as long as its window is open.</summary>
    private readonly List<SignalRow> _history = new(HistoryCapacity);

    private void Record(SignalRow row)
    {
        if (_history.Count == HistoryCapacity) _history.RemoveAt(0);
        _history.Add(row);
    }

    /// <summary>Half the absolute second difference — the shoelace area over evenly spaced vertices.</summary>
    private static double Area(double first, double middle, double last) =>
        Math.Abs(first - (2d * middle) + last) / 2d;

    private static bool Sane(OhlcvBar bar) =>
        double.IsFinite(bar.High) && double.IsFinite(bar.Low) && bar.High > 0d && bar.Low > 0d;

}
