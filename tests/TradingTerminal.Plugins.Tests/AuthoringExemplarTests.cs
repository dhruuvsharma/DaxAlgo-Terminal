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

    [Fact]
    public void The_order_flow_exemplar_demonstrates_the_interaction_model()
    {
        // A model imitates the exemplar far more strongly than it reads the reference. Gestures and
        // verbs were added to the SDK, documented in the generated surface and taught in the drawing
        // pack — and demonstrated in NO exemplar, which is the same "built and never reached" shape
        // this area keeps producing, one level further out: reached by the documentation and not by
        // the thing the model actually copies.
        //
        // The order-flow exemplar is the one chosen for a book, footprint or imbalance brief, which is
        // exactly where a pinned price level and a "forget the accumulated flow" button belong.
        var source = AuthoringExemplar.For(AuthoringKind.Visualizer, "an order book depth ladder");

        source.Should().Contain("Cursor", "the exemplar must show how a pinned level is read");
        source.Should().Contain("HasSelection");
        source.Should().Contain("Viewport.Zoom", "and how zoom is applied to the data range");
        source.Should().Contain("UnitAction", "and that a unit may declare a verb");
        source.Should().Contain("OnActionAsync");
    }

    [Theory]
    [MemberData(nameof(EverySample))]
    public void An_exemplar_obeys_the_rules_it_is_teaching(AuthoringKind kind, string? brief)
    {
        // The samples are library code: they carry `using` directives and a namespace, and an authored
        // unit must have neither. Shipped raw they would demonstrate exactly what the rules a few
        // paragraphs above them forbid, and a model resolves that contradiction by guessing.
        var source = AuthoringExemplar.For(kind, brief);

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

    /// <summary>
    /// Every embedded sample, with a brief that selects it.
    ///
    /// <para>Parameterising by KIND was right when a kind had one exemplar. It stopped being right the
    /// moment the brief started choosing between them: the order-flow sample is reachable only through
    /// a brief, so a theory over kinds compiled two of the three and left the newest -- the one with no
    /// track record -- ungated.</para>
    /// </summary>
    public static TheoryData<AuthoringKind, string?> EverySample => new()
    {
        { AuthoringKind.Strategy, null },
        { AuthoringKind.Visualizer, null },
        { AuthoringKind.Visualizer, "an order book depth ladder with footprint imbalance" },
    };

    [Fact]
    public void The_compile_gate_covers_every_exemplar_that_ships()
    {
        // EverySample is hand-written, so it rots the moment a fourth sample is embedded -- and it rots
        // SILENTLY: an uncovered exemplar just never gets compiled, and the theory above stays green
        // while the newest, least-proven sample is the one nobody is checking. This counts the embedded
        // resources instead, so adding one without a row here fails immediately.
        var embedded = typeof(AuthoringExemplar).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith("DaxAlgo.Codegen.Exemplars.", StringComparison.Ordinal))
            .ToArray();

        embedded.Should().NotBeEmpty("the exemplars are embedded resources -- none found means the "
            + "prefix moved and every check in this file is reading nothing");

        // What the theory rows actually select, by identity rather than by assumption: a row whose
        // brief fails to match its intended sample silently falls back to the default, and counting
        // DISTINCT sources is what catches that.
        var covered = EverySample
            .Select(row => AuthoringExemplar.For((AuthoringKind)row[0]!, (string?)row[1]))
            .Distinct(StringComparer.Ordinal)
            .Count();

        covered.Should().Be(
            embedded.Length,
            "every embedded exemplar must be reachable from a row in EverySample -- {0} shipped, {1} "
            + "distinct sources selected", embedded.Length, covered);
    }

    [Theory]
    [MemberData(nameof(EverySample))]
    public void An_exemplar_still_compiles_after_normalisation(AuthoringKind kind, string? brief)
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
        var source = AuthoringExemplar.For(kind, brief);
        source.Should().NotBeNullOrWhiteSpace();

        var script = new StrategyScript(
            $"exemplar.{kind}.{brief?.Length ?? 0}".ToLowerInvariant(),
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

    [Fact]
    public void AnOrderFlowBriefGetsTheOrderFlowExemplar()
    {
        // Skills were matched to the brief and the exemplar was not, so the one combination people
        // actually ask for was mismatched: "show me a footprint chart" loaded the order-flow skill and
        // a spread-band exemplar that never touches depth or the tape. The strongest teaching signal in
        // the pack was the one piece not aimed at the question.
        var block = AuthoringExemplar.Block(
            AuthoringKind.Visualizer, "show me a footprint chart with the order book beside it");

        block.Should().Contain("BookPressureVisualizer");
        block.Should().Contain("TradeClassifier", "an order-flow exemplar has to demonstrate signing");
        block.Should().Contain("Book.Microprice");
    }

    [Fact]
    public void APriceBriefKeepsTheDefaultExemplar()
    {
        // The other half: a brief about a price series must not be handed a book. "Band" and "bollinger"
        // carry none of the order-flow words, so this stays the spread band.
        var block = AuthoringExemplar.Block(
            AuthoringKind.Visualizer, "plot a bollinger band around the close");

        block.Should().Contain("SpreadBandVisualizer");
    }

    [Fact]
    public void NoBriefKeepsTheDefaultExemplar()
    {
        // A resumed session with no user text yet, and every existing caller that passes nothing.
        AuthoringExemplar.Block(AuthoringKind.Visualizer).Should().Contain("SpreadBandVisualizer");
        AuthoringExemplar.Block(AuthoringKind.Visualizer, "   ").Should().Contain("SpreadBandVisualizer");
    }

    [Theory]
    [InlineData("build me an order book graph", true)]
    [InlineData("a DOM ladder for ES", true)]
    [InlineData("volume footprint chart", true)]
    [InlineData("show queue imbalance at the touch", true)]
    [InlineData("a moving average cross", false)]
    [InlineData("rsi divergence on the daily", false)]
    public void TheOrderFlowWordsAreTheOnesThatImplyABookOrATape(string brief, bool expected)
    {
        // "Delta" and "flow" are deliberately absent from the trigger list: a brief that says delta is
        // not necessarily asking for a book, and showing it one would spend the exemplar budget on the
        // wrong example.
        AuthoringExemplar.WantsOrderFlow(brief).Should().Be(expected);
    }

    [Fact]
    public void TheOrderFlowExemplarIsARealCompiledSample()
    {
        // The exemplars' whole claim is that they compile and are covered by tests in this repository.
        // A third one embedded but not built would quietly break that promise.
        typeof(DaxAlgo.Sandbox.Samples.BookPressureVisualizer)
            .Should().Implement<DaxAlgo.Sdk.IVisualizer>();

        var block = AuthoringExemplar.Block(AuthoringKind.Visualizer, "order book");
        block.Should().NotContain("namespace ", "an authored unit declares none");
        block.Should().NotContain("using DaxAlgo", "the ambient usings are not written by the author");
    }
}
