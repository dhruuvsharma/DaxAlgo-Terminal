using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// What the MULTI-AGENT path actually sends as its system prompt.
///
/// <para>Deep and Max effort route through <c>RunAgentsAsync</c>, and it used to hand the loop
/// <c>StrategyContextPack.Load().SystemPrompt</c> — the generated surface and the conventions, raw.
/// Everything the single-conversation path composes on top was therefore absent at the two highest
/// settings:</para>
///
/// <list type="bullet">
/// <item>the kind block, so the Strategy/Visualizer switch was decoration again — the exact bug
/// <c>AuthoringKindBrief</c> exists to fix, reintroduced by a second path;</item>
/// <item>the questions instruction, which is the sharpest of them: <c>RunAgentsAsync</c> PARSES that
/// block and renders its options as buttons, while never telling the model the format exists. The
/// reader had been wired onto this path and the writer had not;</item>
/// <item>the worked exemplar;</item>
/// <item>the domain packs — so <c>MaxSkills: 5</c> (Deep) and <c>MaxSkills: 8</c> (Max) were dead
/// numbers, and the two efforts that buy the largest skill budget loaded none.</item>
/// </list>
///
/// <para>Every piece above had passing unit tests of its own. None of them could see this, because
/// they tested the composer rather than the path. These drive the real view-model at Deep effort and
/// assert on the bytes the agent loop was actually given.</para>
/// </summary>
public sealed class AgentSharedContextTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-agent-context-" + Guid.NewGuid().ToString("N"));

    public AgentSharedContextTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(StrategyBuildEffort.Deep)]
    [InlineData(StrategyBuildEffort.Max)]
    public async Task Every_effort_that_routes_through_agents_composes_its_prompt(StrategyBuildEffort effort)
    {
        Assert.True(StrategyBuildProfile.For(effort).UseAgents, "this test is about the agent path");

        var sent = await RunAsync(effort, AuthoringKind.Strategy, Brief);

        Assert.Contains("What you are writing right now: a STRATEGY", sent);
        Assert.Contains("questions", sent);
        Assert.Contains("A complete unit of this kind", sent);
        Assert.Contains("# Loaded reference", sent);
    }

    [Fact]
    public async Task The_visualizer_switch_reaches_the_agents()
    {
        // The switch is not decoration at Deep: a user who picked visualizer must not silently get a
        // kernel, which is precisely what a raw pack produces.
        var sent = await RunAsync(StrategyBuildEffort.Deep, AuthoringKind.Visualizer, Brief);

        Assert.Contains("What you are writing right now: a VISUALIZER", sent);
        Assert.DoesNotContain("What you are writing right now: a STRATEGY", sent);
    }

    [Fact]
    public async Task The_brief_picks_the_packs_the_agents_get()
    {
        // Not merely "some skills loaded" — the ones this brief warrants. A ladder-and-heatmap brief
        // that arrives without the order-flow catalogue leaves the model hand-rolling widgets that
        // already exist, which is what the budget mechanism is for.
        var sent = await RunAsync(StrategyBuildEffort.Deep, AuthoringKind.Strategy, Brief);

        var expected = StrategySkillLibrary.Load().SelectFor(
            Brief, StrategyBuildProfile.For(StrategyBuildEffort.Deep).MaxSkills, AuthoringKind.Strategy);

        Assert.NotEmpty(expected);
        foreach (var skill in expected) Assert.Contains(skill.Body[..120], sent);
    }

    [Fact]
    public void The_assertions_above_are_not_vacuous()
    {
        // The guard that makes the rest of this file mean something: the raw pack — what the agent path
        // used to send — carries none of what those tests look for. Without this, a change that made
        // the pack itself contain them would leave every assertion green and the wiring gone.
        var raw = StrategyContextPack.Load().SystemPrompt;

        Assert.DoesNotContain("What you are writing right now", raw);
        Assert.DoesNotContain("A complete unit of this kind", raw);
        Assert.DoesNotContain("# Loaded reference", raw);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private const string Brief =
        "an order book depth ladder with a liquidity heatmap and cumulative delta";

    /// <summary>Drives a real turn and returns the shared context the agent loop actually sent.</summary>
    private static async Task<string> RunAsync(
        StrategyBuildEffort effort, AuthoringKind kind, string brief)
    {
        var builder = new RecordingBuilder();
        var pane = new StrategyAuthoringViewModel(
            new RoslynStrategyCompiler(),
            new NullRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            builder);

        pane.StrategyId = "agent-context";
        pane.DisplayName = "Agent context";
        pane.AuthoringKind = kind;
        pane.BuildEffort = effort;
        pane.Composer = brief;

        await pane.SendCommand.ExecuteAsync(null);

        Assert.True(
            builder.Recorder.SharedContext is { Length: > 0 },
            "the agent loop never called the provider — the turn did not reach the agent path");

        return builder.Recorder.SharedContext!;
    }

    /// <summary>
    /// A builder that hands back a REAL session — real orchestrator, real skill library, real pack —
    /// wrapped around a recording provider. A fake session would test the fake's composition.
    /// </summary>
    private sealed class RecordingBuilder : IAiStrategyBuilder
    {
        public RecordingClient Recorder { get; } = new();

        public IReadOnlyList<IStrategyCodegenClient> Providers => [Recorder];

        public IStrategyCodegenClient? DefaultProvider => Recorder;

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
            Recorder;

        public IReadOnlyList<string> ModelsFor(string providerId) => [];

        public IReadOnlyList<AiModelChoice> AllModels() =>
            [new AiModelChoice("recording", "Recording", "recording-model")];

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

    /// <summary>Records the shared context it was given, then answers with prose so the loop stops at
    /// one turn — this is about the prompt, not about driving a build to completion.</summary>
    private sealed class RecordingClient : IStrategyCodegenClient
    {
        public string? SharedContext { get; private set; }

        public string ProviderId => "recording";
        public string DisplayName => "Recording";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            SharedContext ??= request.SystemContext;
            return Task.FromResult(StrategyCodegenResponse.Reply("Which instrument and tick size?"));
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
