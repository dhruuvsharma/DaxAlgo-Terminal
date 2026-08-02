using System.Collections.Generic;

namespace TradingTerminal.UI.Strategies;

public sealed record VisualizerDescriptor(
    string Id,
    string DisplayName,
    string Description,
    string? ImagePath = null,
    IReadOnlyList<string>? DataRequirementTags = null);
