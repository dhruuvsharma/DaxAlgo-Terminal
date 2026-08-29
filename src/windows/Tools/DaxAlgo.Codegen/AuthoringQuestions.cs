using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>How many answers one question takes.</summary>
public enum AuthoringAnswerMode
{
    /// <summary>Exactly one option.</summary>
    Single,

    /// <summary>Any number of options, including none.</summary>
    Multiple,
}

/// <summary>One option offered for a question.</summary>
/// <param name="Label">What the button says. Short — it is a chip, not a paragraph.</param>
/// <param name="Detail">One line under the label explaining the consequence, or empty.</param>
public readonly record struct AuthoringOption(string Label, string Detail = "");

/// <summary>
/// One question the model asked, with the answers it will accept.
/// </summary>
/// <param name="Id">Stable key, used when composing the reply so the model can match answer to question.</param>
/// <param name="Prompt">The question itself.</param>
/// <param name="Mode">Whether one option or several may be chosen.</param>
/// <param name="Options">The offered answers, in the model's order.</param>
/// <param name="AllowOther">
/// Whether a free-text box is offered alongside. True by default, and deliberately: a fixed list that
/// happens not to contain what the user wants turns a helpful prompt into a dead end.
/// </param>
public sealed record AuthoringQuestion(
    string Id,
    string Prompt,
    AuthoringAnswerMode Mode,
    IReadOnlyList<AuthoringOption> Options,
    bool AllowOther = true);

/// <summary>
/// Parses the structured questions a model may emit instead of prose.
///
/// <para><b>Why structured at all.</b> The builder already supported clarifying questions, but they
/// arrived as a paragraph and the user answered by typing. That is slow, it invites answers the model
/// then has to interpret, and it hides the fact that most of these questions have three or four
/// sensible answers the model already has in mind. Offering them as options is faster to answer and
/// removes a whole class of misread reply.</para>
///
/// <para><b>Prose remains valid.</b> A model that asks in a paragraph, or emits a malformed block, is
/// not broken — it gets the old behaviour. Everything here degrades to "show the text and let them
/// type", because a question the user cannot answer at all would be far worse than an unstyled one.</para>
/// </summary>
public static class AuthoringQuestions
{
    /// <summary>The most questions one turn may ask. The context pack already says two to four; this is
    /// the ceiling that keeps a runaway answer from filling the pane.</summary>
    public const int MaximumQuestions = 6;

    /// <summary>The most options one question may offer, past which a list stops being scannable.</summary>
    public const int MaximumOptions = 8;

    private static readonly Regex Block = new(
        @"```questions\s*\n(?<body>.*?)\n?```",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Pulls the questions out of a reply, or returns empty when there are none.
    ///
    /// <para><b>Never throws.</b> This runs on a model's output, which is the least trustworthy input
    /// in the system; a malformed block costs the buttons, not the turn.</para>
    /// </summary>
    public static IReadOnlyList<AuthoringQuestion> Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return [];

        var match = Block.Match(reply);
        if (!match.Success) return [];

        try
        {
            var wire = JsonSerializer.Deserialize<WireQuestion[]>(match.Groups["body"].Value, Json);
            if (wire is null) return [];

            var parsed = new List<AuthoringQuestion>();
            foreach (var item in wire)
            {
                if (parsed.Count == MaximumQuestions) break;
                if (Convert(item, parsed.Count) is { } question) parsed.Add(question);
            }

            return parsed;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The reply with the questions block removed, so the pane shows the model's prose without
    /// the raw JSON underneath it.</summary>
    public static string StripBlock(string? reply) =>
        string.IsNullOrWhiteSpace(reply) ? string.Empty : Block.Replace(reply, string.Empty).Trim();

    /// <summary>
    /// Turns the user's choices into the text sent back as their next message.
    ///
    /// <para>Written as "question: answer" lines rather than JSON. The model asked in prose and reasons
    /// in prose; handing it back a data structure it did not request is a needless second format to get
    /// right, and every model already reads a labelled list.</para>
    /// </summary>
    public static string ComposeAnswer(
        IReadOnlyList<AuthoringQuestion> questions,
        IReadOnlyDictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);

        var sb = new StringBuilder();
        foreach (var question in questions)
        {
            if (!answers.TryGetValue(question.Id, out var answer) || string.IsNullOrWhiteSpace(answer))
                continue;

            sb.Append(question.Prompt.TrimEnd().TrimEnd(':', '?'))
              .Append(": ")
              .AppendLine(answer.Trim());
        }

        // Unanswered questions are named rather than silently dropped. A model that asked four things
        // and got two back should be told which two are missing, or it will assume a default for them
        // and never say which.
        var skipped = questions
            .Where(q => !answers.TryGetValue(q.Id, out var a) || string.IsNullOrWhiteSpace(a))
            .Select(q => q.Prompt.TrimEnd().TrimEnd(':', '?'))
            .ToArray();

        if (skipped.Length > 0)
        {
            sb.AppendLine()
              .Append("No preference on: ")
              .AppendLine(string.Join("; ", skipped))
              .Append("Choose sensible defaults for those and say what you chose.");
        }

        return sb.ToString().TrimEnd();
    }

    private static AuthoringQuestion? Convert(WireQuestion wire, int index)
    {
        if (string.IsNullOrWhiteSpace(wire.Question)) return null;

        var options = (wire.Options ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o?.Label))
            .Take(MaximumOptions)
            .Select(o => new AuthoringOption(o!.Label!.Trim(), o.Detail?.Trim() ?? string.Empty))
            .ToArray();

        // A question with no options is a prose question wearing a block. Rendering it as an empty
        // chip row would be worse than leaving it in the text.
        if (options.Length == 0) return null;

        var mode = string.Equals(wire.Kind, "multiple", StringComparison.OrdinalIgnoreCase)
            || string.Equals(wire.Kind, "multi", StringComparison.OrdinalIgnoreCase)
                ? AuthoringAnswerMode.Multiple
                : AuthoringAnswerMode.Single;

        // An id is convenience, not contract — the model forgetting one must not lose the question.
        var id = string.IsNullOrWhiteSpace(wire.Id) ? $"q{index + 1}" : wire.Id!.Trim();

        return new AuthoringQuestion(id, wire.Question!.Trim(), mode, options, wire.AllowOther ?? true);
    }

    private sealed record WireQuestion(
        string? Id,
        string? Question,
        string? Kind,
        WireOption[]? Options,
        bool? AllowOther);

    private sealed record WireOption(string? Label, string? Detail);
}
