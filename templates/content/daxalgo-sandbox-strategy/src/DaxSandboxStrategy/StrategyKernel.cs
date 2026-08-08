using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace SandboxStrategy;

/// <summary>
/// Minimal example kernel: compare the two latest final closes and request either one reference
/// unit or a flat model position. Replace this math with the strategy's real deterministic logic.
/// </summary>
public sealed class StrategyKernel : IStrategyKernel
{
    public const string InstrumentParameter = "instrument";
    public const string TargetUnitsParameter = "targetUnits";

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter,
            "Instrument",
            InstrumentId.None,
            description: "Canonical instrument selected by the host."),
        StrategyParameter.Number(
            TargetUnitsParameter,
            "Long target",
            1d,
            min: 0d,
            max: 1000d,
            step: 1d,
            unit: "reference units"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

    public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var instrument = context.Parameters.GetInstrument(InstrumentParameter);
        if (!bar.IsFinal || instrument.IsNone || bar.InstrumentId != instrument)
            return Task.CompletedTask;

        var bars = context.Data.RecentBars(instrument, bar.Size, maxCount: 2);
        if (bars.Count < 2)
            return Task.CompletedTask;

        var target = bar.Close > bars[^2].Close
            ? context.Parameters.GetDouble(TargetUnitsParameter)
            : 0d;

        context.Book.SetTargetPosition(instrument, target);
        context.Alerts.Alert(
            target > 0d ? "Up close: model target is long." : "Down/flat close: model target is flat.",
            AlertLevel.Information,
            dedupeKey: $"close-direction-{bar.OpenTimeUtc.Ticks}");

        return Task.CompletedTask;
    }
}
