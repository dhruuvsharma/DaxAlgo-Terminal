namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// The single hostable type found in a compiled authoring submission, and which contract it implements.
///
/// <para>Authoring targets <c>IStrategyKernel</c> and <c>IVisualizer</c>. <c>IBacktestStrategy</c> is
/// still discovered, and is reported as <see cref="UsesRetiredContract"/>, because it is exactly what
/// its own documentation says it is: the <i>engine-facing</i> contract of the backtest engine that was
/// archived on 2026-08-17. It hands a strategy an <c>IOrderRouter</c> and describes "state transitions
/// produced by the simulated order book" — a direct route to orders that the virtual book replaced and
/// that the architecture now forbids.</para>
///
/// <para>It survives only because <c>TradingTerminal.Core</c> is a published contract package and
/// already-installed plugins implement it. Nothing newly authored should.</para>
/// </summary>
/// <param name="Kind">Whether the unit trades or only draws.</param>
/// <param name="Type">The class the host will instantiate.</param>
/// <param name="UsesRetiredContract">True when the type was found through <c>IBacktestStrategy</c>.</param>
public sealed record AuthoredUnit(AuthoringKind Kind, Type Type, bool UsesRetiredContract = false)
{
    /// <summary>What to call it in a message to the author.</summary>
    public string ContractName => UsesRetiredContract
        ? "IBacktestStrategy"
        : Kind == AuthoringKind.Visualizer ? "IVisualizer" : "IStrategyKernel";
}
