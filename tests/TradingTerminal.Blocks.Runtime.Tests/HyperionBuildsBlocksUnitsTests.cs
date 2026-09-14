using System.Collections.Concurrent;
using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>
/// Hyperion's swarm building a Blocks unit: the planner names blocks per task and fixes the messages
/// between the unit and its page, each builder is sent its own cards and nothing else of the SDK, the gate
/// compiles and drives what came back, and a fault reaches the builder that owns it.
/// </summary>
public sealed class HyperionBuildsBlocksUnitsTests
{
    // ── what the model is shown ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_catalog_is_the_committed_cards_in_index_order_without_their_banners()
    {
        var catalog = BlockCatalog.Load();

        catalog.Ids.Should().HaveCount(BlockCatalogGenerator.Cards().Count);
        catalog.Ids[0].Should().Be(BlockCatalog.UnitBlock);
        catalog.Card("orders").Should().StartWith("## orders");
        catalog.Compose(catalog.Ids).Should().NotContain("GENERATED");
        catalog.SharedContext.Should().StartWith("# Writing a unit").And.Contain("- `market`");

        catalog.Resolve(["ui", "charting", "unit", "UI", " market "]).Should().Equal("unit", "market", "ui");
    }

    [Fact]
    public void A_builder_is_sent_its_own_cards_and_the_whole_message_is_a_fraction_of_the_old_pack()
    {
        var dialect = new BlocksSwarmDialect();
        var plan = SpreadPlan();
        var context = new SwarmContext("spread between two legs");

        var unit = dialect.ComposeBuild(context, plan.Tasks[0], plan);
        var page = dialect.ComposeBuild(context, plan.Tasks[1], plan);

        unit.Should().Contain("## unit").And.Contain("## settings").And.Contain("## market").And.Contain("## ui");
        unit.Should().NotContain("## orders").And.NotContain("## math.");
        page.Should().Contain("## ui").And.NotContain("## market").And.NotContain("## unit —");

        // What every call in the run carries, against what every call used to carry.
        var old = StrategyContextPack.Load().SystemPrompt.Length;
        var largest = dialect.SharedContext.Length
                      + Math.Max(unit.Length, page.Length)
                      + dialect.Builder(plan.Tasks[0], plan.Contract).Length;

        largest.Should().BeLessThan(old / 5, $"a Blocks builder call is {largest} characters against {old} for the widget SDK's pack");
    }

    [Fact]
    public void The_contract_carries_the_messages_both_builders_meet_through()
    {
        var plan = SpreadPlan();
        var rendered = BlocksSwarmDialect.Contract(plan.Contract);

        rendered.Should().Contain("\"spread\" unit → page").And.Contain("{ a: number, b: number, spread: number }");
        rendered.Should().Contain("\"reset\" page → unit");
        new BlocksSwarmDialect().Builder(plan.Tasks[1], plan.Contract).Should().Contain("PAGE").And.Contain("dax.ready()");
    }

    [Fact]
    public void A_plan_reads_blocks_topics_and_a_page_task_and_refuses_a_page_path_that_climbs()
    {
        var plan = BuildPlanReader.Read(PlanJson.Replace("\"ui/index.html\"", "\"ui/../../evil.html\"", StringComparison.Ordinal), AuthoringKind.Visualizer);
        plan!.Tasks.Should().ContainSingle("the task whose file escapes ui/ is dropped");

        plan = BuildPlanReader.Read(PlanJson, AuthoringKind.Visualizer)!;
        plan.Tasks[0].Cards.Should().Equal("settings", "market");
        plan.Tasks[1].OwnsPage.Should().BeTrue();
        plan.Tasks[1].Owns("ui/app.js").Should().BeTrue();
        plan.Tasks[0].Owns("ui/app.js").Should().BeFalse();
        plan.Contract.PageTopics.Should().HaveCount(2);
        plan.Contract.PageTopics[1].FromPage.Should().BeTrue();
    }

    [Fact]
    public void A_reply_yields_the_unit_and_its_page_under_ui_whatever_the_model_called_the_folder()
    {
        const string reply = """
            Here it is.

            ```csharp
            // file: SpreadWatch.cs
            public sealed class SpreadWatch { }
            ```

            ```html
            <!-- file: index.html -->
            <div id="v"></div>
            ```

            **ui/app.js**
            ```js
            dax.ready();
            ```

            ```css
            /* file: ui/style.css */
            body { margin: 0; }
            ```

            ```json
            { "not": "a page" }
            ```
            """;

        var files = CodegenCodeExtractor.ExtractUnitFiles(reply);

        files.Select(f => f.Name).Should().Equal("SpreadWatch.cs", "ui/index.html", "ui/app.js", "ui/style.css");
        files[1].Content.Should().Be("<div id=\"v\"></div>", "the path marker is not part of the page");
        CodegenCodeExtractor.ExtractFiles(reply).Should().ContainSingle("the widget SDK's path still takes only C#");
        CodegenCodeExtractor.StripCode(reply).Should().NotContain("dax.ready").And.NotContain("<div");
    }

    [Fact]
    public void The_Blocks_panel_drops_chart_craft_and_keeps_the_book_for_strategies_only()
    {
        BlocksCritics.For(AuthoringKind.Visualizer).Select(c => c.Id).Should()
            .Equal(Critics.Picture, Critics.MarketLogic, Critics.DataContract, Critics.Integration);
        BlocksCritics.For(AuthoringKind.Strategy).Select(c => c.Id).Should().Contain(Critics.Book);
    }

    // ── the gate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_gate_sorts_a_refusal_onto_its_rung_and_names_the_file()
    {
        var gate = new BlocksGate(new BlocksUnitCompiler(), "gate");

        var broken = await gate.RunAsync([new StrategyFile("Broken.cs", "public sealed class Broken : IUnit { public UnitInfo Info => new(\"x\"); ")]);
        broken.Compiled.Should().BeFalse();
        broken.Report.FailedAt.Should().Be(VerificationRung.Compile);
        broken.Report.Findings.Should().OnlyContain(f => f.File == "Broken.cs");

        var escapes = await gate.RunAsync([new StrategyFile("Escapes.cs", """
            public sealed class Escapes : IUnit
            {
                public UnitInfo Info { get; } = new("Escapes");
                public Task StartAsync(IUnitContext context, CancellationToken ct) { new System.Threading.Thread(() => { }).Start(); return Task.CompletedTask; }
            }
            """)]);
        escapes.Report.FailedAt.Should().Be(VerificationRung.Policy);
    }

    [Fact]
    public async Task The_gate_passes_a_unit_that_feeds_its_page_and_fails_one_that_throws_in_a_handler()
    {
        var gate = new BlocksGate(new BlocksUnitCompiler(), "gate", drive: Quick);

        var good = await gate.RunAsync([new StrategyFile("SpreadWatch.cs", SpreadWatchSource), new StrategyFile("ui/index.html", Page)]);
        good.Passed.Should().BeTrue(string.Join(" / ", good.Report.Findings));
        good.Compiled.Should().BeTrue();
        gate.Latest!.PageFiles.Should().ContainSingle();

        var throws = await gate.RunAsync([new StrategyFile("SpreadWatch.cs", SpreadWatchSource.Replace("_a = q.Mid;", "throw new InvalidOperationException(\"boom\");", StringComparison.Ordinal))]);
        throws.Passed.Should().BeFalse();
        throws.Report.FailedAt.Should().Be(VerificationRung.Lifecycle);
        throws.Report.Findings.Should().Contain(f => f.Code == "handler.threw" && f.File == null);
    }

    // ── the whole run ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_brief_becomes_a_unit_and_its_page_with_each_builder_holding_only_its_cards()
    {
        var dialect = new BlocksSwarmDialect();
        var client = new Scripted((role, _) =>
            IsPlanner(role) ? Json(PlanJson)
            : role.Contains("YOUR FILE: ui/index.html", StringComparison.Ordinal) ? PageReply
            : CSharp("SpreadWatch.cs", SpreadWatchSource));

        var run = await Runner(client, dialect).RunAsync(Request(dialect));

        run.Outcome.Should().Be(SwarmOutcome.Delivered, run.Summary);
        run.Compiled.Should().BeTrue();
        run.Files.Select(f => f.Name).Should().BeEquivalentTo("SpreadWatch.cs", "ui/index.html", "ui/app.js");

        client.Calls.Should().OnlyContain(c => c.System == dialect.SharedContext, "every call shares one cacheable prefix");
        client.Calls.Should().NotContain(c => c.Message.Contains("IRenderSurface") || c.System.Contains("IRenderSurface"));

        var unitCall = client.Calls.Single(c => c.Role.Contains("YOUR FILE: SpreadWatch.cs", StringComparison.Ordinal));
        unitCall.Message.Should().Contain("## market").And.NotContain("## orders");
        var pageCall = client.Calls.Single(c => c.Role.Contains("YOUR FILE: ui/index.html", StringComparison.Ordinal));
        pageCall.Message.Should().NotContain("## market");
    }

    [Fact]
    public async Task A_unit_that_never_feeds_its_page_is_repaired_by_the_unit_builder_and_the_page_is_left_alone()
    {
        var dialect = new BlocksSwarmDialect();
        var silent = SpreadWatchSource.Replace("context.Ui.Send(", "_ = (", StringComparison.Ordinal);

        var client = new Scripted((role, _) =>
            IsPlanner(role) ? Json(PlanJson)
            : role.Contains("YOUR FILE: ui/index.html", StringComparison.Ordinal) ? PageReply
            : IsFixer(role) ? CSharp("SpreadWatch.cs", SpreadWatchSource)
            : CSharp("SpreadWatch.cs", silent));

        var run = await Runner(client, dialect).RunAsync(Request(dialect));

        run.Outcome.Should().Be(SwarmOutcome.Delivered, run.Summary);
        var fixes = client.Calls.Where(c => IsFixer(c.Role)).ToArray();
        fixes.Should().ContainSingle().Which.Role.Should().Contain("SpreadWatch.cs");
        fixes[0].Message.Should().Contain("ui.never-sent");
        client.Calls.Count(c => !IsPlanner(c.Role) && c.Role.Contains("ui/index.html", StringComparison.Ordinal) || c.Role.Contains("the unit's page", StringComparison.Ordinal)).Should().Be(1, "the page was built once and never asked again");
    }

    [Fact]
    public async Task A_page_that_throws_is_repaired_by_the_page_builder_and_the_unit_is_left_alone()
    {
        var dialect = new BlocksSwarmDialect();
        var client = new Scripted((role, _) =>
            IsPlanner(role) ? Json(PlanJson)
            : IsFixer(role) ? PageReply
            : role.Contains("YOUR FILE: ui/index.html", StringComparison.Ordinal) ? PageReply
            : CSharp("SpreadWatch.cs", SpreadWatchSource));

        var probe = new ThrowsOnce();
        var gate = new BlocksGate(new BlocksUnitCompiler(), "spread-watch", probe, Quick);
        var run = await new SwarmRunner(client, gate, dialect: dialect).RunAsync(Request(dialect));

        run.Outcome.Should().Be(SwarmOutcome.Delivered, run.Summary);
        var fixes = client.Calls.Where(c => IsFixer(c.Role)).ToArray();
        fixes.Should().ContainSingle().Which.Role.Should().Contain("the unit's page");
        fixes[0].Message.Should().Contain("page.threw").And.Contain("YOUR FILE — ui/index.html");
        client.Calls.Count(c => c.Role.Contains("SpreadWatch.cs", StringComparison.Ordinal)).Should().Be(1);
        probe.Runs.Should().Be(2);
    }

    [Fact]
    public async Task A_tight_budget_keeps_the_unit_and_its_page_and_drops_helpers_first()
    {
        var dialect = new BlocksSwarmDialect();
        var crowded = PlanJson.Replace(
            "\"tasks\": [",
            "\"tasks\": [ { \"id\": \"h1\", \"title\": \"Helper\", \"kind\": \"Maths\", \"ownedFile\": \"Helper.cs\", \"intent\": \"a helper\", \"dependsOn\": [] },",
            StringComparison.Ordinal);

        var client = new Scripted((role, _) =>
            IsPlanner(role) ? Json(crowded)
            : role.Contains("YOUR FILE: ui/index.html", StringComparison.Ordinal) ? PageReply
            : CSharp("SpreadWatch.cs", SpreadWatchSource));

        var run = await Runner(client, dialect).RunAsync(Request(dialect) with { Budget = new SwarmBudget(1, 1, MaxTasks: 2) });

        run.Plan.Tasks.Select(t => t.Id).Should().Equal("t1", "t2");
        run.Outcome.Should().Be(SwarmOutcome.Delivered, run.Summary);
    }

    [Theory]
    [InlineData(AuthoringKind.Strategy, true)]
    [InlineData(AuthoringKind.Visualizer, false)]
    public void The_starters_compile_and_are_the_kind_they_say(AuthoringKind kind, bool strategy)
    {
        var source = BlocksAuthoring.Starter(kind);
        var compiled = new BlocksUnitCompiler().Compile("starter", [new StrategyFile("MyUnit.cs", source)]);

        compiled.Success.Should().BeTrue(string.Join(" / ", compiled.Errors));
        compiled.UsesOrders.Should().Be(strategy);
        BlocksAuthoring.IsStarter(source).Should().BeTrue();
        BlocksAuthoring.IsBlocksUnit([new StrategyFile("MyUnit.cs", source)]).Should().BeTrue();
        BlocksAuthoring.IsBlocksUnit([new StrategyFile("Old.cs", "public sealed class Old : IStrategyKernel { }")]).Should().BeFalse();
        BlocksAuthoring.IsBlocksUnit([new StrategyFile("ui/index.html", "<p>page</p>")]).Should().BeTrue();
    }

    [Fact]
    public void Registering_puts_the_compiled_unit_in_the_registry_as_the_kind_its_code_is()
    {
        var registry = new BlocksUnitRegistry();
        var changed = 0;
        registry.Changed += (_, _) => changed++;
        var authoring = new BlocksAuthoring(new BlocksUnitCompiler(), registry);

        var files = new[] { new StrategyFile("SpreadWatch.cs", SpreadWatchSource), new StrategyFile("ui/index.html", Page) };
        var message = authoring.Register(authoring.Compiler.Compile("spread", files), files, "spread", null);

        message.Should().Contain("Registered visualizer 'Spread watch'");
        var registration = registry.Find("spread")!;
        registration.IsStrategy.Should().BeFalse();
        registration.PageFiles.Should().ContainSingle();
        registration.Create().Should().NotBeNull();

        authoring.Register(authoring.Compiler.Compile("spread", files), files, "spread", "Renamed");
        registry.All.Should().ContainSingle().Which.DisplayName.Should().Be("Renamed");
        changed.Should().Be(2);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static readonly DriveOptions Quick = new(Steps: 30, SettleTime: TimeSpan.FromMilliseconds(200));

    private const string PlanJson = """
        {
          "contract": {
            "typeName": "SpreadWatch",
            "parameters": [{ "name": "legA", "label": "Leg A", "type": "instrument", "default": "1" }],
            "topics": [
              { "name": "spread", "direction": "to-page", "payload": "{ a: number, b: number, spread: number }" },
              { "name": "reset", "direction": "from-page", "payload": "{}" }
            ]
          },
          "milestones": [{ "id": "m1", "title": "Build", "tasks": [
            { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "SpreadWatch.cs",
              "blocks": ["settings", "market"], "intent": "subscribe to both legs and send the spread", "dependsOn": [] },
            { "id": "t2", "title": "The page", "kind": "Ui", "ownedFile": "ui/index.html",
              "blocks": ["ui"], "intent": "show the spread large", "dependsOn": [] }
          ]}],
          "rubric": ["the spread is legible"],
          "openQuestions": []
        }
        """;

    private const string SpreadWatchSource = """
        public sealed class SpreadWatch : IUnit
        {
            public UnitInfo Info { get; } = new("Spread watch", "Spread between two legs.",
            [
                StrategyParameter.Instrument("legA", "Leg A", new InstrumentId(1)),
                StrategyParameter.Instrument("legB", "Leg B", new InstrumentId(2)),
            ]);

            private double _a, _b;

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                context.Market.OnQuote(context.Settings.Instrument("legA"), q => { _a = q.Mid; Publish(context); });
                context.Market.OnQuote(context.Settings.Instrument("legB"), q => { _b = q.Mid; Publish(context); });
                context.Ui.On("reset", _ => { _a = 0; _b = 0; });
                context.Ui.OnOpened(() => Publish(context));
                return Task.CompletedTask;
            }

            private void Publish(IUnitContext context) =>
                context.Ui.Send("spread", new { a = _a, b = _b, spread = _a - _b });
        }
        """;

    private const string Page = """<body style="background:#111;color:#eee"><div id="v">waiting</div><script>dax.on('spread', s => v.textContent = s.spread); dax.ready();</script></body>""";

    private const string PageReply = """
        ```html
        <!-- file: ui/index.html -->
        <body style="background:#111;color:#eee"><div id="v">waiting</div><script src="app.js"></script></body>
        ```

        ```js
        // file: ui/app.js
        dax.on('spread', s => document.getElementById('v').textContent = s.spread.toFixed(2));
        dax.ready();
        ```
        """;

    private static BuildPlan SpreadPlan() => BuildPlanReader.Read(PlanJson, AuthoringKind.Visualizer)!;

    private static SwarmRequest Request(BlocksSwarmDialect dialect) =>
        new("a window showing the spread between two legs", dialect.SharedContext, AuthoringKind.Visualizer,
            new SwarmBudget(MaxParallel: 2, MaxRounds: 2, MaxTasks: 6), MayAsk: false);

    private static SwarmRunner Runner(Scripted client, BlocksSwarmDialect dialect) =>
        new(client, new BlocksGate(new BlocksUnitCompiler(), "spread-watch", drive: Quick), dialect: dialect);

    private static bool IsPlanner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static bool IsFixer(string role) => role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal);

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string CSharp(string name, string body) => "```csharp\n// file: " + name + "\n" + body + "\n```";

    /// <summary>Answers by role, and remembers what each call carried.</summary>
    private sealed class Scripted(Func<string, string, string> reply) : IStrategyCodegenClient
    {
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public ConcurrentQueue<(string System, string Role, string Message)> Calls { get; } = new();

        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            var message = request.Messages[^1].Content;
            Calls.Enqueue((request.SystemContext, role, message));

            var text = reply(role, message);
            return Task.FromResult(StrategyCodegenResponse.Ok(CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
    }

    /// <summary>A page probe whose first run finds a script error on the page, and whose later runs are clean.</summary>
    private sealed class ThrowsOnce : IPageProbe
    {
        public int Runs;

        public bool IsAvailable => true;

        public async Task<PageCheck> RunAsync(Func<IUnit> factory, IReadOnlyList<StrategyFile> pageFiles, string unitId, DriveOptions drive, CancellationToken ct = default)
        {
            var report = await BlocksDrive.RunAsync(factory, drive, ct);
            var findings = Interlocked.Increment(ref Runs) == 1
                ? [.. report.Findings, new DriveFinding(DriveSeverity.Failure, "page.threw", "ReferenceError: chart is not defined", "Fix the script.")]
                : report.Findings;

            return new PageCheck(report, findings, Png: [1, 2, 3], Width: 10, Height: 10);
        }
    }
}
