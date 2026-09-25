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
/// The composer's model flyout — the list behind the ◇ pill, which is <c>AllModels</c>.
///
/// <para>Two controls fed something the flyout does not show. Closing provider setup rebuilt the
/// provider rows and not the flyout, so a provider whose key had just been saved stayed greyed out
/// there until a restart; and the flyout's ↻ filled <c>Models</c>, which nothing on screen lists, so it
/// said "2 model(s) available" and changed nothing.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class ComposerModelFlyoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-flyout-" + Guid.NewGuid().ToString("N"));

    public ComposerModelFlyoutTests() => AuthoringSessionStore.Directory = _dir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Closing_provider_setup_rebuilds_the_flyout()
    {
        var builder = new Switchable { KeySaved = false };
        var pane = Pane(builder, new Setup(() => builder.KeySaved = true));

        Assert.All(pane.AllModels, choice => Assert.False(choice.IsAvailable));

        pane.OpenProviderSettingsCommand.Execute(null);

        Assert.NotEmpty(pane.AllModels);
        Assert.All(pane.AllModels, choice => Assert.True(choice.IsAvailable));
    }

    [Fact]
    public void Closing_provider_setup_keeps_the_chosen_model()
    {
        var builder = new Switchable();
        var pane = Pane(builder, new Setup(() => { }));

        pane.SelectedModel = "m2";
        pane.OpenProviderSettingsCommand.Execute(null);

        Assert.Equal("m2", pane.SelectedModel);
        Assert.Equal("m2", pane.SelectedModelChoice?.ModelId);
    }

    [Fact]
    public async Task The_flyouts_refresh_shows_what_the_provider_serves()
    {
        var pane = Pane(new Switchable(), setup: null);

        await pane.RefreshModelsCommand.ExecuteAsync(null);

        Assert.Equal(["live-1", "live-2"], pane.AllModels.Where(c => c.ProviderId == "p").Select(c => c.ModelId));
        Assert.Equal("live-1", pane.SelectedModel);
        Assert.Equal("live-1", pane.SelectedModelChoice?.ModelId);
    }

    private static StrategyAuthoringViewModel Pane(Switchable builder, IAiProviderSettingsLauncher? setup) => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        builder,
        providerSettings: setup);

    /// <summary>One provider, whose key is saved when the test says so — as the setup window does.</summary>
    private sealed class Switchable : IAiStrategyBuilder
    {
        public bool KeySaved { get; set; } = true;

        private Client Make() => new(KeySaved);

        public IReadOnlyList<IStrategyCodegenClient> Providers => [Make()];

        public IStrategyCodegenClient? DefaultProvider => Make();

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) => Make();

        public IReadOnlyList<string> ModelsFor(string providerId) => ["m1", "m2"];

        public IReadOnlyList<AiModelChoice> AllModels() =>
        [
            new AiModelChoice("p", "P", "m1") { IsAvailable = KeySaved },
            new AiModelChoice("p", "P", "m2") { IsAvailable = KeySaved },
        ];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient provider, string strategyId, string displayName,
            IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null, AuthoringKind kind = AuthoringKind.Strategy) =>
            throw new NotSupportedException();

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Client(bool available) : IStrategyCodegenClient
    {
        public string ProviderId => "p";
        public string DisplayName => "P";
        public bool IsAvailable => available;
        public string Model => "m1";

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["live-1", "live-2"]);

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Setup(Action onOpen) : IAiProviderSettingsLauncher
    {
        public void Open() => onOpen();
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
