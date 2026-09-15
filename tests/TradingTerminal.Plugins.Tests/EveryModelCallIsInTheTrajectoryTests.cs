using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Every request the swarm makes to a model is one row in the trajectory, whatever came back.
///
/// <para><b>The log used to record only the calls that answered.</b> Measured on a live Blocks run: nine
/// model calls, seven of which reasoned until their budget was gone or met a gateway error, and a
/// trajectory holding two — so the run's summary said "2 calls" over 446,000 output tokens. A builder
/// that never answered, a planner asked twice, a planner that failed and every critic call were all
/// invisible, and the critics' tokens were missing from the run's total as well.</para>
/// </summary>
public sealed class EveryModelCallIsInTheTrajectoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-calls-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TrajectoryLog Log() => new(Path.Combine(_dir, "trajectory.jsonl"));

    /// <summary>Answers by role; a builder call is failed or not by the test.</summary>
    private sealed class Scripted(Func<string, int, StrategyCodegenResponse> answer) : IStrategyCodegenClient
    {
        private int _builders;

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            var nth = Planner(role) ? 0 : Interlocked.Increment(ref _builders);
            return Task.FromResult(answer(role, nth));
        }
    }

    /// <summary>A critic that says whatever it was told to, and bills whatever it was told to.</summary>
    private sealed class Billed(string id, bool ran, CodegenUsage usage) : IUnitCritic
    {
        public string Id => id;
        public CriticPanel Panel => CriticPanel.Quant;
        public bool NeedsPicture => false;

        public Task<CriticVerdict> JudgeAsync(GauntletSubject subject, ReferenceBar bar, CancellationToken ct = default) =>
            Task.FromResult(ran
                ? new CriticVerdict(Id, Panel, [], "fine", Usage: usage)
                : CriticVerdict.Skipped(Id, Panel, "the provider failed") with { Usage = usage });
    }

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static StrategyCodegenResponse Says(string text) =>
        StrategyCodegenResponse.Ok(CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20));

    /// <summary>The shape a reasoning model's exhausted turn arrives in: failed, and billed.</summary>
    private static StrategyCodegenResponse ReasonedOutOfBudget() =>
        new(false, null, null, "spent the whole generation reasoning", Usage: new CodegenUsage(3_000, 75_000));

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) => "```csharp\n// file: " + name + "\n" + body + "\n```";

    private const string TwoTaskPlan = """
        {
          "contract": { "typeName": "Unit", "dataRequirement": "Bars" },
          "milestones": [
            { "id": "m1", "title": "Build", "tasks": [
              { "id": "t1", "title": "Smoother", "kind": "Maths", "ownedFile": "Smoother.cs", "intent": "an EMA", "dependsOn": [] },
              { "id": "t2", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs", "intent": "the kernel", "dependsOn": [] }]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    private const string OneTaskPlan = """
        {
          "contract": { "typeName": "Unit", "dataRequirement": "Bars" },
          "milestones": [
            { "id": "m1", "title": "The unit", "tasks": [
              { "id": "t1", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs", "intent": "the kernel", "dependsOn": [] }]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    /// <summary>A unit that clears the ladder, so the critics are reached.</summary>
    private const string Good = """
        public sealed class Unit : IStrategyKernel
        {
            private readonly List<double> _closes = new(64);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
            {
                _ = context.Parameters.GetInt("lookback");
                _closes.Clear();
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
            {
                if (_closes.Count == 64) _closes.RemoveAt(0);
                _closes.Add(bar.Close);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Unit", RenderPanelKind.Chart);
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
                surface.Text(8d, 20d, "unit");

                using var series = surface.Series("Close", RenderSeriesKind.Line);
                for (var i = 0; i < _closes.Count; i++) surface.Push(i, _closes[i]);
            }
        }
        """;

    private sealed class StubCompiler(Func<StrategyScript, StrategyCompileResult> compile) : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) => compile(script);
    }

    private static StrategyCompileResult Broken() =>
        new(false, null, [new StrategyDiagnostic(StrategyDiagnosticSeverity.Error, "CS0103", "no such name", 3, 5, "Unit.cs")]);

    private static SwarmRequest Request(int rounds = 0) =>
        new("a smoothed close", "PACK", AuthoringKind.Strategy, new SwarmBudget(MaxParallel: 1, MaxRounds: rounds, MaxTasks: 4));

    [Fact]
    public async Task ABuilderThatNeverAnsweredIsARowAndItsTokensAreCounted()
    {
        var log = Log();
        var client = new Scripted((role, nth) =>
            Planner(role) ? Says(Json(TwoTaskPlan))
            : nth == 1 ? ReasonedOutOfBudget()
            : Says(File("Unit.cs", "public sealed class Unit { }")));

        var run = await new SwarmRunner(
                client, new UnitGate(new StubCompiler(_ => Broken()), "test.unit", "Test unit"), log)
            .RunAsync(Request());

        var cost = log.Cost();
        cost.ModelCalls.Should().Be(3, "the planner and both builders were asked");
        cost.Unanswered.Should().Be(1);
        cost.UnansweredOutputTokens.Should().Be(75_000);

        var silent = log.Read().Single(e => e.Unanswered);
        silent.TaskId.Should().NotBeNull("the row says which task burned the budget");
        silent.Files.Should().Be(0);

        run.Usage.OutputTokens.Should().Be(75_040, "an unanswered call is still in the run's total");
    }

    [Fact]
    public async Task APlannerAskedTwiceIsTwoCalls()
    {
        // The reminder after an unparseable plan is a second request, and was folded into one row.
        var log = Log();
        var client = new Scripted((_, _) => Says(File("Unit.cs", "public sealed class Unit { }")));

        await new SwarmRunner(client, new UnitGate(new StubCompiler(_ => Broken()), "test.unit", "Test unit"), log)
            .RunAsync(Request() with { MayAsk = false });

        log.Read().Count(e => e.Role == "Planner").Should().Be(2);
    }

    [Fact]
    public async Task APlannerThatFailedStillLeavesItsRow()
    {
        // The run ends at planning, which is exactly where the row used to be skipped.
        var log = Log();
        var client = new Scripted((_, _) => ReasonedOutOfBudget());

        var run = await new SwarmRunner(client, new UnitGate(new StubCompiler(_ => Broken()), "test.unit", "Test unit"), log)
            .RunAsync(Request());

        run.Outcome.Should().Be(SwarmOutcome.ProviderFailed);
        log.Read().Should().ContainSingle()
            .Which.Should().Match<TrajectoryEntry>(e => e.Role == "Planner" && e.Unanswered && e.OutputTokens == 75_000);
    }

    [Fact]
    public async Task EveryCriticThatAskedAModelIsACallAndItsTokensAreInTheTotal()
    {
        var log = Log();
        var client = new Scripted((role, _) => Planner(role) ? Says(Json(OneTaskPlan)) : Says(File("Unit.cs", Good)));

        var run = await new SwarmRunner(
                client,
                new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"),
                log,
                gauntlet: new GauntletLoop(
                [
                    new Billed("quant", ran: true, new CodegenUsage(400, 100)),
                    new Billed("picture", ran: false, new CodegenUsage(300, 50)),
                ]))
            .RunAsync(Request(rounds: 2));

        run.Outcome.Should().Be(SwarmOutcome.Delivered);

        var rows = log.Read();
        rows.Should().Contain(e => e.Role == "quant" && e.OutputTokens == 100 && !e.Unanswered);
        rows.Should().Contain(e => e.Role == "picture" && e.Unanswered, "a critic whose call failed was still paid for");
        rows.Where(e => !e.IsModelCall).Select(e => e.Role).Should().OnlyContain(r => r == TrajectoryLog.GateRole || r == TrajectoryLog.GauntletRole);

        log.Cost().ModelCalls.Should().Be(4, "planner, builder and two critics");
        run.Usage.OutputTokens.Should().Be(20 + 20 + 100 + 50);
    }
}
