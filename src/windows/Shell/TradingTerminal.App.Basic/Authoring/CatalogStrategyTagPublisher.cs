using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.App.Authoring;

/// <summary>Writes Hyperion <c>BuildTags</c> into the Strategies catalog presentation map on Register.</summary>
internal sealed class CatalogStrategyTagPublisher : IAuthoredStrategyTagPublisher
{
    public void PublishTags(string strategyId, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) return;
        var id = strategyId.Trim();
        var cleaned = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = StrategyPresentationStore.Get(id);
        StrategyPresentationStore.Save(id, current with
        {
            Tags = cleaned.Count == 0 ? null : cleaned,
        });
    }
}
