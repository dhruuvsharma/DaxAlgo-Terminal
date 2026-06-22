namespace TradingTerminal.Core.Research;

/// <summary>
/// The outcome of resolving a paper URL: the resolved <see cref="Paper"/> and the candidate code
/// repositories discovered for it. <see cref="Resolved"/> is false (and the lists empty) when the
/// sidecar is unavailable, the URL couldn't be resolved, or no repo was found.
/// </summary>
public sealed record PaperIngestResult(
    bool Resolved,
    PaperRef? Paper,
    IReadOnlyList<RepoRef> Repos,
    string? Error)
{
    /// <summary>An empty/failed result carrying the reason.</summary>
    public static PaperIngestResult Empty(string reason) =>
        new(Resolved: false, Paper: null, Repos: Array.Empty<RepoRef>(), Error: reason);
}

/// <summary>
/// The Core-side seam to the paper-resolution sidecar. One implementation calls the Python sidecar
/// over HTTP (bound to <c>127.0.0.1</c> only); the Null implementation always returns an empty result
/// so the app builds/runs with no sidecar.
///
/// <para>Implementations MUST NOT throw across this boundary — no sidecar, a bad URL, or a missing
/// repo all fold into <see cref="PaperIngestResult.Empty"/>. Same never-throw contract as
/// <c>IAiAnalystClient</c>. Resolution/scraping logic lives in the sidecar, never the C# build.</para>
/// </summary>
public interface IPaperIngestClient
{
    /// <summary>True when this client expects the sidecar to be reachable (Http when enabled + URL
    /// set; Null returns false). The per-call timeout decides actual success.</summary>
    bool IsAvailable { get; }

    /// <summary>Resolve a paper URL to a <see cref="PaperRef"/> + candidate <see cref="RepoRef"/>s.
    /// Never throws — failures surface as <see cref="PaperIngestResult.Empty"/>.</summary>
    Task<PaperIngestResult> ResolveAsync(string url, CancellationToken ct = default);
}
