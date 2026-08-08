using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>A small host-readable state snapshot produced by <see cref="SpreadBandVisualizer"/>.</summary>
public sealed record SpreadBandViewState(
    bool IsReady,
    DateTime UpdatedAtUtc,
    int SampleCount,
    double LastPrice,
    double Midpoint,
    double LowerBand,
    double UpperBand,
    bool IsOutsideBand)
{
    public static SpreadBandViewState Empty { get; } = new(
        false,
        DateTime.UnixEpoch,
        0,
        0d,
        0d,
        0d,
        0d,
        false);
}

/// <summary>
/// Computes a rolling close-price band and evaluates final bars and scoped quote midpoints against it.
/// </summary>
public sealed class SpreadBandVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string LookbackParameter = "lookback";
    public const string BandMultiplierParameter = "bandMultiplier";

    private InstrumentId _instrument;
    private int _lookback;
    private double _bandMultiplier;
    private bool _wasOutsideBand;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter,
            "Instrument",
            new InstrumentId(1),
            group: "Market"),
        StrategyParameter.Int(
            LookbackParameter,
            "Band lookback",
            20,
            min: 3,
            max: 300,
            group: "Band",
            unit: "bars"),
        StrategyParameter.Number(
            BandMultiplierParameter,
            "Band width",
            2d,
            min: 0.1d,
            max: 10d,
            step: 0.1d,
            group: "Band",
            unit: "sigma"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.Bars | StrategyDataRequirement.L1;

    public SpreadBandViewState ViewState { get; private set; } = SpreadBandViewState.Empty;

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _lookback = context.Parameters.GetInt(LookbackParameter);
        _bandMultiplier = context.Parameters.GetDouble(BandMultiplierParameter);
        _wasOutsideBand = false;
        ViewState = SpreadBandViewState.Empty;
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (bar.InstrumentId == _instrument && bar.Size == BarSize.OneMinute && bar.IsFinal)
            Evaluate(bar.Close, context);

        return Task.CompletedTask;
    }

    public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (quote.InstrumentId != _instrument)
            return Task.CompletedTask;

        var recentQuotes = context.Data.RecentQuotes(_instrument, 1);
        if (recentQuotes.Count > 0)
            Evaluate(recentQuotes[^1].Mid, context);

        return Task.CompletedTask;
    }

    private void Evaluate(double price, IVisualizerContext context)
    {
        var bars = context.Data.RecentBars(_instrument, BarSize.OneMinute, _lookback);
        if (bars.Count < _lookback)
        {
            ViewState = new SpreadBandViewState(
                false,
                context.Clock.UtcNow,
                bars.Count,
                price,
                0d,
                0d,
                0d,
                false);
            _wasOutsideBand = false;
            return;
        }

        var sum = 0d;
        for (var index = 0; index < bars.Count; index++)
            sum += bars[index].Close;
        var midpoint = sum / bars.Count;

        var squaredDifferenceSum = 0d;
        for (var index = 0; index < bars.Count; index++)
        {
            var difference = bars[index].Close - midpoint;
            squaredDifferenceSum += difference * difference;
        }

        var standardDeviation = Math.Sqrt(squaredDifferenceSum / bars.Count);
        var width = _bandMultiplier * standardDeviation;
        var lowerBand = midpoint - width;
        var upperBand = midpoint + width;
        var isOutsideBand = price < lowerBand || price > upperBand;

        ViewState = new SpreadBandViewState(
            true,
            context.Clock.UtcNow,
            bars.Count,
            price,
            midpoint,
            lowerBand,
            upperBand,
            isOutsideBand);

        if (isOutsideBand && !_wasOutsideBand)
        {
            var direction = price > upperBand ? "above" : "below";
            context.Alerts.Alert(
                $"Price moved {direction} the rolling band.",
                AlertLevel.Warning,
                $"spread-band:{direction}");
        }

        _wasOutsideBand = isOutsideBand;
    }
}
