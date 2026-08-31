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
/// The one-click replies a waiting turn offers, and the escape from an interview.
///
/// <para>The pack now tells the model to ask as many questions as the job needs, in as many rounds as
/// it needs. That is right for a window with a book, a heatmap and a strip, and it is intolerable
/// without a one-click way to end it — so the escape is not a nicety, it is the other half of the
/// instruction.</para>
///
/// <para>It did not exist. Every waiting turn got the same three buttons, all of which presume a
/// specification: "looks right — build it" beside "which instrument?" sends "that specification is
/// right", which answers a question nobody asked.</para>
///
/// <para>These drive the real view-model down both paths — the single conversation and the agents —
/// because a button that renders on one of them is a button half the users never see.</para>
/// </summary>
public sealed class AuthoringActionTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-actions-" + Guid.NewGuid().ToString("N"));

    public AuthoringActionTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    private const string WithQuestions = """
        I need one thing before writing this.

        ```questions
        [
          { "id": "instrument", "question": "Which instrument?", "kind": "single",
            "options": [ { "label": "BTCUSDT perp" }, { "label": "ES futures" } ] }
        ]
        ```
        """;

    private const string SpecificationOnly =
        "Here is what I will build: a depth ladder beside a liquidity heatmap, with a "
        + "microstructure strip underneath. Confirm and I will write it.";

    [Theory]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Deep)]
    public async Task An_interview_offers_a_way_out(StrategyBuildEffort effort)
    {
        // Both efforts, because the two paths set their buttons in different methods and only one of
        // them was ever finished before.
        var pane = await TurnAsync(effort, WithQuestions);

        Assert.True(pane.AwaitingAnswer);
        Assert.Contains(pane.Actions, a => a.Label.Contains("Just build it", StringComparison.Ordinal));

        // And the reply it sends asks for the assumptions back — a "just build it" that settles the
        // open questions invisibly leaves the user holding a unit they cannot correct.
        var escape = pane.Actions.First(a => a.Label.Contains("Just build it", StringComparison.Ordinal));
        Assert.Contains("assumed", escape.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Deep)]
    public async Task An_interview_is_not_offered_the_approval_buttons(StrategyBuildEffort effort)
    {
        // The defect this replaces: "Looks right — build it" beside "Which instrument?" sends "that
        // specification is right", which answers a question nobody asked.
        var pane = await TurnAsync(effort, WithQuestions);

        Assert.DoesNotContain(
            pane.Actions, a => a.Label.Contains("Looks right", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Deep)]
    public async Task A_specification_still_gets_its_approval_buttons(StrategyBuildEffort effort)
    {
        // The other shape, which was already right and must stay so. A turn that stops with a plan and
        // no options has nothing to enumerate, and approval is exactly what it is waiting for.
        var pane = await TurnAsync(effort, SpecificationOnly);

        Assert.True(pane.AwaitingAnswer);
        Assert.Contains(pane.Actions, a => a.Label.Contains("Looks right", StringComparison.Ordinal));
        Assert.Contains(pane.Actions, a => a.Label.Contains("simpler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_waiting_turn_offers_a_composer_escape()
    {
        // Whatever the shape, the user must be able to say something of their own without inventing a
        // sentence to type into an empty box.
        foreach (var reply in new[] { WithQuestions, SpecificationOnly })
        {
            var pane = await TurnAsync(StrategyBuildEffort.Standard, reply);
            Assert.Contains(pane.Actions, a => string.IsNullOrEmpty(a.Reply));
        }
    }

    [Fact]
    public async Task Pressing_the_escape_actually_sends_it()
    {
        // A button that renders and does nothing is the same defect one layer down.
        var builder = new ScriptedBuilder(WithQuestions);
        var pane = Pane(builder);
        pane.BuildEffort = StrategyBuildEffort.Standard;
        pane.Composer = "an order book window";
        await pane.SendCommand.ExecuteAsync(null);

        var escape = pane.Actions.First(a => a.Label.Contains("Just build it", StringComparison.Ordinal));
        await pane.ChooseCommand.ExecuteAsync(escape);

        Assert.Contains(
            builder.Sent, sent => sent.Contains("Stop asking", StringComparison.OrdinalIgnoreCase));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<StrategyAuthoringViewModel> TurnAsync(
        StrategyBuildEffort effort, string reply)
    {
        var pane = Pane(new ScriptedBuilder(reply));
        pane.BuildEffort = effort;
        pane.Composer = "an order book window";

        await pane.SendCommand.ExecuteAsync(null);
        return pane;
    }

    private static StrategyAuthoringViewModel Pane(IAiStrategyBuilder builder)
    {
        var pane = new StrategyAuthoringViewModel(
            new RoslynStrategyCompiler(),
            new NullRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            builder);

        pane.StrategyId = "action-test";
        pane.DisplayName = "Action test";
        return pane;
    }

    /// <summary>A real session wrapped around a client that always answers with the same prose, so a
    /// turn ends waiting in a known shape.</summary>
    private sealed class ScriptedBuilder(string reply) : IAiStrategyBuilder
    {
        private readonly ScriptedClient _client = new(reply);

        public IReadOnlyList<string> Sent => _client.Sent;

        public IReadOnlyList<IStrategyCodegenClient> Providers => [_client];

        public IStrategyCodegenClient? DefaultProvider => _client;

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
            _client;

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

    private sealed class ScriptedClient(string reply) : IStrategyCodegenClient
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent => _sent;

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            foreach (var message in request.Messages.Where(m => m.Role == CodegenRole.User))
                _sent.Add(message.Content);

            return Task.FromResult(StrategyCodegenResponse.Reply(reply));
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
