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
            ],
            "pageModules": [
              { "file": "ui/chart.js", "purpose": "the z-score chart on a canvas",
                "exports": "export function mountChart(el: HTMLElement): { update(state: State): void; resize(): void; dispose(): void }" }
            ]
          },
          "milestones": [
            { "id": "m1", "title": "Build", "tasks": [
              { "id": "t1", "title": "Spread model", "kind": "Maths", "ownedFile": "SpreadModel.cs",
                "blocks": ["math.regression"], "intent": "one paragraph: exactly what this file must contain", "dependsOn": [] },
              { "id": "t2", "title": "The unit", "kind": "Signal", "ownedFile": "SpreadUnit.cs",
                "blocks": ["unit", "settings", "market", "ui"], "intent": "…", "dependsOn": [] },
              { "id": "t3", "title": "Page shell", "kind": "Ui", "ownedFile": "ui/index.html",
                "blocks": ["ui"], "intent": "layout, header, controls, the dax bridge; loads and drives ui/chart.js", "dependsOn": [] },
              { "id": "t4", "title": "Chart", "kind": "Ui", "ownedFile": "ui/chart.js",
                "blocks": ["ui"], "intent": "exactly what the chart draws and how it reacts to the mouse", "dependsOn": [] }
            ]}
          ],
          "rubric": ["what would make this excellent, one line each — the bar critics judge against"],
          "openQuestions": ["anything you could not settle from the brief"]
        }

        RULES:

        1. ONE OWNER PER FILE. Builders run at the same time and cannot see each other's work. Every C#
           task owns one `.cs` file. The page is either ONE Ui task owning `ui/index.html` and every
           `ui/*` file beside it — right for a simple page — or, for a RICH page (a 3D scene, several
           charts, a feed, a HUD), a SHELL task owning `ui/index.html` (plus any `ui/*` file no other task
           owns, such as `ui/app.js` and `ui/style.css`) and ONE Ui task per module file (`ui/scene.js`,
           `ui/depth.js` …). Split a rich page: one reply cannot hold all of it, and a page cut off
           half-written is a page that does not work. At most four modules.
        2. Every helper's `signature`, every topic's `payload` fields and every page module's `exports`
           are fixed HERE, exactly. The C# builder and the page builders meet only through `topics`, and
           the shell and the modules only through `pageModules`: anything you leave vague is something
           they will spell differently. Payload field names are camelCase.
        2a. The SHELL owns the dax bridge — dax.on, dax.send, dax.ready() — and hands state to the modules
           through their exports. Modules never touch dax and never import each other.
        3. Exactly ONE task has kind "Signal": the one public class implementing IUnit. Other C# tasks
           write helper types.
        4. `kind` is one of: Maths, Signal, Book, Ui.
        5. `blocks` names, from the index, every block that task's code calls — its builder is given
           those cards and no others. The Signal task always names "unit". A helper often needs one
           math.* card, or none.
        6. {{(kind == AuthoringKind.Strategy
            ? "This is a STRATEGY: the Signal task names \"orders\" and \"portfolio\"."
            : "This is a VISUALIZER: no task names \"orders\".")}}
        7. Page tasks never depend on a C# task and no C# task depends on a page task — they meet through
           `topics`. The shell and the modules never depend on each other — they meet through
           `pageModules`. Keep every other `dependsOn` minimal: a chain of dependencies is a chain of
           waits.
        8. AT MOST {{maxTasks}} TASKS. Plan the smallest thing that satisfies the brief. Most units are one
           Signal task and one Ui task; a rich page adds its modules.
        9. `rubric` is what "good" looks like for THIS brief, in concrete checkable lines.
        10. If something is genuinely undecided, put it in `openQuestions` and pick a sensible default.
        """;

    public string Builder(BuildTask task, UnitContract contract)
    {
        if (task.OwnsAllFiles) return Whole(task, contract);

        var split = contract.PageModules.Count > 0 || task.PageModuleOnly || (task.PageFilesElsewhere?.Count ?? 0) > 0;

        var role = task.PageModuleOnly
            ? $"""
              You are writing ONE MODULE of the unit's page: {task.OwnedFile}. Nothing else — no C#, no HTML.
              - Export EXACTLY what the contract lists for {task.OwnedFile}: the page's shell imports it and
                calls it, written by another builder at the same time from the same contract.
              - Never touch dax (the shell owns the bridge) and never import another module: take state
                only through your exports' arguments.
              - Everything you draw, you own completely: build it to the standard below.

              {PageCraft}
              """
            : task.OwnsPage
                ? $"""
                  You are writing the unit's PAGE{(split ? " SHELL" : string.Empty)} and nothing else — no C#.
                  - Listen with dax.on for every to-page topic and send every from-page topic with dax.send,
                    with exactly the payload fields the contract lists. Call dax.ready() once your listeners
                    are attached.
                  {(split
                      ? "- The page is split: load every module the contract lists with a relative path, hand each one state through its exports exactly as written, and never re-implement one. The layout, header, controls, panels and any ui/*.css or ui/*.js no module owns are yours."
                      : "- The look is yours, and it is what the user judges the unit by. Draw charts on <canvas>, or with a library from an https CDN.")}
                  - Show a waiting state until the first message arrives, and redraw from whole state on
                    every message.

                  {PageCraft}
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

        var file = task.PageModuleOnly
            ? task.OwnedFile
            : task.OwnsPage
                ? split
                    ? "ui/index.html, plus any ui/*.js or ui/*.css no module owns"
                    : "ui/index.html, plus ui/*.js and ui/*.css files if you want them"
                : task.OwnedFile;

        var returns = task.PageModuleOnly
            ? $"{task.OwnedFile}, as one complete fenced block with `// file: {task.OwnedFile}` (or `/* file: … */` for CSS) on its first line"
            : task.OwnsPage
                ? "page files, each in its own complete fenced block with its path on the first line (see Output in the conventions)"
                : $"{task.OwnedFile}, as one ```csharp block with `// file: {task.OwnedFile}` on its first line";

        var cSharp = task.OwnsPage
            ? string.Empty
            : "5. A record or enum only your file needs is declared NESTED inside your own class, never at the top level — other builders are writing files at the same time, and two top-level types with one name break the whole unit.";

        return $"""
            YOUR ROLE: Builder. You are writing {(task.PageModuleOnly ? "one module of the page" : task.OwnsPage ? "the page" : "ONE file")} of a unit other builders are writing at the same time.

            YOUR FILE: {file}
            YOUR TASK: {task.Title} — {task.Intent}

            RULES:

            1. Return ONLY {returns}.
               Anything else you write is discarded.
            2. The contract below is fixed. Match type names, signatures, topics and exports EXACTLY.
            3. Write COMPLETE files. Not a fragment, not a diff. Close every code block.
            4. Call only what the blocks below and the .NET base library provide.
            {cSharp}
            {role}

            {Contract(contract)}
            """;
    }

    /// <summary>
    /// What a page builder is told about the look — shared by the single page, the shell and every module,
    /// so a split page is still one design.
    ///
    /// <para>Written from what the 2026-09-19 Battlefield runs got wrong: a scene lit so dimly it read as
    /// black, walls printing "$0.0M", numbers without units, a page that loaded files it never wrote.</para>
    /// </summary>
    internal const string PageCraft = """
        CRAFT — the page is what the user judges the unit by:
        - Layout: a full-window dark app with no page scroll; one main view, secondary panels docked around
          it, a slim header. CSS grid or flex; panels with a 1px border, 8–12px padding, 6px radius.
        - Type and numbers: system-ui; tabular-nums; numbers right-aligned and formatted
          (Intl.NumberFormat, 12.4K / 3.2M); a label or unit on every number; never "undefined" or "NaN".
        - Colour: one dark background (#0b0f17–#131722), two or three greys for text, and colour only where
          it means something — up/bid #22c55e, down/ask #ef4444, one accent. Text contrast at least 4.5:1.
        - Canvas: size it to its element × devicePixelRatio, redraw on resize (ResizeObserver) and on new
          state through requestAnimationFrame — never on a timer, never once per message.
        - three.js: import it through the import map (three@0.170.0 on cdn.jsdelivr.net, see the
          conventions); WebGLRenderer({ antialias: true }) with outputColorSpace = SRGBColorSpace and ACES
          filmic tone mapping; a HemisphereLight AND a DirectionalLight so nothing is ever black; fog for
          depth; a camera framed on the content; OrbitControls with limits; InstancedMesh for anything
          repeated; pooled objects, never created per frame; dispose on unload; pause while document.hidden.
        - States: an explicit waiting state before the first message, an empty state when there is no data.
        - Motion: ease between states (lerp values and positions) rather than jumping.
        - Bounded: arrays capped, no DOM node per data point, listeners removed when replaced.
        """;

    public string Fixer(BuildTask task, UnitContract contract) =>
        task.OwnsAllFiles
            ? $"""
              YOUR ROLE: Fixer. Repair the unit so it compiles, runs and its page works.

              RULES:

              1. Return ONLY the files you change. A file you do not mention stays exactly as it is.
              {EditForms("each file you change", "Unit.cs")}
              2. Change as little as possible. You are repairing, not rewriting.

              {Contract(contract)}
              """
            : $"""
              YOUR ROLE: Fixer. Repair {(task.PageModuleOnly ? $"ONE page file, {task.OwnedFile}," : task.OwnsPage ? "the unit's page" : $"ONE file, {task.OwnedFile},")} so the unit compiles, runs and its page works.

              RULES:

              1. Return ONLY your changes to {(task.PageModuleOnly ? task.OwnedFile : task.OwnsPage ? "the page files you own" : task.OwnedFile)} — nothing else is yours to change.
              {EditForms(task.OwnsPage && !task.PageModuleOnly ? "each page file you change" : task.OwnedFile, task.OwnedFile)}
              2. Change as little as possible. You are repairing, not rewriting.
              3. If a finding points at a file that is not yours, the fault is a mismatch with the contract —
                 change YOUR file to match the contract, never the contract to match your file.

              {Contract(contract)}
              """;

    /// <summary>
    /// The two ways a repair may answer: edits, or the whole file.
    ///
    /// <para><b>Edits first</b>, because a fix is usually a few lines and a whole file is the size of the
    /// file — measured on the 2026-09-20 Nemotron Battlefield run, repairs re-sent up to 72,000 characters
    /// for faults a handful of lines wide. The whole file stays available for a fix that changes most of
    /// it, and is what a fixer is asked for when one of its edits matches nothing.</para>
    /// </summary>
    private static string EditForms(string what, string example) =>
        $"""
           For {what}, EITHER
           - EDITS, best when the fix touches a few places: one ```edit block per file, its path on the
             first line (`// file: {example}`), then one or more of
             <<<<<<< SEARCH
             lines copied EXACTLY from the current file — enough of them to be found only once
             =======
             the lines that replace them
             >>>>>>> REPLACE
           - or THE COMPLETE FILE, when most of it changes: one fenced block with its `file:` header on the
             first line, exactly as when it was built.
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
            new GauntletSubject(files, verdict.Picture, Described(verdict), verdict.Report, Layout: null, kind));
    }

    /// <summary>
    /// What the critics are told the picture is: the page, and — when the gate measured it — what the
    /// browser reported about its layout, stated as fact. A critic reading the source cannot tell that a
    /// panel covers the scene; the browser can, and a critic that sees does not need to guess either.
    /// </summary>
    internal static string Described(GateResult verdict)
    {
        if (verdict.Advisories.Count == 0) return PageDescription;

        return PageDescription + Environment.NewLine
               + "MEASURED ON THE PAGE by the gate's browser (facts, not opinions):" + Environment.NewLine
               + string.Join(Environment.NewLine, verdict.Advisories.Select(a => "- " + a.Message));
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
            // uses, and the ones its brief names, which is the planning nobody did. Measured 2026-09-18: a
            // footprint brief naming TradeClassifier, FootprintTimeBucketer and the instruments block
            // fell back here, got none of those cards, and its builder guessed their APIs for six rounds.
            // A visualizer never gets the trading blocks, even from a brief that says it must not use them.
            ids.AddRange([BlockCatalog.UnitBlock, "settings", "market", "schedule", BlockCatalog.UiBlock]);
            var strategy = plan.Contract.Kind == AuthoringKind.Strategy;
            ids.AddRange(_catalog.Mentioned(task.Intent).Where(id => strategy || id is not ("orders" or "portfolio")));
            if (strategy) ids.AddRange(["orders", "portfolio"]);
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

        if (contract.PageModules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  Page modules — each written by its own builder; the shell loads them and calls EXACTLY these exports:");
            foreach (var module in contract.PageModules)
            {
                text.AppendLine($"    - {module.File}: {module.Purpose}");
                if (module.Exports.Length > 0) text.AppendLine($"        {module.Exports}");
            }
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
