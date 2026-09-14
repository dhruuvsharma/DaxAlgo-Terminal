using System.Text;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// The swarm's dialect for units written against the Blocks SDK: C# that implements <c>IUnit</c>, and a
/// web page the unit feeds.
///
/// <para><b>Two builders, one contract, and the contract now includes the messages.</b> The C# and the
/// page are written at the same time by builders that cannot see each other, so every topic between
/// them — its name, its direction, the fields of its payload — is fixed by the planner, exactly as helper
/// signatures already are.</para>
///
/// <para><b>Each builder gets its task's cards and nothing else of the SDK.</b> The planner names the
/// blocks per task because deciding what a file does is deciding what it calls; the cards are then the
/// only SDK text in that builder's message. The shared prefix — conventions and index — is the same for
/// every call in the run, so it caches.</para>
/// </summary>
public sealed class BlocksSwarmDialect(BlockCatalog? catalog = null) : ISwarmDialect
{
    private readonly BlockCatalog _catalog = catalog ?? BlockCatalog.Load();

    /// <summary>What a critic is told the "drawing" is, since a Blocks unit draws a web page rather than
    /// primitives.</summary>
    internal const string PageDescription =
        "The unit draws its own web page (ui/index.html). The picture is that page, photographed while the "
        + "unit was feeding it a synthetic market.";

    /// <summary>The shared prefix of every call in a Blocks run.</summary>
    public string SharedContext => _catalog.SharedContext;

    public string Planner(AuthoringKind kind, int maxTasks) =>
        $$"""
        YOUR ROLE: Planner. You do not write code. You write the plan the builders follow.

        Return ONE ```json fenced block and nothing else that matters. This shape:

        {
          "contract": {
            "typeName": "PascalCaseUnitName",
            "parameters": [{ "name": "lookback", "label": "Look-back", "type": "int", "default": "20" }],
            "helpers": [{ "typeName": "SpreadModel", "purpose": "rolling z-score of a spread",
                          "signature": "public SpreadModel(int lookback); public double Update(double a, double b); public bool IsReady { get; }" }],
            "topics": [
              { "name": "state", "direction": "to-page", "payload": "{ z: number, spread: number, history: [{ t: number, z: number }] }" },
              { "name": "setEntry", "direction": "from-page", "payload": "{ z: number }" }
            ]
          },
          "milestones": [
            { "id": "m1", "title": "Build", "tasks": [
              { "id": "t1", "title": "Spread model", "kind": "Maths", "ownedFile": "SpreadModel.cs",
                "blocks": ["math.regression"], "intent": "one paragraph: exactly what this file must contain", "dependsOn": [] },
              { "id": "t2", "title": "The unit", "kind": "Signal", "ownedFile": "SpreadUnit.cs",
                "blocks": ["unit", "settings", "market", "ui"], "intent": "…", "dependsOn": [] },
              { "id": "t3", "title": "The page", "kind": "Ui", "ownedFile": "ui/index.html",
                "blocks": ["ui"], "intent": "what the page shows, laid out how, and what the user can do on it", "dependsOn": [] }
            ]}
          ],
          "rubric": ["what would make this excellent, one line each — the bar critics judge against"],
          "openQuestions": ["anything you could not settle from the brief"]
        }

        RULES:

        1. ONE OWNER PER FILE. Builders run at the same time and cannot see each other's work. The page
           task owns `ui/index.html` and any `ui/*.js` or `ui/*.css` beside it; every other task owns one
           `.cs` file.
        2. Every helper's `signature` and every topic's `payload` fields are fixed HERE, exactly. The C#
           builder and the page builder meet only through `topics`: a field you leave vague is one they
           will spell differently. Payload field names are camelCase.
        3. Exactly ONE task has kind "Signal": the one public class implementing IUnit. Other C# tasks
           write helper types.
        4. `kind` is one of: Maths, Signal, Book, Ui.
        5. `blocks` names, from the index, every block that task's code calls — its builder is given
           those cards and no others. The Signal task always names "unit". A helper often needs one
           math.* card, or none.
        6. {{(kind == AuthoringKind.Strategy
            ? "This is a STRATEGY: the Signal task names \"orders\" and \"portfolio\"."
            : "This is a VISUALIZER: no task names \"orders\".")}}
        7. The page task never depends on a C# task and no C# task depends on the page — they meet
           through `topics`. Keep every other `dependsOn` minimal: a chain of dependencies is a chain of
           waits.
        8. AT MOST {{maxTasks}} TASKS. Plan the smallest thing that satisfies the brief. Most units are one
           Signal task and one Ui task.
        9. `rubric` is what "good" looks like for THIS brief, in concrete checkable lines.
        10. If something is genuinely undecided, put it in `openQuestions` and pick a sensible default.
        """;

    public string Builder(BuildTask task, UnitContract contract)
    {
        if (task.OwnsAllFiles) return Whole(task, contract);

        var role = task.OwnsPage
            ? """
              You are writing the unit's PAGE and nothing else — no C#.
              - Listen with dax.on for every to-page topic and send every from-page topic with dax.send,
                with exactly the payload fields the contract lists. Call dax.ready() once your listeners
                are attached.
              - The look is yours, and it is what the user judges the unit by: a professional trading
                panel — dark, dense, legible, numbers right-aligned, colour that means something (up and
                down, bid and ask). Draw charts on <canvas> or load a library from an https CDN.
              - Show a waiting state until the first message arrives, and redraw from whole state on
                every message.
              """
            : task.Kind == TaskKind.Signal
                ? """
                  You own the ONE public class implementing IUnit.
                  - Construct and call helpers through their contract signatures; do not re-implement them.
                  - Send every to-page topic with context.Ui.Send, as whole state with exactly the payload
                    fields listed, and send full state from context.Ui.OnOpened. Handle every from-page
                    topic with context.Ui.On.
                  - Declare every contract parameter in Info and read it through context.Settings.
                  """
                : "You are writing a helper type. Do not implement IUnit — exactly one file does, and it is not yours.";

        return $"""
            YOUR ROLE: Builder. You are writing {(task.OwnsPage ? "the page" : "ONE file")} of a unit other builders are writing at the same time.

            YOUR FILE: {(task.OwnsPage ? "ui/index.html, plus ui/*.js and ui/*.css files if you want them" : task.OwnedFile)}
            YOUR TASK: {task.Title} — {task.Intent}

            RULES:

            1. Return ONLY {(task.OwnsPage ? "page files, each in its own fenced block with its path on the first line (see Output in the conventions)" : $"{task.OwnedFile}, as one ```csharp block with `// file: {task.OwnedFile}` on its first line")}.
               Anything else you write is discarded.
            2. The contract below is fixed. Match type names, signatures and topics EXACTLY.
            3. Write COMPLETE files. Not a fragment, not a diff.
            4. Call only what the blocks below and the .NET base library provide.
            {role}

            {Contract(contract)}
            """;
    }

    public string Fixer(BuildTask task, UnitContract contract) =>
        task.OwnsAllFiles
            ? $"""
              YOUR ROLE: Fixer. Repair the unit so it compiles, runs and its page works.

              RULES:

              1. Return the COMPLETE file set — every C# file and every page file, each in its own fenced
                 block with its path on the first line. A file you leave out is a file that disappears.
              2. Change as little as possible. You are repairing, not rewriting.

              {Contract(contract)}
              """
            : $"""
              YOUR ROLE: Fixer. Repair {(task.OwnsPage ? "the unit's page" : $"ONE file, {task.OwnedFile},")} so the unit compiles, runs and its page works.

              RULES:

              1. Return ONLY {(task.OwnsPage ? "the page files, complete, each in its own fenced block with its path on the first line" : $"{task.OwnedFile}, complete, in one ```csharp block with its `// file:` header")}.
              2. Change as little as possible. You are repairing, not rewriting.
              3. If a finding points at a file that is not yours, the fault is a mismatch with the contract —
                 change YOUR file to match the contract, never the contract to match your file.

              {Contract(contract)}
              """;

    public string ComposeBuild(SwarmContext context, BuildTask task, BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WithCards(context.ComposeBuild(task, plan), task, plan);
    }

    public string ComposeRepair(
        SwarmContext context, BuildTask task, IReadOnlyList<VerificationFinding> findings, BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WithCards(context.ComposeRepair(task, findings, plan), task, plan);
    }

    public IReadOnlyList<StrategyFile> FilesIn(StrategyCodegenResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var files = string.IsNullOrWhiteSpace(response.RawText)
            ? response.FileList
            : CodegenCodeExtractor.ExtractUnitFiles(response.RawText);

        // A page is not C#, so the prose test is for the C# alone.
        return [.. files.Where(f => CodegenCodeExtractor.IsPageFile(f.Name)
            ? !string.IsNullOrWhiteSpace(f.Content)
            : CodegenCodeExtractor.LooksLikeCode(f.Content))];
    }

    public Task<GauntletSubject?> SubjectAsync(
        GateResult verdict,
        IReadOnlyList<StrategyFile> files,
        AuthoringKind kind,
        IUnitRasterizer? rasterizer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return Task.FromResult<GauntletSubject?>(
            new GauntletSubject(files, verdict.Picture, PageDescription, verdict.Report, Layout: null, kind));
    }

    /// <summary>
    /// The blocks a task is sent. What the planner named, plus what a task of its kind cannot be written
    /// without: the unit's own card for the class implementing <c>IUnit</c>, the bridge for anything
    /// talking to the page.
    /// </summary>
    public IReadOnlyList<string> BlocksFor(BuildTask task, BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(plan);

        var ids = new List<string>(task.Cards);

        if (task.OwnsAllFiles && ids.Count == 0)
        {
            // The fallback plan names nothing — nobody planned it. It gets the blocks almost every unit
            // uses; anything else it needs, it still has the index to name in its reply.
            ids.AddRange([BlockCatalog.UnitBlock, "settings", "market", "schedule", BlockCatalog.UiBlock]);
            if (plan.Contract.Kind == AuthoringKind.Strategy) ids.AddRange(["orders", "portfolio"]);
        }

        if (task.Kind == TaskKind.Signal || task.OwnsAllFiles) ids.Add(BlockCatalog.UnitBlock);
        if (task.OwnsPage || (task.Kind == TaskKind.Signal && plan.Contract.PageTopics.Count > 0))
            ids.Add(BlockCatalog.UiBlock);

        return _catalog.Resolve(ids);
    }

    /// <summary>The contract, rendered for a Blocks builder.</summary>
    public static string Contract(UnitContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var text = new StringBuilder();
        text.AppendLine("THE CONTRACT (fixed — every builder writes against this):");
        text.AppendLine();
        text.AppendLine($"  Unit class: {contract.TypeName} (implements IUnit)");

        if (contract.Parameters.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  Settings (declared in Info AND read through context.Settings):");
            foreach (var p in contract.Parameters)
                text.AppendLine($"    - {p.Name} ({p.Type}, default {p.Default}) — \"{p.Label}\"");
        }

        if (contract.Helpers.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  Helper types — call them through EXACTLY these signatures:");
            foreach (var helper in contract.Helpers)
            {
                text.AppendLine($"    - {helper.TypeName}: {helper.Purpose}");
                text.AppendLine($"        {helper.Signature}");
            }
        }

        if (contract.PageTopics.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  Messages between the unit and its page — names and payload fields EXACTLY:");
            foreach (var topic in contract.PageTopics)
                text.AppendLine(topic.FromPage
                    ? $"    - \"{topic.Name}\" page → unit (dax.send / context.Ui.On): {topic.Payload}"
                    : $"    - \"{topic.Name}\" unit → page (context.Ui.Send / dax.on): {topic.Payload}");
        }

        return text.ToString();
    }

    private string Whole(BuildTask task, UnitContract contract) =>
        $"""
        YOUR ROLE: Builder. Write the whole unit: its C# and its page.

        WHAT TO BUILD: {task.Intent}

        RULES:

        1. Return the COMPLETE file set — one fenced block per file with its path on the first line: the
           C# (exactly one public class implementing IUnit, helpers beside it) and ui/index.html with any
           ui/*.js and ui/*.css.
        2. Complete files only. Not a fragment, not a diff.
        3. Call only what the blocks below and the .NET base library provide.
        4. The page is what the user judges the unit by: make it a professional trading panel.

        {Contract(contract)}
        """;

    private string WithCards(string message, BuildTask task, BuildPlan plan)
    {
        var cards = _catalog.Compose(BlocksFor(task, plan));
        if (cards.Length == 0) return message;

        var heading = "THE BLOCKS YOUR CODE MAY CALL";
        return message.TrimEnd()
               + Environment.NewLine + Environment.NewLine
               + heading + Environment.NewLine
               + new string('─', heading.Length) + Environment.NewLine
               + cards + Environment.NewLine;
    }
}
