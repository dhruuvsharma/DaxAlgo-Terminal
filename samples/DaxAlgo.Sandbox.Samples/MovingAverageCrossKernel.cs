using DaxAlgo.Sdk;
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
