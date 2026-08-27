using System.IO;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Provider setup — the window opened from the composer's provider footer.
///
/// <para>The behaviour worth pinning is the rebuild. A keyed client captures its key when it is
/// constructed, so a pane that edited rows in place would report a provider as ready the moment a key was
/// typed and as broken the moment the window was reopened. Every test here goes through the same client
/// list the composer reads.</para>
/// </summary>
public sealed class AiProviderSettingsTests : IDisposable
{
    /// <summary>
    /// Redirects the user config file into a temporary directory for the duration of the fixture.
    ///
    /// <para>Not hygiene — a bug that was caught by looking. Two of these tests save provider settings,
    /// and against the real path they rewrote the config of the developer running the suite.</para>
    /// </summary>
    private readonly string _configDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-provider-settings-" + Guid.NewGuid().ToString("N"));

    public AiProviderSettingsTests() =>
        AiCodegenUserFile.Path = Path.Combine(_configDir, "ai-codegen.json");

    public void Dispose()
    {
        AiCodegenUserFile.Path = AiCodegenUserFile.DefaultPath;
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The un-redirected path is a real path.
    ///
    /// <para><b>The bug this exists for shipped, and this suite is the reason it did.</b> `Path` was an
    /// auto-property initialised from `DefaultPath` but declared above it. Static initialisers run in
    /// textual order, so it captured null, and the application threw at startup on
    /// <c>AddJsonFile(null)</c> before a window ever appeared. Every test here assigns `Path` in the
    /// constructor above, so not one of them ever read the default — a suite can cover every writer in a
    /// class and still not notice that its most basic value is null.</para>
    ///
    /// <para>So this test clears the redirect and reads what the host would read.</para>
    /// </summary>
    [Fact]
    public void The_default_path_is_what_the_host_would_load()
    {
        AiCodegenUserFile.Path = null!;

        Assert.False(string.IsNullOrWhiteSpace(AiCodegenUserFile.Path));
        Assert.Equal(AiCodegenUserFile.DefaultPath, AiCodegenUserFile.Path);
        Assert.True(Path.IsPathRooted(AiCodegenUserFile.Path));
        Assert.EndsWith("ai-codegen.json", AiCodegenUserFile.Path, StringComparison.Ordinal);
    }

    private static AiCodegenOptions Options() => new()
    {
        Providers =
        {
            ["openai"] = new AiCodegenProvider
            {
                BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini",
            },
        },
    };

    private static AiProviderSettingsViewModel Pane(
        FakeKeyStore? keys = null, AiCodegenOptions? options = null)
    {
        options ??= Options();
        return new AiProviderSettingsViewModel(
            new FakeBuilder(options, keys), Microsoft.Extensions.Options.Options.Create(options), keys);
    }

    [Fact]
    public void EveryKnownProviderIsListedIncludingTheOnesThatAreNotSetUp()
    {
        // The unconfigured ones are the reason the pane exists. Filtering to what already works would
        // leave a user with no providers looking at an empty window.
        var pane = Pane();

        Assert.Single(pane.Providers, p => p.ProviderId == "openai");
        Assert.False(pane.Providers.Single(p => p.ProviderId == "openai").IsAvailable);
    }

    [Fact]
    public void SavingAKeyMakesTheProviderUsable()
    {
        var keys = new FakeKeyStore();
        var pane = Pane(keys);
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.KeyEntry = "sk-test-key";
        pane.SaveKeyCommand.Execute(row);

        Assert.True(keys.HasKey("openai"));
        Assert.True(
            pane.Providers.Single(p => p.ProviderId == "openai").IsAvailable,
            "the pane must re-read the clients, because a client captures its key at construction");
    }

    [Fact]
    public void ThePastedKeyDoesNotSurviveTheSave()
    {
        // The row lives as long as the window. A secret sitting in it afterwards is a secret in a heap
        // dump, in a crash report, and on screen for anyone who reselects the row.
        var pane = Pane(new FakeKeyStore());
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.KeyEntry = "sk-test-key";
        pane.SaveKeyCommand.Execute(row);

        Assert.Empty(row.KeyEntry);
    }

    [Fact]
    public void AKeyIsNeverWrittenToTheProviderConfig()
    {
        // The config file is plain JSON in the user's profile. Keys belong in the DPAPI store and
        // nowhere else, so saving provider settings must not carry one across.
        var options = Options();
        var pane = Pane(new FakeKeyStore(), options);
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.KeyEntry = "sk-secret";
        pane.SaveProviderCommand.Execute(row);

        Assert.DoesNotContain(
            "sk-secret", System.Text.Json.JsonSerializer.Serialize(options.Providers["openai"]));
    }

    [Fact]
    public void ForgettingAKeyTakesTheProviderBackOut()
    {
        var keys = new FakeKeyStore();
        var pane = Pane(keys);
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.KeyEntry = "sk-test-key";
        pane.SaveKeyCommand.Execute(row);
        pane.ClearKeyCommand.Execute(pane.Providers.Single(p => p.ProviderId == "openai"));

        Assert.False(keys.HasKey("openai"));
        Assert.False(pane.Providers.Single(p => p.ProviderId == "openai").IsAvailable);
    }

    [Fact]
    public void SavingWithNoKeyStoreSaysSoRatherThanFailingSilently()
    {
        // A valid state: an edition with no key store can still run an installed agent CLI. What it must
        // not do is take a pasted key and appear to have saved it.
        var pane = Pane(keys: null);
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.KeyEntry = "sk-test-key";
        pane.SaveKeyCommand.Execute(row);

        Assert.False(pane.CanStoreKeys);
        Assert.Contains("no key store", pane.Status);
    }

    [Fact]
    public void ChangingTheModelReachesTheFactoryWithoutARestart()
    {
        // The factory reads this options object on every build. Persisting only to the user file would
        // leave the control looking like it does nothing until the app is restarted.
        var options = Options();
        var pane = Pane(new FakeKeyStore(), options);
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.Model = "gpt-5-codex";
        pane.SaveProviderCommand.Execute(row);

        Assert.Equal("gpt-5-codex", options.Providers["openai"].Model);
    }

    [Fact]
    public void AProviderThatIsNotSetUpCannotBecomeTheDefault()
    {
        // Otherwise the builder opens on a provider that cannot answer, and the first thing the user
        // sees is a failure they did not cause.
        var options = Options();
        var pane = Pane(new FakeKeyStore(), options);

        pane.MakeDefaultCommand.Execute(pane.Providers.Single(p => p.ProviderId == "openai"));

        Assert.Empty(options.DefaultProvider);
        Assert.Contains("not set up", pane.Status);
    }

    [Fact]
    public void ACliRowAsksForNoKey()
    {
        // The vendor's tool owns the login. A key field here would be a box that does nothing, which is
        // worse than no box at all.
        var pane = Pane(new FakeKeyStore());
        var cli = pane.Providers.FirstOrDefault(p => p.IsCli);

        Assert.NotNull(cli); // the fake builder offers one
        Assert.False(cli!.IsKeyed);
        Assert.DoesNotContain("key", cli.StatusText);
    }

    [Fact]
    public void TheChangedEventFiresSoTheComposerCanRefresh()
    {
        // The composer's picker is rebuilt on this. Without it a user adds a key, closes the window and
        // still sees "not set up" — indistinguishable from the save having failed.
        var pane = Pane(new FakeKeyStore());
        var fired = 0;
        pane.Changed += (_, _) => fired++;

        var row = pane.Providers.Single(p => p.ProviderId == "openai");
        row.KeyEntry = "sk-test-key";
        pane.SaveKeyCommand.Execute(row);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void SavingWritesToTheUserConfigFile()
    {
        // Also the guard on the redirect above: if Path were ignored this would assert against a file in
        // the developer's own profile, which is the failure it exists to prevent.
        var pane = Pane(new FakeKeyStore());
        var row = pane.Providers.Single(p => p.ProviderId == "openai");

        row.Model = "gpt-5-codex";
        pane.SaveProviderCommand.Execute(row);

        Assert.True(File.Exists(AiCodegenUserFile.Path));
        Assert.Contains("gpt-5-codex", File.ReadAllText(AiCodegenUserFile.Path));
        Assert.StartsWith(_configDir, AiCodegenUserFile.Path);
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds its clients on every read, exactly as the real factory-backed builder does.</summary>
    private sealed class FakeBuilder(AiCodegenOptions options, IAiKeyStore? keys) : IAiStrategyBuilder
    {
        public IReadOnlyList<IStrategyCodegenClient> Providers =>
        [
            .. options.Providers.Keys.Select(id =>
                (IStrategyCodegenClient)new FakeClient(id, keys?.HasKey(id) ?? false)),
            new FakeClient(AgentCliAdapter.All[0].ProviderId, available: false),
        ];

        public IStrategyCodegenClient? DefaultProvider => Providers.FirstOrDefault(p => p.IsAvailable);

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
            Providers.FirstOrDefault(p => p.ProviderId == providerId);

        public IReadOnlyList<string> ModelsFor(string providerId) => [];

        public IReadOnlyList<AiModelChoice> AllModels() => [];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient provider, string strategyId, string displayName,
            IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null,
            AuthoringKind kind = AuthoringKind.Strategy) => throw new NotSupportedException();

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeClient(string id, bool available) : IStrategyCodegenClient
    {
        public string ProviderId => id;
        public string DisplayName => id;
        public bool IsAvailable => available;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeKeyStore : IAiKeyStore
    {
        private readonly HashSet<string> _keys = [];

        public IReadOnlyCollection<string> ConfiguredProviders => _keys;

        public bool HasKey(string providerId) => _keys.Contains(providerId);

        public void Set(string providerId, string apiKey) => _keys.Add(providerId);

        public void Remove(string providerId) => _keys.Remove(providerId);
    }
}
