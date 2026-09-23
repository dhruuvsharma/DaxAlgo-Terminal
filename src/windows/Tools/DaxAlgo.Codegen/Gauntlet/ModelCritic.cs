using System.Globalization;
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

        return WithoutPhantoms(Read(response.RawText), subject) with { Usage = reported };
    }

    /// <summary>
    /// Drops the findings the gate has already disproved: a file the unit has, reported as missing, cut
    /// off or failing to parse; no class implementing the unit's interface, when the unit compiled and ran.
    ///
    /// <para><b>Evidence over opinion, the ladder's rule applied to the critics.</b> A critic sees text; the
    /// gate compiled every C# file, found the hostable class and ran the page without a script error. When
    /// the two disagree about whether a file is there, the gate is right — and a repair sent to "provide
    /// the missing hud.js" rewrites a working module to satisfy a note about a view that was cut short.
    /// Every other finding is kept: a file MISSING from the unit is still reported, and so is anything
    /// about what a file that exists does.</para>
    /// </summary>
    internal static CriticVerdict WithoutPhantoms(CriticVerdict verdict, GauntletSubject subject)
    {
        if (verdict.Findings.Count == 0 || subject.Ladder.FailedAt is not null) return verdict;

        var names = subject.Files.Select(f => f.Name).ToArray();
        bool Exists(string mentioned)
        {
            var path = mentioned.Replace('\\', '/').TrimStart('.', '/');
            var leaf = path[(path.LastIndexOf('/') + 1)..];
            return names.Any(n => string.Equals(n, path, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(n[(n.LastIndexOf('/') + 1)..], leaf, StringComparison.OrdinalIgnoreCase));
        }

        var kept = verdict.Findings.Where(finding =>
        {
            // Codes arrive as slugs ("scenejs-truncated-syntax-error"), so they are read as words.
            var claim = $"{finding.Code.Replace('-', ' ').Replace('_', ' ')} {finding.Message}";
            if (NoUnitClass.IsMatch(claim)) return false;
            if (!AbsentOrCut.IsMatch(claim)) return true;

            // Which files the claim is about: the file it names, and every file in its words.
            var mentioned = MentionedFile.Matches(claim).Select(m => m.Value)
                .Concat(finding.File is { Length: > 0 } f ? f.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [])
                .ToArray();

            // A claim about a file the unit does NOT have is a real one.
            return mentioned.Length == 0 || !mentioned.All(Exists);
        }).ToArray();

        if (kept.Length == verdict.Findings.Count) return verdict;

        var dropped = verdict.Findings.Count - kept.Length;
        return verdict with
        {
            Findings = kept,
            Verdict = $"{verdict.Verdict} ({dropped} finding(s) dropped: they called a file that compiled and ran missing or cut off.)",
        };
    }

    /// <summary>A claim that a FILE is absent or cut short — not that something inside one is missing,
    /// which is a real finding about a file that exists ("index.html is missing a viewport tag").</summary>
    private static readonly System.Text.RegularExpressions.Regex AbsentOrCut = new(
        @"(?:file|module|script|stylesheet|source)s?\s+(?:is|are|was|were|appears?\s+to\s+be|seems?\s+to\s+be)\s+(?:missing|absent|truncated|cut\s?off|incomplete|not\s+(?:provided|included|present))"
        + @"|missing\s+(?:\w+\s+){0,2}?(?:file|module|script)s?\b"
        + @"|(?:truncated|cut\s?off)\s+(?:file|module|script|source)"
        + @"|\b[\w\-]+\.(?:m?js|cs|html?|css)\s+(?:is\s+|was\s+)?(?:truncated|cut\s?off)"
        + @"|imported\s+but\s+(?:missing|not\s+(?:provided|present|included))"
        + @"|but\s+(?:these|those|the)\s+(?:\w+\s+){0,2}?(?:files|modules|scripts)\s+(?:are|were)\s+not"
        + @"|(?:ends|breaks\s+off|stops)\s+(?:mid|in\s+the\s+middle)|mid\s?statement"
        + @"|syntax\s+error|does\s+not\s+parse|unterminated"
        + @"|(?:is|are)\s+not\s+(?:in|among|part\s+of)\s+the\s+(?:source|files|unit)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex NoUnitClass = new(
        @"no\s+(?:public\s+)?class\s+(?:that\s+)?implements\s+IUnit|missing\s+unit\s+class|no\s+IUnit\s+implementation",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex MentionedFile = new(
        @"[\w.\-/\\]*[\w\-]\.(?:cs|m?js|html?|css)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

        var verdict = WithoutPhantoms(Read(response.RawText), subject);
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
            text.Append(Source(subject, readingThePage: source && NeedsPicture));

        return text.ToString();
    }

    /// <summary>
    /// The unit's source as a critic is shown it: a manifest of EVERY file first, then each file either
    /// whole or as an outline — never cut off part-way.
    ///
    /// <para><b>This used to stop at the budget in the middle of whichever file it had reached, and say
    /// nothing.</b> Measured on the 2026-09-20 Nemotron Battlefield run: the page's scene module alone was
    /// 72,081 characters against a 60,000 budget, so every critic in every round was shown a scene.js that
    /// broke off mid-statement and none of the four files after it. They reported exactly that — "scene.js
    /// is truncated with a syntax error", "hud.js, depthChart.js and feed.js are imported but missing",
    /// "no class implements IUnit" — about files that existed, compiled and ran; fifteen to nineteen
    /// findings a round, and each round's repairs rewrote working files to satisfy them.</para>
    ///
    /// <para><b>What each critic reads first is what it judges</b>: the page for the critic reading the
    /// page, the C# for the market-logic, data-contract and book critics, and for the integration critic
    /// the unit's class and the page's shell — the two ends of every message. A file that does not fit
    /// whole is shown as its declarations with their line numbers, and the manifest says so.</para>
    /// </summary>
    internal string Source(GauntletSubject subject, bool readingThePage)
    {
        var files = subject.Files;
        var ordered = files
            .Select((file, index) => (file, index))
            .OrderBy(x => Priority(x.file, readingThePage))
            .ThenBy(x => x.file.Content.Length)
            .ThenBy(x => x.index)
            .Select(x => x.file)
            .ToArray();

        // Whole where it fits, outline where it does not — decided before anything is written, so the
        // manifest can say which is which.
        var budget = MaximumSourceCharacters;
        var shown = new List<(StrategyFile File, string Body, bool Whole)>();
        foreach (var file in ordered)
        {
            if (file.Content.Length <= budget)
            {
                shown.Add((file, file.Content, true));
                budget -= file.Content.Length;
                continue;
            }

            var outline = Outline(file, Math.Min(MaximumOutlineCharacters, Math.Max(0, budget)));
            shown.Add((file, outline, false));
            budget -= outline.Length;
        }

        var passed = subject.Ladder.FailedAt is null;
        var text = new StringBuilder();
        text.AppendLine("THE UNIT'S FILES — every one of them exists"
                        + (passed ? "; the gate compiled all of the C#, loaded the page and drove it without a script error." : "."));
        foreach (var (file, body, whole) in shown)
            text.AppendLine($"  - {file.Name} · {file.Content.Length.ToString("N0", CultureInfo.InvariantCulture)} characters · "
                            + (whole ? "in full below" : body.Length > 0 ? "OUTLINE below (declarations with line numbers); the file is complete" : "not shown (budget); the file is complete"));
        text.AppendLine("Judge what you are shown. A file shown in outline, or not shown, is not missing, empty or cut off — never report it as such.");
        text.AppendLine();

        text.AppendLine("THE SOURCE");
        foreach (var (file, body, whole) in shown)
        {
            if (body.Length == 0) continue;
            text.AppendLine(whole ? $"// file: {file.Name}" : $"// file: {file.Name} — OUTLINE ONLY ({LineCount(file.Content).ToString("N0", CultureInfo.InvariantCulture)} lines; the rest of the file exists)");
            text.AppendLine(body);
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>How much of the source budget one outline may take, in characters.</summary>
    public const int MaximumOutlineCharacters = 4_000;

    /// <summary>Lower reads first.</summary>
    private int Priority(StrategyFile file, bool readingThePage)
    {
        var page = CodegenCodeExtractor.IsPageFile(file.Name);
        var shell = page && file.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        var unitClass = !page && file.Content.Contains("IUnit", StringComparison.Ordinal);

        if (readingThePage) return shell ? 0 : page ? 1 : 2;
        return Id switch
        {
            Critics.Integration => unitClass ? 0 : shell ? 1 : page ? 3 : 2,
            _ when Panel == CriticPanel.Quant => unitClass ? 0 : page ? 2 : 1,
            _ => shell ? 0 : page ? 1 : 2,
        };
    }

    /// <summary>
    /// A file too large to show whole, reduced to what another file could depend on: its declarations,
    /// exports and wiring, each with its line number.
    /// </summary>
    internal static string Outline(StrategyFile file, int maxCharacters)
    {
        if (maxCharacters <= 0) return string.Empty;

        var lines = file.Content.Replace("\r\n", "\n").Split('\n');
        var keep = file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? CSharpOutline
            : file.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? CssOutline
            : file.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ? HtmlOutline
            : ScriptOutline;

        var text = new StringBuilder();
        var skipped = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || !keep.IsMatch(line)) continue;

            var entry = $"L{i + 1}: {(line.Length > 160 ? line[..160] + " …" : line)}";
            if (text.Length + entry.Length + 1 > maxCharacters)
            {
                skipped++;
                continue;
            }

            text.AppendLine(entry);
        }

        if (skipped > 0) text.AppendLine($"… {skipped} more declaration(s) not listed");
        return text.ToString().TrimEnd();
    }

    private static int LineCount(string content) => content.Count(c => c == '\n') + 1;

    private static readonly System.Text.RegularExpressions.Regex CSharpOutline = new(
        @"^(?:\[|(?:public|internal|private|protected|sealed|static|abstract|partial|readonly|override|async|file|record|class|struct|interface|enum)\b)|Ui\.(?:Send|On)\(|Settings\.|\.On(?:Quote|Trade|Depth|Bar)s?\(|Schedule\.",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ScriptOutline = new(
        @"^(?:export|import|function|async\s+function|class)\b|^(?:const|let|var)\s+[\w$]+\s*=\s*(?:async\s*)?(?:\(|function|[\w$]+\s*=>|new\s)|dax\.(?:on|send|ready)\(|addEventListener\(|requestAnimationFrame\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex HtmlOutline = new(
        @"<(?:script|link|canvas|header|main|aside|section|footer|nav)\b|\bid\s*=",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex CssOutline = new(
        @"\{\s*$|^@(?:media|keyframes|import)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

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
