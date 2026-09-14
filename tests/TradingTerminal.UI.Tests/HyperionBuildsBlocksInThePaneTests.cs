using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Blocks.WebHost;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The authoring pane with the Blocks SDK composed: Hyperion builds a unit and its page from cards, the
/// pane compiles and registers it with the Blocks compiler, and the card reaches the catalog — while a
/// session written against the widget SDK stays on it.
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class HyperionBuildsBlocksInThePaneTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-blocks-pane-" + Guid.NewGuid().ToString("N"));

    public HyperionBuildsBlocksInThePaneTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_pane_opens_on_a_Blocks_starter_and_registers_it_into_the_catalog_as_a_strategy()
    {
        var registry = new BlocksUnitRegistry();
        var items = new ObservableCollection<StrategyCatalogItemViewModel>();
        using var source = new BlocksCatalogSource(registry);
        using var binding = AuthoredUnitCatalog.Bind(items, null, null, dispatch: run => run(), hosted: source);

        var pane = Pane(new ScriptedBuilder(), registry);

        Assert.True(pane.BuildsBlocks);
        Assert.True(BlocksAuthoring.IsStarter(Assert.Single(pane.Files).Content));

        pane.CompileCommand.Execute(null);
        Assert.True(pane.ReviewOpen, pane.Status);
        Assert.Contains("strategy", pane.ReviewSummary, StringComparison.Ordinal);

        pane.ConfirmRegisterCommand.Execute(null);

        var registration = Assert.Single(registry.All);
        Assert.True(registration.IsStrategy, "the starter uses the orders block");
        Assert.True(pane.IsRegistered);

        var card = Assert.Single(items);
        Assert.Equal(registration.Id, card.HostedUnit?.Id);
        Assert.Equal(CatalogItemKind.Strategy, card.Kind);
        Assert.False(card.HasQuickBacktest);
    }

    [Fact]
    public void The_visualizer_starter_registers_as_a_visualizer()
    {
        var registry = new BlocksUnitRegistry();
        var pane = Pane(new ScriptedBuilder(), registry);
        pane.AuthoringKind = AuthoringKind.Visualizer;

        pane.CompileCommand.Execute(null);
        pane.ConfirmRegisterCommand.Execute(null);

        Assert.False(Assert.Single(registry.All).IsStrategy);
    }

    [Fact]
    public void A_session_written_against_the_widget_SDK_stays_on_it()
    {
        var pane = Pane(new ScriptedBuilder(), new BlocksUnitRegistry());
        pane.Files[0].Content = "public sealed class Old : IStrategyKernel { }";

        Assert.False(pane.BuildsBlocks);
    }

    [Fact]
    public async Task A_Hyperion_turn_builds_the_unit_and_its_page_from_cards_and_registers_nothing()
    {
        var builder = new ScriptedBuilder();
        var registry = new BlocksUnitRegistry();
        var pane = Pane(builder, registry);
        pane.AuthoringKind = AuthoringKind.Visualizer;

        pane.Composer = "show the spread between two instruments, big";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.Contains(pane.Files, f => f.Name == "ui/index.html");
        Assert.Contains(pane.Files, f => f.Name == "SpreadWatch.cs");
        Assert.DoesNotContain(pane.Files, f => BlocksAuthoring.IsStarter(f.Content));

        var calls = builder.Client.Calls;
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.StartsWith("# Writing a unit", c.System, StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.System.Contains("IRenderSurface", StringComparison.Ordinal));

        var unitBuild = Assert.Single(calls, c => c.Role.Contains("YOUR FILE: SpreadWatch.cs", StringComparison.Ordinal));
        Assert.Contains("## market", unitBuild.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("## orders", unitBuild.Message, StringComparison.Ordinal);

        Assert.Empty(registry.All);
        Assert.False(pane.IsRegistered);
    }

    private static StrategyAuthoringViewModel Pane(IAiStrategyBuilder builder, IBlocksUnitRegistry registry)
    {
        var pane = new StrategyAuthoringViewModel(
            new RoslynStrategyCompiler(),
            new NullRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            builder,
            blocks: new BlocksAuthoring(new BlocksUnitCompiler(), registry));

        pane.StrategyId = "spread-watch";
        pane.DisplayName = "Spread watch";
        return pane;
    }

    private const string Plan = """
        ```json
        {
          "contract": {
            "typeName": "SpreadWatch",
            "topics": [{ "name": "spread", "direction": "to-page", "payload": "{ spread: number }" }]
          },
          "milestones": [{ "id": "m1", "title": "Build", "tasks": [
            { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "SpreadWatch.cs",
              "blocks": ["settings", "market"], "intent": "send the spread between two legs", "dependsOn": [] },
            { "id": "t2", "title": "The page", "kind": "Ui", "ownedFile": "ui/index.html",
              "blocks": ["ui"], "intent": "show the spread large", "dependsOn": [] }
          ]}],
          "rubric": ["the spread is legible"],
          "openQuestions": []
        }
        ```
        """;

    private const string Unit = """
        ```csharp
        // file: SpreadWatch.cs
        public sealed class SpreadWatch : IUnit
        {
            public UnitInfo Info { get; } = new("Spread watch", "Spread between two legs.",
            [
                StrategyParameter.Instrument("legA", "Leg A", new InstrumentId(1)),
                StrategyParameter.Instrument("legB", "Leg B", new InstrumentId(2)),
            ]);

            private double _a, _b;

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                context.Market.OnQuote(context.Settings.Instrument("legA"), q => { _a = q.Mid; Publish(context); });
                context.Market.OnQuote(context.Settings.Instrument("legB"), q => { _b = q.Mid; Publish(context); });
                context.Ui.OnOpened(() => Publish(context));
                return Task.CompletedTask;
            }

            private void Publish(IUnitContext context) => context.Ui.Send("spread", new { spread = _a - _b });
        }
        ```
        """;

    private const string Page = """
        ```html
        <!-- file: ui/index.html -->
        <body style="background:#111;color:#eee"><div id="v">waiting</div>
        <script>dax.on('spread', s => v.textContent = s.spread.toFixed(2)); dax.ready();</script></body>
        ```
        """;

    private const string NothingWrong = """
        ```json
        { "verdict": "fine", "findings": [] }
        ```
        """;

    /// <summary>A real session around a provider that answers by role.</summary>
    private sealed class ScriptedBuilder : IAiStrategyBuilder
    {
        public RoleClient Client { get; } = new();

        public IReadOnlyList<IStrategyCodegenClient> Providers => [Client];

        public IStrategyCodegenClient? DefaultProvider => Client;

        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) => Client;

        public IReadOnlyList<string> ModelsFor(string providerId) => [];

        public IReadOnlyList<AiModelChoice> AllModels() => [new AiModelChoice("scripted", "Scripted", "scripted-model")];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient provider, string strategyId, string displayName,
            IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null, AuthoringKind kind = AuthoringKind.Strategy) =>
            new StrategyCodegenOrchestrator(new RoslynStrategyCompiler(), logger: null, skills: StrategySkillLibrary.Load())
                .CreateSession(provider, StrategyContextPack.Load().SystemPrompt, strategyId, displayName,
                    maxFixAttempts: 0, history, priorUsage, profile, kind);

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RoleClient : IStrategyCodegenClient
    {
        private readonly Lock _gate = new();

        public List<(string System, string Role, string Message)> Calls { get; } = [];

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            lock (_gate) Calls.Add((request.SystemContext, role, request.Messages[^1].Content));

            var text =
                role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? Plan
                : role.Contains("critic", StringComparison.OrdinalIgnoreCase) ? NothingWrong
                : role.Contains("ui/index.html", StringComparison.Ordinal) || role.Contains("the unit's page", StringComparison.Ordinal) ? Page
                : Unit;

            var files = CodegenCodeExtractor.ExtractFiles(text);
            return Task.FromResult(files.Count > 0 ? StrategyCodegenResponse.Ok(files, text) : StrategyCodegenResponse.Reply(text));
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
