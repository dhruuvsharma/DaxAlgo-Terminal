using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// A single-instrument moving-average cross that emits only declarative model-book targets.
/// </summary>
public sealed class MovingAverageCrossKernel : IStrategyKernel
{
    public const string InstrumentParameter = "instrument";
    public const string FastPeriodParameter = "fastPeriod";
    public const string SlowPeriodParameter = "slowPeriod";
    public const string UseProtectiveStopParameter = "useProtectiveStop";
    public const string ProtectiveStopPercentParameter = "protectiveStopPercent";

    private bool? _fastAboveSlow;

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

        var fastSma = AverageClose(bars, bars.Count - fastPeriod);
        var slowSma = AverageClose(bars, 0);

        // Keep the picture's data before the guards below return: the chart should show the averages
        // converging even on the bar where they are exactly equal and no decision is taken.
        var above = fastSma > slowSma;
        Record(new Sample(
            fastSma,
            slowSma,
            above,
            Crossed: fastSma != slowSma && _fastAboveSlow is { } prior && prior != above));

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

        // Both averages on ONE scale, with the grid, axes, legend and crosshair that go with them. Drawn
        // separately they would each fill the panel and look like they never diverged.
        var range = Series.Chart(surface, [
            SeriesData.Line("Slow SMA", Column(static sample => sample.Slow), RenderThemeColor.Neutral),
            SeriesData.Line("Fast SMA", Column(static sample => sample.Fast), RenderThemeColor.Accent),
        ]);

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

        Signals.Draw(surface, marks, _history.Count, range);
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

    private static double AverageClose(IReadOnlyList<OhlcvBar> bars, int startIndex)
    {
        var total = 0d;
        for (var index = startIndex; index < bars.Count; index++)
            total += bars[index].Close;

        return total / (bars.Count - startIndex);
    }
}
