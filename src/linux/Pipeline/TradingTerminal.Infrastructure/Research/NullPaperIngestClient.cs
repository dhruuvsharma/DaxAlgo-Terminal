using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// Default <see cref="IPaperIngestClient"/> when no sidecar is configured. Always reports unavailable
/// and returns an empty result, so the Paper Lab degrades gracefully (same shape as the Null AI
/// analyst client). Registered by default by <c>AddPaperResearch</c>.
/// </summary>
internal sealed class NullPaperIngestClient : IPaperIngestClient
{
    public bool IsAvailable => false;

    public Task<PaperIngestResult> ResolveAsync(string url, CancellationToken ct = default) =>
        Task.FromResult(PaperIngestResult.Empty("Paper ingestion sidecar is not configured."));
}
