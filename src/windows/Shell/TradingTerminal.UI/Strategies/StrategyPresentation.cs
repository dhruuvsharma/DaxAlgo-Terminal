using System.Collections.Generic;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// User-authored presentation overrides for a strategy's catalog card — how it is NAMED and DESCRIBED,
/// plus optional free-text tags, an alpha-generating formula, and a path to a UI screenshot. Any
/// blank/absent field falls back to the strategy's own compiled metadata (and the app logo for the
/// image). Persisted by <see cref="StrategyPresentationStore"/>, keyed by strategy id.
/// </summary>
public sealed record StrategyPresentation(
    string? Name = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? Formula = null,
    string? ImagePath = null)
{
    public static readonly StrategyPresentation Empty = new();
}
