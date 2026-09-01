using DaxAlgo.Sandbox.Samples;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The 3D exemplar, driven the whole way a generated unit is driven.
///
/// <para>Brief item 3 said 3D was not expressible at all. The answer is projection rather than a
/// host-side 3D surface: a unit computes world → panel itself with <c>Projection3</c> and draws with
/// the 2D primitives it already has, so nothing new reaches the host and the sandbox argument is
/// untouched.</para>
///
/// <para><b>The capability is only real if the exemplar clears the ladder</b>, because that is the
/// exact path a generated unit takes — and because gestures and verbs both shipped documented,
/// undemonstrated, and did not transfer. A worked example that cannot itself be verified teaches a
/// model to write something that cannot be verified either.</para>
/// </summary>
public sealed class ThreeDimensionalUnitTests
{
    [Fact]
    public void The_3D_exemplar_clears_the_ladder()
    {
        var report = AuthoredUnitVerifier.Verify(
            new AuthoredUnit(AuthoringKind.Visualizer, typeof(DepthLandscapeVisualizer)));

        report.Passed.Should().BeTrue(
            string.Join(" | ", report.Findings.Select(f => f.ToString())));
    }

    [Fact]
    public void It_compiles_through_the_sandbox_path_as_authored_source()
    {
        // The exemplar is embedded as SOURCE and a model copies its shape, so it has to survive the
        // compiler an authored unit actually goes through — policy scan included — not merely the
        // build of this repository.
        var source = AuthoringExemplar.For(AuthoringKind.Visualizer, "an order book as a 3D landscape");
        source.Should().Contain("DepthLandscapeVisualizer", "the 3D brief must select the 3D exemplar");

        var compiled = new RoslynStrategyCompiler().Compile(new StrategyScript(
            "landscape", "Landscape", [new StrategyFile("DepthLandscapeVisualizer.cs", source)]));

        compiled.Success.Should().BeTrue(
            string.Join(" | ", compiled.Errors.Select(d => $"{d.Id} {d.Location} {d.Message}")));
    }

    [Theory]
    [InlineData("an order book as a 3D battlefield with soldiers as live orders")]
    [InlineData("a three-dimensional surface of resting liquidity over time")]
    [InlineData("plot the book in 3d")]
    public void A_brief_that_asks_for_depth_in_space_gets_the_3D_exemplar(string brief)
    {
        // 3D wins over the order-flow exemplar on a brief that is both, because the landscape teaches
        // depth handling as well and 3D is the half a model has no other worked example of.
        AuthoringExemplar.For(AuthoringKind.Visualizer, brief)
            .Should().Contain("DepthLandscapeVisualizer");
    }

    [Theory]
    [InlineData("an order book depth ladder with a liquidity heatmap")]
    [InlineData("a spread band chart")]
    [InlineData("cumulative delta over the session")]
    public void An_ordinary_brief_still_gets_the_ordinary_exemplar(string brief)
    {
        // The expensive direction: exactly one exemplar is ever sent, so a 3D one that captured
        // ordinary briefs would silently take the order-flow example away from every book unit.
        AuthoringExemplar.For(AuthoringKind.Visualizer, brief)
            .Should().NotContain("DepthLandscapeVisualizer");
    }

    [Fact]
    public void A_brief_that_asks_for_a_picture_in_space_gets_the_drawing_pack()
    {
        // MEASURED, NOT ASSUMED, AND IT WAS WRONG. The first live battlefield run selected only the
        // order-flow pack: not one of the drawing pack's thirty-five triggers appears in "the order
        // book as a 3D battlefield … soldiers … the armies move". So the entire 3D teaching — written
        // into that pack the same day — could not reach the model that needed it.
        //
        // The unit came out anyway, because the EXEMPLAR reached it; it even copied a comment verbatim.
        // That is the loop's own lesson twice over: a model imitates the exemplar far more strongly
        // than it reads the reference, and a pack a brief cannot select is a pack nobody wrote.
        var chosen = StrategySkillLibrary.Load()
            .SelectFor(HyperionBenchmark.BattlefieldBrief, 3, AuthoringKind.Visualizer)
            .Select(skill => skill.Id);

        chosen.Should().Contain("drawing");
    }

    [Fact]
    public void The_projection_types_are_rationed_rather_than_charged_to_every_prompt()
    {
        // Placement is the whole cost argument, and it is invisible in the code: a type outside
        // DaxAlgo.Sdk.Quant and DaxAlgo.Sdk.Drawing falls through to "Vocabulary", which is NEVER cut,
        // so it would be charged to every prompt forever. This asserts the section they landed in.
        var surface = SdkSurfaceGenerator.Generate();

        foreach (var type in new[] { "Vec3", "Camera3", "Projection3", "Projected" })
        {
            surface.Should().Contain(
                $"<!-- @type {type} | {SdkSurfaceGenerator.QuantSection} -->",
                $"{type} must sit in a rationed section");
        }
    }
}
