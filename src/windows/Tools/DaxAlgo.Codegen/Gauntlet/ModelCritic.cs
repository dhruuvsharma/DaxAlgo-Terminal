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
/// <param name="reader">Who reads the page's source when the picture cannot be looked at — the build
/// model, when <paramref name="client"/> is a borrowed vision model. Null reads with the client itself.</param>
public sealed class ModelCritic(
    IStrategyCodegenClient client,
    CriticDefinition definition,
    string sharedContext,
    bool canSeeImages,
    IStrategyCodegenClient? reader = null) : IUnitCritic
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

        var looking = !NeedsPicture || (subject.Raster is not null && canSeeImages);

        // A PICTURE CRITIC THAT CANNOT LOOK READS INSTEAD — when its definition says how. Without a
        // reading instruction the old rule stands: a picture critic without a picture is a different
        // critic, and saying so beats reporting whatever it makes of a text dump.
        if (!looking && definition.SourceInstruction is null)
            return CriticVerdict.Skipped(
                Id,
                Panel,
                subject.Raster is null
                    ? "No render was available on this host, so nothing could look at the picture."
                    : $"{client.DisplayName} cannot be shown images, so the picture was not reviewed. "
                      + "The drawing commands were still judged.");

        if (!looking)
            return await ReadSourceAsync(subject, bar, CodegenUsage.None, ct).ConfigureAwait(false);

        var images = new List<CodegenImage>();
        if (canSeeImages)
        {
            // References FIRST, then ours. The order is the comparison: a critic asked which is better
            // should meet the standard before it meets the candidate.
            images.AddRange(bar.Images);
            if (subject.Raster is { } raster)
                images.Add(new CodegenImage(UnitRaster.MediaType, raster.Png, "OUR UNIT, as it renders"));
        }

        var message = new CodegenMessage(CodegenRole.User, Compose(subject, bar, source: false), images.Count > 0 ? images : null);
        var (response, reported) = await AskAsync(client, message, definition.Instruction, ct).ConfigureAwait(false);

        // The usage rides on the verdict either way. Critic calls used to be read and thrown away, so a
        // run's total never included them and a run that reached the gauntlet looked cheaper than it was.
        if (!response.Success)
        {
            // THE MODEL THAT LOOKS WAS NOT THERE. A picture critic routed to a borrowed vision model that
            // failed still has the page's source to read, so it reads rather than reporting nothing.
            if (NeedsPicture && definition.SourceInstruction is not null)
                return await ReadSourceAsync(subject, bar, reported, ct).ConfigureAwait(false);

            return CriticVerdict.Skipped(Id, Panel, $"{client.DisplayName} failed: {response.Error}") with { Usage = reported };
        }

        return Read(response.RawText) with { Usage = reported };
    }

    /// <summary>The picture critic's reading mode: the page's source, judged for what it will look like.</summary>
    private async Task<CriticVerdict> ReadSourceAsync(
        GauntletSubject subject, ReferenceBar bar, CodegenUsage spent, CancellationToken ct)
    {
        var who = reader ?? client;
        var message = new CodegenMessage(CodegenRole.User, Compose(subject, bar, source: true));
        var (response, reported) = await AskAsync(who, message, definition.SourceInstruction!, ct).ConfigureAwait(false);
        var usage = spent.Add(reported);

        if (!response.Success)
            return CriticVerdict.Skipped(Id, Panel, $"{who.DisplayName} failed reading the page: {response.Error}") with { Usage = usage };

        var verdict = Read(response.RawText);
        return verdict with { Verdict = "(read from the source — nothing that sees was available) " + verdict.Verdict, Usage = usage };
    }

    /// <summary>
    /// One critic call, and — when it reasoned through its whole budget without answering — one more at
    /// a medium effort.
    ///
    /// <para><b>The run's own effort first, as the owner asked.</b> Measured 2026-09-19 on NVIDIA NIM's
    /// DeepSeek V4 Flash at a high effort: two critics each reasoned through all 131,072 output tokens,
    /// about fifty-five minutes apiece, and returned nothing — so a unit that passed the gate was never
    /// reviewed. A critic's answer is a few lines of JSON; the retry is what makes sure there is one.</para>
    /// </summary>
    private async Task<(StrategyCodegenResponse Response, CodegenUsage Usage)> AskAsync(
        IStrategyCodegenClient who, CodegenMessage message, string instruction, CancellationToken ct)
    {
        var request = new StrategyCodegenRequest(sharedContext, [message], instruction + CriticPrompts.OutputContract);
        var (response, reported) = await CodegenStream.DrainAsync(who, request, events: null, ct).ConfigureAwait(false);

        if (response.Success || !ThoughtWithoutAnswering(response.Error)) return (response, reported);
        if (!AiModelCatalog.SupportsEffort(who.ProviderId, who.Model)) return (response, reported);
        if (who.Effort is CodegenEffort.Low or CodegenEffort.Medium) return (response, reported);

        var (retried, again) = await CodegenStream.DrainAsync(
            who, request with { Effort = CodegenEffort.Medium }, events: null, ct).ConfigureAwait(false);
        return (retried, reported.Add(again));
    }

    /// <summary>A failure that is the model thinking past its budget rather than the provider refusing.</summary>
    internal static bool ThoughtWithoutAnswering(string? error) =>
        error is not null
        && (error.Contains("never started an answer", StringComparison.OrdinalIgnoreCase)
            || error.Contains("reached its output limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>What this critic is shown, in the order it should read it.</summary>
    private string Compose(GauntletSubject subject, ReferenceBar bar, bool source)
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

        if (source || Panel == CriticPanel.Quant || !canSeeImages)
        {
            text.AppendLine("THE SOURCE");
            var budget = MaximumSourceCharacters;
            // A picture critic reading the source reads the PAGE first: the budget is shared, and the C#
            // is what the other critics already read.
            var ordered = source && NeedsPicture
                ? subject.Files.OrderBy(f => CodegenCodeExtractor.IsPageFile(f.Name) ? 0 : 1).ToArray()
                : subject.Files;

            foreach (var file in ordered)
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
