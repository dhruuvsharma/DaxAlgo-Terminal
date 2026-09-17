using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Reads the OpenCode CLI's <c>--format json</c> events into the same deltas every other provider emits.
///
/// <para><b>Parts, not deltas.</b> OpenCode streams a message as parts with stable ids, and rewrites a
/// part as it grows — the same <c>prt_…</c> arriving again with more text — so the delta is the suffix
/// that is new, and the reply is the parts concatenated in the order they first appeared. Treating each
/// event as a delta would repeat the whole answer once per update.</para>
///
/// <para>Thinking arrives as <c>reasoning</c> parts and is kept apart from the answer, as it is on every
/// other provider: it is what makes a long silence explainable, and it is not the reply.</para>
/// </summary>
internal sealed class OpenCodeEventAccumulator
{
    private readonly Dictionary<string, string> _parts = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary>The answer so far.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();
            foreach (var id in _order) text.Append(_parts[id]);
            return text.ToString();
        }
    }

    /// <summary>What the run has been billed, summed over its steps.</summary>
    public CodegenUsage Usage { get; private set; } = CodegenUsage.None;

    /// <summary>What the CLI said went wrong, or null.</summary>
    public string? Error { get; private set; }

    public IEnumerable<CodegenEvent> Consume(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var typeElement)) yield break;
        var type = typeElement.GetString();

        // An error can arrive as its own event or inside a part; both carry a message worth showing.
        if (type is "error" or "session_error")
        {
            Error = Message(message) ?? "the CLI reported an error with no message.";
            yield break;
        }

        if (!message.TryGetProperty("part", out var part)) yield break;

        switch (type)
        {
            case "text":
                if (Grow(part) is { Length: > 0 } written) yield return new CodegenEvent.TextDelta(written);
                break;

            case "reasoning":
                if (part.TryGetProperty("text", out var thought) && thought.GetString() is { Length: > 0 } thinking)
                    yield return new CodegenEvent.ReasoningDelta(thinking);
                break;

            case "step_finish":
                if (part.TryGetProperty("tokens", out var tokens))
                {
                    Usage = Usage.Add(new CodegenUsage(Int(tokens, "input"), Int(tokens, "output")));
                    yield return new CodegenEvent.UsageUpdate(Usage);
                }
                break;
        }
    }

    /// <summary>The text this part added since it was last seen, and remembers the new whole.</summary>
    private string Grow(JsonElement part)
    {
        if (!part.TryGetProperty("text", out var textElement) || textElement.GetString() is not { } text) return string.Empty;

        var id = part.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        if (id.Length == 0) id = "part" + _order.Count;

        if (!_parts.TryGetValue(id, out var seen))
        {
            _parts[id] = text;
            _order.Add(id);
            return text;
        }

        _parts[id] = text;

        // Rewritten rather than extended — a part the CLI revised. The reply is whole either way; only
        // the delta is unknowable, so nothing is emitted for it.
        return text.StartsWith(seen, StringComparison.Ordinal) ? text[seen.Length..] : string.Empty;
    }

    private static string? Message(JsonElement message)
    {
        if (message.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            if (error.TryGetProperty("message", out var inner)) return inner.GetString();
        }

        return message.TryGetProperty("message", out var plain) ? plain.GetString() : null;
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
