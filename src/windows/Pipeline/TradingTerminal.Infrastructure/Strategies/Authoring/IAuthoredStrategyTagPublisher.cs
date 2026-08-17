namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Optional bridge: Hyperion build tags → Strategies catalog presentation tags on Register.
/// Implemented by the shell (owns <c>StrategyPresentationStore</c>); null in hosts that skip catalog cards.
/// </summary>
public interface IAuthoredStrategyTagPublisher
{
    void PublishTags(string strategyId, IReadOnlyList<string> tags);
}
