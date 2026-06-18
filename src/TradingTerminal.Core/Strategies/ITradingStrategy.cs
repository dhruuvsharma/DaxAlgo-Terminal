using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Plug-in metadata for a strategy. The shell uses this for the Strategies list;
/// the actual view + view-model pair is constructed via <see cref="IStrategyFactory"/>.
/// </summary>
public interface ITradingStrategy
{
    /// <summary>Stable, unique identifier (e.g. "example.nvda.3m"). Used to dedupe tabs.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>
    /// The market data this strategy consumes. Defaults to the universal baseline
    /// (<see cref="StrategyDataRequirement.L1"/> | <see cref="StrategyDataRequirement.Bars"/>);
    /// only strategies that additionally need <see cref="StrategyDataRequirement.Depth"/> or
    /// <see cref="StrategyDataRequirement.TradeTape"/> override this. The wiring gates the
    /// extras against the connected broker's data-capability.
    /// </summary>
    StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

    /// <summary>
    /// When this strategy is derived from a published research paper, the canonical URL of that
    /// paper (e.g. an arXiv abstract page). Drives the "Research paper" tag + info link rendered
    /// in the Strategies pane. Defaults to <c>null</c> for strategies that aren't paper-derived.
    /// </summary>
    string? ResearchPaperUrl => null;

    /// <summary>
    /// The asset classes this strategy is designed for, driving the asset-class pills in the
    /// Strategies pane. An <em>empty</em> list (the default) means asset-agnostic — the strategy's
    /// maths work on any instrument's price/flow regardless of class, rendered as an "ANY ASSET"
    /// pill. Strategies built around a specific class (e.g. an index volatility overlay) or that
    /// genuinely span several (an index composite over its equity constituents) list them here.
    /// </summary>
    IReadOnlyList<AssetClass> AssetClasses => Array.Empty<AssetClass>();

    /// <summary>
    /// Whether the strategy works on a single instrument at a time or aggregates many at once.
    /// Defaults to <see cref="StrategyAssetScope.SingleAsset"/>; multi-instrument monitors /
    /// composites (Index K-Score Surface, Order Flow Pressure Map, Index Regime Graph) override it.
    /// Rendered as the SINGLE-ASSET / MULTI-ASSET pill.
    /// </summary>
    StrategyAssetScope AssetScope => StrategyAssetScope.SingleAsset;

    /// <summary>
    /// The brokers that can fully drive this strategy. Defaults to the capability map for the
    /// strategy's <see cref="DataRequirement"/> (see <see cref="StrategyBrokerCapability"/>):
    /// an L1/Bars-only strategy returns the <em>empty</em> list (broker-agnostic — runs on any
    /// backend, rendered as an "ANY BROKER" pill), a tape-requiring strategy returns the
    /// tape-capable brokers, and a depth-requiring strategy the depth-capable brokers. Override
    /// only when a strategy is restricted beyond what its data appetite implies.
    /// </summary>
    IReadOnlyList<BrokerKind> SupportedBrokers => StrategyBrokerCapability.ForRequirement(DataRequirement);
}
