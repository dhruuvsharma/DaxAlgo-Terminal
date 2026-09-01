using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Accumulates Anthropic streaming events into text + usage. Two transports carry exactly these events:
/// the API's SSE stream (<c>data: {…}</c> lines), and Claude Code's <c>--output-format stream-json</c>,
/// which wraps each one as <c>{"type":"stream_event","event":{…}}</c>. One parser serves both — the CLI's
/// events are not a lookalike, they are the same objects.
/// </summary>
internal sealed class AnthropicEventAccumulator
{
    private readonly StringBuilder _text = new();
    private int _input;
    private int _output;
    private int _cached;

    /// <summary>Everything the model has written so far.</summary>
    public string Text => _text.ToString();

    public CodegenUsage Usage => new(_input, _output, _cached);

    /// <summary>
    /// Folds one event in and returns what the UI should hear about it. Unknown event types are ignored
    /// — the wire format grows, and an unrecognized event must never break a generation.
    /// </summary>
    public IEnumerable<CodegenEvent> Consume(JsonElement evt)
    {
        if (!evt.TryGetProperty("type", out var typeElement)) yield break;

        switch (typeElement.GetString())
        {
            case "message_start":
                // Prompt tokens, including what the cache served. Reporting only `input_tokens` would
                // show a ~2-token prompt for a 15k-token context pack, which is worse than useless.
                if (evt.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("usage", out var startUsage))
                {
                    _cached = Int(startUsage, "cache_read_input_tokens");
                    _input = Int(startUsage, "input_tokens")
                           + Int(startUsage, "cache_creation_input_tokens")
                           + _cached;
                    yield return new CodegenEvent.UsageUpdate(Usage);
                }
                break;

            case "content_block_delta":
                if (evt.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("type", out var deltaType) &&
                    deltaType.GetString() == "text_delta" &&
                    delta.TryGetProperty("text", out var text) &&
                    text.GetString() is { Length: > 0 } fragment)
                {
                    _text.Append(fragment);
                    yield return new CodegenEvent.TextDelta(fragment);
                }
                break;

            case "message_delta":
                if (evt.TryGetProperty("usage", out var endUsage))
                {
                    _output = Int(endUsage, "output_tokens");
                    yield return new CodegenEvent.UsageUpdate(Usage);
                }
                break;
        }
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}

/// <summary>
/// Reads <c>text/event-stream</c> lines into their <c>data:</c> payloads. Stops at <c>[DONE]</c> (the
/// OpenAI terminator; Anthropic just ends the stream).
///
/// <para><b>This is where a generation's time is actually spent</b>, and until 2026-09-01 it was
/// neither cancellable nor bounded. The loop was <c>while (!reader.EndOfStream)</c>, and
/// <see cref="StreamReader.EndOfStream"/> is a synchronous, uncancellable read: on a quiet socket it
/// parks the calling thread and ignores the token entirely. So the Stop button did not stop a stalled
/// provider — pinned by <c>StalledStreamTests</c>, which fails against the old loop.</para>
///
/// <para><c>HttpClient.Timeout</c> does not help either. The clients send with
/// <c>ResponseHeadersRead</c>, so it covers the header phase and nothing after it. That is why the
/// bound here is an <b>idle</b> timeout rather than a total one: a reasoning model legitimately emits
/// nothing for minutes — 278 seconds before the first byte, measured — so a total wall clock would
/// abandon exactly the generations worth waiting for, while silence is a real signal that a provider
/// has stopped answering.</para>
/// </summary>
internal static class ServerSentEvents
{
    /// <param name="idleTimeout">How long to wait for the NEXT line before giving up. The clock resets
    /// on every line, keep-alives included, so a slow answer is fine and a silent one is not.
    /// <see cref="Timeout.InfiniteTimeSpan"/> or a non-positive value waits forever, which is what a
    /// configured timeout of zero means: the user's Stop button is the control.</param>
    /// <exception cref="TimeoutException">The provider sent nothing for <paramref name="idleTimeout"/>.
    /// Thrown rather than ended quietly, because a truncated stream returned as a normal end looks to
    /// every caller like a model that chose to stop early.</exception>
    public static async IAsyncEnumerable<JsonElement> ReadAsync(
        Stream stream,
        TimeSpan idleTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);
        var bounded = idleTimeout > TimeSpan.Zero && idleTimeout != Timeout.InfiniteTimeSpan;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (bounded) idle.CancelAfter(idleTimeout);

                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Ours, not the caller's: the linked source fired on silence. Distinguished on the
                    // caller's token rather than on the exception, because both arrive as the same type.
                    throw new TimeoutException(
                        $"The provider sent nothing for {idleTimeout.TotalSeconds:0}s.");
                }
            }

            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            JsonElement element;
            try
            {
                element = JsonDocument.Parse(payload).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // a malformed frame is not worth killing a running generation over
            }

            yield return element;
        }
    }
}
