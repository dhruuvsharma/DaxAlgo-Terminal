using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

/// <summary>
/// One critic, run against a provider.
///
/// <para><b>It is handed the subject and the bar, and nothing else.</b> No plan, no builder transcript,
/// no record of what was tried — fresh context is the mechanism, not a nicety. A critic that can see the
/// reasoning behind a picture starts grading the reasoning, which is how a loop convinces itself that a
/// blank panel was the right call for defensible reasons.</para>
/// </summary>
/// <param name="client">Where to ask. Not necessarily the build model: a picture critic on a model that
/// cannot see images is worth routing elsewhere, and critic calls are small.</param>
/// <param name="definition">Which critic this is.</param>
/// <param name="sharedContext">The system pack, so the critic knows the SDK the unit is written
/// against. Same prefix as every other call in the run, so it stays cached.</param>
/// <param name="canSeeImages">Whether this client's model can be shown the render.</param>
public sealed class ModelCritic(
    IStrategyCodegenClient client,
    CriticDefinition definition,
    string sharedContext,
    bool canSeeImages) : IUnitCritic
{
    public string Id => definition.Id;

    public CriticPanel Panel => definition.Panel;

    public bool NeedsPicture => definition.NeedsPicture;

    /// <summary>How much source one critic is shown, in characters.</summary>
    public const int MaximumSourceCharacters = 60_000;

    public async Task<CriticVerdict> JudgeAsync(
        GauntletSubject subject, ReferenceBar bar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(bar);

        // THE UNIT THAT WAS WRITTEN DECIDES, NOT THE PANE THAT ASKED FOR IT. The panel is built from
        // the kind the user selected, and a model that was asked for a strategy can return a
        // visualizer — the compiler resolves whichever it actually is. Left unchecked, the book critic
        // would review a visualizer and report its missing exits on every round, forever.
        //
        // The same rule the verifier already follows: taken from the resolved type rather than from
        // the brief, because what the author actually wrote is the only reliable answer.
        if (definition.AppliesTo is { } only && only != subject.Kind)
            return CriticVerdict.Skipped(
                Id, Panel, $"Not applicable to a {subject.Kind.ToString().ToLowerInvariant()}.");

        // A picture critic without a picture is not a degraded picture critic — it is a different
        // critic, judging different evidence, and one of the other five already reads the commands.
        // Saying so beats running it and reporting whatever it makes of a text dump.
        if (NeedsPicture && (subject.Raster is null || !canSeeImages))
            return CriticVerdict.Skipped(
                Id,
                Panel,
                subject.Raster is null
                    ? "No render was available on this host, so nothing could look at the picture."
                    : $"{client.DisplayName} cannot be shown images, so the picture was not reviewed. "
                      + "The drawing commands were still judged.");

        var images = new List<CodegenImage>();
        if (canSeeImages)
        {
            // References FIRST, then ours. The order is the comparison: a critic asked which is better
            // should meet the standard before it meets the candidate.
            images.AddRange(bar.Images);
            if (subject.Raster is { } raster)
                images.Add(new CodegenImage(UnitRaster.MediaType, raster.Png, "OUR UNIT, as it renders"));
        }

        var message = new CodegenMessage(CodegenRole.User, Compose(subject, bar), images.Count > 0 ? images : null);

        var (response, _) = await CodegenStream.DrainAsync(
            client,
            new StrategyCodegenRequest(sharedContext, [message], definition.Instruction + CriticPrompts.OutputContract),
            events: null,
            ct).ConfigureAwait(false);

        if (!response.Success)
            return CriticVerdict.Skipped(Id, Panel, $"{client.DisplayName} failed: {response.Error}");

        return Read(response.RawText);
    }

    /// <summary>What this critic is shown, in the order it should read it.</summary>
    private string Compose(GauntletSubject subject, ReferenceBar bar)
    {
        var text = new StringBuilder();

        text.AppendLine($"UNIT KIND: {subject.Kind}");
        text.AppendLine();

        if (bar.Compose() is { Length: > 0 } composed) text.AppendLine(composed);

        text.AppendLine("WHAT IT DREW");
        text.AppendLine(subject.Primitives);

        // What the ladder already settled, so the critic does not spend the user's money re-deriving
        // that the thing compiles.
        text.AppendLine($"OBJECTIVE CHECKS: {subject.Ladder.RungsCleared} rung(s) cleared"
                        + (subject.Ladder.FailedAt is { } failed ? $", stopped at {failed}" : ", nothing failed"));
        text.AppendLine();

        if (Panel == CriticPanel.Quant || !canSeeImages)
        {
            text.AppendLine("THE SOURCE");
            var budget = MaximumSourceCharacters;
            foreach (var file in subject.Files)
            {
                if (budget <= 0) break;
                var body = file.Content.Length <= budget ? file.Content : file.Content[..budget];
                budget -= body.Length;

                text.AppendLine($"// file: {file.Name}");
                text.AppendLine(body);
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a verdict out of whatever came back.
    ///
    /// <para>Tolerant on purpose. A critic that mumbled has found nothing worth acting on, and treating
    /// that as a failure would let one unparseable reply stop a run that six critics were reviewing.
    /// The one thing it must not do is invent findings.</para>
    /// </summary>
    internal CriticVerdict Read(string? reply)
    {
        foreach (var candidate in Candidates(reply))
        {
            try
            {
                if (JsonSerializer.Deserialize<Wire>(candidate, Json) is not { } wire) continue;

                var findings = (wire.Findings ?? [])
                    .Where(f => !string.IsNullOrWhiteSpace(f.Problem))
                    .Take(MaximumFindings)
                    .Select(f => new VerificationFinding(
                        $"{Id}.{Slug(f.Code)}",
                        f.Problem!.Trim(),
                        string.IsNullOrWhiteSpace(f.Remedy) ? null : f.Remedy.Trim(),
                        string.IsNullOrWhiteSpace(f.File) ? null : f.File.Trim()))
                    .ToArray();

                return new CriticVerdict(
                    Id, Panel, findings,
                    string.IsNullOrWhiteSpace(wire.Verdict)
                        ? (findings.Length == 0 ? "Nothing to report." : $"{findings.Length} finding(s).")
                        : wire.Verdict.Trim());
            }
            catch (JsonException)
            {
                // Try the next block.
            }
        }

        return CriticVerdict.Skipped(Id, Panel, "The critic did not answer in a readable shape.");
    }

    /// <summary>The most findings one critic may contribute, whatever it returned.</summary>
    public const int MaximumFindings = 5;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static IEnumerable<string> Candidates(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) yield break;

        foreach (var block in CodegenCodeExtractor.FencedBlocks(reply))
            if (block.TrimStart().StartsWith('{'))
                yield return block;

        var open = reply.IndexOf('{');
        var close = reply.LastIndexOf('}');
        if (open >= 0 && close > open) yield return reply[open..(close + 1)];
    }

    /// <summary>
    /// Reduces a model's code to a stable slug.
    ///
    /// <para>Finding codes are greppable keys that end up in a log and in a repair prompt, so a code
    /// arriving as a sentence — which models do — would make every occurrence unique and the whole
    /// point of a code disappear.</para>
    /// </summary>
    internal static string Slug(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "finding";

        var slug = new StringBuilder(32);
        foreach (var ch in code.Trim().ToLowerInvariant())
        {
            if (slug.Length == 40) break;
            if (char.IsAsciiLetterOrDigit(ch)) slug.Append(ch);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        return slug.ToString().Trim('-') is { Length: > 0 } cleaned ? cleaned : "finding";
    }

    private sealed record Wire(string? Verdict, IReadOnlyList<WireFinding>? Findings);

    private sealed record WireFinding(string? Code, string? Problem, string? Remedy, string? File);
}
