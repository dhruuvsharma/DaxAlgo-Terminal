using System.Text;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>
/// What each participant in a run is told.
///
/// <para>Role instructions are kept apart from the shared pack on purpose: the pack is the cached prefix
/// of every request in a run, so anything that varies per turn must arrive as the role instruction
/// instead. A role appended to the shared pack is how a prompt cache silently stops working, which costs
/// money and shows up nowhere.</para>
/// </summary>
public static class SwarmPrompts
{
    /// <summary>
    /// The orchestrator. It writes the contract and the tasks, and writes <b>no code at all</b>.
    ///
    /// <para>The two rules that make the fan-out safe are stated as rules rather than hoped for: one file
    /// per task, and every helper's signature fixed here. Without the first, two builders write the same
    /// file and the second silently wins; without the second, two builders working simultaneously invent
    /// two different ways to meet and neither compiles.</para>
    ///
    /// <para>It is also told to plan the SMALLEST thing that satisfies the brief. A planner rewarded for
    /// looking thorough will decompose a moving average into six tasks, and the user pays per task.</para>
    /// </summary>
    public static string Planner(AuthoringKind kind, int maxTasks) =>
        $$"""
        YOUR ROLE: Planner. You do not write code. You write the plan the builders follow.

        Return ONE ```json fenced block and nothing else that matters. This shape:

        {
          "contract": {
            "typeName": "PascalCaseUnitName",
            "dataRequirement": "Bars | L1 | Depth | TradeTape (what the unit subscribes to)",
            "parameters": [{ "name": "lookback", "label": "Look-back", "type": "int", "default": "20" }],
            "panels": [{ "id": "ladder", "title": "Depth ladder", "shows": "resting size per price",
                         "typeName": "LadderPanel", "widget": "Ladder.Draw" }],
            "helpers": [{ "typeName": "LadderPanel", "purpose": "paints the depth ladder",
                          "signature": "public void Draw(IRenderSurface surface, IReadOnlyList<DepthLevel> book)" }]
          },
          "milestones": [
            { "id": "m1", "title": "The maths", "tasks": [
              { "id": "t1", "title": "Rolling imbalance", "kind": "Maths", "ownedFile": "Imbalance.cs",
                "intent": "one paragraph: exactly what this file must contain", "dependsOn": [] }
            ]}
          ],
          "rubric": ["what would make this excellent, one line each — the bar critics judge against"],
          "openQuestions": ["anything you could not settle from the brief"]
        }

        RULES, and the first two are what make parallel work possible at all:

        1. ONE FILE PER TASK, and no two tasks may name the same `ownedFile`. Builders run at the same
           time and cannot see each other's work; two tasks writing one file means one of them is thrown
           away.
        2. EVERY helper's `signature` is fixed here, exactly as it will be written. Builders are given
           the contract and nothing else about each other. A signature you leave vague is one two
           builders will guess differently.
        3. Exactly ONE task has kind "Signal" and it owns the hostable class — the single class
           implementing {{(kind == AuthoringKind.Visualizer ? "IVisualizer" : "IStrategyKernel")}}. Every
           other task writes a helper type in its own file.
        4. `kind` is one of: Maths, Signal, Panel, Schema, Book.
        5. `dependsOn` names task ids that must finish first. Keep it minimal: tasks with no dependency
           run together, and a chain of dependencies is a chain of waits the user pays for in wall-clock.
        6. AT MOST {{maxTasks}} TASKS. Plan the smallest thing that satisfies the brief. A moving average
           is one task. If the brief is small, one task is the right answer and you should say so.
        7. `rubric` is what "good" looks like for THIS brief, in concrete checkable lines — "the price
           axis is labelled in ticks", not "looks professional".
        8. If the brief leaves something genuinely undecided, put it in `openQuestions` and pick a
           sensible default anyway. Do not stop to ask; the user can correct a built unit.
        9. EVERY panel names the `widget` that draws it, from the drawing pack's table — a price chart
           with candles, gutter, time axis, crosshair, zoom and pan is `PriceChart.Draw`; a depth ladder
           is `Ladder.Draw`; a footprint is `Footprint.Draw`; a correlation grid is `Heatmap.Draw`; a
           volume profile is `VolumeProfile.Draw`; rows and columns are `Table.Draw`; stated numbers are
           `Tiles.Draw`. Write `"none"` only when the picture genuinely has no widget, which is rare —
           a 3D scene, a novel diagram. Builders are held to what you write here, so a panel you leave
           unnamed is a panel drawn out of rectangles.
        """;

    /// <summary>
    /// A builder. It is given the contract, its own task, and the files its task depends on — never the
    /// other builders' reasoning, and never the whole conversation.
    ///
    /// <para>The one-file rule is repeated here because it is the rule most likely to be broken by a
    /// helpful model: asked for a panel, a model will cheerfully rewrite the kernel to call it. The
    /// runner drops anything outside the owned file, so a builder that ignores this simply wastes the
    /// user's turn.</para>
    /// </summary>
    public static string Builder(BuildTask task, UnitContract contract) =>
        task.OwnsAllFiles ? Whole(task, contract) : $"""
        YOUR ROLE: Builder. You are writing ONE file of a unit other builders are writing at the same time.

        YOUR FILE: {task.OwnedFile}
        YOUR TASK: {task.Title} — {task.Intent}

        RULES:

        1. Return ONLY {task.OwnedFile}, as one ```csharp block with `// file: {task.OwnedFile}` on its
           first line. Anything you write outside that file is discarded, including "small fixes" to
           somebody else's.
        2. The contract below is fixed. Match the type names and signatures EXACTLY. You cannot see the
           other files and they cannot see yours; the contract is the only thing you agree on.
        3. Write the COMPLETE file. Not a fragment, not a diff, no "// ... rest unchanged".
        4. {(task.Kind == TaskKind.Signal
                ? "You own the hostable class. Construct and call the helpers by their contract signatures; "
                  + "do not re-implement what a helper already does."
                : "You are writing a helper type. Do not declare a class implementing IStrategyKernel or "
                  + "IVisualizer — exactly one file does that, and it is not yours.")}
        {Painting(task, contract)}
        {Contract(contract)}
        """;

    /// <summary>
    /// What a task that PAINTS is told, on top of the ordinary builder rules.
    ///
    /// <para><b>Because the catalogue alone did not work.</b> Six live builds loaded the drawing pack —
    /// verified, not assumed — read its opening line "reach for a widget before you draw anything by
    /// hand", and produced 28 <c>SetStyle</c> calls, 15 <c>Text</c>, 12 <c>Rect</c> and one widget call
    /// between them. A depth ladder was built from rectangles beside <c>Ladder</c>; a footprint beside
    /// <c>Footprint</c>; a correlation grid beside <c>Heatmap</c>. The one unit that did call
    /// <c>PriceChart.Draw</c> came back with candles, a price gutter, a time axis, a crosshair, the
    /// session range shaded over the bars and the entries marked on them — the difference is not
    /// subtle.</para>
    ///
    /// <para>A general instruction in a 13,000-character pack is a suggestion. The same instruction in
    /// the role, naming the exact call this file must make, is the job.</para>
    /// </summary>
    private static string Painting(BuildTask task, UnitContract contract)
    {
        if (task.Kind is not (TaskKind.Panel or TaskKind.Signal)) return string.Empty;

        var mine = contract.Panels
            .Where(p => string.Equals(p.TypeName, TypeIn(task.OwnedFile), StringComparison.OrdinalIgnoreCase)
                        || task.Kind == TaskKind.Signal)
            .Where(p => Decided(p.Widget))
            .Select(p => $"{p.Id} \"{p.Title}\" → {p.Widget}")
            .ToArray();

        var named = mine.Length > 0
            ? "5. DRAW WITH THE WIDGET THE CONTRACT NAMES: " + string.Join("; ", mine) + ". Call it, then "
              + "add your own marks ON TOP — overlays, markers, levels, shading. Do not rebuild what it "
              + "already draws."
            : "5. Reach for a widget before drawing by hand. The drawing pack's table has one for almost "
              + "every trading picture; a hand-rolled version of a widget is refused at review.";

        return $"""

        {named}
        6. A chart means a TRADING chart. `PriceChart.Draw(surface, bars)` is the whole thing —
           candles, price gutter, time axis, volume, last-price tag, legend, crosshair, and the wheel
           and the drag already wired. A picture assembled from `surface.Rect` and `surface.Text` has
           none of that and reads as a mock-up of a chart rather than one.
        """;
    }

    /// <summary>True when the planner actually chose a widget. Blank is "did not say"; the literal
    /// word "none" is the deliberate answer for a picture the catalogue does not cover.</summary>
    internal static bool Decided(string? widget) =>
        !string.IsNullOrWhiteSpace(widget)
        && !string.Equals(widget.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>The type a file is expected to declare, so a panel spec can be matched to its builder
    /// without the planner having to repeat the file name.</summary>
    private static string TypeIn(string ownedFile) =>
        ownedFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? ownedFile[..^3]
            : ownedFile;

    /// <summary>
    /// The instruction for the single-task fallback plan: write the whole unit, in as many files as it
    /// takes. This is what the conversation always did, and it is what the fallback exists to be.
    /// </summary>
    private static string Whole(BuildTask task, UnitContract contract) =>
        $"""
        YOUR ROLE: Builder. Write the whole unit.

        WHAT TO BUILD: {task.Intent}

        RULES:

        1. Return the COMPLETE file set — one ```csharp block per file, each with `// file: Name.cs` on
           its first line. Use as many files as the unit needs.
        2. Exactly one class implements the unit contract. Helpers are ordinary types beside it.
        3. Complete files only. Not a fragment, not a diff, no "// ... rest unchanged".

        {Contract(contract)}
        """;

    /// <summary>
    /// A repair turn. It gets the diagnostics and its own file, and deliberately <b>not</b> the brief.
    ///
    /// <para>A fixer that can see the original intent starts redesigning instead of repairing. The
    /// remedy on each finding says what to change; that is the whole input the job needs.</para>
    /// </summary>
    public static string Fixer(BuildTask task, UnitContract contract) =>
        task.OwnsAllFiles ? FixWhole(contract) : $"""
        YOUR ROLE: Fixer. Repair ONE file so the unit compiles and passes verification.

        YOUR FILE: {task.OwnedFile}

        RULES:

        1. Return ONLY {task.OwnedFile}, complete, in one ```csharp block with its `// file:` header.
        2. Change as little as possible. You are repairing, not rewriting: a rewrite loses the parts
           that already passed and is how a run spends its budget going sideways.
        3. The diagnostics name what is wrong and what to change. If a diagnostic points at a file that
           is not yours, the fault is a mismatch with the contract — change YOUR file to match the
           contract, never the contract to match your file.

        {Contract(contract)}
        """;

    /// <summary>Repairing the single-task fallback, which owns every file there is.</summary>
    private static string FixWhole(UnitContract contract) =>
        $"""
        YOUR ROLE: Fixer. Repair the unit so it compiles and passes verification.

        RULES:

        1. Return the COMPLETE file set — every file, each in its own ```csharp block with its
           `// file:` header. A file you leave out is a file that disappears.
        2. Change as little as possible. You are repairing, not rewriting: a rewrite loses the parts
           that already passed and is how a run spends its budget going sideways.

        {Contract(contract)}
        """;

    /// <summary>The contract, rendered for a prompt. Rendered rather than serialised: a builder reads
    /// this, and a wall of JSON is read less carefully than a list.</summary>
    public static string Contract(UnitContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var text = new StringBuilder();
        text.AppendLine("THE CONTRACT (fixed — every builder writes against this):");
        text.AppendLine();
        text.AppendLine($"  Hostable class: {contract.TypeName} ({(contract.Kind == AuthoringKind.Visualizer ? "IVisualizer" : "IStrategyKernel")})");
        text.AppendLine($"  Data requirement: {contract.DataRequirement}");

        if (contract.Parameters.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  Parameters (declared in the schema AND actually read by the code):");
            foreach (var p in contract.Parameters)
                text.AppendLine($"    - {p.Name} ({p.Type}, default {p.Default}) — \"{p.Label}\"");
        }

        if (contract.Panels.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  Panels:");
            foreach (var panel in contract.Panels)
            {
                text.Append($"    - {panel.Id} \"{panel.Title}\" — {panel.Shows} (painted by {panel.TypeName})");
                // AN EMPTY FIELD IS THE PLANNER SAYING NOTHING, NOT THE PLANNER SAYING "NONE".
                //
                // This first rendered "no widget fits; build the picture yourself" whenever the field
                // was blank, and a live run came back with a panel whose own header comment opened
                // "No widget fits this picture" — the model quoting an instruction it had been handed
                // as a finding. A default that licenses the exact behaviour the field exists to stop is
                // worse than no field. Silence renders as silence; the builder's standing rule to reach
                // for a widget still applies. A planner that means none says so.
                text.AppendLine(Decided(panel.Widget) ? $" — DRAW IT WITH {panel.Widget}" : string.Empty);
            }
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

        return text.ToString();
    }
}
