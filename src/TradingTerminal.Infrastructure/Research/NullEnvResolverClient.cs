using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// Default <see cref="IEnvResolverClient"/> when no sidecar is configured. Always reports unavailable
/// and returns an empty plan, so the Paper Lab degrades gracefully (same shape as the Null paper-ingest
/// client). Registered by default by <c>AddPaperResearch</c>.
/// </summary>
internal sealed class NullEnvResolverClient : IEnvResolverClient
{
    public bool IsAvailable => false;

    public Task<MinimalReproPlan> ResolvePlanAsync(RepoRef repo, CancellationToken ct = default) =>
        Task.FromResult(MinimalReproPlan.Empty("Environment-resolution sidecar is not configured."));
}
