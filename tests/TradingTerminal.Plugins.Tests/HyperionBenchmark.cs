using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The benchmark, as a thing that can be re-run.
///
/// <para>The goal loop's question is not "does Hyperion run" — that was answered on 2026-09-01 — but
/// "how does what it builds compare to the hand-written window". Answering that once, by hand, from a
/// console nobody kept, produces a number that cannot be checked and cannot be compared to the next
/// one. So the drive is a committed object: the same two turns, the same brief, the same effort, the
/// same artifacts written to disk every time.</para>
///
/// <para><b>The only thing that varies between the offline and the live run is the client.</b> That is
/// deliberate and it is the reach argument: <see cref="HyperionBenchmarkTests"/> drives this with
/// <see cref="FakeCodegenClient"/> on every build, so the driver — the composition, the escape reply,
/// the compile, the ladder, the artifact writing — is exercised in CI. A live run swaps one
/// constructor argument. A harness that only ever runs against a provider is a harness that rots
/// between runs.</para>
/// </summary>
public static class HyperionBenchmark
{
    /// <summary>
    /// The brief, kept here rather than passed in, because a benchmark whose input moves measures
    /// nothing. One line, and it names the three things <c>TradingTerminal.OrderBook</c> actually shows,
    /// so the hand-written window, the control (<c>LiquidityBookVisualizer</c>) and the model's answer
    /// are all answering the same question.
    /// </summary>
    public const string OrderBookBrief =
        "An order book window: the depth ladder, a liquidity heatmap over time, and the microstructure "
        + "statistics.";

    /// <summary>
    /// The second brief, and the harder one: a picture the widget library has never seen.
    ///
    /// <para>Deliberately the goal's own reference case rather than something tamer. It asks for
    /// three things at once that nothing in the tables provides — a scene in 3D, one mark per resting
    /// order, and motion — so it cannot be answered by reaching for a widget, which is exactly what
    /// makes it worth measuring.</para>
    /// </summary>
    /// <summary>
    /// The vague brief, and it exists to settle one question rather than to measure a window.
    ///
    /// <para>Four live runs have produced zero questions, on briefs that named what they wanted. The
    /// one run that ever asked had a vaguer brief AND a different model, so specificity and model have
    /// never been separated. This is what a user actually types when they have not thought about it
    /// yet: it names no picture, no statistic, no instrument and no timeframe, so every answer would
    /// change a line of code — which is the pack's own test for whether to ask.</para>
    /// </summary>
    public const string VagueBrief = "Show me what is happening in the order book.";

    public const string BattlefieldBrief =
        "The order book as a 3D battlefield: each resting order is a soldier standing on the price it "
        + "rests at, and the armies move as the book changes.";

    /// <summary>What one drive measured. Every field is read off the run rather than estimated.</summary>
    /// <param name="Turns">One entry per user turn — the brief, then the escape if the model asked.</param>
    /// <param name="SystemPromptCharacters">The composed system prompt actually sent.</param>
    /// <param name="SurfaceCharactersSaved">What cutting the SDK surface to the brief bought.</param>
    /// <param name="Skills">The domain packs the brief pulled in.</param>
    /// <param name="Files">The files the last code-bearing turn produced.</param>
    /// <param name="Compile">The compile behind those files, or null if nothing compiled.</param>
    /// <param name="Report">The verification ladder over the compiled unit, or null.</param>
    /// <param name="Directory">Where the artifacts were written.</param>
    public sealed record Result(
        IReadOnlyList<TurnRecord> Turns,
        int SystemPromptCharacters,
        int SurfaceCharactersSaved,
        IReadOnlyList<string> Skills,
        IReadOnlyList<StrategyFile> Files,
        StrategyCompileResult? Compile,
        VerificationReport? Report,
        string Directory)
    {
        public bool Compiled => Compile is { Success: true };

        public int TotalGenerations => Turns.Sum(t => t.Generations);

        public TimeSpan Elapsed => TimeSpan.FromMilliseconds(Turns.Sum(t => t.Elapsed.TotalMilliseconds));
    }

    /// <param name="UserMessage">What was sent.</param>
    /// <param name="Kind">How the turn ended.</param>
    /// <param name="QuestionsAsked">Structured questions parsed out of the reply, when it asked.</param>
    /// <param name="Generations">Model calls this turn — one, plus any auto-fix retries.</param>
    public sealed record TurnRecord(
        string UserMessage,
        BuildTurnKind Kind,
        int QuestionsAsked,
        int Generations,
        CodegenUsage Usage,
        TimeSpan Elapsed,
        string AssistantText,
        string? Error);

    /// <summary>
    /// Drives the two turns a user drives: the brief, and — if the model asks rather than builds — the
    /// escape.
    ///
    /// <para>The escape text is <see cref="AuthoringAction.JustBuildIt"/>, the same string the app's
    /// primary button sends. It is not retyped here: the pack instructs the model to honour that
    /// sentence, so a benchmark sending a paraphrase would be measuring a prompt no user can produce.
    /// </para>
    /// </summary>
    public static async Task<Result> RunAsync(
        IStrategyCodegenClient client,
        IStrategyCompiler compiler,
        AuthoringKind kind,
        StrategyBuildEffort effort,
        string brief,
        string outputDirectory,
        TextWriter? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var pack = StrategyContextPack.Load();
        var profile = StrategyBuildProfile.For(effort);
        var session = new StrategyCodegenOrchestrator(
                compiler, logger: null, skills: StrategySkillLibrary.Load(), pack: pack)
            .CreateSession(
                client, pack.SystemPrompt, "hyperion-benchmark", "Hyperion benchmark",
                profile.MaxFixAttempts, profile: profile, kind: kind);

        Directory.CreateDirectory(outputDirectory);

        var turns = new List<TurnRecord>();
        var activity = new Progress<string>(line => log?.WriteLine($"  · {line}"));

        var record = await SendAsync(session, brief, activity, log, ct).ConfigureAwait(false);
        turns.Add(record);

        // A question is the interview working, not a failure. The user's way out of it is one button;
        // the benchmark takes it, because measuring the picture means getting to one.
        if (record.Kind == BuildTurnKind.Question)
        {
            turns.Add(await SendAsync(session, AuthoringAction.JustBuildIt, activity, log, ct)
                .ConfigureAwait(false));
        }

        var compile = session.Files.Count > 0
            ? compiler.Compile(new StrategyScript(session.StrategyId, session.DisplayName, session.Files))
            : null;

        // The ladder is the half of the comparison a human cannot eyeball. A unit that compiles and
        // never draws looks identical to one that works, in source.
        var report = compile is { Success: true, Unit: not null }
            ? AuthoredUnitVerifier.Verify(compile.Unit)
            : null;

        var result = new Result(
            turns,
            session.SystemContext.Length,
            session.SurfaceCharactersSaved,
            [.. session.LoadedSkills.Select(s => s.Name)],
            session.Files,
            compile,
            report,
            outputDirectory);

        Write(result, session, brief, kind, effort, client);
        return result;
    }

    /// <summary>
    /// Compiles a unit that has already been generated and runs it up the ladder.
    ///
    /// <para>The loop is told to re-run the benchmark after every change and report the delta, and most
    /// changes are to the LADDER rather than to the prompt. Paying a provider for a fresh generation to
    /// measure those confounds the two: a different unit came back, so the delta is unattributable. This
    /// re-judges the exact file the last run produced.</para>
    /// </summary>
    public static (StrategyCompileResult Compile, VerificationReport? Report) Reverify(
        IStrategyCompiler compiler, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(compiler);

        var name = Path.GetFileName(sourcePath);
        var compile = compiler.Compile(new StrategyScript(
            "hyperion-benchmark", "Hyperion benchmark",
            [new StrategyFile(name, File.ReadAllText(sourcePath))]));

        return (compile, compile is { Success: true, Unit: not null }
            ? AuthoredUnitVerifier.Verify(compile.Unit)
            : null);
    }

    private static async Task<TurnRecord> SendAsync(
        StrategyBuildSession session, string message, IProgress<string> activity, TextWriter? log,
        CancellationToken ct)
    {
        log?.WriteLine($"→ {Ellipsis(message, 96)}");
        var clock = Stopwatch.StartNew();
        var turn = await session.SendAsync(message, activity, ct).ConfigureAwait(false);
        clock.Stop();

        var asked = turn.Kind == BuildTurnKind.Question
            ? AuthoringQuestions.Parse(turn.AssistantText).Count
            : 0;

        log?.WriteLine(
            $"← {turn.Kind} in {clock.Elapsed.TotalSeconds:F1}s · {turn.Generations} generation(s) · "
            + $"{turn.Files.Count} file(s) · {asked} question(s)");

        return new TurnRecord(
            message, turn.Kind, asked, turn.Generations, turn.Usage, clock.Elapsed,
            turn.AssistantText, turn.Error);
    }

    /// <summary>
    /// Everything the run produced, on disk. The transcript and the generated source are the evidence
    /// behind whatever the comparison claims, and a claim about a model with no transcript behind it is
    /// the thing this loop keeps being told not to write.
    /// </summary>
    private static void Write(
        Result result, StrategyBuildSession session, string brief, AuthoringKind kind,
        StrategyBuildEffort effort, IStrategyCodegenClient client)
    {
        File.WriteAllText(Path.Combine(result.Directory, "system-prompt.md"), session.SystemContext);

        foreach (var file in result.Files)
            File.WriteAllText(Path.Combine(result.Directory, Path.GetFileName(file.Name)), file.Content);

        var transcript = new StringBuilder();
        foreach (var message in session.Transcript)
            transcript.Append("## ").Append(message.Role).Append("\n\n").Append(message.Content).Append("\n\n");
        File.WriteAllText(Path.Combine(result.Directory, "transcript.md"), transcript.ToString());

        // Every number goes through Fixed/Seconds, which are InvariantCulture. These are compared
        // between runs and across machines, and the first run recorded its prompt length as
        // "1,09,247" — correct lakh grouping for this machine's locale, and not a number anyone will
        // diff against the next one.
        var summary = new StringBuilder()
            .Append("# Hyperion benchmark run\n\n")
            .Append("| | |\n|---|---|\n")
            .Append($"| when | {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC |\n")
            .Append($"| provider | {client.ProviderId} · ")
            .Append(string.IsNullOrWhiteSpace(client.Model) ? "(vendor default)" : client.Model).Append(" |\n")
            .Append($"| kind | {kind} |\n")
            .Append($"| effort | {effort} |\n")
            .Append($"| brief | {brief} |\n")
            .Append($"| system prompt | {Fixed(result.SystemPromptCharacters)} chars |\n")
            .Append($"| surface cut saved | {Fixed(result.SurfaceCharactersSaved)} chars |\n")
            .Append($"| skills | {(result.Skills.Count == 0 ? "none" : string.Join(", ", result.Skills))} |\n")
            .Append($"| turns | {result.Turns.Count} |\n")
            .Append($"| generations | {result.TotalGenerations} |\n")
            .Append($"| elapsed | {Seconds(result.Elapsed)} |\n")
            .Append($"| tokens in/out | {Fixed(result.Turns.Sum(t => t.Usage.InputTokens))} / ")
            .Append($"{Fixed(result.Turns.Sum(t => t.Usage.OutputTokens))} |\n")
            .Append($"| compiled | {(result.Compiled ? "yes" : "NO")} |\n");

        if (result.Files.Count > 0)
        {
            summary.Append("| files | ")
                   .Append(string.Join(", ", result.Files.Select(f => $"{f.Name} ({Fixed(f.Content.Length)} chars)")))
                   .Append(" |\n");
        }

        summary.Append('\n');

        foreach (var turn in result.Turns)
        {
            summary.Append($"- turn: `{Ellipsis(turn.UserMessage, 60)}` → **{turn.Kind}**, ")
                   .Append($"{turn.Generations} generation(s), {Seconds(turn.Elapsed)}");
            if (turn.QuestionsAsked > 0) summary.Append($", {turn.QuestionsAsked} structured question(s)");
            if (turn.Error is { Length: > 0 } error) summary.Append($" — {Ellipsis(error, 160)}");
            summary.Append('\n');
        }

        if (result.Compile is { Success: false })
        {
            summary.Append("\n## Compile errors\n\n");
            foreach (var diagnostic in result.Compile.Errors.Take(20))
                summary.Append("- ").Append(diagnostic.Id).Append(": ").Append(diagnostic.Message).Append('\n');
        }

        if (result.Report is { } report)
        {
            summary.Append("\n## Verification ladder\n\n");
            foreach (var step in report.Steps)
                summary.Append($"- {step.Rung}: **{step.Outcome}**\n");
            foreach (var finding in report.Findings)
                summary.Append($"  - {finding}\n");
        }

        File.WriteAllText(Path.Combine(result.Directory, "summary.md"), summary.ToString());
    }

    private static string Fixed(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

    private static string Ellipsis(string text, int maximum)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= maximum ? flat : flat[..maximum] + "…";
    }
}
