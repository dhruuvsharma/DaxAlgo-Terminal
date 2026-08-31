using System.Text;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What one turn of the conversation produced.</summary>
public enum BuildTurnKind
{
    /// <summary>The model wrote files and they compiled (possibly after auto-fixes).</summary>
    Compiled,

    /// <summary>The model wrote files that still don't compile after the auto-fix bound.</summary>
    CompileFailed,

    /// <summary>The model replied without code — it is asking the user something, or explaining.</summary>
    Question,

    /// <summary>The provider itself failed (no key, no CLI, timeout). Retrying won't fix it.</summary>
    ProviderError,
}

/// <summary>
/// The result of one user turn: what the model said (for the chat), the files it produced, the compile
/// result, and what the turn cost.
/// </summary>
/// <param name="Kind">How the turn ended — the UI branches on this, not on booleans.</param>
/// <param name="AssistantText">The model's reply verbatim (prose + fences) — what the chat shows.</param>
/// <param name="Files">The files the model emitted this turn (empty for a question).</param>
/// <param name="Compile">The last compile result, or null when nothing was compiled.</param>
/// <param name="Error">Provider-level error text, when <see cref="BuildTurnKind.ProviderError"/>.</param>
/// <param name="Generations">How many times the model was called this turn (1 + auto-fix retries).</param>
/// <param name="Usage">Tokens billed this turn, summed over its generations.</param>
public sealed record StrategyBuildTurn(
    BuildTurnKind Kind,
    string AssistantText,
    IReadOnlyList<StrategyFile> Files,
    StrategyCompileResult? Compile,
    string? Error,
    int Generations,
    CodegenUsage Usage)
{
    public bool Success => Kind == BuildTurnKind.Compiled;
}

/// <summary>
/// A running conversation with one provider about one strategy — the object behind the builder's chat.
/// <para>
/// It owns the message list, so every turn (the first instruction, a follow-up like "make the stop
/// tighter", an answer to the model's own question, the compiler's errors) lands in the SAME thread and
/// the model keeps its context. Inside a turn it runs the build loop: generate → compile through the
/// shared <see cref="IStrategyCompiler"/> (so the policy scan + version gates apply) → feed any errors
/// back → retry, bounded. It never registers anything: the caller decides what to do with a compiled
/// result, through the same scan + consent gate as a plugin.
/// </para>
/// <para>
/// A reply with no code is not an error — it is the model asking a clarifying question. The session
/// returns it as <see cref="BuildTurnKind.Question"/> and waits for the user's next
/// <see cref="SendAsync"/>, which is what makes the builder conversational rather than one-shot.
/// </para>
/// </summary>
public sealed class StrategyBuildSession
{
    private readonly IStrategyCompiler _compiler;
    private readonly ILogger? _logger;
    private readonly List<CodegenMessage> _messages = [];

    /// <param name="history">A thread to resume — the conversation as it stood when the app last closed.
    /// Replaying it is what makes a restored chat more than a transcript: the model can still answer
    /// "now tighten the stop" because it remembers what it wrote.</param>
    /// <param name="priorUsage">Tokens already spent on this thread, so the counter continues rather than
    /// restarting at zero.</param>
    /// <param name="profile">The build-effort profile: overrides <paramref name="maxFixAttempts"/>, caps
    /// the skill budget, and turns on the self-review / backtest-smoke passes. Null ⇒ the defaults.</param>
    internal StrategyBuildSession(
        IStrategyCompiler compiler,
        IStrategyCodegenClient provider,
        string systemContext,
        string strategyId,
        string displayName,
        int maxFixAttempts,
        ILogger? logger = null,
        IReadOnlyList<CodegenMessage>? history = null,
        CodegenUsage? priorUsage = null,
        StrategySkillLibrary? skills = null,
        StrategyBuildProfile? profile = null,
        AuthoringKind kind = AuthoringKind.Strategy,
        StrategyContextPack? pack = null)
    {
        _compiler = compiler;
        _logger = logger;
        _skills = skills;
        _pack = pack;
        Provider = provider;
        Kind = kind;
        // The kind block joins the base pack, so it survives the skill recomposition below and is part
        // of the prompt from the first turn — a session cannot change which of the two it is writing.
        _systemContext = systemContext;
        BasePack = AuthoringKindBrief.Compose(systemContext, kind);
        SystemContext = BasePack;
        StrategyId = strategyId;
        DisplayName = displayName;
        Profile = profile;
        MaxFixAttempts = Math.Max(0, profile?.MaxFixAttempts ?? maxFixAttempts);

        if (history is { Count: > 0 }) _messages.AddRange(history);
        TotalUsage = priorUsage ?? CodegenUsage.None;

        // A resumed conversation already has a brief — resolve its skills now, from the same text, so the
        // system prompt (and therefore the cached prefix) is identical to the one it had before.
        if (_messages.Count > 0) ResolveSkills(BriefFrom(_messages));
    }

    private readonly StrategySkillLibrary? _skills;

    /// <summary>Whether the brief has already shaped the prompt. Separate from
    /// <see cref="LoadedSkills"/> being non-empty: a brief that warrants no pack still fixes the
    /// exemplar, and re-running that on a later turn would move the cached prefix.</summary>
    private bool _resolved;

    /// <summary>The shared pack as handed in, kept so the base pack can be recomposed once the brief
    /// names which exemplar to show.</summary>
    private readonly string _systemContext;

    /// <summary>
    /// The two halves of the shared pack, when the caller supplied them, so the generated SDK surface
    /// can be cut to the brief once there is one.
    ///
    /// <para>Optional because a caller that hands over a pre-joined string — the CLI, and every test
    /// that passes a literal — keeps exactly today's behaviour. Without the halves there is no way to
    /// tell where the surface ends and the conventions begin, and guessing at a separator inside a
    /// string is how a filter starts cutting the wrong document.</para>
    /// </summary>
    private readonly StrategyContextPack? _pack;

    /// <summary>The characters of SDK-surface library detail this session kept. Zero when nothing was
    /// cut. Recorded so a caller can report the saving rather than assert it in the abstract.</summary>
    public int SurfaceCharactersSaved { get; private set; }

    public IStrategyCodegenClient Provider { get; }

    /// <summary>The SDK contract, before any domain packs are added.</summary>
    /// <summary>Which of the two contracts this session is writing. Fixed at creation: it shapes the
    /// system prompt, and a prompt that changed mid-thread would leave the model's own earlier replies
    /// disagreeing with its instructions.</summary>
    public AuthoringKind Kind { get; }

    public string BasePack { get; private set; }

    /// <summary>The system prompt actually sent: the base pack plus the domain packs this strategy needs.
    /// Fixed for the life of the session — it is the cached prefix of every request in the thread, so
    /// changing it mid-conversation would throw the prompt cache away on every turn.</summary>
    public string SystemContext { get; private set; }

    /// <summary>The domain packs loaded for this strategy (empty until the first turn).</summary>
    public IReadOnlyList<StrategySkill> LoadedSkills { get; private set; } = [];
    public string StrategyId { get; }
    public string DisplayName { get; }
    public int MaxFixAttempts { get; }

    /// <summary>The build-effort profile this session runs under, or null for the defaults.</summary>
    public StrategyBuildProfile? Profile { get; }

    /// <summary>The whole thread so far — user turns, model replies, and the auto-fix prompts.</summary>
    public IReadOnlyList<CodegenMessage> Transcript => _messages;

    /// <summary>The most recent files the model produced (across turns — a follow-up rewrites them).</summary>
    public IReadOnlyList<StrategyFile> Files { get; private set; } = [];

    /// <summary>Tokens billed across the whole session.</summary>
    public CodegenUsage TotalUsage { get; private set; } = CodegenUsage.None;

    /// <summary>
    /// Send one user turn and run the build loop over the reply.
    /// </summary>
    /// <param name="userMessage">What the user typed — an instruction, a follow-up, or an answer to the
    /// model's question.</param>
    /// <param name="activity">Progress for the UI's activity strip ("Asking Claude…", "Compiling 3
    /// file(s)…", "Fixing 2 error(s)…"). Reported on the calling context.</param>
    /// <param name="events">Streamed events — text deltas as the model writes, usage as the provider
    /// reports it. A provider that can't stream reports nothing here and the turn still returns normally,
    /// so the caller never branches on it.</param>
    public async Task<StrategyBuildTurn> SendAsync(
        string userMessage,
        IProgress<string>? activity = null,
        CancellationToken ct = default,
        IProgress<CodegenEvent>? events = null)
    {
        // First turn: pick the domain packs this strategy needs, from the brief. Once only — the system
        // prompt is the cached prefix for every later turn, and re-picking would invalidate it each time.
        if (_messages.Count == 0 && ResolveSkills(userMessage) is { Count: > 0 } loaded)
            activity?.Report($"Loaded reference: {string.Join(", ", loaded.Select(s => s.Name))}.");

        _messages.Add(new CodegenMessage(CodegenRole.User, userMessage));

        // One generation, plus MaxFixAttempts more that each get the compiler's errors fed back.
        var totalGenerations = MaxFixAttempts + 1;
        var usage = CodegenUsage.None;
        StrategyCompileResult? lastCompile = null;
        IReadOnlyList<StrategyFile> lastFiles = [];
        var lastText = string.Empty;

        for (var generation = 1; generation <= totalGenerations; generation++)
        {
            ct.ThrowIfCancellationRequested();

            activity?.Report(generation == 1
                ? $"Asking {Provider.DisplayName}…"
                : $"Asking {Provider.DisplayName} to fix {Count(lastCompile)} error(s)…");

            // Stream it. A provider that can't yields one Completed and nothing else, so this is the only
            // path — there is no non-streaming branch to keep in step.
            var (response, reported) = await GenerateOnceAsync(events, ct).ConfigureAwait(false);
            usage = usage.Add(reported);

            if (!response.Success)
            {
                // A provider-level failure (auth, timeout, no CLI) — not a compile error. Stop; retrying
                // won't fix a missing key.
                _logger?.LogWarning("Codegen provider {Provider} failed: {Error}", Provider.ProviderId, response.Error);
                activity?.Report($"{Provider.DisplayName} failed.");
                return new StrategyBuildTurn(
                    BuildTurnKind.ProviderError, string.Empty, lastFiles, lastCompile,
                    response.Error ?? "The provider returned nothing.", generation - 1, usage);
            }

            // Record the model's turn verbatim so the transcript reads naturally and the next call has context.
            lastText = response.RawText ?? string.Empty;
            _messages.Add(new CodegenMessage(CodegenRole.Assistant, lastText));

            if (!response.HasFiles)
            {
                // Prose, no code: the model is asking something back. Hand it to the user and stop —
                // auto-fixing a question would be nonsense.
                activity?.Report($"{Provider.DisplayName} has a question.");
                return new StrategyBuildTurn(
                    BuildTurnKind.Question, lastText, lastFiles, lastCompile, null, generation, usage);
            }

            lastFiles = response.FileList;
            Files = lastFiles;

            // Prose in a code fence is not a compile error, and telling the model it is makes things
            // worse: it reads CS1003 and tries to FIX THE PROSE. Seen live — three generations spent
            // that way, the last of them a paragraph explaining that the file contained no program.
            // Named for what it is instead, like the wrong-kind mismatch beside it.
            if (lastFiles.FirstOrDefault(f => !CodegenCodeExtractor.LooksLikeCode(f.Content)) is { } prose)
            {
                var complaint =
                    $"'{prose.Name}' contains prose, not C#. Do not explain and do not apologise: return "
                    + "the COMPLETE file, C# only, in a ```csharp fence with its `// file:` header.";

                _logger?.LogInformation(
                    "AI-authored unit {Id} returned prose instead of code on generation {Generation}.",
                    StrategyId, generation);
                activity?.Report($"{Provider.DisplayName} returned prose, not code.");

                if (generation < totalGenerations)
                {
                    _messages.Add(new CodegenMessage(CodegenRole.User, complaint));
                    continue;
                }

                return new StrategyBuildTurn(
                    BuildTurnKind.CompileFailed, lastText, lastFiles, lastCompile, complaint, generation, usage);
            }

            activity?.Report($"Compiling {lastFiles.Count} file(s)…");
            var compile = _compiler.Compile(new StrategyScript(StrategyId, DisplayName, lastFiles));
            lastCompile = compile;

            // Compiled, but is it the thing the user asked for? A model handed a visualizer brief will
            // sometimes write a kernel anyway, and nothing downstream would notice: the compiler resolves
            // whatever is there and the ladder judges it on its own terms. Silently delivering a strategy
            // to somebody who ticked Visualizer is worse than the toggle not existing, so a mismatch is
            // treated exactly like a compiler error — same fix loop, same budget, same visible reason.
            if (compile.Success && Mismatch(compile) is { } mismatch)
            {
                _logger?.LogInformation(
                    "AI-authored unit {Id} compiled as the wrong kind on generation {Generation}: {Reason}",
                    StrategyId, generation, mismatch);
                activity?.Report(mismatch);

                if (generation < totalGenerations)
                {
                    _messages.Add(new CodegenMessage(CodegenRole.User, KindFixPrompt(mismatch)));
                    continue;
                }

                return new StrategyBuildTurn(
                    BuildTurnKind.CompileFailed, lastText, lastFiles, compile, mismatch, generation, usage);
            }

            if (compile.Success)
            {
                _logger?.LogInformation(
                    "AI-authored strategy {Id} compiled on generation {Generation}/{Total} ({Files} file(s))",
                    StrategyId, generation, totalGenerations, lastFiles.Count);
                activity?.Report($"Compiled {lastFiles.Count} file(s) cleanly.");

                var generations = generation;

                // Deep/Max effort: one extra generation critiquing the compiled strategy. A review that
                // doesn't compile is discarded — the last good files always win.
                if (Profile is { SelfReview: true })
                {
                    var review = await SelfReviewAsync(activity, events, ct).ConfigureAwait(false);
                    usage = usage.Add(review.Usage);
                    generations += review.Generations;
                    if (review.Adopted)
                    {
                        lastText = review.Text;
                        lastFiles = review.Files;
                        compile = review.Compile!;
                    }
                }

                // Deep/Max effort: catch the runtime throws a compiler can't. Advisory — a failure lands
                // as a warning diagnostic, never a block; the user still reviews before registering.
                if (Profile is { Verify: true })
                    compile = await SmokeAsync(compile, activity, ct).ConfigureAwait(false);

                return new StrategyBuildTurn(
                    BuildTurnKind.Compiled, lastText, lastFiles, compile, null, generations, usage);
            }

            if (generation < totalGenerations)
                _messages.Add(new CodegenMessage(CodegenRole.User, FixPrompt(compile)));
        }

        _logger?.LogWarning("AI-authored strategy {Id} did not compile after {Total} generation(s)",
            StrategyId, totalGenerations);
        activity?.Report($"Still {Count(lastCompile)} error(s) after {totalGenerations} attempt(s).");
        return new StrategyBuildTurn(
            BuildTurnKind.CompileFailed, lastText, lastFiles, lastCompile, null, totalGenerations, usage);
    }

    /// <summary>Push the user's hand-edits into the session, so the next turn shows the model the code
    /// that is actually in the editor rather than the version it last wrote.</summary>
    public void SyncEditedFiles(IReadOnlyList<StrategyFile> files) => Files = files;

    /// <summary>One generation against the current thread: streams deltas/usage to
    /// <paramref name="events"/>, banks the reported tokens into <see cref="TotalUsage"/>, and returns
    /// the assembled response — shared by the main loop and the self-review pass.</summary>
    private async Task<(StrategyCodegenResponse Response, CodegenUsage Reported)> GenerateOnceAsync(
        IProgress<CodegenEvent>? events, CancellationToken ct)
    {
        StrategyCodegenResponse? response = null;
        var generationUsage = CodegenUsage.None;

        await foreach (var evt in Provider
            .StreamAsync(new StrategyCodegenRequest(SystemContext, WireMessages()), ct)
            .ConfigureAwait(false))
        {
            switch (evt)
            {
                case CodegenEvent.TextDelta:
                    events?.Report(evt);
                    break;

                case CodegenEvent.UsageUpdate update:
                    // Absolute for THIS generation — replace it, then re-derive the running totals, so
                    // an auto-fix retry doesn't double-count the generations before it.
                    generationUsage = update.Usage;
                    events?.Report(evt);
                    break;

                case CodegenEvent.Completed completed:
                    response = completed.Response;
                    break;
            }
        }

        response ??= StrategyCodegenResponse.Fail($"{Provider.DisplayName} returned nothing.");
        var reported = response.Usage ?? generationUsage;
        TotalUsage = TotalUsage.Add(reported);
        return (response, reported);
    }

    /// <summary>What the self-review pass asks. Improved code comes back per the same output contract as
    /// any turn; a sound strategy earns a code-free sign-off, which is how the pass says "keep it".</summary>
    private const string SelfReviewPrompt =
        "The code compiles. Now review your own strategy as a skeptical senior quant: check the signal " +
        "logic for correctness (warm-up, division by zero, look-ahead bias, unit mistakes), the order " +
        "handling for unique idempotent client ids and a flatten in OnEndAsync, and the risk handling " +
        "for unbounded position growth. If you find real problems, return the COMPLETE corrected file " +
        "set (every file, each in its own fenced block with its `// file:` header). If the strategy is " +
        "sound as written, reply briefly WITHOUT any code blocks.";

    /// <summary>
    /// The self-review pass (Deep/Max build effort): one extra generation over the SAME thread asking
    /// the model to critique what it just wrote. Adopted only when the improved files compile; any other
    /// outcome — a prose sign-off, a broken rewrite, a provider hiccup — keeps the last good files. The
    /// pass can therefore only ever raise the floor, never lower it.
    /// </summary>
    private async Task<(bool Adopted, string Text, IReadOnlyList<StrategyFile> Files, StrategyCompileResult? Compile,
        CodegenUsage Usage, int Generations)> SelfReviewAsync(
        IProgress<string>? activity, IProgress<CodegenEvent>? events, CancellationToken ct)
    {
        activity?.Report("Self-review pass…");
        _messages.Add(new CodegenMessage(CodegenRole.User, SelfReviewPrompt));

        var (response, reported) = await GenerateOnceAsync(events, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            // Advisory: a provider hiccup must not fail a turn that already compiled. Drop the dangling
            // prompt so the thread keeps alternating user → assistant.
            _messages.RemoveAt(_messages.Count - 1);
            activity?.Report("Self-review unavailable — keeping the compiled version.");
            return (false, string.Empty, [], null, reported, 1);
        }

        _messages.Add(new CodegenMessage(CodegenRole.Assistant, response.RawText ?? string.Empty));

        if (!response.HasFiles)
        {
            activity?.Report("Self-review: nothing to change.");
            return (false, string.Empty, [], null, reported, 1);
        }

        activity?.Report($"Compiling {response.FileList.Count} self-reviewed file(s)…");
        var compile = _compiler.Compile(new StrategyScript(StrategyId, DisplayName, response.FileList));
        if (!compile.Success)
        {
            _logger?.LogInformation(
                "Self-review of {Id} did not compile — keeping the last good files", StrategyId);
            activity?.Report("The self-review didn't compile — keeping the last good version.");
            return (false, string.Empty, [], null, reported, 1);
        }

        Files = response.FileList;
        activity?.Report("Self-review improvements adopted.");
        return (true, response.RawText ?? string.Empty, response.FileList, compile, reported, 1);
    }

    /// <summary>
    /// The verification pass (Deep/Max build effort): instantiate the compiled unit, drive it through a
    /// short awkward series, and read back what it drew and what it did to its book.
    ///
    /// <para>This replaced a backtest smoke that ran forty-eight fabricated ticks past a stub clock and
    /// a stub router. It needed the engine-era registration type, so for a unit written against the
    /// contracts the guidance teaches it silently did nothing — and against those stubs a strategy could
    /// not fail in any way that mattered, because the only thing it could do was place an order into
    /// nothing.</para>
    ///
    /// <para>Findings are appended as warnings and the turn stays <see cref="BuildTurnKind.Compiled"/>:
    /// the ladder is advice at this point in the flow, not a gate. Making it a gate is a decision about
    /// the agent loop, not about the verifier.</para>
    /// </summary>
    private Task<StrategyCompileResult> SmokeAsync(
        StrategyCompileResult compile, IProgress<string>? activity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (compile.Unit is null) return Task.FromResult(compile);

        activity?.Report("Verifying: driving the compiled unit and reading back what it drew…");
        var report = AuthoredUnitVerifier.Verify(compile.Unit);

        if (report.Passed)
        {
            activity?.Report($"Verification passed — {report.RungsCleared} rung(s) cleared.");
            return Task.FromResult(compile);
        }

        _logger?.LogWarning(
            "Verification failed for {Id} at rung {Rung}: {Findings}",
            StrategyId,
            report.FailedAt,
            string.Join("; ", report.Findings.Select(f => f.Code)));

        foreach (var finding in report.Findings)
            activity?.Report($"Verification: {finding.Message}");

        return Task.FromResult(compile with
        {
            Diagnostics =
            [
                .. compile.Diagnostics,
                // Line 0 / column 0: a verification finding is about the unit's behaviour, not about a
                // position in the file, and pointing at line 1 would send a reader somewhere arbitrary.
                .. report.Findings.Select(finding => new StrategyDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    finding.Code,
                    finding.Remedy is null
                        ? finding.Message
                        : $"{finding.Message} {finding.Remedy}",
                    Line: 0,
                    Column: 0)),
            ],
        });
    }

    /// <summary>
    /// The prompt actually sent — and the reason a long session doesn't cost a fortune.
    /// <para>
    /// The naive thing is to replay the raw thread. But every time the model rewrites the files it emits
    /// the WHOLE set again, so after three rewrites the thread carries three full copies of code that has
    /// been superseded, and each turn re-sends all of them. Cost grows with the square of the work.
    /// </para>
    /// <para>
    /// So: <b>the files are state, not conversation.</b> Code is stripped out of the history (leaving what
    /// the model actually SAID, which is what a follow-up depends on), and exactly one copy — the current
    /// contents of the editor — rides along with the newest turn. The prompt then stays roughly flat:
    /// pack + prose + the code as it is right now.
    /// </para>
    /// </summary>
    internal IReadOnlyList<CodegenMessage> WireMessages()
    {
        var wire = new List<CodegenMessage>(_messages.Count);

        foreach (var message in _messages)
        {
            wire.Add(message.Role == CodegenRole.Assistant
                ? message with { Content = CodegenCodeExtractor.StripCode(message.Content) }
                : message);
        }

        // Attach the current file set to the last turn (which is always the user's — a generation is only
        // ever kicked off by a user message or a fix prompt).
        if (Files.Count > 0 && wire.Count > 0 && wire[^1].Role == CodegenRole.User)
            wire[^1] = wire[^1] with { Content = $"{wire[^1].Content}\n\n{CurrentFilesBlock()}" };

        return wire;
    }

    /// <summary>
    /// The system prompt this session sends for <paramref name="brief"/>: the shared pack, the kind
    /// block, the questions instruction, the brief-matched exemplar, and the domain packs the brief
    /// warrants.
    ///
    /// <para>Public because the <b>multi-agent path is a second driver of the same conversation</b> and
    /// used to compose nothing at all — it sent <c>StrategyContextPack.SystemPrompt</c> raw. Deep and
    /// Max effort are exactly the two that route through the agents, so the two efforts that buy the
    /// largest skill budget (5 and 8) were loading zero packs, were never told which of the two
    /// contracts they were writing, and were never taught the <c>questions</c> block whose replies that
    /// same path parses and renders as buttons. One method both drivers call is what keeps them from
    /// drifting apart again; <c>AgentSharedContextTests</c> asserts the agent path reaches it.</para>
    /// </summary>
    public string PrepareFor(string brief)
    {
        ResolveSkills(brief);
        return SystemContext;
    }

    /// <summary>
    /// The shared pack with its SDK surface cut to <paramref name="brief"/>, when the caller supplied
    /// the pack in halves; otherwise exactly what it handed over.
    ///
    /// <para>The surface is the bulk of the prompt and grows with the SDK, so this is where the
    /// prompt-size problem is actually addressed. The contract sections go through whole and only the
    /// two libraries are rationed — and even a rationed type keeps a one-line entry, so nothing the
    /// model might want becomes invisible to it.</para>
    /// </summary>
    private string SharedContextFor(string brief)
    {
        // The halves are only usable when they are the halves of the string actually handed over. A
        // caller that passed a different context — a test with a literal, a CLI with its own text —
        // gets its own text back; cutting the injected pack instead would silently substitute one
        // document for another, which is a worse failure than not cutting at all.
        if (_pack is null || !string.Equals(_systemContext, _pack.SystemPrompt, StringComparison.Ordinal))
            return _systemContext;

        var cut = SdkSurfaceSelector.For(_pack.SdkSurfaceSource, brief);
        SurfaceCharactersSaved = _pack.SdkSurface.Length - cut.Length;

        if (SurfaceCharactersSaved > 0)
        {
            _logger?.LogInformation(
                "AI builder cut the SDK surface for {Id} by {Saved} characters ({Before} to {After}).",
                StrategyId, SurfaceCharactersSaved, _pack.SdkSurface.Length, cut.Length);
        }

        return StrategyContextPack.Join(cut, _pack.Conventions);
    }

    /// <summary>Picks the domain packs for this strategy and folds them into the system prompt. Idempotent
    /// and stable: the same brief always yields the same prompt, which is what keeps it cacheable.</summary>
    private IReadOnlyList<StrategySkill> ResolveSkills(string brief)
    {
        if (_resolved) return LoadedSkills;
        _resolved = true;

        // Narrowed to the kind being authored: a visualizer session must not be handed guidance for
        // an API it does not have. A session built without a library still falls through to the
        // recomposition below — the brief-matched exemplar is not the skills' to withhold.
        LoadedSkills = _skills?.SelectFor(
            brief, Profile?.MaxSkills ?? StrategySkillLibrary.MaxSkillsPerSession, Kind) ?? [];

        // The exemplar is chosen from the same brief, at the same moment, for the same reason: it is
        // reference material aimed at the question. Recomposing the base pack here rather than in the
        // constructor is what lets it be — the brief does not exist yet when a session is built.
        //
        // Once per session, like the skills. The prefix a provider caches must not move between turns.
        BasePack = AuthoringKindBrief.Compose(SharedContextFor(brief), Kind, brief);
        SystemContext = StrategySkillLibrary.Compose(BasePack, LoadedSkills);

        if (LoadedSkills.Count > 0)
        {
            _logger?.LogInformation("AI builder loaded reference packs for {Id}: {Skills}",
                StrategyId, string.Join(", ", LoadedSkills.Select(s => s.Id)));
        }

        return LoadedSkills;
    }

    /// <summary>The brief, for skill selection, when a session is resumed rather than started: everything
    /// the USER has said (the model's replies would drag in whatever it happened to mention).</summary>
    private static string BriefFrom(IEnumerable<CodegenMessage> messages) =>
        string.Join('\n', messages.Where(m => m.Role == CodegenRole.User).Select(m => m.Content));

    /// <summary>The one authoritative copy of the code in the prompt.</summary>
    private string CurrentFilesBlock()
    {
        var sb = new StringBuilder("The strategy's files, as they stand right now (this is the code to work from):\n");
        foreach (var file in Files)
        {
            sb.AppendLine().AppendLine("```csharp");
            sb.Append("// file: ").AppendLine(file.Name);
            sb.AppendLine(file.Content.TrimEnd());
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    private static int Count(StrategyCompileResult? compile) => compile?.Errors.Count() ?? 0;

    /// <summary>
    /// The reason this unit is not what was asked for, or null when it is.
    ///
    /// <para>Read off the resolved type rather than off anything the model said about itself, for the
    /// same reason the ladder reads <c>MustDraw</c> that way: what the author actually wrote is the only
    /// reliable answer.</para>
    /// </summary>
    private string? Mismatch(StrategyCompileResult compile)
    {
        if (compile.Unit is not { } unit || unit.Kind == Kind) return null;

        return Kind == AuthoringKind.Visualizer
            ? $"You wrote {unit.ContractName}, but this session is authoring a VISUALIZER."
            : $"You wrote {unit.ContractName}, but this session is authoring a STRATEGY.";
    }

    /// <summary>The re-prompt for a kind mismatch. It names the contract and the one structural
    /// difference, because "wrong kind" alone tends to produce the same file with a renamed class.</summary>
    private string KindFixPrompt(string mismatch) =>
        Kind == AuthoringKind.Visualizer
            ? mismatch + " Rewrite it as `IVisualizer` with an `IVisualizerContext`. That context has no "
              + "`Book`, so remove anything that takes a position; if the brief genuinely needs to trade, "
              + "say so instead of implementing it."
            : mismatch + " Rewrite it as `IStrategyKernel` with an `IStrategyRuntimeContext`, and use "
              + "`context.Book` for any position it takes.";

    /// <summary>The auto-fix message: the compiler's own errors, verbatim, and a request for the whole
    /// corrected file set (partial diffs confuse the file-per-fence contract).</summary>
    private static string FixPrompt(StrategyCompileResult compile)
    {
        var sb = new StringBuilder(
            "The code did not compile. Fix these errors and return the COMPLETE corrected file set " +
            "(every file, each in its own fenced block with its `// file:` header):\n");
        foreach (var error in compile.Errors)
            sb.Append("- ").Append(error.Id).Append(' ').Append(error.Location).Append(": ").AppendLine(error.Message);
        return sb.ToString();
    }
}
