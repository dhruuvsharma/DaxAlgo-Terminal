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
/// That the dial reaches the pane, and that the fallback is SAID.
///
/// <para>The catalogue's own tests prove <c>ResearchEffort</c> returns the right answer. That proves
/// nothing about whether the pane asks it, which is the half this area keeps getting wrong: nine
/// defects in this codebase have been "a component that works and nothing calls it". So these drive
/// the view-model the window binds to, and assert what a user would see.</para>
///
/// <para><b>What these do NOT prove, stated because it was measured rather than assumed.</b> A pane
/// built without a builder has no provider to select, so every lookup here goes in with an empty
/// provider id and comes back Default — which means these would still pass if the per-model rule were
/// deleted. It was checked: with <c>Measured()</c> disabled, all seven stayed green and exactly one
/// test in <c>CodegenModeTests</c> went red. So the model-level rule is proven there, and what is
/// proven HERE is the plumbing: that the dial calls the catalogue at all, that its answer reaches
/// <c>SelectedEffort</c>, that the notice surfaces and clears, and that the pipeline follows.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class ResearchModeReachesThePaneTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-mode-" + Guid.NewGuid().ToString("N"));

    public ResearchModeReachesThePaneTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    private static StrategyAuthoringViewModel Pane() => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance);

    [Fact]
    public void The_pane_opens_on_Standard_and_offers_exactly_two_positions()
    {
        var pane = Pane();

        Assert.Equal(CodegenMode.Standard, pane.Mode);
        Assert.Equal([CodegenMode.Standard, CodegenMode.Research], pane.Modes);
    }

    [Fact]
    public void Standard_sends_nothing_and_shows_no_notice()
    {
        var pane = Pane();

        pane.Mode = CodegenMode.Standard;

        Assert.Equal(CodegenEffort.Default, pane.SelectedEffort);
        Assert.Equal(string.Empty, pane.ResearchNotice);
    }

    [Fact]
    public void Research_on_a_model_with_no_usable_setting_falls_back_and_says_so()
    {
        // The behaviour asked for: fall back to the default, tell the user, and carry on. Driven
        // through the pane rather than the catalogue, because a notice nothing displays is not a
        // notice.
        var pane = Pane();
        pane.SelectedModel = "z-ai/glm-5.3-free";

        pane.Mode = CodegenMode.Research;

        Assert.Equal(CodegenEffort.Default, pane.SelectedEffort);
        Assert.Contains("not available", pane.ResearchNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("glm-5.3", pane.ResearchNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_notice_clears_when_the_model_can_do_research()
    {
        // The other direction, and the one that rots: a notice left standing after the reason for it
        // has gone makes the dial lie in the opposite direction.
        var pane = Pane();
        pane.SelectedModel = "z-ai/glm-5.3-free";
        pane.Mode = CodegenMode.Research;
        Assert.NotEqual(string.Empty, pane.ResearchNotice);

        pane.Mode = CodegenMode.Standard;

        Assert.Equal(string.Empty, pane.ResearchNotice);
    }

    [Fact]
    public void Switching_the_model_re_resolves_what_Research_would_send()
    {
        // Research is a fact about the MODEL. Picking it and then changing model must move the answer,
        // or the pill says Research while the request carries the setting from the model before it.
        var pane = Pane();
        pane.Mode = CodegenMode.Research;

        pane.SelectedModel = "z-ai/glm-5.3-free";

        Assert.Equal(CodegenEffort.Default, pane.SelectedEffort);
        Assert.NotEqual(string.Empty, pane.ResearchNotice);
    }

    [Fact]
    public void Research_still_buys_the_agents_when_the_thinking_falls_back()
    {
        // What the notice promises. The user asked for the expensive path; only the reasoning
        // parameter is unavailable.
        var pane = Pane();
        pane.SelectedModel = "z-ai/glm-5.3-free";
        pane.Mode = CodegenMode.Research;

        var profile = StrategyBuildProfile.For(pane.Mode, pane.SelectedEffort);

        Assert.True(profile.UseAgents);
        Assert.True(profile.SelfReview);
        Assert.Equal(CodegenEffort.Default, profile.Reasoning);
    }

    [Fact]
    public void The_pipeline_effort_follows_the_dial()
    {
        var pane = Pane();

        pane.Mode = CodegenMode.Standard;
        Assert.Equal(StrategyBuildEffort.Standard, pane.BuildEffort);

        pane.Mode = CodegenMode.Research;
        Assert.Equal(StrategyBuildEffort.Max, pane.BuildEffort);
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
