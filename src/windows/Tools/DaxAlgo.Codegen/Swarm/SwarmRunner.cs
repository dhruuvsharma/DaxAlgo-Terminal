using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>What a run was asked to build.</summary>
/// <param name="Brief">The user's words.</param>
/// <param name="SharedContext">The composed system pack — the cached prefix of every call in the run.</param>
/// <param name="Kind">Strategy or visualizer. Fixed for the run: it shapes the contract.</param>
/// <param name="Budget">The limits.</param>
/// <param name="Existing">Files the pane already has, so a second turn revises rather than restarts.</param>
/// <param name="Plan">A plan to continue with, skipping the planner.</param>
/// <param name="MayAsk">
/// Whether the planner is allowed to come back with a question instead of a plan.
///
/// <para>False when the user has just said "build it". Otherwise the escape is only a suggestion, and a
/// model that keeps asking keeps winning — which is the shape of the bug that produced six briefs, six
/// interviews and no code in a user's saved session.</para>
/// </param>
/// <param name="Thread">
/// The conversation so far, ending with this turn's message.
///
/// <para><b>The planner is the only participant that gets one</b>, and it needs it: an answer arrives as
/// "approved, now start building", which means nothing without the brief it approves. That exact
/// omission is what made the committee interview forever — turn two was handed the user's answer with
/// nothing it was an answer TO, and another interview is the only sane reply to that. Builders are
/// stateless by contrast, and deliberately: each gets its task and its dependencies, never the
/// history.</para>
/// </param>
public sealed record SwarmRequest(
    string Brief,
    string SharedContext,
    AuthoringKind Kind,
    SwarmBudget Budget,
    IReadOnlyList<StrategyFile>? Existing = null,
    BuildPlan? Plan = null,
    bool MayAsk = true,
    IReadOnlyList<CodegenMessage>? Thread = null,
    ReferenceBar? Bar = null)
{
    /// <summary>The standard the critics judge against. Defaults to the plan's own rubric, which the
    /// planner wrote from the brief before there was anything to be defensive about.</summary>
    public ReferenceBar Bar { get; init; } = Bar ?? ReferenceBar.None;
}

/// <summary>Everything a run produced.</summary>
/// <param name="PlannerNote">What the planner said when it did not return a plan — its own words, kept
/// so they can be shown rather than replaced by a description of work it never agreed to.</param>
public sealed record SwarmRun(
    SwarmOutcome Outcome,
    BuildPlan Plan,
    PlanOrigin Origin,
    IReadOnlyList<StrategyFile> Files,
    VerificationReport? Report,
    StrategyCompileResult? Compile,
    CodegenUsage Usage,
    string Summary,
    string? Error = null,
    string? PlannerNote = null);

/// <summary>
/// The swarm: plan the work, build it in parallel against one contract, gate it, and repair what the
/// gate rejected — routed to whoever owns the broken file.
///
/// <para><b>Every model call here is streamed, and that is not a detail.</b> What this replaces called
/// the blocking entry point once per agent, so a run was a sequence of silent HTTP requests with no
/// text, no thinking and no token movement — measured on a user's own machine as 158 turns, 140 of them
/// repairs, every one scoring zero against the same rung, 120 of them inside three minutes. Streaming,
/// a per-call timeout from the provider's own client, live task reporting and a stall detector are
/// each a direct answer to part of that.</para>
///
/// <para>Everything is injected, so a whole run can be driven from a fake client and asserted on. A loop
/// that can only be observed against a live provider is a loop nobody will change.</para>
/// </summary>
public sealed class SwarmRunner(
    IStrategyCodegenClient client,
    UnitGate gate,
    TrajectoryLog? trajectory = null,
    ILogger? logger = null,
    GauntletLoop? gauntlet = null,
    IUnitRasterizer? rasterizer = null)
{
    private readonly IStrategyCodegenClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly UnitGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>The critics, or null to deliver on the ladder alone — which is what a host with no
    /// second provider, and every test that is about orchestration, does.</summary>
    private readonly GauntletLoop? _gauntlet = gauntlet;

    /// <summary>Turns the unit into a picture for the critics. Null on a headless host; the critics
    /// then judge the drawing commands and say so.</summary>
    private readonly IUnitRasterizer? _rasterizer = rasterizer;

    /// <summary>Runs a brief to a verified unit, or to an honest account of why not.</summary>
    /// <param name="request">What to build.</param>
    /// <param name="progress">Plan and per-task progress — the task board.</param>
    /// <param name="events">
    /// The model's own output: text as it is written, its thinking, and token movement.
    ///
    /// <para><b>Forwarded from every SEQUENTIAL call and never from a parallel one</b>, and the
    /// distinction is the whole reason this is not simply passed through everywhere. Deltas from four
    /// builders arriving in one transcript bubble interleave into noise — worse than nothing, because
    /// the user cannot tell it is four agents rather than one confused one. So a fan-out reports
    /// through the task board, and only usage (which is a number, and sums correctly) still flows
    /// here. Standard is a swarm of one, so it streams exactly as the single conversation did.</para>
    /// </param>
    public async Task<SwarmRun> RunAsync(
        SwarmRequest request,
        IProgress<SwarmEvent>? progress = null,
        IProgress<CodegenEvent>? events = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // EVERYTHING THE RUN HAS REACHED, declared out here so the catch below can still report it.
        // A cancelled run is not a failed one: the builders that already answered were paid for, and
        // their files are the whole reason Stop is not called Discard.
        var usage = CodegenUsage.None;
        var context = new SwarmContext(request.Brief, request.Existing);
        string? note = null;
        GateResult? verdict = null;

        // The last provider failure, kept rather than acted on. See the build loop: one builder failing
        // is not the run failing, but if the run ends with nothing at all, this is why.
        string? providerError = null;

        // The plan a cancellation before planning reports: the one-task fallback, which is a truthful
        // description of a run that never got as far as deciding anything else.
        var plan = BuildPlan.Single(request.Brief, request.Kind);
        var origin = PlanOrigin.Planned;

        try
        {
            // ── plan ────────────────────────────────────────────────────────────────────────────────

            if (request.Plan is { } resumed)
            {
                (plan, origin) = (resumed, PlanOrigin.Planned);
            }
            else
            {
                progress?.Report(new SwarmEvent.Planning());
                var planned = await PlanAsync(request, events, ct).ConfigureAwait(false);
                usage = usage.Add(planned.Usage);
                (plan, origin) = (planned.Plan, planned.Origin);

                if (origin == PlanOrigin.ProviderFailed)
                    return Failed(plan, origin, context, usage, planned.Error);

                // A question is the one reply that must stop the run, because the answer is the user's.
                // Building anyway would spend the whole budget on a brief the model has just said it does
                // not understand — and would throw away the turn in which it could have been corrected.
                if (origin == PlanOrigin.Asked)
                    return new SwarmRun(
                        SwarmOutcome.AwaitingUser, plan, origin, context.Files, null, null, usage,
                        planned.Asked ?? string.Empty, PlannerNote: planned.Asked);

                note = planned.Asked;
            }

            progress?.Report(new SwarmEvent.Planned(plan, origin));
            Record("Planner", null, usage);

            // ── build ───────────────────────────────────────────────────────────────────────────────
            foreach (var milestone in plan.Milestones)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new SwarmEvent.MilestoneStarted(milestone));

                foreach (var layer in Layers(milestone.Tasks))
                {
                    await FanOutAsync(
                        layer,
                        task => BuildOneAsync(task, plan, context, request, isRepair: false, [], events, progress, ct),
                        request.Budget.MaxParallel,
                        ct,
                        landed: (task, result) =>
                    {
                        usage = usage.Add(result.Usage);

                        // A BUILDER THAT FAILED IS A TASK THAT WROTE NOTHING, NOT A FAILED RUN.
                        //
                        // Aborting here threw away every sibling task the moment any one call came back
                        // empty — measured on a real run: three tasks, the first wrote its file in half
                        // an hour, the second reasoned itself out of its token budget, and the third —
                        // the one owning the hostable class — was never asked. An hour and fifty
                        // minutes and 170,000 tokens produced one orphan helper and a unit with no
                        // entry point.
                        //
                        // The failure modes are not alike. A missing key fails EVERY call, and that run
                        // ends below with nothing built and this error as the reason. A model that
                        // over-thinks one turn fails ONE call, and the gate and the repair round are
                        // already built to notice a missing file and route somebody at it.
                        if (result.Error is { Length: > 0 } failed)
                        {
                            providerError = failed;
                            progress?.Report(new SwarmEvent.TaskFinished(
                                task, false, result.Usage, failed, context.Files));
                            return;
                        }

                        var wrote = context.Accept(task, result.Files);
                        progress?.Report(new SwarmEvent.TaskFinished(
                            task, wrote, result.Usage,
                            wrote ? null : "returned no file — the turn was spent without producing one",
                            context.Files));
                    }).ConfigureAwait(false);
                }
            }

            // ── gate, and repair what it rejected ───────────────────────────────────────────────────
            var best = int.MinValue;
            var stalled = 0;
            GauntletResult? lastReview = null;

            for (var round = 0; round <= request.Budget.MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                if (context.Files.Count == 0)
                    return providerError is { Length: > 0 }
                        ? Failed(plan, origin, context, usage, providerError)
                        : Done(SwarmOutcome.BudgetExhausted, plan, origin, context, null, usage, note: note, summary:
                            "No builder produced a file. Nothing was compiled.");

                verdict = _gate.Run(context.Files);

                // A file nobody owns cannot be repaired by anybody, so if it is what broke the build, take
                // it out — but only on the compiler's word. See ShedDeadWeight.
                if (!verdict.Passed && ShedDeadWeight(plan, context, verdict, progress) is { } lighter)
                    verdict = lighter;

                progress?.Report(new SwarmEvent.Gated(verdict.Report, round));
                Record("Gate", null, CodegenUsage.None, verdict.Report);

                // THE LADDER FIRST, ALWAYS. It is deterministic and free; a critic costs a model call. A
                // unit that does not compile is never shown to one.
                var findings = verdict.Report.Findings;
                GauntletResult? review = null;

                if (verdict.Passed)
                {
                    if (_gauntlet is null || verdict.Unit is null)
                        return Done(SwarmOutcome.Delivered, plan, origin, context, verdict, usage, note: note, summary:
                            $"Delivered: {plan.Tasks.Count} task(s), {verdict.Report.RungsCleared} rung(s) cleared.");

                    var subject = await GauntletSubjects.BuildAsync(
                        AuthoredUnitPreview.Create(verdict.Unit), context.Files, verdict.Report, _rasterizer, ct)
                        .ConfigureAwait(false);

                    // The bar the planner wrote, unless the caller supplied a stronger one. A rubric
                    // written from the brief at plan time beats one invented after the fact by whoever is
                    // now defending what got built.
                    var bar = request.Bar.Rubric.Count > 0 || request.Bar.HasImages
                        ? request.Bar
                        : ReferenceBar.FromRubric(plan.Rubric);

                    review = await _gauntlet.RunAsync(
                        subject, bar, request.Budget.MaxParallel, progress: null, ct).ConfigureAwait(false);

                    // A SKIPPED REVIEW IS NOT AN APPROVAL. The pass was skipped because the artifact has
                    // not changed since the last one — so the last one's findings are still true, and
                    // reading "no findings this time" as "the critics are happy" would let a repair that
                    // changed nothing arrive as a success. Carrying them forward instead lets the stall
                    // detector do its job: nothing moved, so the run ends and says so.
                    if (review.Skipped && lastReview is { } standing) review = standing;
                    else if (!review.Skipped) lastReview = review;

                    progress?.Report(new SwarmEvent.Reviewed(review, round));
                    Record("Gauntlet", null, CodegenUsage.None, verdict.Report);

                    if (review.Findings.Count == 0)
                        return Done(SwarmOutcome.Delivered, plan, origin, context, verdict, usage, note: note, summary:
                            $"Delivered: {plan.Tasks.Count} task(s), {verdict.Report.RungsCleared} rung(s) "
                            + $"cleared, {review.Summary}.");

                    findings = review.Findings;
                }

                // Ground gained: rungs cleared, then findings removed at the same height — and a critic's
                // findings count, or a run could clear the ladder and then circle a picture forever.
                var height = LadderScore.HeightOf(verdict.Report) - (review?.Findings.Count ?? 0);
                if (height > best) { best = height; stalled = 0; } else { stalled++; }

                if (stalled >= request.Budget.StallLimit)
                    return Done(SwarmOutcome.Stalled, plan, origin, context, verdict, usage, note:  note, summary:
                        $"Stopped after {round} repair round(s): the last {request.Budget.StallLimit} bought no "
                        + "further ground. Read the diagnostics and say what to change — repeating the same "
                        + "round will not.");

                // Out of rounds with a unit that builds and was only criticised: that is delivered with
                // notes, not a failure. Saying otherwise would send a user to the diagnostics list to look
                // for an error that is not there.
                if (round == request.Budget.MaxRounds)
                {
                    if (verdict.Passed)
                        return Done(SwarmOutcome.Delivered, plan, origin, context, verdict, usage, note: note, summary:
                            $"Delivered with {findings.Count} open review note(s) — the repair budget ran out "
                            + "before they were addressed. " + (review?.Summary ?? string.Empty));

                    break;
                }

                var targets = RepairTargets(
                    verdict.Passed ? new VerificationReport([]) : verdict.Report, plan, context, findings);

                if (targets.Count == 0)
                    return Done(SwarmOutcome.Stalled, plan, origin, context, verdict, usage, note:  note, summary:
                        "The unit failed verification and no file could be identified as the cause.");

                await FanOutAsync(
                    targets,
                    task => BuildOneAsync(task, plan, context, request, isRepair: true, findings, events, progress, ct),
                    request.Budget.MaxParallel,
                    ct,
                    landed: (task, result) =>
                {
                    usage = usage.Add(result.Usage);

                    // Same rule on the way back: a repair that failed is a file that did not improve,
                    // and the next round — or the budget — decides what that is worth. Ending the run
                    // here would discard every OTHER repair in the same wave.
                    if (result.Error is { Length: > 0 } failedRepair)
                    {
                        providerError = failedRepair;
                        progress?.Report(new SwarmEvent.TaskFinished(
                            task, false, result.Usage, failedRepair, context.Files));
                        return;
                    }

                    var wrote = context.Accept(task, result.Files);
                    progress?.Report(new SwarmEvent.TaskFinished(
                        task, wrote, result.Usage, wrote ? null : "returned no file", context.Files));
                }).ConfigureAwait(false);
            }

            return Done(SwarmOutcome.BudgetExhausted, plan, origin, context, verdict, usage, note:  note, summary:
                $"Stopped at the {request.Budget.MaxRounds}-round repair budget. "
                + $"Furthest it got: {(verdict?.Compile?.Success == true ? "it compiles" : "it does not compile")}.");
        }
        catch (OperationCanceledException)
        {
            // STOP MUST NOT ALSO MEAN DISCARD, and it did: nothing produced SwarmOutcome.Cancelled, so
            // pressing Stop threw out of the run and took every file the builders had already written
            // with it. On this provider that is minutes of thinking and real money for nothing — and
            // the enum member has said "whatever was built is kept" since the day it was written.
            var stopped = Done(
                SwarmOutcome.Cancelled, plan, origin, context, verdict, usage, note: note, summary:
                context.Files.Count == 0
                    ? "Stopped before anything was written."
                    : $"Stopped. {context.Files.Count} file(s) kept: "
                      + string.Join(", ", context.Files.Select(f => f.Name)) + ".");

            progress?.Report(new SwarmEvent.Finished(stopped.Outcome, stopped.Summary));
            return stopped;
        }
    }

    // ── the planner ─────────────────────────────────────────────────────────────────────────────

    private sealed record Planned(
        BuildPlan Plan, PlanOrigin Origin, CodegenUsage Usage, string? Error, string? Asked = null);

    /// <summary>
    /// Asks for a plan, and accepts a worse one rather than failing.
    ///
    /// <para>One repair attempt when the JSON does not parse — models mis-close a brace and fix it when
    /// told — and then the single-file plan, which is exactly what the single-conversation path does.
    /// <b>A mumbling planner must never cost a user their build.</b></para>
    /// </summary>
    private async Task<Planned> PlanAsync(
        SwarmRequest request, IProgress<CodegenEvent>? events, CancellationToken ct)
    {
        var usage = CodegenUsage.None;
        var messages = request.Thread is { Count: > 0 } thread
            ? [.. thread]
            : new List<CodegenMessage> { new(CodegenRole.User, request.Brief) };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (response, reported) = await CodegenStream.DrainAsync(
                _client,
                new StrategyCodegenRequest(
                    request.SharedContext,
                    messages,
                    SwarmPrompts.Planner(request.Kind, request.Budget.MaxTasks)),
                ThinkingOnly(events),
                ct).ConfigureAwait(false);

            usage = usage.Add(reported);

            if (!response.Success)
                return new Planned(
                    BuildPlan.Single(request.Brief, request.Kind), PlanOrigin.ProviderFailed, usage,
                    response.Error ?? "The provider returned nothing.");

            if (BuildPlanReader.Read(response.RawText, request.Kind) is { } plan)
                return new Planned(Trim(plan, request.Budget.MaxTasks), PlanOrigin.Planned, usage, null);

            // NO PLAN. What happens next depends on what came back instead, and the distinction is the
            // whole of the interview.
            //
            // PROSE is the model talking to the user — a question, or a specification awaiting
            // approval. It is not a failure to follow instructions, and it is exactly what the single
            // conversation has always treated as a turn that waits: no code means the model wants
            // something. Building anyway would spend the budget on a brief the model has just said it
            // does not have, and would burn the one turn in which the user could have corrected it.
            //
            // CODE means the model skipped planning and wrote the unit instead. That is a formatting
            // failure rather than a question, so it earns one reminder and then the single-file plan —
            // which is the shape it was trying to produce anyway.
            var wroteCode = CodegenCodeExtractor.ExtractFiles(response.RawText).Count > 0;

            if (!wroteCode && request.MayAsk)
                return new Planned(
                    BuildPlan.Single(request.Brief, request.Kind), PlanOrigin.Asked, usage, null,
                    response.RawText);

            logger?.LogInformation(
                "Swarm planner returned no plan on attempt {Attempt} (code: {Code}).", attempt, wroteCode);

            if (attempt == 1)
            {
                messages.Add(new CodegenMessage(CodegenRole.Assistant, response.RawText ?? string.Empty));
                messages.Add(new CodegenMessage(
                    CodegenRole.User,
                    "That did not parse. Return ONE ```json block, nothing before or after it, exactly "
                    + "the shape you were given. No commentary."));
            }
        }

        return new Planned(
            BuildPlan.Single(request.Brief, request.Kind), PlanOrigin.Unparsed, usage, null,
            messages.LastOrDefault(m => m.Role == CodegenRole.Assistant)?.Content);
    }

    /// <summary>
    /// Cuts a plan down to the task budget, keeping milestone order.
    ///
    /// <para>Enforced rather than trusted: the ceiling is in the planner's prompt, and a prompt is where
    /// a limit is requested, not where it holds. A plan of thirty tasks is thirty model calls the user
    /// did not agree to.</para>
    /// </summary>
    private static BuildPlan Trim(BuildPlan plan, int maxTasks)
    {
        if (plan.Tasks.Count <= maxTasks) return plan;

        var kept = new List<Milestone>();
        var left = maxTasks;

        foreach (var milestone in plan.Milestones)
        {
            if (left <= 0) break;
            var tasks = milestone.Tasks.Take(left).ToArray();
            left -= tasks.Length;
            if (tasks.Length > 0) kept.Add(milestone with { Tasks = tasks });
        }

        return plan with { Milestones = kept };
    }

    // ── one builder turn ────────────────────────────────────────────────────────────────────────

    private sealed record TaskResult(IReadOnlyList<StrategyFile> Files, CodegenUsage Usage, string? Error);

    private async Task<TaskResult> BuildOneAsync(
        BuildTask task,
        BuildPlan plan,
        SwarmContext context,
        SwarmRequest request,
        bool isRepair,
        IReadOnlyList<VerificationFinding> findings,
        IProgress<CodegenEvent>? events,
        IProgress<SwarmEvent>? progress,
        CancellationToken ct)
    {
        // A REPAIR FOR A FILE THAT DOES NOT EXIST IS NOT A REPAIR, and telling the model otherwise is
        // asking it to do something impossible.
        //
        // The missing-file routing rule is what surfaced this: a builder that produced nothing is now
        // correctly targeted every round, and then handed the Fixer prompt — "repair ONE file", "change
        // as little as possible", "the diagnostics name what is wrong" — with no file attached and a
        // diagnostic (no hostable class) that names none. Measured on the opening-range brief: the
        // kernel was asked four times, spent between 76,000 and 115,000 output tokens each time, and
        // never produced a line. It was being asked to minimally edit something it could not see.
        //
        // So the ROUND is a repair and the TURN is a build: the builder's prompt, the builder's
        // context, and a task board that says "building" rather than "repairing", because that is what
        // is actually happening.
        var writingFresh = !task.OwnsAllFiles && context.File(task.OwnedFile) is null;
        var repairing = isRepair && !writingFresh;

        progress?.Report(new SwarmEvent.TaskStarted(task, repairing));

        var instruction = repairing
            ? SwarmPrompts.Fixer(task, plan.Contract)
            : SwarmPrompts.Builder(task, plan.Contract);

        var message = repairing
            ? context.ComposeRepair(task, findings)
            : context.ComposeBuild(task, plan);

        // THE HEARTBEAT. Counted per task and reported as numbers, so a fan-out has something moving
        // in every row — without which a builder thinking for five minutes looks exactly like a
        // provider that has stopped answering, which is what a user actually saw.
        var thinking = 0;
        var written = 0;
        var billed = CodegenUsage.None;

        var beat = new Progress<CodegenEvent>(evt =>
        {
            switch (evt)
            {
                case CodegenEvent.ReasoningDelta reasoning:
                    thinking += reasoning.Text.Length;
                    break;

                case CodegenEvent.TextDelta text:
                    written += text.Text.Length;
                    break;

                case CodegenEvent.UsageUpdate update:
                    billed = update.Usage;
                    break;

                default:
                    return;
            }

            progress?.Report(new SwarmEvent.TaskProgress(task, thinking, written, billed));

            // Sequential ⇒ the pane also shows what the model is writing, exactly as one conversation
            // does. Parallel ⇒ the counts above are the whole signal, because four builders' deltas in
            // one bubble are noise.
            if (request.Budget.MaxParallel <= 1) events?.Report(evt);
            else if (evt is CodegenEvent.UsageUpdate) events?.Report(evt);
        });

        var (response, reported) = await CodegenStream.DrainAsync(
            _client,
            new StrategyCodegenRequest(
                request.SharedContext,
                [new CodegenMessage(CodegenRole.User, message)],
                instruction),
            beat,
            ct).ConfigureAwait(false);

        if (!response.Success)
            return new TaskResult([], reported, response.Error ?? "The provider returned nothing.");

        // Prose in a fence is not code whoever wrote it. The single-conversation path has refused it
        // since the day it cost three generations — the fix loop reads CS1003 and tries to FIX THE
        // PROSE — and a second path that compiled it happily is exactly the defect this area keeps
        // producing.
        var files = response.FileList
            .Where(f => CodegenCodeExtractor.LooksLikeCode(f.Content))
            .ToArray();

        Record(task.Kind.ToString(), task.Id, reported, files: files.Length);
        return new TaskResult(files, reported, null);
    }

    // ── a file nobody owns, that nobody can fix ─────────────────────────────────────────────────

    /// <summary>
    /// Drops carried-in files that no task owns when they are what is failing, and only when the
    /// compiler agrees the unit is better without them.
    ///
    /// <para><b>This is the hole a real session fell into.</b> The pane opened on a starter that did not
    /// compile, handed it to the run as an existing file, and no task owned it. Every repair round
    /// concluded the same thing — "omit Strategy.cs, the other four files are correct" — and every round
    /// was structurally incapable of doing it: repairs are routed by owner, an orphan has none, and the
    /// fixer that got the findings anyway is told in its own prompt not to touch files that are not its.
    /// Three rounds, the full budget, and four perfectly good files thrown away with the scaffold.</para>
    ///
    /// <para><b>The compiler decides, not a model.</b> Removing a file is the only destructive act in
    /// this pipeline, so it happens on evidence: the reduced set is compiled, and it is kept only if it
    /// climbs strictly higher than the set that included the file. A file carrying the only hostable
    /// class takes the unit down with it and is therefore kept — which is the case that makes "just drop
    /// what fails" the wrong rule and this the right one.</para>
    /// </summary>
    private GateResult? ShedDeadWeight(
        BuildPlan plan, SwarmContext context, GateResult verdict, IProgress<SwarmEvent>? progress)
    {
        var orphans = context.Orphans(plan);
        if (orphans.Count == 0) return null;

        var blamed = verdict.Report.Findings
            .Select(f => f.File)
            .Where(f => f is { Length: > 0 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dead = orphans.Where(f => blamed.Contains(f.Name)).Select(f => f.Name).ToArray();
        if (dead.Length == 0) return null;

        var reduced = context.Files
            .Where(f => !dead.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (reduced.Length == 0) return null;

        // RUNGS, not findings. Findings always fall when a failing file is deleted — that is what
        // deleting a failing file does — so scoring on them would license dropping anything that
        // happened to be broken. Clearing a rung that was not cleared before is the only evidence that
        // the file was in the way rather than merely unfinished, and it is the claim the notice makes.
        var retry = _gate.Run(reduced);
        if (retry.Compile?.Success != true) return null;
        if (retry.Report.RungsCleared <= verdict.Report.RungsCleared) return null;

        foreach (var name in dead) context.Remove(name);

        progress?.Report(new SwarmEvent.Dropped(
            dead,
            $"{(dead.Length == 1 ? "It was" : "They were")} already in the editor, no task in this plan "
            + "wrote or owns it, and the unit compiles without it."));

        Record("Shed", null, CodegenUsage.None, retry.Report);
        return retry;
    }

    // ── routing a repair to whoever owns the broken file ────────────────────────────────────────

    /// <summary>
    /// Which builders are asked to repair, from what the ladder said.
    ///
    /// <para>By file wherever the finding names one, which the compiler always does. A draw or replay
    /// failure names none — it is about behaviour, not a line — so it goes to the tasks whose job that
    /// behaviour is: the panels for a picture, the signal for anything else. Sending every finding to
    /// whoever wrote the hostable class is what makes one builder repair files it never saw.</para>
    /// </summary>
    private static IReadOnlyList<BuildTask> RepairTargets(
        VerificationReport report, BuildPlan plan, SwarmContext context,
        IReadOnlyList<VerificationFinding>? extra = null)
    {
        var tasks = plan.Tasks;
        if (tasks.Count == 0) return [];

        IReadOnlyList<VerificationFinding> all = extra is { Count: > 0 } ? extra : report.Findings;

        // A TASK THAT NEVER WROTE ITS FILE IS ALWAYS A TARGET, whatever the findings say.
        //
        // Routing is by file name, and a file that does not exist is named by no diagnostic — so a
        // builder that returned nothing was never asked again. Measured: the kernel task spent
        // sixty-six minutes reasoning and produced no answer, and the gate then reported "no public
        // class implementing IStrategyKernel" (which names no file) beside two ordinary compile errors
        // (which do). The two named files were repaired; the missing unit was not, because nothing
        // pointed at it. The run finished with four correct helpers and no entry point.
        var missing = tasks
            .Where(t => !t.OwnsAllFiles && context.File(t.OwnedFile) is null)
            .ToArray();

        var named = all
            .Select(f => f.File)
            .Where(f => f is { Length: > 0 })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => tasks.FirstOrDefault(t =>
                string.Equals(t.OwnedFile, file, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        if (named.Length > 0 || missing.Length > 0)
            return [.. missing.Concat(named).DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase)];

        // A picture failure names no file — it is about behaviour, not a line — so it belongs to
        // whoever paints. Critic codes are prefixed by critic id, so the picture panel's two are
        // recognised the same way the draw probe's are.
        var drawing = all.Any(f =>
            f.Code.StartsWith("draw.", StringComparison.Ordinal)
            || f.Code.StartsWith(Critics.Picture + ".", StringComparison.Ordinal)
            || f.Code.StartsWith(Critics.ChartCraft + ".", StringComparison.Ordinal));

        // THE HOSTABLE CLASS PAINTS TOO, and leaving it out made a drawing fault unfixable.
        //
        // Every unit has a Draw on the hostable class — for many that is the ONLY Draw, and even with
        // helper panels it is what declares the layout, sizes the regions and titles them. Sending a
        // picture finding to the Panel tasks alone means the one file that could resolve it is never
        // asked. Measured on the opening-range brief: three rounds against the same
        // "'130' and '130' are drawn on top of each other", the two panel builders rewriting
        // themselves each time, and the panel named in the finding belonging to the kernel.
        if (drawing && tasks.Any(t => t.Kind is TaskKind.Panel or TaskKind.Signal))
            return [.. tasks.Where(t => t.Kind is TaskKind.Panel or TaskKind.Signal)];

        return [tasks.FirstOrDefault(t => t.Kind == TaskKind.Signal) ?? tasks[0]];
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tasks grouped into waves that can run together: everything whose dependencies are already
    /// satisfied.
    ///
    /// <para>A dependency naming something unknown, or a cycle, does not stall the run — the remaining
    /// tasks are emitted as one last layer. A plan written by a model will eventually contain a cycle,
    /// and refusing to build because of it would spend the planner's turn for nothing.</para>
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<BuildTask>> Layers(IReadOnlyList<BuildTask> tasks)
    {
        var remaining = tasks.ToList();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layers = new List<IReadOnlyList<BuildTask>>();

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(t => t.DependsOn.All(d =>
                    done.Contains(d) || remaining.All(r => !string.Equals(r.Id, d, StringComparison.OrdinalIgnoreCase))))
                .ToArray();

            if (ready.Length == 0)
            {
                layers.Add([.. remaining]);
                break;
            }

            layers.Add(ready);
            foreach (var task in ready)
            {
                done.Add(task.Id);
                remaining.Remove(task);
            }
        }

        return layers;
    }

    /// <summary>
    /// Runs a layer, at most <paramref name="maxParallel"/> at a time.
    ///
    /// <para>Bounded rather than unbounded, and the bound is not politeness: a provider answers a burst
    /// of eight with 429s, and an agent-CLI provider answers it by starting eight processes.</para>
    /// </summary>
    /// <param name="landed">
    /// Run as each task finishes, <b>serialised</b>, so a wave's results are taken one at a time.
    ///
    /// <para>This is where a finished file becomes visible, and it used to be the end of the wave. A
    /// four-way fan-out on a slow model measured 32:23 to 01:11:59 wall-clock, and all four files
    /// appeared at 01:11:59 — the first was finished and invisible for the better part of forty
    /// minutes. "The code appears as each task writes it" was true of the event and false of when it
    /// was raised.</para>
    ///
    /// <para>Serialised because the callback merges into the shared <see cref="SwarmContext"/> and adds
    /// to the run's usage, neither of which is safe from several threads. The lock is held only across
    /// the merge, never across a model call.</para>
    /// </param>
    private static async Task<IReadOnlyList<(BuildTask Task, T Result)>> FanOutAsync<T>(
        IReadOnlyList<BuildTask> tasks,
        Func<BuildTask, Task<T>> run,
        int maxParallel,
        CancellationToken ct,
        Action<BuildTask, T>? landed = null)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, maxParallel));
        var pen = new Lock();

        var running = tasks.Select(async task =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await run(task).ConfigureAwait(false);
                if (landed is not null) lock (pen) landed(task, result);
                return (task, result);
            }
            finally
            {
                slots.Release();
            }
        });

        return await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>
    /// A sink that passes the planner's THINKING and its token movement, and drops its text.
    ///
    /// <para>The planner's text is a JSON object. Streamed into the transcript it renders as a fenced
    /// block, which the pane summarises as "code written to the workbench" — a sentence that is not
    /// true and stays on screen for the whole build. Seen exactly that way in a real run.</para>
    ///
    /// <para>The thinking still flows, because on a reasoning model that IS the interesting part of
    /// planning and it is what tells a user the turn is alive. What the planner decided is already said
    /// properly by the plan card underneath.</para>
    /// </summary>
    private static IProgress<CodegenEvent>? ThinkingOnly(IProgress<CodegenEvent>? inner) =>
        inner is null ? null : new Progress<CodegenEvent>(evt =>
        {
            if (evt is not CodegenEvent.TextDelta) inner.Report(evt);
        });

    /// <summary>
    /// An event sink that passes token movement and drops everything else.
    ///
    /// <para>Usage still flows from a parallel builder because it is a number and numbers sum: a
    /// counter that keeps rising is how a user tells a working fan-out from a hung one. Text does not
    /// sum, which is why it stops here.</para>
    /// </summary>
    private static IProgress<CodegenEvent>? UsageOnly(IProgress<CodegenEvent>? inner) =>
        inner is null ? null : new Progress<CodegenEvent>(evt =>
        {
            if (evt is CodegenEvent.UsageUpdate) inner.Report(evt);
        });

    /// <summary>A logging fault is not worth a run: the user came for a strategy.</summary>
    private void Record(string role, string? taskId, CodegenUsage usage, VerificationReport? report = null, int files = 0)
    {
        try
        {
            trajectory?.Append(role, taskId, report ?? new VerificationReport([]), usage, files);
        }
        catch (Exception)
        {
            // Deliberately swallowed. TrajectoryLog already tolerates a malformed line on read.
        }
    }

    private SwarmRun Done(
        SwarmOutcome outcome, BuildPlan plan, PlanOrigin origin, SwarmContext context,
        GateResult? verdict, CodegenUsage usage, string summary, string? note = null) =>
        new(outcome, plan, origin, context.Files, verdict?.Report, verdict?.Compile, usage, summary,
            PlannerNote: note);

    private SwarmRun Failed(
        BuildPlan plan, PlanOrigin origin, SwarmContext context, CodegenUsage usage, string? error) =>
        new(SwarmOutcome.ProviderFailed, plan, origin, context.Files, null, null, usage,
            $"Provider failed: {error}", error);
}
