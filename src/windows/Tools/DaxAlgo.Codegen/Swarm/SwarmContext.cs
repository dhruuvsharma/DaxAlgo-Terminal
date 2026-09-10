using System.Text;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>
/// What a run has produced so far, as <b>artifacts rather than a transcript</b>, and the composer that
/// hands each builder only the artifacts its task acts on.
///
/// <para>The obvious design — one shared conversation every agent appends to — is wrong on both counts
/// that matter here.</para>
///
/// <para><b>Cost.</b> A transcript grows with every turn and every turn re-sends all of it, so the bill
/// is quadratic in turns. Artifacts are replaced rather than accumulated, so a run's context is bounded
/// by the size of the work rather than by how long it took to get there — and under a token budget, how
/// long it took is exactly what you do not want to pay for twice.</para>
///
/// <para><b>Quality.</b> A transcript hands a panel builder the maths derivation, a repair the original
/// brief, and everyone three stale versions of the same file. Models repair the wrong copy when an old
/// one is in context; that is not a token problem, it is a correctness one.</para>
/// </summary>
public sealed class SwarmContext
{
    private readonly Dictionary<string, StrategyFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public SwarmContext(string brief, IReadOnlyList<StrategyFile>? existing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        Brief = brief;
        foreach (var file in existing ?? []) _files[file.Name] = file;
    }

    /// <summary>What the user asked for, verbatim. Given to the planner and to nobody else.</summary>
    public string Brief { get; }

    /// <summary>The current files, one per name. Replaced on every write, never appended to.</summary>
    public IReadOnlyList<StrategyFile> Files => [.. _files.Values];

    /// <summary>The current content of one file, or null.</summary>
    public StrategyFile? File(string name) => _files.GetValueOrDefault(name);

    /// <summary>
    /// Files in the build that no task in this plan owns.
    ///
    /// <para>They can only have arrived one way: they were already in the editor when the run started.
    /// That makes them the one category of file this pipeline cannot repair — <see cref="Accept"/>
    /// writes only through a task's owned name, and a repair is routed by owner, so a finding against
    /// an orphan is a finding addressed to nobody. A real session spent three rounds on exactly that:
    /// every round concluded "omit Strategy.cs", and not one of them was able to.</para>
    /// </summary>
    public IReadOnlyList<StrategyFile> Orphans(BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // A one-task fallback owns everything there is, so nothing is ever orphaned under one.
        if (plan.Tasks.Any(t => t.OwnsAllFiles)) return [];

        var owned = new HashSet<string>(plan.Tasks.Select(t => t.OwnedFile), StringComparer.OrdinalIgnoreCase);
        return [.. _files.Values.Where(f => !owned.Contains(f.Name))];
    }

    /// <summary>Takes a file out of the build. Returns false when it was not in it.</summary>
    public bool Remove(string name) => _files.Remove(name);

    /// <summary>
    /// Records what a task produced, <b>keeping only the file that task owns</b>.
    ///
    /// <para>This is the merge rule enforced rather than requested. Asked for a panel, a model will
    /// cheerfully also rewrite the kernel to call it — and that rewrite, applied, would clobber whatever
    /// the kernel's own builder wrote in the same round. The rule is stated in the builder's prompt and
    /// enforced here, because a rule only stated in a prompt is a rule that holds most of the time.</para>
    /// </summary>
    /// <returns>True when the task's own file arrived; false when the builder returned nothing usable,
    /// which the runner reports rather than silently treating as success.</returns>
    public bool Accept(BuildTask task, IReadOnlyList<StrategyFile> produced)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(produced);

        // The fallback plan's one task owns everything, under the builder's own names. There is no
        // concurrency to protect in a plan with one task, and forcing the planner's invented file name
        // onto the model's answer discarded every file after the first.
        if (task.OwnsAllFiles)
        {
            var usable = produced.Where(f => !string.IsNullOrWhiteSpace(f.Content)).ToArray();
            if (usable.Length == 0) return false;

            foreach (var file in usable) _files[file.Name] = file;
            return true;
        }

        // By name first: a builder that labelled its block correctly is taken at its word. Otherwise, a
        // single returned file IS the answer whatever it called itself — models rename constantly, and
        // discarding a correct file over its label would be the pedantic reading of the same rule.
        var owned =
            produced.FirstOrDefault(f => string.Equals(f.Name, task.OwnedFile, StringComparison.OrdinalIgnoreCase))
            ?? (produced.Count == 1 ? produced[0] : null);

        if (owned is null) return false;

        _files[task.OwnedFile] = new StrategyFile(task.OwnedFile, owned.Content);
        return true;
    }

    /// <summary>
    /// The user message a builder gets: its task, and the CURRENT text of the files it depends on.
    ///
    /// <para>Dependencies are sent whole rather than summarised. A builder writing against a helper needs
    /// the helper's actual members, and a signature it was told about but cannot see is a signature it
    /// will approximate.</para>
    /// </summary>
    public string ComposeBuild(BuildTask task, BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(plan);

        var text = new StringBuilder();
        Section(text, "WHAT THIS FILE MUST DO", task.Intent);

        foreach (var id in task.DependsOn)
        {
            if (plan.Tasks.FirstOrDefault(t => t.Id == id) is not { } upstream) continue;
            if (File(upstream.OwnedFile) is not { } file) continue;
            Section(text, $"ALREADY WRITTEN — {file.Name}", file.Content);
        }

        // Its own previous attempt, when there is one. A second round on the same task is a revision,
        // and a revision that cannot see what it is revising rewrites from scratch.
        foreach (var file in Owned(task))
            Section(text, $"YOUR CURRENT {file.Name} — revise this", file.Content);

        return text.ToString();
    }

    /// <summary>
    /// The user message a repair gets: its own file, and the findings against it.
    ///
    /// <para>Findings for OTHER files are included by name only. A mismatch shows up as an error in the
    /// file that called it, so a repair needs to know its caller is unhappy — but handing it the other
    /// file's diagnostics invites it to fix code it does not own and cannot see.</para>
    /// </summary>
    public string ComposeRepair(BuildTask task, IReadOnlyList<VerificationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(findings);

        var text = new StringBuilder();

        foreach (var file in Owned(task))
            Section(text, $"YOUR FILE — {file.Name}", file.Content);

        // A task that owns everything owns every finding too — there is nobody else to route them to.
        IReadOnlyList<VerificationFinding> mineFindings = task.OwnsAllFiles
            ? findings
            : findings
                .Where(f => f.File is null
                            || string.Equals(f.File, task.OwnedFile, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        Section(
            text,
            "WHAT IS WRONG",
            mineFindings.Count > 0
                ? string.Join(Environment.NewLine, mineFindings.Select(f => f.ToString()))
                : string.Join(Environment.NewLine, findings.Select(f => f.ToString())));

        string[] elsewhere = task.OwnsAllFiles ? [] : [.. findings
            .Where(f => f.File is { Length: > 0 }
                        && !string.Equals(f.File, task.OwnedFile, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.File!)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (elsewhere.Length > 0)
            Section(
                text,
                "ALSO FAILING, IN FILES THAT ARE NOT YOURS",
                string.Join(", ", elsewhere) + Environment.NewLine
                + "Do not try to fix them. If your file is what they disagree with, change yours.");

        return text.ToString();
    }

    /// <summary>The files this task may rewrite: its own, or all of them for the fallback task.</summary>
    private IReadOnlyList<StrategyFile> Owned(BuildTask task) =>
        task.OwnsAllFiles
            ? Files
            : File(task.OwnedFile) is { } mine ? [mine] : [];

    private static void Section(StringBuilder text, string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        if (text.Length > 0) text.AppendLine();
        text.AppendLine(heading);
        text.AppendLine(new string('─', Math.Min(heading.Length, 72)));
        text.AppendLine(body.Trim());
    }
}
