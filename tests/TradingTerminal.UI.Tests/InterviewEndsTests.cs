using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The interview has to end, driven through the real view-model at the effort a user actually picks.
///
/// <para><b>The bug this pins, in the user's own words: "I'm not getting any output."</b> A saved
/// session on disk showed six briefs and six interviews — including "approved, now start building" and
/// "can you start writnig the code bro" — and not one line of code, ever. Three causes compounded:
/// <c>RoutingState.HasSpec</c> had no writer anywhere in the product (twenty-eight in the tests, all
/// pre-seeded true), so the prior returned Only(Interviewer) on every turn; the state and the agent
/// context were rebuilt from scratch inside each call, so an answer arrived with nothing it was an
/// answer to; and the loop returned AwaitingUser on every reply that carried no code, so the handover
/// the Interviewer's own prompt promises had no code to perform it.</para>
///
/// <para>It only ever affected Deep and Max — the two efforts that routed through the agents. Quick and
/// Standard used the single conversation and were always fine, which is why the dial's top two settings
/// were the broken ones.</para>
///
/// <para><b>The committee is gone.</b> Those three causes were each fixed and the builder still shipped
/// nothing on a real provider: the agent loop calls the BLOCKING, unstreamed entry point, so a Deep or
/// Max turn was one silent HTTP request per role with no timeout — no text, no thinking, no token
/// movement. Every effort is one streaming conversation now, so these tests drive that instead. They
/// keep their names and their scenario, because the property they guard — the interview must end and
/// code must arrive — is the same one.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class InterviewEndsTests : IDisposable
{
    /// <summary>
    /// Redirects the saved-chat store, the way every fixture in this area must.
    ///
    /// <para>Not hygiene: a turn calls Save() in its finally and the pane RESTORES on construction, so
    /// without this these tests both write into the running user Hyperion session rail and read back
    /// their own previous run — which is how the first assertion here saw a compiled unit before the
    /// model had written one.</para>
    /// </summary>
    private readonly string _sessionDir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "daxalgo-interview-" + Guid.NewGuid().ToString("N"));

    public InterviewEndsTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    private const string Kernel = """
        ```csharp
        // file: Unit.cs
        public sealed class TriangleKernel : IStrategyKernel
        {
            public static StrategyParameterSchema Schema { get; } = new([]);

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public void OnBar(OhlcvBar bar, IUnitContext context) { }
        }
        ```
        """;

    /// <summary>
    /// THE SCENARIO, end to end: interview, the user says build it, code arrives. At Max, which is what
    /// the user had configured.
    /// </summary>
    [Fact]
    public async Task A_max_effort_session_stops_interviewing_once_the_spec_is_settled()
    {
        // Two replies, because Max is a single streaming conversation now rather than a committee: the
        // model asks, the user answers, and the NEXT reply carries the code. It used to take three,
        // with the middle one spending a magic sentence to get the router from Interviewer to Coder.
        var builder = new ScriptedBuilder(
            "Which timeframe, and one position at a time?",
            Kernel);

        var pane = Pane(builder);
        pane.BuildEffort = StrategyBuildEffort.Max;

        pane.Composer = "three candles, two triangles, trade the bigger area";
        await pane.SendCommand.ExecuteAsync(null);

        // Turn one is an interview, and that is correct — the brief leaves real things open.
        Assert.True(pane.AwaitingAnswer, "a question must stop the run and wait");

        // The pane is seeded with a scaffold file, so "no code yet" means the model has not written
        // its unit into it — not that the list is empty.
        Assert.DoesNotContain(pane.Files, f => f.Content.Contains("TriangleKernel"));

        pane.Composer = "approved, now start building";
        await pane.SendCommand.ExecuteAsync(null);

        // THE DEADLOCK, pinned where it can still happen. The answer has to reach the model, and the
        // code it replies with has to land in the editor. There is no routing state between the two
        // any more, which is the point: nothing sits between a settled brief and its code.
        Assert.Contains(builder.Client.Prompts,
            p => p.Contains("approved, now start building", StringComparison.Ordinal));
        Assert.Contains(pane.Files, f => f.Content.Contains("TriangleKernel"));
        Assert.False(pane.AwaitingAnswer, "code arrived, so nothing is waiting on the user");
    }

    /// <summary>
    /// The interviewer must be able to see what it is building on the turn after the first. It used to
    /// be handed the user's answer as the ENTIRE brief, so "approved, now start building" arrived with
    /// no instrument, no rules and nothing to approve — and another interview is the only sane reply.
    /// </summary>
    [Fact]
    public async Task The_second_turn_still_carries_the_original_brief()
    {
        var builder = new ScriptedBuilder(
            "Which timeframe?",
            "Specification settled. " + AgentPrompts.Handover,
            Kernel);

        var pane = Pane(builder);
        pane.BuildEffort = StrategyBuildEffort.Max;

        pane.Composer = "three candles, two triangles, trade the bigger area";
        await pane.SendCommand.ExecuteAsync(null);

        pane.Composer = "approved, now start building";
        await pane.SendCommand.ExecuteAsync(null);

        var second = builder.Client.Prompts[1];
        Assert.Contains("three candles, two triangles", second);
        Assert.Contains("approved, now start building", second);
    }

    /// <summary>
    /// The escape has to actually be sent.
    ///
    /// <para><b>This assertion is weaker than it was, and deliberately so.</b> It used to check that
    /// the escape flipped the ROUTING STATE, forcing a Coder turn whatever the model wanted — a
    /// guarantee only a committee can make. The committee is gone: at every effort this is now one
    /// streaming conversation, because the committee never delivered a file on a real provider and its
    /// blocking, unstreamed calls are what left users watching a status line for minutes.</para>
    ///
    /// <para>What a single conversation can promise is that the instruction REACHES the model, which is
    /// exactly what every other coding tool promises. So that is what is pinned: press the escape, and
    /// the words go up the wire. A button that renders and sends nothing is still the defect worth
    /// catching, and it is the one that can actually regress here.</para>
    /// </summary>
    [Fact]
    public async Task Pressing_just_build_it_sends_the_escape_to_the_model()
    {
        // A model that asks forever, which is exactly the failure mode observed live.
        var builder = new ScriptedBuilder("But what about the stop loss?");

        var pane = Pane(builder);
        pane.BuildEffort = StrategyBuildEffort.Max;

        pane.Composer = "three candles, two triangles";
        await pane.SendCommand.ExecuteAsync(null);

        pane.Composer = AuthoringAction.JustBuildIt;
        await pane.SendCommand.ExecuteAsync(null);

        Assert.Contains(
            builder.Client.Prompts.Skip(1),
            prompt => prompt.Contains("Stop asking", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE ONE THAT DELETED WORK. New Strategy must not leave the new conversation wearing the old
    /// conversation's id, because Save() writes to a file named after it — so the first turn of the new
    /// chat saved itself over the session you pressed New to keep.
    /// </summary>
    [Fact]
    public async Task New_strategy_does_not_overwrite_the_conversation_it_just_left()
    {
        var pane = Pane(new ScriptedBuilder("Anything."));

        pane.Composer = "a cumulative delta divergence fade";
        await pane.SendCommand.ExecuteAsync(null);

        var first = pane.StrategyId;
        Assert.False(string.IsNullOrWhiteSpace(first));

        pane.NewChatCommand.Execute(null);
        pane.Composer = "an order book liquidity heatmap";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.NotEqual(first, pane.StrategyId);

        // Both are on disk. Before the fix the second overwrote the first.
        Assert.NotNull(AuthoringSessionStore.Load(first!));
        Assert.NotNull(AuthoringSessionStore.Load(pane.StrategyId!));
    }

    /// <summary>New Strategy restores the defaults, which is what lets the next brief name itself.</summary>
    [Fact]
    public void New_strategy_returns_the_identity_to_its_default()
    {
        var pane = Pane(new ScriptedBuilder("Anything."));
        pane.StrategyId = "somethingTheUserNamed";
        pane.DisplayName = "Something the user named";

        pane.NewChatCommand.Execute(null);

        Assert.Equal("myStrategy", pane.StrategyId);
        Assert.Equal("My custom strategy", pane.DisplayName);
    }

    /// <summary>
    /// A session names itself after the IDEA, not the act of asking.
    ///
    /// <para>The user's real brief opened "create me a strategy, in the strategy we take recent 3
    /// candles…" and every session it produced was called <c>createMeStrategy</c> / "Create me a
    /// strategy" — three in a row named after the request rather than the thing requested.</para>
    /// </summary>
    [Theory]
    [InlineData("create me a strategy,  in the strategy we take recent 3 candles data OHLC and build two triangles")]
    [InlineData("Build a visualizer: an order book heatmap with a depth ladder")]
    [InlineData("can you make me an indicator that shows cumulative delta divergence")]
    public async Task A_brief_names_its_session_after_the_idea_not_the_request(string brief)
    {
        var pane = Pane(new ScriptedBuilder("Anything."));

        pane.Composer = brief;
        await pane.SendCommand.ExecuteAsync(null);

        Assert.NotEqual("myStrategy", pane.StrategyId);
        Assert.False(pane.StrategyId!.StartsWith("create", StringComparison.OrdinalIgnoreCase));
        Assert.False(pane.StrategyId!.StartsWith("build", StringComparison.OrdinalIgnoreCase));
        Assert.False(pane.StrategyId!.StartsWith("make", StringComparison.OrdinalIgnoreCase));
        Assert.False(pane.DisplayName!.StartsWith("Create me", StringComparison.OrdinalIgnoreCase));
        Assert.False(pane.DisplayName!.StartsWith("Can you", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name the user typed is theirs, and a brief never overwrites it.</summary>
    [Fact]
    public async Task A_name_the_user_chose_survives_the_first_brief()
    {
        var pane = Pane(new ScriptedBuilder("Anything."));
        pane.StrategyId = "myOwnName";
        pane.DisplayName = "My own name";

        pane.Composer = "create me a strategy that fades the touch";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.Equal("myOwnName", pane.StrategyId);
        Assert.Equal("My own name", pane.DisplayName);
    }

    private static StrategyAuthoringViewModel Pane(IAiStrategyBuilder builder)
    {
        var pane = new StrategyAuthoringViewModel(
            new RoslynStrategyCompiler(),
            new NullRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            builder);

        pane.StrategyId = "triangles";
        pane.DisplayName = "Triangles";
        return pane;
    }

    /// <summary>A real session — real orchestrator, real pack — around a scripted provider.</summary>
    private sealed class ScriptedBuilder(params string[] replies) : IAiStrategyBuilder
    {
        public ScriptedClient Client { get; } = new(replies);

        public IReadOnlyList<IStrategyCodegenClient> Providers => [Client];

        public IStrategyCodegenClient? DefaultProvider => Client;

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
            Client;

        public IReadOnlyList<string> ModelsFor(string providerId) => [];

        public IReadOnlyList<AiModelChoice> AllModels() =>
            [new AiModelChoice("scripted", "Scripted", "scripted-model")];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient provider, string strategyId, string displayName,
            IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null, AuthoringKind kind = AuthoringKind.Strategy) =>
            new StrategyCodegenOrchestrator(
                    new RoslynStrategyCompiler(), logger: null, skills: StrategySkillLibrary.Load())
                .CreateSession(
                    provider, StrategyContextPack.Load().SystemPrompt, strategyId, displayName,
                    maxFixAttempts: 0, history, priorUsage, profile, kind);

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Answers in order, and records which role asked and what it was shown.</summary>
    private sealed class ScriptedClient(string[] replies) : IStrategyCodegenClient
    {
        private int _index;

        /// <summary>The role instruction of each call, so a test can say who took the turn.</summary>
        public List<string> Roles { get; } = [];

        /// <summary>The composed user message of each call.</summary>
        public List<string> Prompts { get; } = [];

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            Roles.Add(request.RoleInstruction ?? string.Empty);
            Prompts.Add(string.Join("\n", request.Messages.Select(m => m.Content)));

            var text = replies[Math.Min(_index++, replies.Length - 1)];

            // Exactly what a real client does: the transport hands back prose, and the fences in it are
            // extracted into files. A double that skipped this would never produce code and would prove
            // the loop delivers nothing, which is the bug rather than the contract.
            var files = CodegenCodeExtractor.ExtractFiles(text);

            return Task.FromResult(files.Count > 0
                ? StrategyCodegenResponse.Ok(files, text)
                : StrategyCodegenResponse.Reply(text));
        }
    }

    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];

        public event EventHandler? Changed;

        public StrategyCatalogEntry? Find(string id) => null;

        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);

        public bool Remove(string id) => false;
    }
}
