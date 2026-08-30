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
/// The composer has to accept a second prompt.
///
/// <para>`IsGenerating` gates Send and swaps it for Stop, and it used to be raised several statements
/// before the `try` whose `finally` lowered it. Anything that threw in that gap left it true forever:
/// Send permanently disabled, Stop permanently showing, and no way back except restarting the
/// terminal. The gap contained `EnsureSession`, which builds a `StrategyBuildSession` — whose
/// constructor throws by design when the generated context pack is missing. So a build shipped
/// without `sdk/ai-context/generated/sdk-surface.md` bricked the composer on the user's FIRST
/// prompt.</para>
///
/// <para>These drive the real view-model against a builder that fails the way a real one can.</para>
/// </summary>
public sealed class StrategyAuthoringSendTests : IDisposable
{
    /// <summary>
    /// Redirects the saved-chat store for the fixture's life.
    ///
    /// <para>Not hygiene. A turn calls <c>Save()</c> in its finally, so without this these tests write
    /// their fixtures into the chat list of whoever runs them — which is exactly what happened, and was
    /// found by rendering the composer and seeing "Test strategy" in the session rail.</para>
    /// </summary>
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-authoring-send-" + Guid.NewGuid().ToString("N"));

    public StrategyAuthoringSendTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_default_session_directory_is_what_the_app_would_use()
    {
        // The un-redirected value is a real path — the same guard AiCodegenUserFile carries, for the
        // same reason: every test here reassigns it, so none of them would ever observe a broken one.
        AuthoringSessionStore.Directory = null!;

        Assert.False(string.IsNullOrWhiteSpace(AuthoringSessionStore.Directory));
        Assert.Equal(AuthoringSessionStore.DefaultDirectory, AuthoringSessionStore.Directory);
        Assert.True(Path.IsPathRooted(AuthoringSessionStore.Directory));
    }

    [Fact]
    public async Task A_provider_that_throws_leaves_the_composer_usable()
    {
        // The bug, directly: one failed turn must not cost the session.
        var pane = Pane(new ThrowingBuilder());
        pane.Composer = "fade order-flow imbalance at the touch";

        await pane.SendCommand.ExecuteAsync(null);

        Assert.False(pane.IsGenerating, "a failed turn must lower the busy flag");
        Assert.Contains("Generation error", pane.AiStatus);

        // And the button is genuinely usable again, not merely un-flagged.
        pane.Composer = "try again";
        Assert.True(pane.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_failure_is_reported_in_the_transcript_not_swallowed()
    {
        // A composer that silently does nothing is worse than one that says what went wrong: the user
        // retries the same prompt, pays for it again, and learns nothing.
        var pane = Pane(new ThrowingBuilder());
        pane.Composer = "a mean-reversion strategy on the ES";

        await pane.SendCommand.ExecuteAsync(null);

        Assert.Contains(pane.Messages, m => m.Text.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_prompt_goes_through_after_a_failure()
    {
        // The whole point. Two turns in a row against a provider that fails both times.
        var builder = new ThrowingBuilder();
        var pane = Pane(builder);

        pane.Composer = "first";
        await pane.SendCommand.ExecuteAsync(null);
        pane.Composer = "second";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, builder.Attempts);
        Assert.False(pane.IsGenerating);
    }

    [Fact]
    public void Stop_lowers_the_flag_itself_rather_than_asking_the_turn_to()
    {
        // Cancellation is cooperative: a provider that ignores its token, or a subprocess that has
        // stopped reading it, leaves the await outstanding. Stop is what a user presses precisely when
        // something is not responding, so it cannot depend on that thing responding.
        var pane = Pane(new ThrowingBuilder());
        pane.IsGenerating = true;

        Assert.True(pane.StopCommand.CanExecute(null));
        pane.StopCommand.Execute(null);

        Assert.False(pane.IsGenerating);
        Assert.Contains("Stopped", pane.AiStatus);
    }

    [Fact]
    public async Task An_empty_prompt_is_not_a_turn()
    {
        var builder = new ThrowingBuilder();
        var pane = Pane(builder);

        pane.Composer = "   ";
        Assert.False(pane.SendCommand.CanExecute(null));

        await pane.SendCommand.ExecuteAsync(null);
        Assert.Equal(0, builder.Attempts);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static StrategyAuthoringViewModel Pane(IAiStrategyBuilder builder)
    {
        var pane = new StrategyAuthoringViewModel(
            new RoslynStrategyCompiler(),
            new NullRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            builder);

        pane.StrategyId = "test-strategy";
        pane.DisplayName = "Test strategy";
        return pane;
    }

    /// <summary>
    /// A builder that fails where the real one can: at session construction.
    ///
    /// <para>`StrategyBuildSession`'s constructor loads the context pack and throws when it is absent,
    /// which is what made this a first-prompt failure rather than a rare one.</para>
    /// </summary>
    private sealed class ThrowingBuilder : IAiStrategyBuilder
    {
        public int Attempts { get; private set; }

        public IReadOnlyList<IStrategyCodegenClient> Providers => [new AvailableClient()];

        public IStrategyCodegenClient? DefaultProvider => Providers[0];

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
            Providers[0];

        public IReadOnlyList<string> ModelsFor(string providerId) => [];

        public IReadOnlyList<AiModelChoice> AllModels() =>
            [new AiModelChoice("fake", "Fake", "fake-model")];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient provider, string strategyId, string displayName,
            IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null, AuthoringKind kind = AuthoringKind.Strategy)
        {
            Attempts++;
            throw new InvalidOperationException("boom: the context pack is missing");
        }

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AvailableClient : IStrategyCodegenClient
    {
        public string ProviderId => "fake";
        public string DisplayName => "Fake";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
