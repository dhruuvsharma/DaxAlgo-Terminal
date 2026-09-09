using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Reference;

/// <summary>One thing found, or supplied.</summary>
/// <param name="Title">What it is called.</param>
/// <param name="Url">Where it came from, so the user can go and look.</param>
/// <param name="Snippet">Text about it, if any. <b>Untrusted:</b> written by whoever owns the page.</param>
/// <param name="Image">The picture, when one was fetched and accepted.</param>
public sealed record ReferenceCandidate(string Title, string Url, string? Snippet, CodegenImage? Image);

/// <summary>
/// Finds something to judge a generated picture against.
///
/// <para><b>A seam rather than a hard dependency</b>, because the search backend is a key the user has
/// to obtain, and a builder that could not run without one would be worse than a builder that reviews
/// against its own rubric. <see cref="NullReferenceSearch"/> is what an unconfigured install gets.</para>
/// </summary>
public interface IReferenceSearch
{
    /// <summary>False when nothing is configured, so a caller can ask the user instead.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Looks for references matching a brief.
    /// </summary>
    /// <param name="query">What the unit is meant to be — the brief, or the planner's own words for it.</param>
    /// <param name="wantImages">True for a picture bar, false when only text references are useful
    /// (a maths brief is better served by an article than by a screenshot).</param>
    /// <returns>What was found, newest-relevance first. Never throws: a failed search costs the bar,
    /// not the build.</returns>
    Task<IReadOnlyList<ReferenceCandidate>> FindAsync(
        string query, bool wantImages = true, CancellationToken ct = default);
}

/// <summary>
/// The search for an install with no key: it finds nothing, and says so.
///
/// <para>Returning empty rather than throwing is what lets the rest of the review work. The bar falls
/// back to the rubric the planner wrote, which is weaker but real.</para>
/// </summary>
public sealed class NullReferenceSearch : IReferenceSearch
{
    public static NullReferenceSearch Instance { get; } = new();

    public bool IsConfigured => false;

    public Task<IReadOnlyList<ReferenceCandidate>> FindAsync(
        string query, bool wantImages = true, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReferenceCandidate>>([]);
}
