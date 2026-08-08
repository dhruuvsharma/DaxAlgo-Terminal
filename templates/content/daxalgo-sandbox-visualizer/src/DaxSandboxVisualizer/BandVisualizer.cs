using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace SandboxVisualizer;

public sealed record BandViewState(
    double Center,
    double Lower,
    double Upper,
    double LastPrice,
    bool IsOutside);

/// <summary>
/// Minimal visualizer: maintain a percentage band around recent final closes and alert when the
/// latest close is outside it. The host owns presentation; this type owns only computed view-state.
/// </summary>
public sealed class BandVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string LookbackParameter = "lookback";
    public const string BandPercentParameter = "bandPercent";

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter,
            "Instrument",
            InstrumentId.None,
            description: "Canonical instrument selected by the host."),
        StrategyParameter.Int(
            LookbackParameter,
            "Look-back",
            10,
            min: 2,
            max: 500,
            unit: "bars"),
        StrategyParameter.Number(
            BandPercentParameter,
            "Band width",
            1d,
            min: 0.01d,
            max: 100d,
            step: 0.1d,
            unit: "%"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

    public BandViewState? ViewState { get; private set; }

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ViewState = null;
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var instrument = context.Parameters.GetInstrument(InstrumentParameter);
        if (!bar.IsFinal || instrument.IsNone || bar.InstrumentId != instrument)
            return Task.CompletedTask;

        var lookback = context.Parameters.GetInt(LookbackParameter);
        var bars = context.Data.RecentBars(instrument, bar.Size, lookback);
        if (bars.Count < lookback)
            return Task.CompletedTask;

        var sum = 0d;
        foreach (var item in bars)
            sum += item.Close;

        var center = sum / bars.Count;
        var halfWidth = Math.Abs(center) * context.Parameters.GetDouble(BandPercentParameter) / 100d;
        var lower = center - halfWidth;
        var upper = center + halfWidth;
        var outside = bar.Close < lower || bar.Close > upper;

        ViewState = new BandViewState(center, lower, upper, bar.Close, outside);
        context.Alerts.AlertIf(
            outside,
            $"Price {bar.Close:F4} is outside the visualizer band.",
            AlertLevel.Warning,
            dedupeKey: $"band-exit-{bar.OpenTimeUtc.Ticks}");

        return Task.CompletedTask;
    }
}
