using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// A single-instrument moving-average cross that emits only declarative model-book targets.
///
/// <para><b>This is the strategy exemplar Hyperion is shown.</b> The trading rule is deliberately the
/// simplest one there is, because the rule is not what it is teaching. What it is teaching is the
/// shape: the maths comes from <c>DaxAlgo.Sdk.Quant</c>, the picture comes from the widget library,
/// the window is declared as a <see cref="UnitLayout"/>, and the numbers behind the decision are on
/// screen beside it.</para>
///
/// <para><b>A strategy cannot read its own book.</b> <c>IVirtualBook</c> takes targets and returns
/// nothing — a kernel declares the position it wants and never learns what came of it, which is what
/// keeps a strategy a pure compute unit. So the statistics here are about the SIGNAL, not the P&amp;L:
/// the host draws the book row under the picture, and that is where equity and realised profit
/// belong.</para>
/// </summary>
public sealed class MovingAverageCrossKernel : IStrategyKernel
{
    public const string InstrumentParameter = "instrument";
    public const string FastPeriodParameter = "fastPeriod";
    public const string SlowPeriodParameter = "slowPeriod";
    public const string UseProtectiveStopParameter = "useProtectiveStop";
    public const string ProtectiveStopPercentParameter = "protectiveStopPercent";

    private bool? _fastAboveSlow;

    // Rebuilt per evaluation from the host's own bar history rather than accumulated across calls.
    // That is what makes the picture correct after a gap in the feed and after the periods are changed
    // at runtime — an estimator re-fed a stream it has already seen would double-count.
    private readonly Atr _atr = new(14);
    private int _barsSinceCross;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter,
            "Instrument",
            new InstrumentId(1),
            group: "Market"),
        StrategyParameter.Int(
            FastPeriodParameter,
            "Fast SMA period",
            10,
            min: 2,
            max: 100,
            group: "Signal",
            unit: "bars"),
        StrategyParameter.Int(
            SlowPeriodParameter,
            "Slow SMA period",
            30,
            min: 3,
            max: 300,
            group: "Signal",
            unit: "bars"),
        StrategyParameter.Bool(
            UseProtectiveStopParameter,
            "Use protective stop",
            true,
            group: "Risk"),
        StrategyParameter.Number(
            ProtectiveStopPercentParameter,
            "Protective stop distance",
            2d,
            min: 0.1d,
            max: 50d,
            step: 0.1d,
            group: "Risk",
            unit: "%"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

    /// <summary>
    /// The averages take the space; the numbers behind the decision take a strip under them.
    ///
    /// <para>A strategy is not obliged to declare a layout, and most do not need one. This one does
    /// because the exemplar has to show that it is possible — a generated unit that never saw a
    /// two-panel window will never build one.</para>
    /// </summary>
    public UnitLayout Layout => UnitLayout.Rows(
        UnitLayout.Panel("Moving average cross", DrawChart).Star(4),
        UnitLayout.Panel("Signal", DrawStats).Pixels(64));

    public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        ReadPeriods(context.Parameters);
        _fastAboveSlow = null;
        return Task.CompletedTask;
    }

    public Task OnBarAsync(
        OhlcvBar bar,
        IStrategyRuntimeContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var instrument = context.Parameters.GetInstrument(InstrumentParameter);
        if (bar.InstrumentId != instrument || bar.Size != BarSize.OneMinute || !bar.IsFinal)
            return Task.CompletedTask;

        var (fastPeriod, slowPeriod) = ReadPeriods(context.Parameters);
        var bars = context.Data.RecentBars(instrument, BarSize.OneMinute, slowPeriod);
        if (bars.Count < slowPeriod)
            return Task.CompletedTask;

        // Sma rather than a hand-written loop. Two lines instead of a helper, and the window edges,
        // the warm-up gate and the numerics are somebody else's problem — a tested one's.
        var fast = new Sma(fastPeriod);
        var slow = new Sma(slowPeriod);
        _atr.Reset();
        foreach (var seen in bars)
        {
            fast.Update(seen.Close);
            slow.Update(seen.Close);
            _atr.Update(seen);
        }

        var fastSma = fast.Value;
        var slowSma = slow.Value;
        _barsSinceCross++;

        // Keep the picture's data before the guards below return: the chart should show the averages
        // converging even on the bar where they are exactly equal and no decision is taken.
        var above = fastSma > slowSma;
        Record(new Sample(
            fastSma,
            slowSma,
            above,
            Crossed: fastSma != slowSma && _fastAboveSlow is { } prior && prior != above));

        if (_history.Count > 0 && _history[^1].Crossed) _barsSinceCross = 0;

        if (fastSma == slowSma)
            return Task.CompletedTask;

        var fastAboveSlow = fastSma > slowSma;
        if (_fastAboveSlow is { } previous && previous != fastAboveSlow)
        {
            double? protectiveStop = fastAboveSlow && context.Parameters.GetBool(UseProtectiveStopParameter)
                ? bar.Close * (1d - context.Parameters.GetDouble(ProtectiveStopPercentParameter) / 100d)
                : null;

            context.Book.SetTargetPosition(
                instrument,
                fastAboveSlow ? 1d : 0d,
                protectiveStopPrice: protectiveStop);

            var direction = fastAboveSlow ? "long" : "flat";
            context.Alerts.Alert(
                $"Fast SMA crossed the slow SMA; model target is now {direction}.",
                AlertLevel.Information,
                $"moving-average-cross:{direction}");
        }

        _fastAboveSlow = fastAboveSlow;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Draws the two averages and the crosses they produced.
    ///
    /// <para>A strategy does not have to draw — the default does nothing, and plenty of strategies are
    /// pure signal logic. When one does, the rules are the visualizer's rules: keep what the picture
    /// needs in the data callbacks, and read only that here. <see cref="Draw"/> gets a surface and
    /// nothing else, because it runs on the render thread while <see cref="OnBarAsync"/> runs on a pump
    /// thread that may fire far faster.</para>
    /// </summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var panel = surface.Panel("Moving average cross", RenderPanelKind.Chart);
        if (_history.Count == 0) { Plot.Waiting(surface, "Waiting for enough bars to average…"); return; }

        // The same two pictures the layout declares, divided with PlotArea instead of into real
        // panels. PlotArea divides a picture; UnitLayout divides a window.
        // (Taken, Remainder): the strip comes back FIRST. Reversed, the chart gets 58 pixels at the
        // bottom and the tiles get the whole panel.
        var (stats, chart) = PlotArea.Of(surface).SplitBottom(58d);
        DrawChart(surface, chart);
        DrawStats(surface, stats);
    }

    private void DrawChart(IRenderSurface surface) => DrawChart(surface, PlotArea.Of(surface));

    private void DrawChart(IRenderSurface surface, PlotArea area)
    {
        if (_history.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for enough bars to average…");
            return;
        }

        // Both averages on ONE scale, with the grid, axes, legend and crosshair that go with them. Drawn
        // separately they would each fill the panel and look like they never diverged.
        var range = Series.Chart(surface, [
            SeriesData.Line("Slow SMA", Column(static sample => sample.Slow), RenderThemeColor.Neutral),
            SeriesData.Line("Fast SMA", Column(static sample => sample.Fast), RenderThemeColor.Accent),
        ], area: area);

        // The crosses are the signal, so they are what the eye should land on. Signals draws shape as
        // well as colour, which is what makes them readable to the roughly one man in twelve who cannot
        // separate the bullish and bearish roles reliably.
        var marks = new List<Signal>();
        for (var index = 0; index < _history.Count; index++)
        {
            if (!_history[index].Crossed) continue;

            marks.Add(new Signal(
                index,
                _history[index].Fast,
                _history[index].FastAboveSlow ? SignalKind.Buy : SignalKind.Sell));
        }

        Signals.Draw(surface, marks, _history.Count, range, area: area);
        Plot.Crosshair(surface, range, area: area);
    }

    /// <summary>
    /// The numbers behind the decision.
    ///
    /// <para>The separation is normalised by ATR rather than shown in price, which is the lesson worth
    /// carrying out of here: "the averages are 0.42 apart" means nothing without knowing the
    /// instrument, and "0.8 ATR apart" means the same thing on every one of them.</para>
    /// </summary>
    private void DrawStats(IRenderSurface surface) => DrawStats(surface, PlotArea.Of(surface));

    private void DrawStats(IRenderSurface surface, PlotArea area)
    {
        if (_history.Count == 0)
        {
            Plot.Caption(surface, area, "The signal appears once the averages have enough bars.");
            return;
        }

        var last = _history[^1];
        var separation = last.Fast - last.Slow;
        var inAtr = _atr.IsReady ? Num.SafeDiv(separation, _atr.Value) : 0d;

        Tiles.Draw(
            surface,
            [
                new Tile(
                    "Stance",
                    last.FastAboveSlow ? "LONG" : "FLAT",
                    last.FastAboveSlow ? "fast above slow" : "fast below slow",
                    last.FastAboveSlow ? RenderThemeColor.Bullish : RenderThemeColor.Neutral),
                Tile.Signed("Separation", inAtr, inAtr.ToString("F2"), "ATR"),
                new Tile("ATR", _atr.IsReady ? _atr.Value.ToString("F4") : "—", "14 bars"),
                new Tile("Since cross", _barsSinceCross.ToString(), "bars"),
            ],
            area: area);
    }

    /// <summary>One field of the sample history as a plain column, which is what the widgets take.</summary>
    private double[] Column(Func<Sample, double> select)
    {
        var values = new double[_history.Count];
        for (var index = 0; index < _history.Count; index++) values[index] = select(_history[index]);
        return values;
    }

    /// <summary>Bounded, for the same reason the visualizer's is: a strategy runs for as long as its
    /// window is open.</summary>
    private const int HistoryCapacity = 240;

    private readonly List<Sample> _history = new(HistoryCapacity);

    private readonly record struct Sample(double Fast, double Slow, bool FastAboveSlow, bool Crossed);

    private void Record(Sample sample)
    {
        if (_history.Count == HistoryCapacity)
            _history.RemoveAt(0);
        _history.Add(sample);
    }

    private static (int Fast, int Slow) ReadPeriods(IParameters parameters)
    {
        var fast = parameters.GetInt(FastPeriodParameter);
        var slow = parameters.GetInt(SlowPeriodParameter);
        if (fast >= slow)
        {
            throw new InvalidOperationException(
                $"'{FastPeriodParameter}' must be smaller than '{SlowPeriodParameter}'.");
        }

        return (fast, slow);
    }
}
