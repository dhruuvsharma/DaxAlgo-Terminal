using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Cutting the generated SDK surface to the brief.
///
/// <para>The surface is the bulk of the system prompt and is reflected from the SDK, so it grows every
/// time the SDK does — and the measured wall is real: 67 KB reached first byte at 278 s on NIM, 83 KB
/// returned a 504 at 302 s. A brief about an order book does not need the whole SDK reflected at it.</para>
///
/// <para>Two directions are pinned here, and both matter. That a real brief is genuinely CUT, or the
/// mechanism is decoration. And that the types the brief named SURVIVE the cut, or it is worse than
/// decoration.</para>
///
/// <para>The last test is the one this area keeps failing: a filter that works perfectly and is never
/// reached by the application.</para>
/// </summary>
public sealed class SdkSurfaceSelectionTests
{
    private const string BookBrief =
        "an order book depth ladder with a liquidity heatmap and cumulative delta";

    /// <summary>The surface as generated, markers and all — what the selector parses. The pack
    /// exposes the marker-free form separately, so every caller that composes its own prompt is safe
    /// without having to remember to strip them.</summary>
    private static string Surface => StrategyContextPack.Load().SdkSurfaceSource;

    [Fact]
    public void The_surface_splits_into_the_types_it_documents()
    {
        var types = SdkSurfaceSelector.TypesIn(Surface);

        types.Should().HaveCountGreaterThan(50, "the surface documents the whole authoring API");
        types.Should().Contain(t => t.Name == "Ladder" && t.Section == SdkSurfaceGenerator.DrawingSection);
        types.Should().Contain(t => t.Name == "Vwap" && t.Section == SdkSurfaceGenerator.QuantSection);
        types.Should().Contain(t => t.Name == "IRenderSurface");

        // Derived, never hand-written: OrderFlowImbalance must be findable by the words a brief uses.
        types.Single(t => t.Name == "OrderFlowImbalance").Terms
            .Should().Contain("order").And.Contain("flow").And.Contain("imbalance");
    }

    [Fact]
    public void A_real_brief_is_genuinely_cut()
    {
        var cut = SdkSurfaceSelector.For(Surface, BookBrief);

        cut.Length.Should().BeLessThan(
            Surface.Length,
            "a mechanism that does not shrink a real brief is decoration");

        // Not a token saving either. The libraries are ~48,000 characters and the budget is 24,000, so
        // a brief that names a handful of them should give back a fifth of the whole document at least.
        (Surface.Length - cut.Length).Should().BeGreaterThan(
            Surface.Length / 5, "the cut has to be worth the mechanism");
    }

    [Fact]
    public void What_the_brief_asked_for_survives_the_cut()
    {
        var detailed = SdkSurfaceSelector.Detailed(Surface, BookBrief);

        // Every one of these is named, or all but named, by the brief above.
        detailed.Should().Contain("Ladder");
        detailed.Should().Contain("DepthCurve");
        detailed.Should().Contain("Heatmap");
        detailed.Should().Contain("OrderFlowImbalance");
    }

    [Fact]
    public void The_contracts_are_never_cut()
    {
        // What you implement, what you draw onto, and the vocabulary they are written in. A unit not
        // shown its own interface cannot be written at all; a unit not shown KalmanHedgeRatio writes a
        // slightly worse strategy.
        var cut = SdkSurfaceSelector.For(Surface, "a two-line moving average cross");

        foreach (var required in new[]
                 {
                     "IStrategyKernel", "IVisualizer", "IRenderSurface", "IParameters",
                     "IVirtualBook", "IMarketDataView", "UnitLayout", "RenderCursor", "RenderViewport",
                 })
        {
            cut.Should().Contain($"### `{required}`", $"'{required}' is a contract, not a library entry");
        }
    }

    [Fact]
    public void Nothing_ever_becomes_invisible()
    {
        // The difference between this budget and the skill budget, and the whole reason the failure
        // mode is acceptable. A skipped skill pack is gone; a rationed type keeps its name and its
        // first sentence, so the model knows it exists. A model that cannot see Ladder does not use a
        // worse ladder — it writes one.
        var cut = SdkSurfaceSelector.For(Surface, "a two-line moving average cross");
        var everything = SdkSurfaceSelector.TypesIn(Surface);

        foreach (var type in everything)
            cut.Should().Contain($"`{type.Name}`", $"'{type.Name}' must still be nameable");
    }

    [Fact]
    public void An_empty_brief_is_not_guessed_at()
    {
        // There is nothing to be relevant to. Cutting on a guess would be worse than spending the
        // tokens, so every type keeps its full block.
        var whole = SdkSurfaceSelector.For(Surface, null);

        foreach (var type in SdkSurfaceSelector.TypesIn(Surface).Where(t => !t.IsCompact))
            whole.Should().Contain($"### `{type.Name}`", $"'{type.Name}' must be written out in full");

        SdkSurfaceSelector.For(Surface, "   ").Should().Be(whole);
    }

    [Fact]
    public void The_boundary_markers_never_reach_a_model()
    {
        // They exist for the parser. The first version carried the search terms in them too — 26 KB of
        // them — which is a quarter of the document, embedded in the assembly and sent verbatim on any
        // path that does not cut. A mechanism built to shrink the prompt had grown it.
        foreach (var brief in new[] { BookBrief, null, "an EMA cross" })
        {
            SdkSurfaceSelector.For(Surface, brief)
                .Should().NotContain(SdkSurfaceGenerator.MarkerPrefix);
        }
    }

    [Fact]
    public void The_same_brief_always_produces_the_same_prompt()
    {
        // The surface is the cached prefix of every request in a session. A selection that reordered
        // between runs would throw the provider's prompt cache away on each one.
        SdkSurfaceSelector.For(Surface, BookBrief)
            .Should().Be(SdkSurfaceSelector.For(Surface, BookBrief));
    }

    [Fact]
    public void Spare_budget_is_spent_rather_than_wasted()
    {
        // Given room for everything, everything is written out — so on a small SDK this class costs
        // nothing, and it starts mattering exactly when the library grows.
        var everything = SdkSurfaceSelector.For(Surface, BookBrief, maxCharacters: 10_000_000);

        everything.Length.Should().BeGreaterThan(SdkSurfaceSelector.For(Surface, BookBrief).Length);
        SdkSurfaceSelector.Detailed(Surface, BookBrief, maxCharacters: 10_000_000)
            .Should().Contain("KalmanHedgeRatio", "there was room for it");
    }

    [Fact]
    public void A_brief_that_names_nothing_still_gets_a_full_library_worth()
    {
        // Zero relevance is not zero budget: the ordering falls back to smallest-first, so a vague
        // brief is given as much of the library as fits rather than none of it.
        var detailed = SdkSurfaceSelector.Detailed(Surface, "make me something");

        detailed.Should().NotBeEmpty("an unhelpful brief must not cost the model the whole library");
    }

    // ── the part that keeps being missed ────────────────────────────────────────────────────────

    [Fact]
    public void The_application_actually_cuts_its_prompt()
    {
        // Three defects in this area have been of exactly one shape: built, unit-tested, never reached.
        // So this resolves the real container the shell builds, opens a real session the way the
        // builder does, and asserts on the bytes it would send.
        var services = new ServiceCollection();
        services.AddSingleton<IStrategyCompiler>(new RoslynStrategyCompiler());
        services.AddLogging();
        services.AddStrategyCodegen(new ConfigurationBuilder().Build());

        var provider = services.BuildServiceProvider();
        var builder = provider.GetRequiredService<IAiStrategyBuilder>();

        var session = builder.StartSession(
            new Silent(), "surface.cut", "Surface cut",
            profile: StrategyBuildProfile.For(StrategyBuildEffort.Deep));

        var composed = session.PrepareFor(BookBrief);

        session.SurfaceCharactersSaved.Should().BeGreaterThan(
            0, "the pack never reached the session, so nothing was cut");
        composed.Should().NotContain(
            Surface, "the composed prompt still carries the whole uncut surface");

        // And it is the RIGHT cut: what the brief named is still there in full.
        composed.Should().Contain("### `Ladder`").And.Contain("### `Heatmap`");
        composed.Should().Contain("### `IRenderSurface`", "a contract is never cut");
    }

    [Fact]
    public void A_caller_that_supplies_its_own_context_keeps_it()
    {
        // Cutting an injected pack when the caller handed over different text would silently
        // substitute one document for another — worse than not cutting.
        var session = new StrategyCodegenOrchestrator(
                new RoslynStrategyCompiler(), logger: null, skills: null,
                pack: StrategyContextPack.Load())
            .CreateSession(new Silent(), "MY OWN CONTEXT", "own", "Own", maxFixAttempts: 0);

        session.PrepareFor(BookBrief).Should().Contain("MY OWN CONTEXT");
        session.SurfaceCharactersSaved.Should().Be(0);
    }

    private sealed class Silent : IStrategyCodegenClient
    {
        public string ProviderId => "silent";
        public string DisplayName => "Silent";
        public bool IsAvailable => false;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
