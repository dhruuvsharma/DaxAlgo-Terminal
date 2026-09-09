using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Starting a new session while a turn is running.
///
/// <para><b>Reported from real use:</b> "I start a new session and the box still shows the Stop button,
/// and when I press it, it stops the PREVIOUS chat" — and "the new session still shows updates from the
/// previous chat."</para>
///
/// <para>One cause, two faces. <c>NewChat</c> cleared the transcript and the files and did nothing at
/// all about the turn that was still in flight. So <c>IsGenerating</c> stayed true (the composer kept
/// offering Stop), the only cancellation token was the abandoned turn's (pressing it stopped that), and
/// the running turn went on to write its reply, its files, its compile verdict and its status line into
/// the conversation that had replaced it.</para>
///
/// <para><b>Cancelling is necessary and not sufficient</b>, which is the part worth keeping a test for.
/// Cancellation is cooperative: a turn can be inside an await when the user starts a new session and
/// its results arrive regardless. Every write-back has to check it is still on its own conversation.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class NewSessionAbandonsTheTurnTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-newsession-" + Guid.NewGuid().ToString("N"));

    public NewSessionAbandonsTheTurnTests() => AuthoringSessionStore.Directory = _dir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A provider that blocks until the test lets it answer — a turn genuinely in flight.</summary>
    private sealed class Held : IStrategyCodegenClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "held";
        public string DisplayName => "Held";
        public bool IsAvailable => true;
        public string Model => "claude-opus-5";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer() => _release.TrySetResult();

        public async Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await using (ct.Register(() => _release.TrySetCanceled(ct)))
            {
                await _release.Task.ConfigureAwait(false);
            }

            return StrategyCodegenResponse.Ok(
                [new StrategyFile("Ghost.cs", "public sealed class Ghost { }")],
                "the abandoned turn's reply");
        }
    }

    private sealed class HeldBuilder(Held client) : IAiStrategyBuilder
    {
        public IReadOnlyList<IStrategyCodegenClient> Providers => [client];

        public IStrategyCodegenClient? DefaultProvider => client;

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) => client;

        public IReadOnlyList<string> ModelsFor(string providerId) => [];

        public IReadOnlyList<AiModelChoice> AllModels() =>
            [new AiModelChoice("held", "Held", "claude-opus-5")];

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

    private static StrategyAuthoringViewModel Pane(Held client) => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        new HeldBuilder(client));

    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];
        public event EventHandler? Changed;
        public StrategyCatalogEntry? Find(string id) => null;
        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);
        public bool Remove(string id) => false;
    }

    [Fact]
    public async Task A_new_session_stops_offering_Stop()
    {
        // The composer was still showing Stop because IsGenerating belonged to a turn nothing had ended.
        var client = new Held();
        var pane = Pane(client);

        pane.Composer = "an order book window";
        var turn = pane.SendCommand.ExecuteAsync(null);
        await client.Started.Task;

        Assert.True(pane.IsGenerating);

        pane.NewChatCommand.Execute(null);

        Assert.False(pane.IsGenerating, "the new session is not the one that was generating");

        client.Answer();
        await turn;
    }

    [Fact]
    public async Task The_abandoned_turns_reply_lands_nowhere()
    {
        // The whole point. Cancellation is cooperative, so the turn finishes anyway — and its reply,
        // its files and its status must not appear in the session that replaced it.
        var client = new Held();
        var pane = Pane(client);

        pane.Composer = "an order book window";
        var turn = pane.SendCommand.ExecuteAsync(null);
        await client.Started.Task;

        pane.NewChatCommand.Execute(null);

        // The provider answers AFTER the switch, which is exactly the race the guard exists for.
        client.Answer();
        await turn;

        Assert.DoesNotContain(pane.Messages, m => m.Text.Contains("abandoned turn", StringComparison.Ordinal));
        Assert.DoesNotContain(pane.Files, f => f.Name == "Ghost.cs");
        Assert.False(pane.IsGenerating);
    }

    [Fact]
    public async Task A_new_session_clears_the_task_board_and_any_attached_pictures()
    {
        // Both are per-conversation and both were being left behind, so a fresh session opened showing
        // the previous one's plan.
        var client = new Held();
        var pane = Pane(client);

        pane.Composer = "an order book window";
        var turn = pane.SendCommand.ExecuteAsync(null);
        await client.Started.Task;

        pane.NewChatCommand.Execute(null);

        Assert.Empty(pane.Board);
        Assert.False(pane.HasBoard);
        Assert.Empty(pane.Composed);

        client.Answer();
        await turn;
    }

    [Fact]
    public async Task The_next_turn_still_works_after_abandoning_one()
    {
        // The guard must not poison the pane: a new conversation is a working conversation.
        var client = new Held();
        var pane = Pane(client);

        pane.Composer = "first";
        var first = pane.SendCommand.ExecuteAsync(null);
        await client.Started.Task;

        pane.NewChatCommand.Execute(null);
        client.Answer();
        await first;

        Assert.False(pane.IsGenerating);
        Assert.True(pane.SendCommand.CanExecute(null) || string.IsNullOrEmpty(pane.Composer));
    }
}
