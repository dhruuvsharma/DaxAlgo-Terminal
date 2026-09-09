using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>
/// What a task is for. Five kinds, taken from what a trading unit actually decomposes into rather than
/// from a general agent taxonomy — a "backend/frontend/tests" split says nothing here, because the whole
/// artifact is one compiled unit that computes something and draws it.
/// </summary>
public enum TaskKind
{
    /// <summary>An indicator, estimator or feature, as its own helper type.</summary>
    Maths,

    /// <summary>The hostable class: what it decides, and when. Owns the entry point.</summary>
    Signal,

    /// <summary>One panel's picture, as its own helper type.</summary>
    Panel,

    /// <summary>The declared parameters and their wiring.</summary>
    Schema,

    /// <summary>What a strategy does to its virtual book — sizing, exits, exposure.</summary>
    Book,
}

/// <summary>One declared parameter, as the contract fixes it before anybody writes code.</summary>
public sealed record ParameterSpec(string Name, string Label, string Type, string Default);

/// <summary>One panel the window will show, and the helper type that paints it.</summary>
/// <param name="Id">Stable id, used as the panel's key.</param>
/// <param name="Title">What its header reads.</param>
/// <param name="Shows">What is in it, in one line.</param>
/// <param name="TypeName">The helper type that paints it, which is what makes it independently
/// buildable.</param>
public sealed record PanelSpec(string Id, string Title, string Shows, string TypeName);

/// <summary>A helper type, and the exact signature everything else will call it through.</summary>
/// <param name="TypeName">Its name.</param>
/// <param name="Purpose">What it computes or paints.</param>
/// <param name="Signature">The public members the hostable class will call. <b>This is the whole
/// mechanism</b>: it is fixed before the fan-out, so two builders working at the same time cannot
/// disagree about how they meet.</param>
public sealed record HelperSpec(string TypeName, string Purpose, string Signature);

/// <summary>
/// The skeleton, written before anybody builds anything.
///
/// <para><b>Contract-first is what makes a swarm possible on a single compiled unit.</b> Parallel
/// builders cannot each rewrite one file, and they cannot discover each other's decisions halfway
/// through — so the shape is settled first, in one cheap call, and every builder writes against it.
/// The Roslyn compiler is then the merge check: a builder that ignored the contract fails to compile
/// against the files that honoured it, and the repair is routed to whoever owns the file.</para>
/// </summary>
public sealed record UnitContract(
    string TypeName,
    AuthoringKind Kind,
    string DataRequirement,
    IReadOnlyList<ParameterSpec> Parameters,
    IReadOnlyList<PanelSpec> Panels,
    IReadOnlyList<HelperSpec> Helpers)
{
    public static UnitContract Minimal(string typeName, AuthoringKind kind) =>
        new(typeName, kind, "Bars", [], [], []);
}

/// <summary>
/// One unit of work, owned by exactly one builder, producing exactly one file.
///
/// <para><b>One file, one owner, per round</b> is the merge rule, and it is the only one. Two builders
/// writing the same file is a conflict nothing in this pipeline could resolve — there is no diff, no
/// three-way merge, and the second write simply wins.</para>
/// </summary>
/// <param name="Id">Stable within a plan; the trajectory log records it.</param>
/// <param name="Title">What the task board shows.</param>
/// <param name="Kind">What sort of work it is.</param>
/// <param name="OwnedFile">The one file this task may write.</param>
/// <param name="Intent">What the file must contain, in the planner's own words.</param>
/// <param name="DependsOn">Task ids that must finish first. Everything with no unfinished dependency
/// runs together.</param>
/// <param name="OwnsAllFiles">
/// This task owns the WHOLE unit, under whatever names the builder chooses.
///
/// <para>True only for the single-task fallback plan, and the exception is not a loophole — it is the
/// point. The one-file rule exists to stop concurrent builders colliding, and a plan with one task has
/// no concurrency to protect. Enforcing it there did active harm: a model answering with a kernel and
/// two helpers had two of them discarded, and the third renamed to a file name the planner invented
/// instead of the one the model wrote in its own header.</para>
/// </param>
public sealed record BuildTask(
    string Id,
    string Title,
    TaskKind Kind,
    string OwnedFile,
    string Intent,
    IReadOnlyList<string> DependsOn,
    bool OwnsAllFiles = false);

/// <summary>A group of tasks that finish together and are worth reporting as one step.</summary>
public sealed record Milestone(string Id, string Title, IReadOnlyList<BuildTask> Tasks);

/// <summary>
/// The orchestrator's answer: what is being built, in what order, and what "good" means for it.
/// </summary>
/// <param name="Contract">The skeleton every builder writes against.</param>
/// <param name="Milestones">Ordered. Tasks inside one may run in parallel.</param>
/// <param name="Rubric">What the critics judge against when the user supplied no reference of their
/// own — written from the brief, at plan time, before anything exists to be defensive about.</param>
/// <param name="OpenQuestions">What the planner could not settle. Surfaced to the user rather than
/// guessed at silently.</param>
public sealed record BuildPlan(
    UnitContract Contract,
    IReadOnlyList<Milestone> Milestones,
    IReadOnlyList<string> Rubric,
    IReadOnlyList<string> OpenQuestions)
{
    /// <summary>Every task, in milestone order.</summary>
    public IReadOnlyList<BuildTask> Tasks => [.. Milestones.SelectMany(m => m.Tasks)];

    /// <summary>
    /// The plan a brief gets when the planner could not produce one: build the whole thing in one file,
    /// which is exactly what the single-conversation path does.
    ///
    /// <para><b>A mumbling planner must never fail a build.</b> The fallback is not a degraded mode to
    /// apologise for — it is the behaviour the product had before the swarm existed, and it works.</para>
    /// </summary>
    public static BuildPlan Single(string brief, AuthoringKind kind, string typeName = "AuthoredUnit") =>
        new(UnitContract.Minimal(typeName, kind),
            [new Milestone(
                "m1",
                "Build it",
                [new BuildTask(
                    "t1", "Write the unit", TaskKind.Signal, "Unit.cs", brief, [], OwnsAllFiles: true)])],
            [],
            []);
}

/// <summary>How a plan was arrived at, which the pane says out loud.</summary>
public enum PlanOrigin
{
    /// <summary>The planner returned a plan and it parsed.</summary>
    Planned,

    /// <summary>The planner's JSON did not parse twice, so the single-file plan was used.</summary>
    Unparsed,

    /// <summary>The provider failed outright.</summary>
    ProviderFailed,

    /// <summary>The planner asked the user something rather than returning a plan.</summary>
    Asked,
}

/// <summary>Reads a plan out of whatever the model actually returned.</summary>
public static class BuildPlanReader
{
    /// <summary>
    /// Whether a reply that carried no plan is <b>asking the user something</b> rather than failing to
    /// follow instructions.
    ///
    /// <para>Two signals: a structured questions block, or a closing question mark. <b>Only the TAIL is
    /// examined</b> — a good specification restates the questions it already resolved ("Which timeframe?
    /// — 5-minute bars"), so scanning the whole reply for a question mark would find one in nearly every
    /// plan worth having. What a turn ends on is what it wants next.</para>
    /// </summary>
    public static bool IsAsking(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        if (AuthoringQuestions.Parse(reply).Count > 0) return true;

        var trimmed = reply.AsSpan().TrimEnd();

        // Markdown emphasis and closing punctuation routinely follow the mark, and a reply ending
        // "**...which instrument?**" is every bit as much a question as one ending in a bare "?".
        while (trimmed.Length > 0 && trimmed[^1] is '*' or '_' or '`' or '"' or '\'' or ')' or ']')
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed.Length > 0 && trimmed[^1] == '?';
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// Pulls the plan out of a reply, or returns null.
    ///
    /// <para><b>Fenced JSON rather than a tool call, and that is a portability decision, not a
    /// preference.</b> No provider client in this tree implements tool calling, two of the supported
    /// providers are agent CLIs that never will in the same shape, and several are gateways fronting
    /// models whose tool support depends on which model was picked. A fenced block is the one contract
    /// every one of them can honour.</para>
    ///
    /// <para>The reply is searched for a JSON object rather than required to be one, because models
    /// preface things. A reply that is nothing but the object parses by the same path.</para>
    /// </summary>
    public static BuildPlan? Read(string? reply, AuthoringKind kind)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        foreach (var candidate in Candidates(reply))
        {
            try
            {
                if (JsonSerializer.Deserialize<PlanWire>(candidate, Json) is { } wire &&
                    wire.ToPlan(kind) is { } plan)
                    return plan;
            }
            catch (JsonException)
            {
                // Try the next candidate. A model that wrote two blocks and got the first one wrong is
                // not a reason to give up on the second.
            }
        }

        return null;
    }

    /// <summary>Fenced blocks first, then the widest brace span — in that order, because a fence is the
    /// model saying which part it meant.</summary>
    private static IEnumerable<string> Candidates(string reply)
    {
        foreach (var block in CodegenCodeExtractor.FencedBlocks(reply))
            if (block.TrimStart().StartsWith('{'))
                yield return block;

        var open = reply.IndexOf('{');
        var close = reply.LastIndexOf('}');
        if (open >= 0 && close > open) yield return reply[open..(close + 1)];
    }

    /// <summary>
    /// The wire shape, kept separate from <see cref="BuildPlan"/> so a malformed field degrades instead
    /// of throwing. Every collection is nullable here and non-null after <see cref="ToPlan"/>: a model
    /// that omits <c>helpers</c> has said "none", not "fail".
    /// </summary>
    private sealed record PlanWire(
        ContractWire? Contract,
        IReadOnlyList<MilestoneWire>? Milestones,
        IReadOnlyList<string>? Rubric,
        IReadOnlyList<string>? OpenQuestions)
    {
        public BuildPlan? ToPlan(AuthoringKind kind)
        {
            var milestones = (Milestones ?? [])
                .Select(m => m.ToMilestone())
                .Where(m => m.Tasks.Count > 0)
                .ToArray();

            // A plan with no work in it is not a plan. Returning null here is what sends the caller to
            // the single-file fallback rather than running a swarm over nothing.
            if (milestones.Length == 0) return null;

            var contract = Contract?.ToContract(kind) ?? UnitContract.Minimal("AuthoredUnit", kind);

            return new BuildPlan(contract, milestones, Rubric ?? [], OpenQuestions ?? []);
        }
    }

    private sealed record ContractWire(
        string? TypeName,
        string? DataRequirement,
        IReadOnlyList<ParameterSpec>? Parameters,
        IReadOnlyList<PanelSpec>? Panels,
        IReadOnlyList<HelperSpec>? Helpers)
    {
        public UnitContract ToContract(AuthoringKind kind) => new(
            string.IsNullOrWhiteSpace(TypeName) ? "AuthoredUnit" : TypeName.Trim(),
            kind,
            string.IsNullOrWhiteSpace(DataRequirement) ? "Bars" : DataRequirement.Trim(),
            Parameters ?? [],
            Panels ?? [],
            Helpers ?? []);
    }

    private sealed record MilestoneWire(string? Id, string? Title, IReadOnlyList<TaskWire>? Tasks)
    {
        public Milestone ToMilestone() => new(
            string.IsNullOrWhiteSpace(Id) ? "m" : Id.Trim(),
            string.IsNullOrWhiteSpace(Title) ? "Build" : Title.Trim(),
            [.. (Tasks ?? []).Select(t => t.ToTask()).Where(t => t is not null).Select(t => t!)]);
    }

    private sealed record TaskWire(
        string? Id,
        string? Title,
        TaskKind? Kind,
        string? OwnedFile,
        string? Intent,
        IReadOnlyList<string>? DependsOn)
    {
        public BuildTask? ToTask()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Intent)) return null;

            // A task with no file has nowhere to put its work, and a swarm that let one through would
            // silently drop whatever it produced.
            var file = string.IsNullOrWhiteSpace(OwnedFile) ? null : SafeFileName(OwnedFile);
            if (file is null) return null;

            return new BuildTask(
                Id.Trim(),
                string.IsNullOrWhiteSpace(Title) ? Id.Trim() : Title.Trim(),
                Kind ?? TaskKind.Signal,
                file,
                Intent.Trim(),
                DependsOn ?? []);
        }
    }

    /// <summary>
    /// Reduces a planner's file name to a bare <c>*.cs</c> leaf.
    ///
    /// <para>The file name comes from a model and is used as a key and a compilation path, so it is
    /// treated as untrusted: a directory separator, a drive, or a <c>..</c> segment would let a plan
    /// name something outside the unit. Nothing here ever writes to disk, but a name that could only be
    /// safe because of what the current caller happens to do is a defect waiting for the next caller.</para>
    /// </summary>
    private static string? SafeFileName(string name)
    {
        var leaf = name.Trim().Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        if (leaf.Length == 0 || leaf is "." or ".." || leaf.Contains(':')) return null;
        if (leaf.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;

        return leaf.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? leaf : leaf + ".cs";
    }
}
