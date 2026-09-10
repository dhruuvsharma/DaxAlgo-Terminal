using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.App.Login;
using TradingTerminal.Authoring.Rasterizer;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Drives one of <see cref="HyperionBriefs"/> through the real swarm, against a real provider, and
/// leaves everything it did on disk.
///
/// <para><b>Off unless <c>HYPERION_LIVE=1</c>.</b> It spends money and takes minutes, so it is not part
/// of anybody's suite; it is the thing you run when the question is "what does it actually build", which
/// is a question the offline suite is structurally unable to answer. Everything below the pane is the
/// SAME code the pane runs — the same session, the same <see cref="SwarmRunner"/>, the same gate, the
/// same critics — because a QA harness that reimplements the pipeline measures the harness.</para>
///
/// <para><b>It writes a live log, flushed per line.</b> A run takes long enough that "is it working"
/// has to be answerable from outside the process, and the last thing written before a hang is the most
/// useful sentence in the file.</para>
///
/// <para>Its output is a saved authoring session, so the six units arrive where they are meant to be
/// tested: the builder's own session list, ready to open, compile and register.</para>
/// </summary>
public sealed class HyperionLiveRun(ITestOutputHelper output)
{
    /// <summary>Where a run leaves its evidence: the source, the log, the summary.</summary>
    private static string Root => Environment.GetEnvironmentVariable("HYPERION_OUT")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo Terminal", "hyperion-runs");

    /// <summary>
    /// TokenRouter's GLM, and only that.
    ///
    /// <para>Left on the model's own reasoning default deliberately. <c>reasoning_effort=high</c> is
    /// ACCEPTED by this endpoint and is not usable: the model reasons until the budget is gone and never
    /// begins an answer — 1,088 seconds and 95,763 output tokens for no code, measured, against the same
    /// brief on the default which compiled in 374. <see cref="AiModelCatalog"/> encodes that; this
    /// passes it explicitly so a run cannot quietly acquire a setting nobody measured.</para>
    /// </summary>
    private const string ProviderId = "tokenrouter";
    private const string BaseUrl = "https://api.tokenrouter.com/v1";
    private const string Model = "z-ai/glm-5.3-free";

    [Fact]
    public async Task Build_one_brief()
    {
        if (Environment.GetEnvironmentVariable("HYPERION_LIVE") != "1") return;

        var id = Environment.GetEnvironmentVariable("HYPERION_BRIEF")
            ?? throw new InvalidOperationException("Set HYPERION_BRIEF to one of: "
                + string.Join(", ", HyperionBriefs.All.Select(b => b.Id)));

        var brief = HyperionBriefs.Find(id)
            ?? throw new InvalidOperationException($"No brief called '{id}'.");

        var directory = Path.Combine(Root, brief.Id);
        Directory.CreateDirectory(directory);

        // UTF-8 explicitly, and FileShare.ReadWrite so the log can be tailed while the run is still
        // writing it — which is the only way to watch a run that takes an hour.
        using var log = new StreamWriter(
            new FileStream(Path.Combine(directory, "run.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };

        var clock = Stopwatch.StartNew();

        // LOCKED, because a fan-out reports from several threads at once. Progress<T> posts its
        // callbacks to the pool, so two parallel builders call this simultaneously — and a
        // StreamWriter is not thread-safe. Unguarded, it wrote 35 NUL bytes into the middle of a real
        // run's log and turned the evidence file into something grep calls binary and refuses to read.
        var pen = new Lock();
        void Say(string line)
        {
            var stamped = $"[{clock.Elapsed:hh\\:mm\\:ss}] {line}";
            lock (pen)
            {
                log.WriteLine(stamped);
                output.WriteLine(stamped);
            }
        }

        Say($"{brief.DisplayName} ({brief.Kind}) · {ProviderId} · {Model}");
        Say($"brief: {brief.Text}");

        // The key never leaves the process: it is read from the same DPAPI store the app reads, decrypted
        // here, and handed to the client. Nothing prints it and nothing writes it to the run directory.
        var key = new AiKeyStore(NullLogger<AiKeyStore>.Instance).Get(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
        {
            Say($"NO KEY for '{ProviderId}'. Add it in Settings → AI providers and run again.");
            Assert.Fail($"No stored key for '{ProviderId}'.");
            return;
        }

        // No client-side deadline. This model streams its thinking for minutes before its first output
        // character, and an HttpClient timeout would cut a working generation off at the knees; the run's
        // own token below is the one control, and it is the one a user's Stop button is.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OpenAiCompatibleCodegenClient(
            http, ProviderId, "TokenRouter", BaseUrl, Model, key, effort: CodegenEffort.Default);

        using var cts = new CancellationTokenSource(Minutes(
            Environment.GetEnvironmentVariable("HYPERION_MINUTES"), fallback: 90));

        var compiler = new RoslynStrategyCompiler();
        var pack = StrategyContextPack.Load();

        // Research: the full fan-out, which is the half of the pipeline worth watching. The reasoning
        // setting comes from the catalogue rather than from the profile, for the reason above.
        var profile = StrategyBuildProfile.For(
            CodegenMode.Research, AiModelCatalog.ResearchEffort(ProviderId, Model));

        var session = new StrategyCodegenOrchestrator(
                compiler, logger: null, skills: StrategySkillLibrary.Load(), pack: pack)
            .CreateSession(
                client, pack.SystemPrompt, brief.Id, brief.DisplayName,
                profile.MaxFixAttempts, profile: profile, kind: brief.Kind);

        // The real off-screen renderer, so the picture critics judge pixels rather than a description of
        // pixels. It owns one STA thread and is disposed with the run.
        using var rasterizer = new WpfUnitRasterizer();

        // The budget the mode buys, with two knobs on top. Both exist because this is a harness being
        // watched: a free-tier endpoint rate-limits a four-way fan-out, and six repair rounds each
        // carrying a six-critic panel is a bill worth being able to cap while the pipeline is still
        // being fixed. Unset, they are exactly what the app would use.
        var budget = SwarmBudget.For(profile, isAgentCli: false) with { };
        if (Count(Environment.GetEnvironmentVariable("HYPERION_ROUNDS")) is { } rounds)
            budget = budget with { MaxRounds = rounds };
        if (Count(Environment.GetEnvironmentVariable("HYPERION_PARALLEL")) is { } parallel)
            budget = budget with { MaxParallel = parallel };

        Say($"budget: {budget.MaxParallel} parallel · {budget.MaxRounds} round(s) · {budget.MaxTasks} task(s) max");

        var thinking = 0;
        var written = 0;
        var beats = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        var turn = await session.SendToSwarmAsync(
            brief.Text,
            new SwarmRunner(
                client,
                new UnitGate(compiler, brief.Id, brief.DisplayName),
                new TrajectoryLog(Path.Combine(directory, "trajectory.jsonl")),
                logger: null,
                gauntlet: GauntletLoop.For(client, vision: null, session.SystemContext, brief.Kind),
                rasterizer: rasterizer),
            budget,
            bar: null,
            images: null,

            // The brief is the whole instruction. A run that stops to interview nobody produces nothing,
            // and there is no one at this keyboard to answer — which is exactly what the app's own
            // "Just build it" says.
            mayAsk: false,
            activity: null,
            swarm: new Progress<SwarmEvent>(evt =>
            {
                // A heartbeat fires per DELTA — dozens a second, per task. The pane wants that (it
                // updates a row in place); a log does not, and the first run wrote thousands of lines
                // of it in ten minutes and buried everything worth reading. Once every fifteen seconds
                // per task is enough to answer "is it alive", which is all a heartbeat is for.
                // The beat table is read and written from those same pool threads, so it shares the pen.
                if (evt is SwarmEvent.TaskProgress beat)
                {
                    lock (pen) { if (!DueForABeat(beats, beat.Task.Id)) return; }
                }

                Say(Describe(evt));
            }),
            events: new Progress<CodegenEvent>(evt =>
            {
                switch (evt)
                {
                    case CodegenEvent.ReasoningDelta r: thinking += r.Text.Length; break;
                    case CodegenEvent.TextDelta t: written += t.Text.Length; break;
                }
            }),
            cts.Token).ConfigureAwait(false);

        Say($"turn: {turn.Kind} · {turn.Files.Count} file(s) · {thinking} thought / {written} written chars");
        if (turn.Error is { Length: > 0 } error) Say($"error: {error}");

        // Judged again here rather than trusting the run's own verdict: this is the report the user will
        // be handed, and it has to come from compiling what is actually on disk.
        var gate = turn.Files.Count > 0
            ? new UnitGate(compiler, brief.Id, brief.DisplayName).Run(turn.Files)
            : null;

        if (gate is not null)
        {
            Say($"ladder: passed={gate.Passed} rungs={gate.Report.RungsCleared} "
                + $"failed at {gate.Report.FailedAt?.ToString() ?? "nothing"}");
            foreach (var finding in gate.Report.Findings) Say($"  {finding}");
        }

        Save(brief, turn, session, gate, directory, clock.Elapsed);
        Say($"written to {directory}");
    }

    /// <summary>True when this task's heartbeat has been quiet long enough to be worth a line.</summary>
    private static bool DueForABeat(Dictionary<string, DateTime> beats, string taskId)
    {
        var now = DateTime.UtcNow;
        if (beats.TryGetValue(taskId, out var last) && now - last < TimeSpan.FromSeconds(15)) return false;

        beats[taskId] = now;
        return true;
    }

    /// <summary>One swarm event as one line. Terse on purpose: this is read by tailing.</summary>
    private static string Describe(SwarmEvent evt) => evt switch
    {
        SwarmEvent.Planning => "planning…",
        SwarmEvent.Planned p =>
            $"plan ({p.Origin}): " + string.Join(" · ", p.Plan.Tasks.Select(t => $"{t.Kind} {t.OwnedFile}")),
        SwarmEvent.MilestoneStarted m => $"milestone: {m.Milestone.Title}",
        SwarmEvent.TaskStarted s => $"  → {(s.IsRepair ? "repair" : "build")} {s.Task.OwnedFile}",
        SwarmEvent.TaskProgress p =>
            $"     {p.Task.OwnedFile}: {p.Thinking} thought / {p.Written} written / {p.Usage.TotalTokens} tok",
        SwarmEvent.TaskFinished f =>
            $"  ← {f.Task.OwnedFile}: {(f.Wrote ? "written" : "NOTHING — " + (f.Note ?? "no file"))}"
            + $" · {f.Usage.TotalTokens} tok",
        SwarmEvent.Dropped d => $"  dropped {string.Join(", ", d.Files)} — {d.Why}",
        SwarmEvent.Gated g => g.Report.Passed
            ? $"gate round {g.Round}: PASSED, {g.Report.RungsCleared} rung(s)"
            : $"gate round {g.Round}: {g.Report.Findings.Count} problem(s) at {g.Report.FailedAt} — "
              + string.Join(" | ", g.Report.Findings.Take(4).Select(f => f.ToString())),
        SwarmEvent.Reviewed r => $"critics round {r.Round}: {r.Result.Summary}",
        SwarmEvent.Finished f => $"FINISHED {f.Outcome}: {f.Summary}",
        _ => evt.GetType().Name,
    };

    /// <summary>
    /// The source, a summary, and — the point of the exercise — a saved authoring session.
    ///
    /// <para>The session is what makes the result testable by a person: it opens in the builder's own
    /// session list with the brief, the reply and the files already in it, so the next step is pressing
    /// Compile &amp; Register rather than pasting code into an editor.</para>
    /// </summary>
    private static void Save(
        HyperionBriefs.Brief brief, StrategyBuildTurn turn, StrategyBuildSession session,
        GateResult? gate, string directory, TimeSpan elapsed)
    {
        foreach (var file in turn.Files)
            File.WriteAllText(Path.Combine(directory, Path.GetFileName(file.Name)), file.Content);

        // The REAL session list, said out loud. The store's directory is a static that the rest of this
        // suite redirects into a temp folder, and a run whose whole purpose is to put six units in front
        // of a person must not inherit somebody else's redirect.
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;

        var summary = new StringBuilder()
            .AppendLine($"# {brief.DisplayName}")
            .AppendLine()
            .AppendLine($"- kind: {brief.Kind}")
            .AppendLine($"- expects: {brief.Streams}")
            .AppendLine($"- provider: {ProviderId} · {Model}")
            .AppendLine($"- elapsed: {elapsed:hh\\:mm\\:ss}")
            .AppendLine($"- turn: {turn.Kind}")
            .AppendLine($"- tokens: {session.TotalUsage.InputTokens} in / {session.TotalUsage.OutputTokens} out")
            .AppendLine($"- compiled: {(gate?.Compile?.Success == true ? "yes" : "NO")}")
            .AppendLine($"- ladder: {(gate is null ? "not run" : $"{gate.Report.RungsCleared} rung(s), passed={gate.Passed}")}")
            .AppendLine()
            .AppendLine("## brief")
            .AppendLine()
            .AppendLine(brief.Text)
            .AppendLine()
            .AppendLine("## files")
            .AppendLine();

        foreach (var file in turn.Files) summary.AppendLine($"- `{file.Name}` — {file.Content.Length} chars");

        if (gate is { } result && result.Report.Findings.Count > 0)
        {
            summary.AppendLine().AppendLine("## findings").AppendLine();
            foreach (var finding in result.Report.Findings) summary.AppendLine($"- {finding}");
        }

        summary.AppendLine().AppendLine("## reply").AppendLine().AppendLine(turn.AssistantText);
        File.WriteAllText(Path.Combine(directory, "summary.md"), summary.ToString());

        var now = DateTime.Now;
        AuthoringSessionStore.Save(new AuthoringSessionSnapshot(
            brief.Id,
            brief.DisplayName,
            [
                new AuthoringChatEntry(AuthoringChatEntry.User, brief.Text, now),
                new AuthoringChatEntry(AuthoringChatEntry.Assistant, turn.AssistantText, now),
            ],
            session.Transcript,
            turn.Files,
            ProviderId,
            Model,
            CodegenEffort.Default.Wire(),
            StrategyBuildEffort.Max.Wire(),
            session.TotalUsage.InputTokens,
            session.TotalUsage.OutputTokens));
    }

    private static int? Count(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static TimeSpan Minutes(string? value, int fallback) =>
        TimeSpan.FromMinutes(int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback);
}
