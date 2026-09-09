using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Reference;

/// <summary>
/// Assembles the standard the critics judge against.
///
/// <para><b>What the user gave us always wins.</b> An attached screenshot is not one opinion among ten
/// search results — it is the thing they actually want, stated in the only unambiguous way available.
/// So a supplied reference is used alone, and nothing is searched for.</para>
///
/// <para>Only a brief that named no reference triggers a search, and only when a key is configured.
/// Failing that, the bar is the rubric the planner wrote from the brief: weaker, but written before
/// there was anything to be defensive about, which is the property that matters.</para>
/// </summary>
public sealed class ReferenceBarBuilder(IReferenceSearch search, ILogger? logger = null)
{
    private readonly IReferenceSearch _search = search ?? throw new ArgumentNullException(nameof(search));

    /// <summary>True when a search backend is configured, so a caller can decide whether asking the
    /// user for a reference is the only way to get one.</summary>
    public bool CanSearch => _search.IsConfigured;

    /// <summary>
    /// Builds the bar for one build.
    /// </summary>
    /// <param name="brief">What the user asked for, used as the search query when nothing was
    /// attached.</param>
    /// <param name="rubric">The planner's own success criteria. Always included: it is the only part of
    /// the bar written specifically for THIS brief.</param>
    /// <param name="supplied">Images the user attached, and links they pasted.</param>
    /// <param name="wantImages">False for a brief where an article beats a screenshot — a mean-reversion
    /// estimator is not judged by looking at pictures of one.</param>
    public async Task<ReferenceBar> BuildAsync(
        string brief,
        IReadOnlyList<string>? rubric = null,
        IReadOnlyList<CodegenImage>? supplied = null,
        bool wantImages = true,
        CancellationToken ct = default)
    {
        var lines = rubric ?? [];

        if (supplied is { Count: > 0 })
            return new ReferenceBar(ReferenceOrigin.Supplied, lines, supplied, []);

        if (!_search.IsConfigured || string.IsNullOrWhiteSpace(brief))
            return ReferenceBar.FromRubric(lines);

        var found = await _search.FindAsync(brief, wantImages, ct).ConfigureAwait(false);
        if (found.Count == 0)
        {
            logger?.LogInformation("No references found; the critics will judge against the rubric.");
            return ReferenceBar.FromRubric(lines);
        }

        var images = found.Select(f => f.Image).OfType<CodegenImage>().ToArray();

        // Titles and snippets, attributed to their source. The URL is carried so a user reading the
        // review can go and look at what their unit was measured against — a bar nobody can inspect is
        // indistinguishable from a critic's opinion.
        var notes = found
            .Select(f => string.IsNullOrWhiteSpace(f.Snippet) ? $"{f.Title} — {f.Url}" : $"{f.Title} — {f.Snippet} ({f.Url})")
            .ToArray();

        return new ReferenceBar(ReferenceOrigin.Searched, lines, images, notes);
    }
}
