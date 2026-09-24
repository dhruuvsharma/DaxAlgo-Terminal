using System.Collections.Concurrent;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What the delivered 2026-09-20 Nemotron Battlefield run spent its 746,727 tokens on, held in place:
///
/// <para>CRITICS were shown a source cut off at 60,000 characters in the middle of a 72,081-character
/// scene module, and reported the files after it as missing and the scene as truncated — about files
/// that compiled and ran. PAGE TASKS were handed every page file as their own, so each module was sent
/// its neighbours and the shell's repair rewrote five files of which one was kept. BUILDERS never read
/// the brief, so the depth panel's "bottom-left" reached no task. REPAIRS re-sent whole files for
/// faults a few lines wide. And NOTHING MEASURED the page, so a depth chart covering the whole window
/// was delivered as a battlefield.</para>
/// </summary>
public sealed class HyperionPaysForTheWorkNotTheViewTests
{
    // ── what a critic is shown ──────────────────────────────────────────────────────────────────

    private static string Filler(string name, int characters)
    {
        var lines = new List<string>();
        var total = 0;
        for (var i = 0; total < characters; i++)
        {
            var line = $"  const {name}{i} = {i}; // body of {name}";
            lines.Add(line);
            total += line.Length + 1;
        }

        return string.Join('\n', lines);
    }

    /// <summary>Shaped like the Nemotron unit: a 72,081-character scene among smaller files.</summary>
    private static GauntletSubject Battlefield() => new(
        [
            new StrategyFile("LiquidityBattlefield.cs", "public sealed class LiquidityBattlefield : IUnit\n{\n" + Filler("unit", 30_000) + "\n}\n// END OF THE UNIT"),
            new StrategyFile("ui/index.html", "<html><body><canvas id=\"scene\"></canvas><script type=\"module\" src=\"scene.js\"></script></body></html>\n<!-- END OF THE SHELL -->"),
            new StrategyFile("ui/scene.js", "export function mountScene(canvas, getState) {\n" + Filler("scene", 72_000) + "\n}\n// END OF THE SCENE"),
            new StrategyFile("ui/hud.js", "export function mountHUD(root) {\n" + Filler("hud", 12_000) + "\n}\n// END OF THE HUD"),
            new StrategyFile("ui/feed.js", "export function mountFeed(root) {\n" + Filler("feed", 9_000) + "\n}\n// END OF THE FEED"),
        ],
        Raster: null,
        "the page",
        new VerificationReport([VerificationStep.Pass(VerificationRung.Compile), VerificationStep.Pass(VerificationRung.DrawProbe)]),
        Layout: null,
        AuthoringKind.Visualizer);

    private static CriticDefinition Critic(string id) => BlocksCritics.All.Single(c => c.Id == id);

    [Fact]
    public async Task A_critic_is_shown_every_file_whole_or_as_an_outline_and_never_one_cut_off_part_way()
    {
        var client = new Scripted(_ => "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```");
        await new ModelCritic(client, Critic(Critics.Integration), "PACK", canSeeImages: false)
            .JudgeAsync(Battlefield(), ReferenceBar.None);

        var message = client.Messages.Single();

        // Every file is named up front, with how it is shown.
        message.Should().Contain("THE UNIT'S FILES — every one of them exists");
        foreach (var name in new[] { "LiquidityBattlefield.cs", "ui/index.html", "ui/scene.js", "ui/hud.js", "ui/feed.js" })
            message.Should().Contain($"  - {name} · ");
        message.Should().MatchRegex(@"ui/scene\.js · [\d,]+ characters · OUTLINE");

        // Whatever is shown whole is shown to its last line; the scene is its outline, not its first 50k.
        foreach (var end in new[] { "END OF THE UNIT", "END OF THE SHELL", "END OF THE HUD", "END OF THE FEED" })
            message.Should().Contain(end);
        message.Should().NotContain("END OF THE SCENE");
        message.Should().NotContain("scene100 =", "no part of a file too large to show whole is pasted in");
        message.Should().Contain("L1: export function mountScene(canvas, getState)");
    }

    [Fact]
    public async Task Each_critic_reads_first_what_it_judges()
    {
        var page = new Scripted(_ => "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```");
        await new ModelCritic(page, Critic(Critics.Picture), "PACK", canSeeImages: false)
            .JudgeAsync(Battlefield(), ReferenceBar.None);

        var logic = new Scripted(_ => "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```");
        await new ModelCritic(logic, Critic(Critics.MarketLogic), "PACK", canSeeImages: false)
            .JudgeAsync(Battlefield(), ReferenceBar.None);

        static int At(string message, string file) => message.IndexOf($"// file: {file}", StringComparison.Ordinal);

        var reading = page.Messages.Single();
        At(reading, "ui/index.html").Should().BeLessThan(At(reading, "ui/hud.js"), "the page reader reads the shell first");
        reading.Should().Contain("END OF THE HUD");

        var judging = logic.Messages.Single();
        At(judging, "LiquidityBattlefield.cs").Should().BeLessThan(At(judging, "ui/index.html"), "the maths critic reads the C# first");
        judging.Should().Contain("END OF THE UNIT");
    }

    [Fact]
    public async Task A_finding_that_calls_a_file_the_gate_ran_missing_or_cut_off_is_dropped_and_real_ones_are_kept()
    {
        // The four the Nemotron critics actually returned, and two that are true.
        const string reply = """
            ```json
            { "verdict": "broken", "findings": [
              { "code": "missing-ui-modules", "problem": "index.html imports ./hud.js and ./feed.js but these files are not provided.", "remedy": "Provide them.", "file": "ui/index.html" },
              { "code": "scenejs-truncated-syntax-error", "problem": "scene.js ends mid-statement at `performance.no` — a syntax error.", "remedy": "Complete it.", "file": "ui/scene.js" },
              { "code": "missing-unit-class", "problem": "No class implements IUnit.", "remedy": "Add one.", "file": "BattlefieldVisualizer.cs" },
              { "code": "no-viewport-meta", "problem": "ui/index.html is missing a viewport meta tag, so it scales wrongly.", "remedy": "Add one.", "file": "ui/index.html" },
              { "code": "app-js-missing", "problem": "index.html loads app.js, but that file is missing from the unit.", "remedy": "Write it.", "file": "ui/app.js" }
            ] }
            ```
            """;

        var verdict = await new ModelCritic(new Scripted(_ => reply), Critic(Critics.Integration), "PACK", canSeeImages: false)
            .JudgeAsync(Battlefield(), ReferenceBar.None);

        verdict.Findings.Select(f => f.Code).Should().BeEquivalentTo(
            ["integration.no-viewport-meta", "integration.app-js-missing"],
            "what is wrong inside a file that exists, and a file that does not, are both real");
        verdict.Verdict.Should().Contain("3 finding(s) dropped");
    }

    // ── who is shown which file ─────────────────────────────────────────────────────────────────

    private const string SplitPlan = """
        {
          "contract": {
            "typeName": "Battlefield",
            "topics": [ { "name": "battle", "direction": "to-page", "payload": "{ price: number }" } ],
            "pageModules": [
              { "file": "ui/scene.js", "purpose": "the 3D field", "exports": "export function mountScene(el): { update(s): void }" },
              { "file": "ui/depth.js", "purpose": "the depth chart", "exports": "export function mountDepth(el): { update(d): void }" }
            ]
          },
          "milestones": [
            { "id": "m1", "title": "Build", "tasks": [
              { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "Battlefield.cs", "blocks": ["unit", "ui"], "intent": "the unit", "dependsOn": [] },
              { "id": "t2", "title": "Shell", "kind": "Ui", "ownedFile": "ui/index.html", "blocks": ["ui"], "intent": "the shell", "dependsOn": [] },
              { "id": "t3", "title": "Scene", "kind": "Ui", "ownedFile": "ui/scene.js", "blocks": ["ui"], "intent": "the scene", "dependsOn": [] },
              { "id": "t4", "title": "Depth", "kind": "Ui", "ownedFile": "ui/depth.js", "blocks": ["ui"], "intent": "the depth chart", "dependsOn": [] }
            ]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    private static BuildPlan Split() => BuildPlanReader.Read("```json\n" + SplitPlan + "\n```", AuthoringKind.Visualizer)!;

    private static SwarmContext Written(string brief = "battlefield") => new(brief,
    [
        new StrategyFile("Battlefield.cs", "UNIT-BODY"),
        new StrategyFile("ui/index.html", "SHELL-BODY"),
        new StrategyFile("ui/app.js", "APP-BODY"),
        new StrategyFile("ui/scene.js", "export function mountScene(canvas, getState) { SCENE-BODY }"),
        new StrategyFile("ui/depth.js", "DEPTH-BODY"),
    ]);

    [Fact]
    public void A_page_module_is_shown_its_own_file_and_nobody_else_s()
    {
        var plan = Split();
        var context = Written();
        var scene = plan.Tasks.Single(t => t.Id == "t3");
        var shell = plan.Tasks.Single(t => t.Id == "t2");

        var build = context.ComposeBuild(scene, plan);
        build.Should().Contain("SCENE-BODY");
        build.Should().NotContainAny("DEPTH-BODY", "SHELL-BODY", "APP-BODY");

        var repair = context.ComposeRepair(scene, [new VerificationFinding("page.threw", "boom", null, "ui/scene.js")], plan);
        repair.Should().Contain("SCENE-BODY");
        repair.Should().NotContainAny("DEPTH-BODY", "SHELL-BODY", "APP-BODY");

        var shellRepair = context.ComposeRepair(shell, [new VerificationFinding("page.layout.covered", "covered", null, "ui/index.html")], plan);
        shellRepair.Should().Contain("SHELL-BODY").And.Contain("APP-BODY", "a page file no module owns is the shell's");
        shellRepair.Should().NotContainAny("SCENE-BODY", "DEPTH-BODY");
    }

    [Fact]
    public void A_single_page_task_still_owns_the_whole_folder()
    {
        var single = BuildPlanReader.Read("```json\n" + SplitPlan
            .Replace("\"ownedFile\": \"ui/scene.js\"", "\"ownedFile\": \"Scene.cs\"")
            .Replace("\"ownedFile\": \"ui/depth.js\"", "\"ownedFile\": \"Depth.cs\"") + "\n```", AuthoringKind.Visualizer)!;

        var repair = Written().ComposeRepair(single.Tasks.Single(t => t.Id == "t2"), [new VerificationFinding("page.threw", "boom", null, "ui/index.html")], single);

        repair.Should().Contain("SHELL-BODY").And.Contain("APP-BODY").And.Contain("SCENE-BODY").And.Contain("DEPTH-BODY");
    }

    [Fact]
    public void A_builder_reads_the_brief_and_a_repair_does_not()
    {
        const string brief = "Depth panel (bottom-left, collapsible): a cumulative depth chart.";
        var plan = Split();
        var context = Written(brief);

        context.ComposeBuild(plan.Tasks.Single(t => t.Id == "t4"), plan).Should().Contain(brief);
        context.ComposeRepair(plan.Tasks.Single(t => t.Id == "t4"), [new VerificationFinding("page.threw", "boom", null, "ui/depth.js")], plan)
            .Should().NotContain(brief, "a repair works from its findings");

        // The fallback task's intent IS the brief, so it is not repeated.
        var fallback = BuildPlan.Single(brief, AuthoringKind.Visualizer);
        var whole = new SwarmContext(brief).ComposeBuild(fallback.Tasks.Single(), fallback);
        whole.Split(brief).Length.Should().Be(2, "the brief appears exactly once");
    }

    [Fact]
    public async Task The_shell_is_built_after_its_modules_and_calls_what_they_actually_export()
    {
        var client = new Scripted(role =>
            role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + SplitPlan + "\n```"
            : role.Contains("ONE MODULE of the unit's page: ui/scene.js", StringComparison.Ordinal) ? "```js\n// file: ui/scene.js\nexport function mountScene(canvas, getState) {\n  return { update() {} };\n}\n```"
            : role.Contains("ONE MODULE of the unit's page: ui/depth.js", StringComparison.Ordinal) ? "```js\n// file: ui/depth.js\nexport const mountDepth = (root, options) => {\n  return { update() {} };\n};\n```"
            : role.Contains("PAGE SHELL", StringComparison.Ordinal) ? "```html\n<!-- file: ui/index.html -->\n<script type=\"module\" src=\"scene.js\"></script>\n```"
            : "```csharp\n// file: Battlefield.cs\npublic sealed class Battlefield { }\n```");

        await new SwarmRunner(client, new Passes(), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("battlefield", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 1, MaxTasks: 8)));

        var calls = client.Calls.ToArray();
        int Index(string marker) => Array.FindIndex(calls, c => c.Role.Contains(marker, StringComparison.Ordinal));

        Index("PAGE SHELL").Should().BeGreaterThan(Index("ONE MODULE of the unit's page: ui/scene.js"))
            .And.BeGreaterThan(Index("ONE MODULE of the unit's page: ui/depth.js"));

        var shell = calls[Index("PAGE SHELL")].Message;
        shell.Should().Contain("export function mountScene(canvas, getState)", "the shell calls the module as it was written");
        shell.Should().Contain("export const mountDepth = (root, options) =>");
    }

    [Fact]
    public async Task A_plan_with_no_page_gets_one_and_it_is_written_against_the_unit_s_own_sends()
    {
        // NIM's GLM 5.3 Flash at a low effort, 2026-09-24: the Battlefield brief planned as ONE task, the
        // unit's class, and nothing that would ever write ui/index.html.
        const string unitOnly = """
            { "contract": { "typeName": "Battlefield" },
              "milestones": [ { "id": "m1", "title": "Build", "tasks": [
                { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "Battlefield.cs", "blocks": ["unit", "ui"], "intent": "the unit", "dependsOn": [] }
              ]} ] }
            """;

        var client = new Scripted(role =>
            role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + unitOnly + "\n```"
            : role.Contains("PAGE", StringComparison.Ordinal) ? "```html\n<!-- file: ui/index.html -->\n<p>battle</p>\n```"
            : "```csharp\n// file: Battlefield.cs\npublic sealed class Battlefield { void Tick() => context.Ui.Send(\"battle\", new { price = 1 }); }\n```");

        var run = await new SwarmRunner(client, new Passes(), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("battlefield brief", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 1, MaxTasks: 8)));

        run.Plan.Tasks.Should().Contain(t => t.OwnedFile == "ui/index.html" && t.DependsOn.Contains("t1"));
        run.Files.Should().Contain(f => f.Name == "ui/index.html");

        var page = client.Calls.Single(c => c.Role.Contains("PAGE", StringComparison.Ordinal));
        page.Message.Should().Contain("ALREADY WRITTEN — Battlefield.cs").And.Contain("context.Ui.Send(\"battle\"",
            "with no topics in the contract, the page builder reads what the unit actually sends");
    }

    [Fact]
    public void A_task_that_names_several_files_owns_the_page_s_entry_and_the_shell_still_owns_the_rest()
    {
        // NIM's DeepSeek V4.1 Flash, 2026-09-24, verbatim: one ownedFile naming three page files.
        var plan = BuildPlanReader.Read("```json\n" + SplitPlan.Replace(
            "\"ownedFile\": \"ui/index.html\"", "\"ownedFile\": \"ui/index.html, ui/style.css, ui/app.js\"") + "\n```", AuthoringKind.Visualizer)!;

        var shell = plan.Tasks.Single(t => t.Id == "t2");
        shell.OwnedFile.Should().Be("ui/index.html");
        shell.OwnsPageShell.Should().BeTrue();
        shell.Owns("ui/style.css").Should().BeTrue();
        shell.Owns("ui/app.js").Should().BeTrue();
        shell.Owns("ui/scene.js").Should().BeFalse();

        // And a C# list takes its first file.
        BuildPlanReader.Read("```json\n" + SplitPlan.Replace("\"ownedFile\": \"Battlefield.cs\"", "\"ownedFile\": \"Battlefield.cs and Helpers.cs\"") + "\n```",
            AuthoringKind.Visualizer)!.Tasks.Single(t => t.Id == "t1").OwnedFile.Should().Be("Battlefield.cs");
    }

    [Fact]
    public async Task A_run_that_is_continued_keeps_what_it_wrote_and_builds_only_what_is_missing()
    {
        // Two Battlefield runs on NIM, 2026-09-24, reached a unit and page that passed the gate and were cut
        // off by the time limit inside the critics. Continuing one must not pay for the builds again.
        var client = new Scripted(role =>
            role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + SplitPlan + "\n```"
            : role.Contains("ONE MODULE of the unit's page: ui/depth.js", StringComparison.Ordinal) ? "```js\n// file: ui/depth.js\nexport function mountDepth(el) { return { update() {} }; }\n```"
            : "```csharp\n// file: Battlefield.cs\npublic sealed class Battlefield { }\n```");

        var kept = new[]
        {
            new StrategyFile("Battlefield.cs", "public sealed class Battlefield { }"),
            new StrategyFile("ui/index.html", "<html>shell</html>"),
            new StrategyFile("ui/scene.js", "export function mountScene(el) { return { update() {} }; }"),
        };

        var run = await new SwarmRunner(client, new Passes(), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("battlefield", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 1, MaxTasks: 8),
                Existing: kept, Plan: Split(), MayAsk: false, Continue: true));

        client.Calls.Should().ContainSingle("only the module that was never written is built")
            .Which.Role.Should().Contain("ONE MODULE of the unit's page: ui/depth.js");
        run.Outcome.Should().Be(SwarmOutcome.Delivered);
        run.Files.Single(f => f.Name == "ui/index.html").Content.Should().Be("<html>shell</html>");
    }

    [Fact]
    public void A_plan_that_has_a_page_is_left_as_it_is()
    {
        var plan = Split();
        new BlocksSwarmDialect().Complete(plan).Should().BeSameAs(plan);
    }

    // ── repairs as edits ────────────────────────────────────────────────────────────────────────

    private const string Unit = "public sealed class Unit\n{\n    public int A => 1;\n    public int B => 2;\n}";

    [Fact]
    public void An_edit_replaces_only_its_lines_and_tolerates_reindented_copies()
    {
        var files = new[] { new StrategyFile("Unit.cs", Unit), new StrategyFile("ui/scene.js", "function a() {\n  return 1;\n}\n") };

        var outcome = EditBlocks.Apply(EditBlocks.Parse("""
            ```edit
            // file: Unit.cs
            <<<<<<< SEARCH
                public int A => 1;
            =======
                public int A => 42;
            >>>>>>> REPLACE
            ```
            ```edit
            // file: scene.js
            <<<<<<< SEARCH
            return 1;
            =======
              return 2;
            >>>>>>> REPLACE
            ```
            """), files, defaultFile: null);

        outcome.Failed.Should().BeEmpty();
        outcome.Edited.Single(f => f.Name == "Unit.cs").Content.Should().Contain("A => 42").And.Contain("B => 2");
        outcome.Edited.Single(f => f.Name == "ui/scene.js").Content.Should().Be("function a() {\n  return 2;\n}\n",
            "a bare page name is the page's file, and a line copied without its indentation still matches");
    }

    [Fact]
    public void A_file_whose_edits_do_not_all_match_is_left_exactly_as_it_was()
    {
        var outcome = EditBlocks.Apply(EditBlocks.Parse("""
            // file: Unit.cs
            <<<<<<< SEARCH
                public int A => 1;
            =======
                public int A => 42;
            >>>>>>> REPLACE
            <<<<<<< SEARCH
                public int Z => 9;
            =======
                public int Z => 10;
            >>>>>>> REPLACE
            """), [new StrategyFile("Unit.cs", Unit)], defaultFile: "Unit.cs");

        outcome.Edited.Should().BeEmpty("half a fix is not applied");
        outcome.Failed.Should().Equal("Unit.cs");
        outcome.Unmatched.Should().ContainSingle().Which.Should().Contain("public int Z => 9;");
    }

    private const string Socket = "        lock (_gate) _cts = cts;\n        _ = RunLoopAsync(cts);\n    }\n";

    [Fact]
    public void A_stray_divider_before_REPLACE_is_dropped_rather_than_written_into_the_file()
    {
        // NIM's Kimi K3, 2026-09-24, verbatim in shape: a second "=======" closing the replacement.
        var outcome = EditBlocks.Apply(EditBlocks.Parse("""
            ```edit
            // file: LiqSocket.cs
            <<<<<<< SEARCH
                    _ = RunLoopAsync(cts);
            =======
                    _ = RunLoopAsync(cts, _source, _symbol);
            =======
            >>>>>>> REPLACE
            ```
            """), [new StrategyFile("LiqSocket.cs", Socket)], defaultFile: "LiqSocket.cs");

        outcome.Failed.Should().BeEmpty();
        outcome.Edited.Single().Content.Should().Contain("RunLoopAsync(cts, _source, _symbol);").And.NotContain("=======");
    }

    [Fact]
    public void A_block_with_a_divider_in_the_middle_of_its_replacement_is_never_applied()
    {
        // Kimi's next turn, trying to search for the marker line the stray divider had left: the block
        // cannot be read unambiguously, so the file is asked for whole instead.
        var outcome = EditBlocks.Apply(EditBlocks.Parse("""
            // file: LiqSocket.cs
            <<<<<<< SEARCH
                    _ = RunLoopAsync(cts);
            =======
                }
            =======
                    _ = RunLoopAsync(cts);
                }
            >>>>>>> REPLACE
            """), [new StrategyFile("LiqSocket.cs", Socket)], defaultFile: "LiqSocket.cs");

        outcome.Edited.Should().BeEmpty();
        outcome.Failed.Should().Equal("LiqSocket.cs");
        outcome.Unmatched.Single().Should().Contain("second =======");
    }

    [Fact]
    public void Edits_that_would_leave_a_marker_line_in_the_file_are_refused()
    {
        var outcome = EditBlocks.Apply(
            [new FileEdit("LiqSocket.cs", "        _ = RunLoopAsync(cts);", "        _ = RunLoopAsync(cts);\n>>>>>>> REPLACE")],
            [new StrategyFile("LiqSocket.cs", Socket)], defaultFile: null);

        outcome.Edited.Should().BeEmpty();
        outcome.Unmatched.Single().Should().Contain("would leave");
    }

    [Fact]
    public void An_edit_is_never_read_as_the_file_it_edits()
    {
        const string reply = "```csharp\n// file: Unit.cs\n<<<<<<< SEARCH\n    public int A => 1;\n=======\n    public int A => 42;\n>>>>>>> REPLACE\n```";

        CodegenCodeExtractor.ExtractFiles(reply).Should().BeEmpty();
        CodegenCodeExtractor.ExtractUnitFiles(reply).Should().BeEmpty();
        CodegenCodeExtractor.ExtractUnitFiles(reply.Replace("```csharp\n", string.Empty).Replace("\n```", string.Empty)).Should().BeEmpty();
    }

    private const string TwoTaskPlan = """
        { "contract": { "typeName": "Unit" },
          "milestones": [ { "id": "m1", "title": "Build", "tasks": [
            { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "Unit.cs", "blocks": ["unit"], "intent": "the unit", "dependsOn": [] },
            { "id": "t2", "title": "Page", "kind": "Ui", "ownedFile": "ui/index.html", "blocks": ["ui"], "intent": "the page", "dependsOn": [] }
          ]} ] }
        """;

    private static Scripted UnitWithFixer(Func<StrategyCodegenRequest, string> fixer) => new((role, request) =>
        role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + TwoTaskPlan + "\n```"
        : role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal) ? fixer(request)
        : role.Contains("the page", StringComparison.Ordinal) && role.Contains("PAGE", StringComparison.Ordinal) ? "```html\n<!-- file: ui/index.html -->\n<p>page</p>\n```"
        : "```csharp\n// file: Unit.cs\n" + Unit + "\n```");

    [Fact]
    public async Task A_repair_that_answers_with_edits_changes_only_those_lines()
    {
        var client = UnitWithFixer(_ => "```edit\n// file: Unit.cs\n<<<<<<< SEARCH\n    public int A => 1;\n=======\n    public int A => 42;\n>>>>>>> REPLACE\n```");

        var run = await new SwarmRunner(client, new FailsOnceIn("Unit.cs"), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("unit", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 2, MaxTasks: 4)));

        run.Outcome.Should().Be(SwarmOutcome.Delivered);
        run.Files.Single(f => f.Name == "Unit.cs").Content.Should().Contain("A => 42").And.Contain("B => 2");
        client.Calls.Count(c => c.Role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal)).Should().Be(1);
        client.Calls.Single(c => c.Role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal)).Role
            .Should().Contain(EditBlocks.SearchMarker, "the fixer is offered edits");
    }

    [Fact]
    public async Task An_edit_that_matches_nothing_is_asked_for_once_more_whole()
    {
        var client = UnitWithFixer(request => request.Messages.Count == 3
            ? "```csharp\n// file: Unit.cs\npublic sealed class Unit\n{\n    public int A => 7;\n}\n```"
            : "```edit\n// file: Unit.cs\n<<<<<<< SEARCH\n    public int Z => 9;\n=======\n    public int Z => 10;\n>>>>>>> REPLACE\n```");

        var run = await new SwarmRunner(client, new FailsOnceIn("Unit.cs"), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("unit", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 2, MaxTasks: 4)));

        run.Files.Single(f => f.Name == "Unit.cs").Content.Should().Contain("A => 7");
        var retry = client.Calls.Single(c => c.Count == 3);
        retry.Last.Should().Contain("public int Z => 9;").And.Contain("COMPLETE");
    }

    [Fact]
    public void A_page_that_makes_its_own_dax_is_found_by_file_and_line_and_an_alias_is_not()
    {
        // NIM's Nemotron 3 Ultra, 2026-09-24: app.js built its own bridge and assigned it over the real one.
        const string app = """
            import { mountScene } from './scene.js';

            // ── DAX bridge ──
            const dax = {
              ready() { window.daxHost?.postMessage({ topic: 'ready' }, '*'); }
            };
            window.dax = dax;
            dax.ready();
            """;

        var found = PageAssets.OwnBridges([new StrategyFile("ui/app.js", app), new StrategyFile("ui/ok.js", "const dax = window.dax;\ndax.on('x', () => {});\nconst daxBridge = {};\nif (window.dax === undefined) {}")]);

        found.Select(b => (b.File, b.Line)).Should().Equal(("ui/app.js", 4), ("ui/app.js", 7));
        found[0].Text.Should().StartWith("const dax = {");
    }

    // ── what the gate measured on the page ──────────────────────────────────────────────────────

    [Fact]
    public async Task What_the_gate_measured_on_the_page_reaches_the_critics_and_the_page_s_owner()
    {
        var covered = new VerificationFinding(
            "page.layout.covered",
            "#depth-root covers 94% of the page's main view (#scene), so what is drawn under it cannot be seen.",
            "Dock it to one side.",
            BlocksGate.PageEntry);

        var critic = new Scripted(_ => "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```");
        var gauntlet = new GauntletLoop([new ModelCritic(critic, Critic(Critics.MarketLogic), "PACK", canSeeImages: false)]);

        var client = UnitWithFixer(_ => "```html\n<!-- file: ui/index.html -->\n<p>docked</p>\n```");
        var run = await new SwarmRunner(client, new MeasuresOnce(covered), gauntlet: gauntlet, dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("unit", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 3, MaxTasks: 4)));

        critic.Messages.First().Should().Contain("MEASURED ON THE PAGE").And.Contain("#depth-root covers 94%");
        client.Calls.Should().Contain(c => c.Role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal)
                                           && c.Role.Contains("the unit's page", StringComparison.Ordinal)
                                           && c.Message.Contains("page.layout.covered", StringComparison.Ordinal));
        run.Outcome.Should().Be(SwarmOutcome.Delivered);
        run.Files.Single(f => f.Name == "ui/index.html").Content.Should().Contain("docked");
    }

    // ── a provider that cannot be reached ───────────────────────────────────────────────────────

    private const string NoHost = "NVIDIA NIM request failed: No such host is known. (integrate.api.nvidia.com:443)";

    [Fact]
    public async Task A_review_no_critic_could_give_stops_the_run_with_its_files_instead_of_delivering_it()
    {
        // 2026-09-24: DNS stopped resolving NIM mid-run; every critic "could not run", and "0 findings"
        // delivered a unit nobody had reviewed.
        var critic = new Scripted(_ => null, NoHost);
        var gauntlet = new GauntletLoop([new ModelCritic(critic, Critic(Critics.MarketLogic), "PACK", canSeeImages: false)]);
        var client = UnitWithFixer(_ => "```html\n<!-- file: ui/index.html -->\n<p>fixed</p>\n```");

        var run = await new SwarmRunner(client, new Passes(), gauntlet: gauntlet, dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("unit", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 4, MaxTasks: 4)));

        run.Outcome.Should().Be(SwarmOutcome.ProviderFailed);
        run.Summary.Should().Contain("no critic could reach the provider");
        run.Files.Should().Contain(f => f.Name == "Unit.cs").And.Contain(f => f.Name == "ui/index.html");
        client.Calls.Should().NotContain(c => c.Role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal), "nothing is repaired on findings nobody made");
    }

    [Fact]
    public async Task A_round_whose_every_repair_cannot_reach_the_provider_stops_instead_of_spending_the_rounds()
    {
        var client = new Scripted((role, _) =>
            role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + TwoTaskPlan + "\n```"
            : role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal) ? null
            : role.Contains("PAGE", StringComparison.Ordinal) ? "```html\n<!-- file: ui/index.html -->\n<p>page</p>\n```"
            : "```csharp\n// file: Unit.cs\n" + Unit + "\n```", NoHost);

        var run = await new SwarmRunner(client, new FailsOnceIn("Unit.cs"), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("unit", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 6, MaxTasks: 4)));

        run.Outcome.Should().Be(SwarmOutcome.ProviderFailed);
        client.Calls.Count(c => c.Role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal)).Should().Be(1, "one round of unreachable repairs, not six");
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────

    private static VerificationReport Pass() =>
        new([VerificationStep.Pass(VerificationRung.Compile), VerificationStep.Pass(VerificationRung.DrawProbe)]);

    private sealed class Passes : IUnitGate
    {
        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(new GateResult(Pass(), Compile: null) { Compiled = true });
    }

    /// <summary>A compile error in one file on the first round, then a pass.</summary>
    private sealed class FailsOnceIn(string file) : IUnitGate
    {
        private int _runs;

        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(Interlocked.Increment(ref _runs) == 1
                ? new GateResult(new VerificationReport(
                    [VerificationStep.Fail(VerificationRung.Compile, new VerificationFinding("CS0103", $"{file} (3,20): the name 'x' does not exist", "Fix it.", file))]),
                    Compile: null)
                : new GateResult(Pass(), Compile: null) { Compiled = true });
    }

    /// <summary>Passes every round; the first round also measures a layout fault.</summary>
    private sealed class MeasuresOnce(VerificationFinding advisory) : IUnitGate
    {
        private int _runs;

        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(new GateResult(Pass(), Compile: null)
            {
                Compiled = true,
                Advisories = Interlocked.Increment(ref _runs) == 1 ? [advisory] : [],
            });
    }

    private sealed record Call(string Role, string Message, int Count, string Last);

    private sealed class Scripted(Func<string, StrategyCodegenRequest, string?> reply, string failure = "The provider returned nothing.") : IStrategyCodegenClient
    {
        public Scripted(Func<string, string?> reply, string failure = "The provider returned nothing.") : this((role, _) => reply(role), failure)
        {
        }

        public ConcurrentQueue<Call> Calls { get; } = new();

        public IEnumerable<string> Messages => Calls.Select(c => c.Message);

        public string ProviderId => "scripted";
        public string DisplayName => "scripted";
        public bool IsAvailable => true;
        public string Model => "m";
        public CodegenEffort Effort => CodegenEffort.Default;

        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            Calls.Enqueue(new Call(
                role,
                request.Messages.Count > 0 ? request.Messages[0].Content : string.Empty,
                request.Messages.Count,
                request.Messages.Count > 0 ? request.Messages[^1].Content : string.Empty));

            return Task.FromResult(reply(role, request) is { } text
                ? StrategyCodegenResponse.Ok(CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20))
                : StrategyCodegenResponse.Fail(failure));
        }
    }
}
