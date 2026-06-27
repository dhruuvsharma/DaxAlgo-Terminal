using TradingTerminal.Core.AiAnalyst;

namespace TradingTerminal.Infrastructure.AiAnalyst;

/// <summary>
/// Stand-in registered when <c>AiAnalystOptions.Enabled</c> is false (no Python sidecar
/// configured). Every <see cref="RunAsync"/> resolves immediately with the "unavailable"
/// sentinel so the UI's empty state renders cleanly and the enricher passes through.
/// </summary>
internal sealed class NullAiAnalystClient : IAiAnalystClient
{
    public bool IsAvailable => false;

    public Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default) =>
        Task.FromResult(AnalystReport.Unavailable(
            "AI Analyst unavailable — Python sidecar not configured. Enable in Settings → Notifications → AI Analyst."));
}
