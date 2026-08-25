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

        if (_history.Count == 0)
        {
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary)));
            surface.Text(8d, 20d, "Waiting for enough bars to average…");
            return;
        }

        var range = PlotRange.Empty;
        for (var index = 0; index < _history.Count; index++)
            range = range.Include(_history[index].Fast).Include(_history[index].Slow);
        range = range.Padded();

        Plot.HorizontalGrid(surface, range);
        surface.AxisX(0d, Math.Max(1, _history.Count - 1));

        Average("Slow SMA", RenderThemeColor.Neutral, sample => sample.Slow);
        Average("Fast SMA", RenderThemeColor.Accent, sample => sample.Fast);

        // The crosses are the signal, so they are what the eye should land on. Bullish and bearish are
        // theme roles, which is how the same picture stays right in a light theme and a dark one.
        for (var index = 0; index < _history.Count; index++)
        {
            if (!_history[index].Crossed) continue;

            // Shape AND colour, never colour alone: roughly one man in twelve cannot separate the
            // bullish and bearish roles reliably, and the cross is the one thing on this chart that
            // has to read at a glance.
            var up = _history[index].FastAboveSlow;
            surface.SetStyle(new RenderStyle(
                surface.Theme(up ? RenderThemeColor.Bullish : RenderThemeColor.Bearish)));
            surface.Marker(
                index,
                _history[index].Fast,
                up ? RenderMarkerShape.Triangle : RenderMarkerShape.Diamond);
        }

        void Average(string name, RenderThemeColor color, Func<Sample, double> select)
        {
            surface.SetStyle(new RenderStyle(surface.Theme(color)));
            using var series = surface.Series(name, RenderSeriesKind.Line);
            for (var index = 0; index < _history.Count; index++)
                surface.Push(index, select(_history[index]));
        }
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
