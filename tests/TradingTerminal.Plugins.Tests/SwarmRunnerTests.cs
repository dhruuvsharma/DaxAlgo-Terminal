using System.Collections.Concurrent;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The swarm: plan, fan out against one contract, gate, repair whoever owns the broken file.
///
/// <para>Everything here runs offline against a scripted client, because the property worth guarding is
/// the ORCHESTRATION — who is asked what, in what order, and what happens to what comes back. A harness
/// that can only be watched against a live provider is a harness nobody will change, and the committee
/// this replaces was exactly that.</para>
/// </summary>
public sealed class SwarmRunnerTests
{
    // ── scripted provider ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Answers by ROLE rather than by call order, so a test says what the planner returns and what a
    /// builder returns without depending on how many times each is asked.
    /// </summary>
    private sealed class Scripted : IStrategyCodegenClient
    {
        private readonly Func<string, string, string> _reply;
        private int _inFlight;

        public Scripted(Func<string, string, string> reply) => _reply = reply;

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        /// <summary>Every (role instruction, user message) pair, in the order they were sent.</summary>
        public ConcurrentQueue<(string Role, string Message)> Calls { get; } = new();

        /// <summary>The most builders that were ever in flight at once.</summary>
        public int PeakParallel { get; private set; }

        /// <summary>Set to fail every call, the way a missing key does.</summary>
        public string? Error { get; init; }

        /// <summary>Held open while a call is "running", so parallelism is observable.</summary>
        public TimeSpan Latency { get; init; } = TimeSpan.Zero;

        public async Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            var message = request.Messages[^1].Content;
            Calls.Enqueue((role, message));

            var now = Interlocked.Increment(ref _inFlight);
            lock (this) PeakParallel = Math.Max(PeakParallel, now);

            try
            {
                if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct).ConfigureAwait(false);
                if (Error is { Length: > 0 }) return StrategyCodegenResponse.Fail(Error);

                var text = _reply(role, message);
                return StrategyCodegenResponse.Ok(CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public bool IsPlanner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);
        public bool IsFixer(string role) => role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal);
    }

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);
    private static bool Fixer(string role) => role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal);

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) =>
        "```csharp\n// file: " + name + "\n" + body + "\n```";

    private static SwarmRequest Request(
        SwarmBudget? budget = null, BuildPlan? plan = null, AuthoringKind kind = AuthoringKind.Strategy) =>
        new("an order book window", "PACK", kind,
            budget ?? new SwarmBudget(MaxParallel: 4, MaxRounds: 2, MaxTasks: 8),
            Plan: plan);

    private static SwarmRunner Runner(Scripted client, IStrategyCompiler? compiler = null) =>
        new(client, new UnitGate(compiler ?? new RoslynStrategyCompiler(), "test.unit", "Test unit"));

    /// <summary>A gate that says whatever the test needs, so orchestration can be tested without
    /// making every scripted reply a compiling unit.</summary>
    private sealed class StubCompiler(Func<StrategyScript, StrategyCompileResult> compile) : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) => compile(script);
    }

    private static StrategyCompileResult Broken(string file, string id = "CS0103") =>
        new(false, null, [new StrategyDiagnostic(StrategyDiagnosticSeverity.Error, id, "no such name", 3, 5, file)]);

    private const string TwoTaskPlan = """
        {
          "contract": {
            "typeName": "BookUnit",
            "dataRequirement": "Bars",
            "helpers": [{ "typeName": "Smoother", "purpose": "smooths", "signature": "public double Push(double v)" }]
          },
          "milestones": [
            { "id": "m1", "title": "Maths", "tasks": [
              { "id": "t1", "title": "Smoother", "kind": "Maths", "ownedFile": "Smoother.cs",
                "intent": "an EMA", "dependsOn": [] }]},
            { "id": "m2", "title": "The unit", "tasks": [
              { "id": "t2", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs",
                "intent": "the kernel", "dependsOn": ["t1"] }]}
          ],
          "rubric": ["the price axis is labelled"],
          "openQuestions": []
        }
        """;

    // ── planning ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePlanIsParsedAndEveryTaskGetsItsOwnBuilder()
    {
        var client = new Scripted((role, _) =>
            Planner(role) ? Json(TwoTaskPlan) : File("x.cs", "public sealed class X { }"));

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        run.Origin.Should().Be(PlanOrigin.Planned);
        run.Plan.Tasks.Select(t => t.Id).Should().Equal("t1", "t2");
        client.Calls.Count(c => !Planner(c.Role) && !Fixer(c.Role)).Should().Be(2, "one builder per task");
    }

    [Fact]
    public async Task APlannerThatWroteCodeInsteadIsRetriedOnceAndThenFallsBackToOneFile()
    {
        // A model that skipped planning and wrote the unit has made a formatting mistake, not asked a
        // question. It earns one reminder and then the single-file plan — which is the shape it was
        // trying to produce anyway, and is exactly what the single conversation does.
        var client = new Scripted((_, _) => File("Unit.cs", "public sealed class X { }"));

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        run.Origin.Should().Be(PlanOrigin.Unparsed);
        run.Plan.Tasks.Should().ContainSingle();
        client.Calls.Count(c => Planner(c.Role)).Should().Be(2, "one repair prompt, then give up");
    }

    [Fact]
    public async Task ProseFromThePlannerIsAQuestionAndTheRunWaits()
    {
        // THE INTERVIEW, and the line the committee died on. A model replying "which instrument?" to a
        // vague brief is doing its job; treating that as malformed JSON and building anyway spends the
        // budget on a brief the model has just said it does not have, and burns the one turn in which
        // the user could have corrected it.
        var client = new Scripted((_, _) => "Which instrument, and over what timeframe?");

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        run.Outcome.Should().Be(SwarmOutcome.AwaitingUser);
        run.PlannerNote.Should().Contain("Which instrument");
        client.Calls.Should().ContainSingle("asking is an answer, not a retry");
    }

    [Fact]
    public async Task AnApprovalAwaitingSpecificationWaitsToo()
    {
        // "Here is what I will build. Confirm and I will write it." has no question mark and is every
        // bit as much a turn that waits. No code means the model wants something — which is the rule
        // the single conversation has always used, rather than a heuristic about punctuation.
        var client = new Scripted((_, _) =>
            "Here is what I will build: a depth ladder beside a heatmap. Confirm and I will write it.");

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        run.Outcome.Should().Be(SwarmOutcome.AwaitingUser);
    }

    [Fact]
    public async Task TheEscapeStopsThePlannerAskingAgain()
    {
        // Otherwise the escape is only a suggestion, and a model that keeps asking keeps winning —
        // the shape of the bug that produced six briefs, six interviews and no code in a real session.
        var client = new Scripted((role, _) =>
            Planner(role) ? "But which instrument?" : File("Unit.cs", "public sealed class X { }"));

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs")))
            .RunAsync(Request() with { MayAsk = false });

        run.Outcome.Should().NotBe(SwarmOutcome.AwaitingUser);
        run.Origin.Should().Be(PlanOrigin.Unparsed);
    }

    [Fact]
    public async Task ThePlannerSeesTheWholeThreadAndTheBuildersSeeNoneOfIt()
    {
        // An answer arrives as "approved, now start building" and means nothing without the brief it
        // approves. Handing the planner only the latest message is what made every interview restart.
        var client = new Scripted((role, _) => Planner(role) ? Json(TwoTaskPlan) : File("x.cs", "public sealed class X { }"));

        await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(
            Request() with
            {
                Thread =
                [
                    new CodegenMessage(CodegenRole.User, "three candles, two triangles"),
                    new CodegenMessage(CodegenRole.Assistant, "Which timeframe?"),
                    new CodegenMessage(CodegenRole.User, "approved, now start building"),
                ],
            });

        var planner = client.Calls.First(c => Planner(c.Role));
        planner.Message.Should().Be("approved, now start building",
            "the thread is sent as messages, so the last one is this turn's");

        client.Calls.Where(c => !Planner(c.Role)).Should().NotContain(
            c => c.Message.Contains("three candles", StringComparison.Ordinal),
            "builders are stateless: a task and its dependencies, never the history");
    }

    [Fact]
    public void TheFallbackTaskKeepsEveryFileTheModelNamed()
    {
        // The one-file rule exists to stop concurrent builders colliding, and a plan with one task has
        // no concurrency to protect. Enforcing it there discarded every file after the first and
        // renamed the survivor to a name the planner invented.
        var context = new SwarmContext("brief");
        var task = BuildPlan.Single("brief", AuthoringKind.Strategy).Tasks[0];

        context.Accept(task, [
            new StrategyFile("EdgeKernel.cs", "public sealed class EdgeKernel { }"),
            new StrategyFile("Smoother.cs", "public sealed class Smoother { }"),
        ]).Should().BeTrue();

        context.Files.Select(f => f.Name).Should().BeEquivalentTo(["EdgeKernel.cs", "Smoother.cs"]);
    }

    [Fact]
    public async Task APlannerThatOverspendsIsCutToTheTaskBudget()
    {
        // The ceiling is in the planner's prompt, which is where a limit is REQUESTED. This is where it
        // holds: a plan of thirty tasks is thirty model calls the user never agreed to.
        var many = string.Join(",", Enumerable.Range(1, 12).Select(i =>
            $$"""{ "id": "t{{i}}", "title": "T{{i}}", "kind": "Maths", "ownedFile": "F{{i}}.cs", "intent": "x" }"""));
        var plan = $$"""{ "contract": { "typeName": "U" }, "milestones": [{ "id": "m1", "title": "M", "tasks": [{{many}}] }] }""";

        var client = new Scripted((role, _) => Planner(role) ? Json(plan) : File("x.cs", "public sealed class X { }"));

        var run = await Runner(client, new StubCompiler(_ => Broken("F1.cs")))
            .RunAsync(Request(new SwarmBudget(MaxParallel: 2, MaxRounds: 0, MaxTasks: 3)));

        run.Plan.Tasks.Should().HaveCount(3);
    }

    [Fact]
    public async Task AProviderFailureAtPlanTimeStopsBeforeAnyBuilderIsPaidFor()
    {
        var client = new Scripted((_, _) => string.Empty) { Error = "401 unauthorized" };

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        run.Outcome.Should().Be(SwarmOutcome.ProviderFailed);
        run.Error.Should().Contain("401");
        client.Calls.Should().OnlyContain(c => Planner(c.Role));
    }

    // ── the fan-out ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IndependentTasksShareALayerAndADependentOneWaits()
    {
        var tasks = new[]
        {
            new BuildTask("a", "A", TaskKind.Maths, "A.cs", "x", []),
            new BuildTask("b", "B", TaskKind.Panel, "B.cs", "x", []),
            new BuildTask("c", "C", TaskKind.Signal, "C.cs", "x", ["a", "b"]),
        };

        var layers = SwarmRunner.Layers(tasks);

        layers.Should().HaveCount(2);
        layers[0].Select(t => t.Id).Should().BeEquivalentTo(["a", "b"]);
        layers[1].Should().ContainSingle().Which.Id.Should().Be("c");
    }

    [Fact]
    public void ACycleIsBuiltRatherThanRefused()
    {
        // A plan written by a model will eventually contain a cycle, and refusing to build because of
        // one would spend the planner's turn for nothing.
        var tasks = new[]
        {
            new BuildTask("a", "A", TaskKind.Maths, "A.cs", "x", ["b"]),
            new BuildTask("b", "B", TaskKind.Maths, "B.cs", "x", ["a"]),
        };

        SwarmRunner.Layers(tasks).Should().ContainSingle().Which.Should().HaveCount(2);
    }

    [Fact]
    public void ADependencyOnATaskThatDoesNotExistDoesNotStallTheRun()
    {
        var tasks = new[] { new BuildTask("a", "A", TaskKind.Maths, "A.cs", "x", ["ghost"]) };

        SwarmRunner.Layers(tasks).Should().ContainSingle().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task ParallelismIsBoundedByTheBudget()
    {
        // Not politeness: a provider answers a burst of eight with 429s, and an agent CLI answers it by
        // starting eight processes.
        var wide = string.Join(",", Enumerable.Range(1, 6).Select(i =>
            $$"""{ "id": "t{{i}}", "title": "T{{i}}", "kind": "Maths", "ownedFile": "F{{i}}.cs", "intent": "x" }"""));
        var plan = $$"""{ "contract": { "typeName": "U" }, "milestones": [{ "id": "m1", "title": "M", "tasks": [{{wide}}] }] }""";

        var client = new Scripted(
            (role, _) => Planner(role) ? Json(plan) : File("x.cs", "public sealed class X { }"))
        {
            Latency = TimeSpan.FromMilliseconds(40),
        };

        await Runner(client, new StubCompiler(_ => Broken("F1.cs")))
            .RunAsync(Request(new SwarmBudget(MaxParallel: 2, MaxRounds: 0, MaxTasks: 8)));

        client.PeakParallel.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task ABuilderIsGivenTheFilesItDependsOnAndNotTheOthers()
    {
        var client = new Scripted((role, _) => Planner(role)
            ? Json(TwoTaskPlan)
            : File("caller-decides.cs", "public sealed class X { }"));

        await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        var builders = client.Calls.Where(c => !Planner(c.Role)).ToArray();
        builders[1].Message.Should().Contain("ALREADY WRITTEN — Smoother.cs",
            "t2 depends on t1, so it is shown what t1 wrote");
        builders[0].Message.Should().NotContain("ALREADY WRITTEN", "t1 depends on nothing");
    }

    // ── the merge rule ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFileOutsideTheTasksOwnNameIsDiscarded()
    {
        // Asked for a panel, a model will cheerfully rewrite the kernel to call it — and that rewrite,
        // applied, clobbers whatever the kernel's own builder wrote in the same round.
        var context = new SwarmContext("brief");
        var task = new BuildTask("t1", "Panel", TaskKind.Panel, "Panel.cs", "x", []);

        context.Accept(task, [
            new StrategyFile("Panel.cs", "public sealed class P { }"),
            new StrategyFile("Unit.cs", "public sealed class STOLEN { }"),
        ]).Should().BeTrue();

        context.Files.Should().ContainSingle().Which.Name.Should().Be("Panel.cs");
    }

    [Fact]
    public void ASingleFileIsAcceptedWhateverItCalledItself()
    {
        // Models rename constantly. Discarding a correct file over its label would be the pedantic
        // reading of a rule that exists to prevent collisions, and there is no collision in one file.
        var context = new SwarmContext("brief");
        var task = new BuildTask("t1", "Panel", TaskKind.Panel, "Panel.cs", "x", []);

        context.Accept(task, [new StrategyFile("MyPanel.cs", "public sealed class P { }")]).Should().BeTrue();

        context.Files.Should().ContainSingle().Which.Name.Should().Be("Panel.cs");
    }

    [Fact]
    public void ABuilderThatReturnedNothingIsReportedRatherThanCountedAsDone()
    {
        var context = new SwarmContext("brief");
        var task = new BuildTask("t1", "Panel", TaskKind.Panel, "Panel.cs", "x", []);

        context.Accept(task, []).Should().BeFalse();
        context.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task ProseInAFenceIsNotAcceptedAsCode()
    {
        // The single-conversation path has refused this since the day it cost three generations — the
        // fix loop reads CS1003 and tries to FIX THE PROSE. A second path that compiled it happily is
        // exactly the defect this area keeps producing.
        var client = new Scripted((role, _) => Planner(role)
            ? Json(TwoTaskPlan)
            : "```csharp\n// file: Smoother.cs\nFirst we compute the average, then we smooth it.\n```");

        var run = await Runner(client, new StubCompiler(_ => Broken("Unit.cs"))).RunAsync(Request());

        run.Files.Should().BeEmpty();
        run.Summary.Should().Contain("No builder produced a file");
    }

    // ── repair routing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARepairGoesToWhoeverOwnsTheBrokenFile()
    {
        // Sending every finding to whoever wrote the hostable class is what makes one builder repair
        // files it never saw and cannot fix.
        var client = new Scripted((role, _) => Planner(role)
            ? Json(TwoTaskPlan)
            : File("caller-decides.cs", "public sealed class X { }"));

        await Runner(client, new StubCompiler(_ => Broken("Smoother.cs")))
            .RunAsync(Request(new SwarmBudget(MaxParallel: 2, MaxRounds: 1, MaxTasks: 8)));

        var repairs = client.Calls.Where(c => Fixer(c.Role)).ToArray();
        repairs.Should().ContainSingle();
        repairs[0].Role.Should().Contain("YOUR FILE: Smoother.cs");
    }

    [Fact]
    public async Task ADrawFailureWithNoFileGoesToThePanels()
    {
        // A picture failure is about behaviour, not a line, so nothing names a file. It belongs to
        // whoever paints.
        const string withPanel = """
            {
              "contract": { "typeName": "U" },
              "milestones": [{ "id": "m1", "title": "M", "tasks": [
                { "id": "t1", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs", "intent": "x" },
                { "id": "t2", "title": "Ladder", "kind": "Panel", "ownedFile": "Ladder.cs", "intent": "x" }
              ]}]
            }
            """;

        var compiler = new StubCompiler(_ => new StrategyCompileResult(
            false, null, [new StrategyDiagnostic(StrategyDiagnosticSeverity.Error, "draw.blank", "nothing drawn", 0, 0)]));

        var client = new Scripted((role, _) => Planner(role) ? Json(withPanel) : File("x.cs", "public sealed class X { }"));

        await Runner(client, compiler).RunAsync(Request(new SwarmBudget(MaxParallel: 2, MaxRounds: 1, MaxTasks: 8)));

        client.Calls.Where(c => Fixer(c.Role)).Should().ContainSingle()
            .Which.Role.Should().Contain("YOUR FILE: Ladder.cs");
    }

    [Fact]
    public void ARepairIsShownItsOwnFindingsAndOnlyTheNamesOfOthers()
    {
        var context = new SwarmContext("brief", [new StrategyFile("Panel.cs", "public sealed class P { }")]);
        var task = new BuildTask("t1", "Panel", TaskKind.Panel, "Panel.cs", "x", []);

        var message = context.ComposeRepair(task, [
            new VerificationFinding("CS0103", "no such name", "declare it", "Panel.cs"),
            new VerificationFinding("CS1002", "semicolon", "add one", "Unit.cs"),
        ]);

        message.Should().Contain("no such name");
        message.Should().NotContain("semicolon", "another file's diagnostics invite a repair it cannot make");
        message.Should().Contain("Unit.cs", "but it must know its caller is unhappy");
    }

    // ── stopping ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunThatBuysNoGroundStopsRatherThanSpendingTheBudget()
    {
        // The expensive failure mode, measured on a user machine: 158 turns, 140 of them repairs, every
        // one scoring zero against the same rung, 120 of them inside three minutes.
        var client = new Scripted((role, _) => Planner(role)
            ? Json(TwoTaskPlan)
            : File("caller-decides.cs", "public sealed class X { }"));

        var run = await Runner(client, new StubCompiler(_ => Broken("Smoother.cs")))
            .RunAsync(Request(new SwarmBudget(MaxParallel: 2, MaxRounds: 20, MaxTasks: 8, StallLimit: 3)));

        run.Outcome.Should().Be(SwarmOutcome.Stalled);
        run.Summary.Should().Contain("bought no further ground");
        client.Calls.Count(c => Fixer(c.Role)).Should().BeLessThan(5, "it must not spend the whole budget");
    }

    [Fact]
    public async Task TheBudgetIsAnHonestStopRatherThanAnotherAttempt()
    {
        // Progress every round, so the stall detector never fires — the budget is what ends it, and it
        // must say so rather than inviting another spend.
        var rungs = 0;
        var compiler = new StubCompiler(_ =>
        {
            rungs++;
            return new StrategyCompileResult(
                false, null,
                [.. Enumerable.Range(0, Math.Max(1, 9 - rungs))
                    .Select(i => new StrategyDiagnostic(
                        StrategyDiagnosticSeverity.Error, "CS" + i, "wrong", 1, 1, "Smoother.cs"))]);
        });

        var client = new Scripted((role, _) => Planner(role)
            ? Json(TwoTaskPlan)
            : File("caller-decides.cs", "public sealed class X { }"));

        var run = await Runner(client, compiler)
            .RunAsync(Request(new SwarmBudget(MaxParallel: 2, MaxRounds: 2, MaxTasks: 8)));

        run.Outcome.Should().Be(SwarmOutcome.BudgetExhausted);
        run.Summary.Should().Contain("does not compile");
    }

    // ── the whole thing, composed ───────────────────────────────────────────────────────────────

    private const string Ambient = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using DaxAlgo.Sdk;
        using DaxAlgo.Sdk.Drawing;
        using TradingTerminal.Core.Domain;
        using TradingTerminal.Core.Strategies;
        using TradingTerminal.Core.Strategies.Parameters;
        """;

    [Fact]
    public async Task ASwarmDeliversARealCompiledVerifiedUnitFromTwoBuilders()
    {
        // The capstone: a plan, two builders writing two files against one contract, the real Roslyn
        // compiler as the merge check, and all eight rungs over what came out.
        var client = new Scripted((role, _) =>
        {
            if (Planner(role)) return Json(TwoTaskPlan);

            return role.Contains("YOUR FILE: Smoother.cs", StringComparison.Ordinal)
                ? File("Smoother.cs", Ambient + """

                    public sealed class Smoother
                    {
                        private double _value;
                        private bool _seeded;

                        public double Push(double v)
                        {
                            _value = _seeded ? (0.2 * v) + (0.8 * _value) : v;
                            _seeded = true;
                            return _value;
                        }
                    }
                    """)
                : File("Unit.cs", Ambient + """

                    public sealed class BookUnit : IStrategyKernel
                    {
                        private readonly Smoother _smoother = new();
                        private readonly System.Collections.Generic.List<double> _line = new(64);
                        private int _lookback;

                        public StrategyParameterSchema Schema { get; } = new(
                            StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 200));

                        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

                        public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct)
                        {
                            _lookback = c.Parameters.GetInt("lookback");
                            _line.Clear();
                            return Task.CompletedTask;
                        }

                        public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext c, CancellationToken ct)
                        {
                            if (_line.Count == 64) _line.RemoveAt(0);
                            _line.Add(_smoother.Push(bar.Close));
                            return Task.CompletedTask;
                        }

                        public void Draw(IRenderSurface surface)
                        {
                            using var panel = surface.Panel("Book", RenderPanelKind.Chart);
                            if (_line.Count == 0)
                            {
                                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary)));
                                surface.Text(8d, 20d, "Waiting for bars…");
                                return;
                            }

                            var range = PlotRange.Empty;
                            for (var i = 0; i < _line.Count; i++) range = range.Include(_line[i]);
                            Plot.HorizontalGrid(surface, range.Padded());
                            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
                            using var series = surface.Series("Smoothed", RenderSeriesKind.Line);
                            for (var i = 0; i < _line.Count; i++) surface.Push(i, _line[i]);
                        }
                    }
                    """);
        });

        var run = await Runner(client).RunAsync(Request());

        run.Outcome.Should().Be(SwarmOutcome.Delivered,
            run.Report is null ? run.Summary : string.Join("; ", run.Report.Findings.Select(f => f.ToString())));
        run.Files.Select(f => f.Name).Should().BeEquivalentTo(["Smoother.cs", "Unit.cs"]);
        run.Compile!.Unit!.Type.Name.Should().Be("BookUnit");
        run.Usage.TotalTokens.Should().BeGreaterThan(0, "three calls were billed");
    }

    // ── the plan reader on its own ──────────────────────────────────────────────────────────────

    [Fact]
    public void APlanIsFoundWhetherOrNotItIsFenced()
    {
        BuildPlanReader.Read(Json(TwoTaskPlan), AuthoringKind.Strategy).Should().NotBeNull();
        BuildPlanReader.Read("Sure! " + TwoTaskPlan, AuthoringKind.Strategy).Should().NotBeNull();
    }

    [Fact]
    public void APlanWithNoTasksIsNoPlan()
    {
        // Returning null is what sends the caller to the single-file fallback rather than running a
        // swarm over nothing.
        BuildPlanReader.Read(Json("""{ "contract": { "typeName": "U" }, "milestones": [] }"""), AuthoringKind.Strategy)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("../../etc/passwd.cs")]
    [InlineData("C:\\Windows\\evil.cs")]
    [InlineData("sub/dir/Unit.cs")]
    public void APlannersFileNameIsReducedToABareLeaf(string name)
    {
        // The name comes from a model and is used as a compilation path. Nothing here writes to disk,
        // but a name that is only safe because of what today's caller happens to do is a defect waiting
        // for tomorrow's.
        var plan = BuildPlanReader.Read(
            Json($$"""
                { "contract": { "typeName": "U" }, "milestones": [{ "id": "m1", "title": "M", "tasks": [
                  { "id": "t1", "title": "T", "kind": "Maths", "ownedFile": "{{name.Replace("\\", "\\\\")}}", "intent": "x" }]}]}
                """),
            AuthoringKind.Strategy);

        plan.Should().NotBeNull();
        var file = plan!.Tasks[0].OwnedFile;
        file.Should().NotContain("/").And.NotContain("\\").And.NotContain("..");
    }

    [Fact]
    public void AKindTheModelInventedFallsBackRatherThanFailingTheWholePlan()
    {
        var plan = BuildPlanReader.Read(
            Json("""
                { "contract": { "typeName": "U" }, "milestones": [{ "id": "m1", "title": "M", "tasks": [
                  { "id": "t1", "title": "T", "kind": "Refactoring", "ownedFile": "A.cs", "intent": "x" }]}]}
                """),
            AuthoringKind.Strategy);

        // The enum converter rejects the value, so the whole block fails to parse and the caller falls
        // back — which is the honest outcome, and is asserted here so a future permissive parse is a
        // deliberate change rather than an accident.
        plan.Should().BeNull();
    }
}
