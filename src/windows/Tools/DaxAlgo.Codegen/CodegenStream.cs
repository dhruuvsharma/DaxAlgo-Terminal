using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// One generation, streamed and drained — the only way anything in this assembly asks a model for
/// something.
///
/// <para><b>Streaming is not an optimisation here, it is the difference between a build and a hang.</b>
/// The committee this codebase used to run called the blocking entry point instead: one silent HTTP
/// request per agent turn, no text, no thinking, no token movement, and nothing to look at but a status
/// line. A user watching that has no way to tell a working run from a dead one, and the measured
/// outcome was that they stopped believing the builder worked at all.</para>
///
/// <para>It is a shared helper rather than a method on each caller because this area's recurring defect
/// is two paths to one state with only one of them finished — the questions block parsed on one path
/// and not the other, the prose guard applied on one path and not the other. A second hand-rolled drain
/// would be the next one: a provider event added tomorrow would reach the session and not the swarm.</para>
/// </summary>
public static class CodegenStream
{
    /// <summary>
    /// Runs one request to completion, forwarding every event as it arrives.
    /// </summary>
    /// <param name="client">The provider. A client that cannot stream yields one
    /// <see cref="CodegenEvent.Completed"/> and nothing else, so there is no non-streaming branch to
    /// keep in step.</param>
    /// <param name="request">What to ask.</param>
    /// <param name="events">Where deltas, reasoning and usage go, or null to forward nothing.</param>
    /// <returns>The response, and the usage the provider reported for this generation — which is not
    /// always on the response, because some providers report it only as a streamed event.</returns>
    public static async Task<(StrategyCodegenResponse Response, CodegenUsage Reported)> DrainAsync(
        IStrategyCodegenClient client,
        StrategyCodegenRequest request,
        IProgress<CodegenEvent>? events = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        StrategyCodegenResponse? response = null;
        var streamed = CodegenUsage.None;

        await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case CodegenEvent.TextDelta:
                    events?.Report(evt);
                    break;

                // Forwarded — and this switch is precisely why that has to be written down. It
                // enumerates the event types it passes on, so a NEW one is dropped in silence rather
                // than failing anywhere a compiler or a test would notice. That is what happened to the
                // model's thinking: the client emitted it, the workspace had a panel waiting for it,
                // and this case did not exist, so a reasoning model still showed the user nothing at
                // all for minutes at a time.
                case CodegenEvent.ReasoningDelta:
                    events?.Report(evt);
                    break;

                case CodegenEvent.UsageUpdate update:
                    // Absolute for THIS generation — replaced rather than accumulated, so a retry does
                    // not double-count the generations before it.
                    streamed = update.Usage;
                    events?.Report(evt);
                    break;

                case CodegenEvent.Completed completed:
                    response = completed.Response;
                    break;
            }
        }

        response ??= StrategyCodegenResponse.Fail($"{client.DisplayName} returned nothing.");
        return (response, response.Usage ?? streamed);
    }
}
