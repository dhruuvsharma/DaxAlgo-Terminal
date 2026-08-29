using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The exemplars Hyperion is shown — a complete unit of each kind (#44 phase 3).
///
/// <para><b>What makes them "verified".</b> Not that someone read them: they are the source of
/// <c>samples/DaxAlgo.Sandbox.Samples</c>, a real project CI compiles and tests. This file adds the
/// step that matters for their use as a prompt — the exemplar is normalised into authored-unit shape
/// first, and <b>the normalised form is put through the same Roslyn compiler the model's own answer
/// goes through</b>. An exemplar that would not survive that is teaching the model to write something
/// the pipeline rejects.</para>
/// </summary>
public sealed class AuthoringExemplarTests
{
    [Theory]
    [InlineData(AuthoringKind.Strategy)]
    [InlineData(AuthoringKind.Visualizer)]
    public void An_exemplar_exists_for_each_kind(AuthoringKind kind)
    {
        AuthoringExemplar.For(kind).Should().NotBeNullOrWhiteSpace(
            "a missing embedded resource degrades silently — the prompt simply loses its example");
    }

    [Theory]
    [InlineData(AuthoringKind.Strategy)]
    [InlineData(AuthoringKind.Visualizer)]
    public void An_exemplar_obeys_the_rules_it_is_teaching(AuthoringKind kind)
    {
        // The samples are library code: they carry `using` directives and a namespace, and an authored
        // unit must have neither. Shipped raw they would demonstrate exactly what the rules a few
        // paragraphs above them forbid, and a model resolves that contradiction by guessing.
        var source = AuthoringExemplar.For(kind);

        source.Should().NotContain("namespace ");
        source.Should().NotStartWith("using ");
        source.Split('\n').Should().NotContain(line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AuthoringKind.Strategy, "IStrategyKernel")]
    [InlineData(AuthoringKind.Visualizer, "IVisualizer")]
    public void An_exemplar_shows_the_contract_for_its_own_kind(AuthoringKind kind, string contract)
    {
        AuthoringExemplar.For(kind).Should().Contain(contract);
    }

    [Fact]
    public void The_strategy_exemplar_is_not_the_visualizer_one()
    {
        // Kind selection is one ternary. Getting it backwards would show a strategy brief a unit with
        // no book, and nothing else in the pipeline would notice.
        AuthoringExemplar.For(AuthoringKind.Strategy)
            .Should().NotBe(AuthoringExemplar.For(AuthoringKind.Visualizer));
    }

    [Fact]
    public void The_block_is_empty_rather_than_a_heading_with_nothing_under_it()
    {
        // Guards the degradation path: if the resource ever goes missing, the prompt should read as it
        // did before exemplars existed, not as a promise of an example that is not there.
        AuthoringExemplar.Block((AuthoringKind)999).Should().NotContain("### A complete unit");
    }

    [Theory]
    [InlineData(AuthoringKind.Strategy, "MovingAverageCrossKernel")]
    [InlineData(AuthoringKind.Visualizer, "SpreadBandVisualizer")]
    public void The_exemplar_actually_reaches_the_prompt(AuthoringKind kind, string expected)
    {
        // The wiring, not the unit. This file originally tested AuthoringExemplar.For() and nothing
        // else, so when the line that appends it to the kind brief silently failed to apply, every
        // test here still passed and the model was never shown an example at all. An exemplar that
        // exists and is never sent is the same defect as one that does not exist — and harder to
        // notice, because the code and its tests look complete.
        var composed = AuthoringKindBrief.Compose("SHARED PACK", kind);

        composed.Should().Contain(expected);
        composed.Should().Contain("### A complete unit of this kind");
        composed.IndexOf(expected, StringComparison.Ordinal)
            .Should().BeGreaterThan(
                composed.IndexOf("SHARED PACK", StringComparison.Ordinal),
                "it must sit after the cached prefix, or every session pays a cache miss");
    }

    // ── the check that earns the word "verified" ────────────────────────────────────────────────

    [Theory]
    [InlineData(AuthoringKind.Strategy)]
    [InlineData(AuthoringKind.Visualizer)]
    public void An_exemplar_still_compiles_after_normalisation(AuthoringKind kind)
    {
        // The whole point. The samples compile as a library — CI proves that — but what the model is
        // shown is the NORMALISED form, stripped of its usings and namespace. If that transformation
        // breaks the source, the exemplar teaches the model to write something this very compiler
        // rejects, and the first anyone would know is a build loop that will not converge.
        //
        // Compiled with the same RoslynStrategyCompiler the model's own answer goes through, so this
        // is the real gate rather than a lookalike. No usings are supplied: the compiler injects its
        // own GlobalUsings tree, and adding them here is a duplicate-directive error — which is itself
        // the check that the exemplar relies on exactly the ambient set the model is promised.
        var source = AuthoringExemplar.For(kind);
        var script = new StrategyScript(
            $"exemplar.{kind}".ToLowerInvariant(),
            $"{kind} exemplar",
            [new StrategyFile("Unit.cs", source)]);

        var compiled = new RoslynStrategyCompiler().Compile(script);

        compiled.Success.Should().BeTrue(
            "the exemplar must survive the same compiler the model's answer does — errors: "
            + string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
    }

    // ── normalisation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_block_scoped_namespace_is_removed_and_the_body_de_indented()
    {
        // The samples are file-scoped today. This covers the other form, because "it happens to be
        // file-scoped right now" is not a property anyone will preserve deliberately.
        var normalised = AuthoringExemplar.Normalise(
            """
            using System;

            namespace Some.Where
            {
                public sealed class Unit
                {
                    public int Value => 1;
                }
            }
            """);

        normalised.Should().StartWith("public sealed class Unit");
        normalised.Should().NotContain("namespace");
        normalised.Should().Contain("    public int Value => 1;");
    }

    [Fact]
    public void Normalising_nothing_yields_nothing()
    {
        AuthoringExemplar.Normalise(string.Empty).Should().BeEmpty();
        AuthoringExemplar.Normalise("   \n  ").Should().BeEmpty();
    }
}
