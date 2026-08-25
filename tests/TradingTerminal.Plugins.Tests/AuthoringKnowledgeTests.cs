using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The hand-written half of the codegen prompt, guarded.
///
/// <para>The generated surface cannot drift because nobody writes it. This half is judgement and has to
/// be written, so it needs a different defence: the specific wrong statements that were actually found
/// in it are asserted absent, one test per mistake, named after the mistake.</para>
///
/// <para>These are not hypothetical. Every identifier below was in the shipped pack: it taught
/// <c>IOrderRoutedStrategy</c> with <c>IOrderRouter</c> and direct order placement — which the virtual-book
/// rule forbids outright — instructed the model to write four files including a WPF view, and pointed at
/// a scaffold template that does not exist. It also contradicted itself: the header said a strategy
/// "never touches the broker directly" and the contract section immediately handed over a router.</para>
/// </summary>
public sealed class AuthoringKnowledgeTests
{
    private static string Pack => StrategyContextPack.Load().Conventions;

    private static IEnumerable<(string Id, string Body)> Skills =>
        StrategySkillLibrary.Load().All.Select(skill => (skill.Id, skill.Body));

    /// <param name="deniedByName">
    /// True when the base pack is allowed to name this identifier in order to say it does not exist.
    /// Telling a model "there is no order router" and "a `UserControl` will not be used" is worth the
    /// tokens, because those are precisely the things it would otherwise reach for — the old pack
    /// demanded both. A skill never gets that latitude: skills teach how to do something, so naming a
    /// retired contract there can only mislead.
    /// </param>
    [Theory]
    [InlineData("IOrderRoutedStrategy", "the authoring contract is IStrategyKernel", false)]
    [InlineData("IOrderRouter", "a strategy's only output is its virtual book", true)]
    [InlineData("PlaceOrder", "a strategy never places an order", true)]
    [InlineData("UserControl", "the host owns the window; authors never write WPF", true)]
    [InlineData("OnTickAsync", "the quote callback is OnQuoteAsync", false)]
    [InlineData("OnEndAsync", "the lifecycle ends at OnStopAsync", false)]
    [InlineData("LiveSignalStrategyViewModelBase", "retired with the view-model authoring model", false)]
    [InlineData("daxplugin", "retired on 2026-08-24", false)]
    public void TheKnowledgeDoesNotTeachARetiredContract(string identifier, string why, bool deniedByName)
    {
        foreach (var (id, body) in Skills)
            body.Should().NotContain(identifier, $"skill '{id}' must not teach it — {why}");

        if (!deniedByName)
            Pack.Should().NotContain(identifier, why);
    }

    [Fact]
    public void TheBasePackTeachesTheContractsThatExist()
    {
        Pack.Should().Contain("IStrategyKernel");
        Pack.Should().Contain("IVisualizer");
        Pack.Should().Contain("context.Book");
        Pack.Should().Contain("context.Clock");
    }

    [Fact]
    public void TheBasePackTeachesThatTheVirtualBookIsTheOnlyOutput()
    {
        // The single most important thing in the document, and the thing the old pack got backwards.
        Pack.Should().Contain("SetTargetPosition");
        Pack.Should().Contain("no order router");
    }

    [Fact]
    public void SomethingTeachesDrawing()
    {
        // The gap that made every generated unit invisible: nothing in the pack mentioned the surface,
        // so nothing generated against it could draw.
        var everything = Pack + string.Concat(Skills.Select(skill => skill.Body));

        everything.Should().Contain("IRenderSurface");
        everything.Should().Contain("surface.Panel");
        everything.Should().Contain("RenderThemeColor");
    }

    [Fact]
    public void TheWordsAUserSaysWhenTheyWantAPictureReachTheDrawingSkill()
    {
        // The worst failure mode of a trigger-selected library is right triggers on wrong content: the
        // retired live-window skill fired on "chart", "panel", "plot" and "render" and then taught
        // hand-written WPF. These are the briefs that must now land on drawing.
        var library = StrategySkillLibrary.Load();

        foreach (var brief in new[]
                 {
                     "show me a footprint chart",
                     "a panel with a depth ladder",
                     "plot cumulative delta",
                     "I want to render candles",
                     "a dashboard for order flow",
                 })
        {
            library.SelectFor(brief).Should().Contain(
                skill => skill.Id == "drawing",
                $"'{brief}' asks for a picture");
        }
    }

    [Fact]
    public void EverySkillDeclaresTriggersAndABody()
    {
        // A skill whose front matter fails to parse is dropped silently by the loader, so it would
        // simply never be selected — and nothing else would ever say so.
        var skills = StrategySkillLibrary.Load().All;

        skills.Should().HaveCountGreaterThanOrEqualTo(5);
        skills.Should().OnlyContain(skill => skill.Triggers.Count > 0 && skill.Body.Length > 200);
        skills.Select(skill => skill.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheRetiredLiveWindowSkillIsGone()
    {
        StrategySkillLibrary.Load().All.Should().NotContain(skill => skill.Id == "live-window");
    }
}
