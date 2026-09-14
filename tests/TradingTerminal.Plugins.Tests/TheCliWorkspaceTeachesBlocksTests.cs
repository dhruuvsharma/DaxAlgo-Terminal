using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The agent-CLI workspace Hyperion scaffolds: it teaches the Blocks SDK the in-app builder uses — the
/// conventions and index in the guide, one card per block beside it — and a starter unit and page that
/// pass the Blocks gate. It had gone on teaching the retired order-routed contract long after the pane
/// moved on.
/// </summary>
public sealed class TheCliWorkspaceTeachesBlocksTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "daxalgo-cli-workspace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_workspace_carries_the_conventions_the_index_and_one_card_per_block()
    {
        var catalog = BlockCatalog.Load();
        var root = new CliWorkspaceLauncher().Scaffold("spread watch", "Spread watch", StrategyBuildEffort.Standard, _base);

        var guide = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));
        guide.Should().Contain("IUnit").And.Contain("# Writing a unit").And.Contain("- `market`");
        guide.Should().NotContain("IOrderRoutedStrategy").And.NotContain("IRenderSurface");
        guide.Should().NotContain("Calls on `IOrders`", "cards stay in their own files, read only when needed");
        File.ReadAllText(Path.Combine(root, "AGENTS.md")).Should().Be(guide);
        File.ReadAllText(Path.Combine(root, "system-prompt.md")).Should().Be(catalog.SharedContext);

        Directory.GetFiles(Path.Combine(root, "blocks", "cards"), "*.md").Should().HaveCount(catalog.Ids.Count);
        File.ReadAllText(Path.Combine(root, "blocks", "cards", "orders.md")).Should().Be(catalog.Card("orders"));

        File.ReadAllText(Path.Combine(root, "MyUnit.cs")).Should().Be(BlockStarters.Strategy);
        File.ReadAllText(Path.Combine(root, "ui", "index.html")).Should().Be(BlockStarters.Page);
    }

    [Fact]
    public void A_relaunch_refreshes_the_guide_and_never_touches_the_users_unit_or_page()
    {
        var launcher = new CliWorkspaceLauncher();
        var root = launcher.Scaffold("mine", "Mine", StrategyBuildEffort.Standard, _base);

        File.WriteAllText(Path.Combine(root, "MyUnit.cs"), "// my work");
        File.WriteAllText(Path.Combine(root, "ui", "index.html"), "<p>my page</p>");
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "stale");

        launcher.Scaffold("mine", "Mine", StrategyBuildEffort.Standard, _base);

        File.ReadAllText(Path.Combine(root, "MyUnit.cs")).Should().Be("// my work");
        File.ReadAllText(Path.Combine(root, "ui", "index.html")).Should().Be("<p>my page</p>");
        File.ReadAllText(Path.Combine(root, "CLAUDE.md")).Should().NotBe("stale");
    }

    [Theory]
    [InlineData(AuthoringKind.Strategy)]
    [InlineData(AuthoringKind.Visualizer)]
    public async Task The_starter_unit_and_page_pass_the_Blocks_gate(AuthoringKind kind)
    {
        var gate = new BlocksGate(new BlocksUnitCompiler(), "starter",
            drive: BlocksGate.DefaultDrive with { Steps = 30, SettleTime = TimeSpan.FromMilliseconds(200) });

        var verdict = await gate.RunAsync(
            [new StrategyFile("MyUnit.cs", BlockStarters.For(kind)), new StrategyFile("ui/index.html", BlockStarters.Page)]);

        verdict.Passed.Should().BeTrue(string.Join(" / ", verdict.Report.Findings));
        gate.Latest!.UsesOrders.Should().Be(kind == AuthoringKind.Strategy);
    }
}
