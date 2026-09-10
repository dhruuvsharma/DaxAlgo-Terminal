using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The code the builder opens on has to build.
///
/// <para><b>It did not, and had not for some time.</b> The starter targeted
/// <c>IOrderRoutedStrategy</c> — the retired contract, which lives in
/// <c>TradingTerminal.Core.Strategies.Legacy</c> and is not among the global usings the authoring
/// compiler injects. It did not resolve. So every new session opened on code that could not compile,
/// anyone hand-writing a strategy from it failed on their first Compile, and every AI build carried it
/// into the compile set and failed there too.</para>
///
/// <para>Found from a real session: four generated files, all fine, and exactly two errors — both
/// CS0246 inside the scaffold, on a type name the model never wrote. Nothing in the suite compiled the
/// template, so nothing noticed.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class TheStarterTemplateCompilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-template-" + Guid.NewGuid().ToString("N"));

    public TheStarterTemplateCompilesTests() => AuthoringSessionStore.Directory = _dir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];
        public event EventHandler? Changed;
        public StrategyCatalogEntry? Find(string id) => null;
        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);
        public bool Remove(string id) => false;
    }

    private static StrategyAuthoringViewModel Pane() => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance);

    private static GateResult Compile(StrategyAuthoringViewModel pane)
    {
        var files = pane.Files.Select(f => new StrategyFile(f.Name, f.Content)).ToArray();
        return new UnitGate(new RoslynStrategyCompiler(), "starter", "Starter").Run(files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_starter_the_pane_opens_on_compiles(bool visualizer)
    {
        var pane = Pane();
        pane.IsAuthoringVisualizer = visualizer;

        var result = Compile(pane);

        Assert.True(
            result.Compile?.Success == true,
            "the starter must build: " + string.Join("; ", result.Report.Findings.Select(f => f.ToString())));
    }

    [Theory]
    [InlineData(false, AuthoringKind.Strategy)]
    [InlineData(true, AuthoringKind.Visualizer)]
    public void The_starter_resolves_to_the_kind_that_was_asked_for(bool visualizer, AuthoringKind expected)
    {
        // A visualizer brief opening on a strategy scaffold gives the compiler two hostable classes the
        // moment the model writes its own, which is the error the real session actually hit.
        var pane = Pane();
        pane.IsAuthoringVisualizer = visualizer;

        Assert.Equal(expected, Compile(pane).Unit?.Kind);
    }

    [Fact]
    public void The_starter_clears_every_rung_it_is_meant_to()
    {
        // Compiling is not enough for a visualizer: it owes a picture, and one that paints nothing is
        // refused. A starter that cannot pass its own ladder is a starter that teaches the wrong shape.
        var pane = Pane();
        pane.IsAuthoringVisualizer = true;

        var result = Compile(pane);

        Assert.True(result.Passed, string.Join("; ", result.Report.Findings.Select(f => f.ToString())));
    }

    [Fact]
    public void An_untouched_starter_is_not_mistaken_for_the_users_work()
    {
        // It is a placeholder the pane put there, not something anybody wrote. Shipping it to the swarm
        // made it an existing file that no task owned — so no builder could change it and nothing could
        // remove it, and the gate compiled it alongside the real unit for ever.
        var pane = Pane();

        Assert.True(pane.FilesAreUntouchedTemplate);

        pane.Files[0].Content += "\n// mine now";

        Assert.False(pane.FilesAreUntouchedTemplate, "an edited file is the user's work and must be sent");
    }
}
