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
/// Every build effort runs as ONE STREAMING CONVERSATION, and a turn always leaves a trace.
///
/// <para><b>Deep and Max used to route to the six-agent committee, and that is where the builder went
/// to die.</b> The committee calls GenerateAsync, which posts with stream:false — one blocking request
/// per agent turn, no streamed text, no thinking, and by deliberate design no timeout. On a reasoning
/// model over a slow route that is an unbounded silent wait: the workspace showed a status line and
/// nothing else for as long as the user was willing to stare at it.</para>
///
/// <para>Measured from a user's own saved state before this changed: five TokenRouter/GLM sessions,
/// every completed agent turn logged as "Interviewer — answered without code", 0/0 tokens on four of
/// them, and not one line of generated code in any of them.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class EveryEffortStreamsTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-streams-" + Guid.NewGuid().ToString("N"));

    public EveryEffortStreamsTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(StrategyBuildEffort.Quick)]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Deep)]
    [InlineData(StrategyBuildEffort.Max)]
    public async Task Every_effort_streams_and_the_reply_lands_in_the_transcript(StrategyBuildEffort effort)
    {
        // StreamAsync is the streaming seam; GenerateAsync is the blocking one the committee used.
        // Counting each tells us which path the effort actually took.
        var builder = new CountingBuilder("I need one thing before I write it.");
        var pane = Pane(builder);

        pane.BuildEffort = effort;
        pane.Composer = "build me a strategy";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.True(builder.BlockingCalls == 0,
            $"{effort} used the blocking GenerateAsync path ({builder.BlockingCalls} call(s)); every " +
            "effort must stream, or the user watches a status line and nothing else.");
        Assert.True(builder.StreamCalls > 0, $"{effort} never streamed");
        Assert.Contains(pane.Messages, m => m.IsAssistant && m.Text.Contains("before I write it"));
    }

    [Theory]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Max)]
    public async Task The_models_thinking_reaches_the_transcript(StrategyBuildEffort effort)
    {
        var pane = Pane(new CountingBuilder("Here is the plan."));

        pane.BuildEffort = effort;
        pane.Composer = "build me a strategy";
        await pane.SendCommand.ExecuteAsync(null);

        // The collapsed disclosure the workspace renders. Its absence is what the user saw as a
        // shimmering verb and nothing else through minutes of a reasoning model's silence.
        Assert.Contains(pane.Messages, m => m.Kind == AuthoringMessage.KindThinking);
    }

    [Theory]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Max)]
    public async Task A_turn_that_dies_mid_flight_still_says_so(StrategyBuildEffort effort)
    {
        // A dropped connection used to append NOTHING: the transcript kept the user's message and no
        // reply, and the status line explaining it is not saved — so reopening the session showed a
        // brief that had apparently never been answered. That is what the stalled sessions look like.
        var pane = Pane(new DroppingBuilder());

        pane.BuildEffort = effort;
        pane.Composer = "build me a strategy";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.Contains(pane.Messages, m => m.Kind == AuthoringMessage.KindTool);
        Assert.False(pane.IsGenerating, "the composer must always come back");
    }

    private static StrategyAuthoringViewModel Pane(IAiStrategyBuilder builder) => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        builder);

    // ── doubles ─────────────────────────────────────────────────────────────────────────────────

    private abstract class BuilderBase(IStrategyCodegenClient client) : IAiStrategyBuilder
    {
        public IReadOnlyList<IStrategyCodegenClient> Providers => [client];

        public IStrategyCodegenClient? DefaultProvider => client;

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
            client;

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

    private sealed class CountingBuilder : BuilderBase
    {
        private readonly CountingClient _client;

        public CountingBuilder(string reply) : this(new CountingClient(reply)) { }

        private CountingBuilder(CountingClient client) : base(client) => _client = client;

        public int BlockingCalls => _client.BlockingCalls;

        public int StreamCalls => _client.StreamCalls;
    }

    private sealed class DroppingBuilder() : BuilderBase(new DroppingClient());

    private sealed class CountingClient(string reply) : IStrategyCodegenClient
    {
        public int BlockingCalls { get; private set; }

        public int StreamCalls { get; private set; }

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;
        public string Model => "scripted-model";

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            BlockingCalls++;
            return Task.FromResult(StrategyCodegenResponse.Reply(reply));
        }

        public async IAsyncEnumerable<CodegenEvent> StreamAsync(
            StrategyCodegenRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCalls++;
            await Task.Yield();
            yield return new CodegenEvent.ReasoningDelta("considering the entry rule");
            yield return new CodegenEvent.TextDelta(reply);
            yield return new CodegenEvent.Completed(StrategyCodegenResponse.Reply(reply));
        }
    }

    /// <summary>A provider whose connection dies mid-stream, the way a gateway hanging up looks.</summary>
    private sealed class DroppingClient : IStrategyCodegenClient
    {
        public string ProviderId => "dropping";
        public string DisplayName => "Dropping";
        public bool IsAvailable => true;
        public string Model => "drop-1";

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            throw new OperationCanceledException();

        public async IAsyncEnumerable<CodegenEvent> StreamAsync(
            StrategyCodegenRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new CodegenEvent.TextDelta("starting");
            throw new OperationCanceledException();
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
