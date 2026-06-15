using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.FilteredOrderFlow;

/// <summary>
/// Plug-in metadata for the Filtered Order-Flow Imbalance strategy (the Strategies-pane entry).
/// The engine-side signal logic is
/// <see cref="TradingTerminal.Infrastructure.Backtest.Strategies.FilteredOrderFlowStrategy"/>.
/// </summary>
public sealed class FilteredOrderFlowStrategy : ITradingStrategy
{
    public string Id => "filtered.orderflow.imbalance";
    public string DisplayName => "Filtered Order-Flow Imbalance";

    public string Description =>
        "Trade-based order-book imbalance OBI(T) — net signed-trade count over a rolling window — " +
        "regime-classified on a 9-bin grid, with a directional signal on strong same-sign regimes. " +
        "Tracks filtered vs unfiltered OBI(T) so you can see whether filtering fleeting flow sharpens " +
        "the signal (the paper's central finding). Lifecycle filters are approximated at the tape level " +
        "by a genuine-intent (min-size) filter since broker feeds lack per-order lifecycles. Signals only.";

    /// <summary>Tape-primary: needs the signed trade tape (IB / Simulated), with L1 for tick-rule
    /// signing and bars for the price chart.</summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;

    /// <summary>Anantha, Jain &amp; Maiti (2025), "Order-Flow Filtration and Directional Association
    /// with Short-Horizon Returns" — drives the Research-paper tag + info link in the Strategies pane.</summary>
    public string? ResearchPaperUrl => "https://arxiv.org/abs/2507.22712";
}
