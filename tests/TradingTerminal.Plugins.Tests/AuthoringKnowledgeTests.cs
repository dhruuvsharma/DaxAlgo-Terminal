using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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

    // ── the character budget ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeHeaviestPacksFitTogether()
    {
        // Skipping is per-skill and all-or-nothing, which makes a ceiling that NEARLY fits worse than one
        // that comfortably does. At the old 12,000 a brief for an order-flow picture loaded the order-flow
        // pack, found too little room for the drawing catalogue, and dropped it in silence — a brief that
        // said "plot" got no drawing guidance whatsoever.
        var heaviest = StrategySkillLibrary.Load().All
            .OrderByDescending(skill => skill.Body.Length)
            .Take(StrategySkillLibrary.MaxSkillsPerSession)
            .Sum(skill => skill.Body.Length);

        heaviest.Should().BeLessThanOrEqualTo(
            StrategySkillLibrary.MaxCharacters,
            "a session must be able to load its full skill allowance, not silently lose the last one");
    }

    [Fact]
    public void TheCeilingStillBinds()
    {
        // The other half of the same decision. A budget nothing can exceed is not a budget, and the split
        // exists precisely so a brief mentioning everything cannot rebuild the monolith.
        StrategySkillLibrary.Load().All.Sum(skill => skill.Body.Length)
            .Should().BeGreaterThan(StrategySkillLibrary.MaxCharacters);
    }

    [Fact]
    public void AnOrderFlowPictureGetsBothPacks()
    {
        // The brief the budget change was made for, stated as the requirement rather than the arithmetic.
        var chosen = StrategySkillLibrary.Load()
            .SelectFor("show me a footprint chart with cumulative delta underneath")
            .Select(skill => skill.Id)
            .ToArray();

        chosen.Should().Contain("drawing");
        chosen.Should().Contain("order-flow");
    }

    [Fact]
    public void EveryWidgetTheLibraryShipsIsNamedInTheDrawingSkill()
    {
        // A widget the catalogue does not mention is a widget the model writes from scratch — which is
        // the whole cost the library was built to remove. Reflected, so adding a widget and forgetting
        // the skill fails here rather than showing up as a needlessly long generated Draw.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "drawing").Body;

        var widgets = typeof(DaxAlgo.Sdk.Drawing.Plot).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "DaxAlgo.Sdk.Drawing" && t.IsAbstract && t.IsSealed)
            .Where(t => t.GetMethods().Any(m => m.IsStatic && m.IsPublic && m.Name == "Draw"))
            .Select(t => t.Name)
            .Where(name => !body.Contains(name, StringComparison.Ordinal))
            .ToArray();

        widgets.Should().BeEmpty("these widgets are not in the drawing skill, so a model cannot find them");
    }

    [Fact]
    public void EveryEstimatorTheLibraryShipsIsNamedInTheKnowledge()
    {
        // The mirror of the widget guard above, and its absence is why the order-flow skill went on
        // teaching a hand-rolled quote rule, a hand-summed CVD and a hand-computed queue imbalance
        // after `TradeClassifier`, `OrderFlowImbalance` and `Book` had shipped. The maths skill was
        // rewritten and that one was not, because nothing checked.
        //
        // Reflected rather than listed, so adding an estimator and forgetting the knowledge fails here
        // rather than showing up as forty lines of arithmetic in somebody's generated strategy.
        var knowledge = string.Join(
            Environment.NewLine,
            StrategySkillLibrary.Load().All.Select(skill => skill.Body)
                .Append(StrategyContextPack.Load().Conventions));

        var estimators = typeof(DaxAlgo.Sdk.Quant.Num).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "DaxAlgo.Sdk.Quant")
            // Interfaces and enums are vocabulary the surface already carries; what has to be
            // discoverable is the thing a model would otherwise write by hand.
            .Where(t => !t.IsInterface && !t.IsEnum)
            .Select(t => t.Name)
            .Where(name => !knowledge.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        estimators.Should().BeEmpty(
            "these estimators are in no skill, so a model will re-derive them — badly, and at the "
            + "user's expense");
    }

    [Fact]
    public void TheOrderFlowSkillPointsAtTheOrderFlowEstimators()
    {
        // Named directly as well as by the reflected sweep above: order flow is what people actually
        // ask Hyperion for, and these four are the ones it cannot get right by hand. `Classify`
        // matters most — signing a trade wrongly corrupts every statistic built on top of it.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "order-flow").Body;

        foreach (var name in new[] { "TradeClassifier", "OrderFlowImbalance", "Vpin", "Book.Microprice" })
            body.Should().Contain(name, $"an order-flow brief needs {name}");
    }

    [Fact]
    public void TheOrderFlowSkillNamesTheCallbackArgumentsThatExist()
    {
        // It described the L1 event as `Tick`, which is the RETIRED broker-facing record. It still
        // exists, so a model following the skill wrote a reference that compiled and bound nothing —
        // the worst shape of wrong. The callback takes a `Quote`.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "order-flow").Body;

        body.Should().Contain("OnQuoteAsync").And.Contain("Quote");
        body.Should().NotContain("`Tick` (L1)", "the L1 callback receives a Quote, not the legacy Tick");
    }

    [Fact]
    public void TheDrawingSkillTeachesThatASplitReturnsTheStripFirst()
    {
        // The most expensive sentence in the pack. `SplitRight` returns (strip, remainder) — the strip
        // FIRST — and the skill's worked example named them the other way round:
        //
        //     var (chart, side) = body.SplitRight(140d);
        //
        // which puts the chart in 140 pixels and the volume profile across everything else. It
        // compiles, it draws, and every test passes.
        //
        // Both exemplars inherited exactly that inversion from here, and I made it a third time in the
        // harness I wrote to look at the rendered window. Three call sites from one wrong example is
        // the clearest evidence available that the knowledge is load-bearing.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "drawing").Body;

        body.Should().Contain("(strip, remainder)", "the rule has to be stated, not implied");

        // And demonstrated correctly: the name bound first must be the strip.
        body.Should().Contain("var (book, chart)   = body.SplitRight(140d);");
        body.Should().NotContain("var (chart, side)", "that names the strip 'chart' and inverts the picture");
    }

    [Fact]
    public void TheDrawingSkillDoesNotClaimSomeWidgetsCannotBePlaced()
    {
        // It said "Any widget can be given a rectangle", which was aspirational rather than true:
        // `Ladder`, `Candles` and `Footprint` read the viewport directly and ignored the area they were
        // never given. The library has caught up, so the claim is now correct — and
        // `EveryWidgetCanBePlaced` in the drawing suite is what keeps it that way.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "drawing").Body;

        body.Should().Contain("Every widget takes an `area`");
    }

    [Fact]
    public void TheLayoutSkillTeachesTheSpellingThatCompiles()
    {
        // Its every example read `Layout.Columns(...)` inside a class whose property is called
        // `Layout`, so the identifier bound to the PROPERTY and nothing compiled — and separately, the
        // result was a `SplitNode` being returned from a `UnitLayout` property with no conversion
        // between them. Two errors in one line, in every example in the file.
        //
        // Fixed in the library rather than documented around: `UnitLayout` mirrors the three verbs and
        // converts implicitly, so the natural spelling is now the working one.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "layout").Body;

        body.Should().Contain("UnitLayout.Rows(").And.Contain("UnitLayout.Panel(");
        body.Should().NotContain("=> Layout.", "the property shadows the class inside the unit");
    }

    [Fact]
    public void TheRiskSkillDeclaresTheSchemaTheWayTheContractRequires()
    {
        // It showed `public static StrategyParameterSchema Schema`. `IStrategyKernel.Schema` is an
        // INSTANCE member, so a static one does not implement the interface and the class does not
        // compile — while the base pack, three exemplars and the contract itself all say instance.
        var body = StrategySkillLibrary.Load().All.Single(skill => skill.Id == "risk-and-exits").Body;

        body.Should().NotContain("public static StrategyParameterSchema Schema");
        body.Should().Contain("public StrategyParameterSchema Schema");
    }

    [Fact]
    public void NoSkillTeachesTheRetiredConstructionModel()
    {
        // `instruments-and-data` opened by saying a Contract "is handed to the kernel's constructor and
        // to BuildStrategy". Both belong to the retired IOrderRoutedStrategy path — `BuildStrategy`
        // exists only on `LiveSignalStrategyViewModelBase` and returns that contract — and a class
        // written from it has no parameterless constructor, so the host cannot host it at all.
        //
        // Same drift class as the retired `Tick` in the order-flow skill: a name that still exists, so
        // following the guidance compiles into something that binds nothing.
        var knowledge = string.Join(
            Environment.NewLine,
            StrategySkillLibrary.Load().All.Select(skill => skill.Body));

        knowledge.Should().NotContain("BuildStrategy");
        knowledge.Should().NotContain("kernel's constructor");
    }

    [Fact]
    public void NoSkillReadsTheClockOffAValueThatIsNotInScope()
    {
        // `clock.UtcNow` is left over from the retired contract, where an IClock was a callback
        // parameter. A kernel reaches it through its context, and the base pack says so.
        var knowledge = string.Join(
            Environment.NewLine,
            StrategySkillLibrary.Load().All.Select(skill => skill.Body));

        knowledge.Should().NotContain("`clock.UtcNow`");
        knowledge.Should().NotContain(" clock.UtcNow");
    }

    // ── the promise the compiler has to keep ────────────────────────────────────────────────────

    [Fact]
    public void TheAmbientNamespacesThePackPromisesAreTheOnesTheCompilerInjects()
    {
        // The pack tells the model to write no usings because a fixed set is ambient. That is a PROMISE
        // about another file, and RoslynStrategyCompiler's own remarks record it being broken: the two
        // lists disagreed until 2026-08-28, so a unit that obeyed the instruction failed its first
        // compile on a namespace the pack had said was already there.
        //
        // Nothing pinned them afterwards. The exemplar compile gate catches the reverse direction --
        // a namespace the compiler DROPS that a sample needs -- and a bogus entry in the injected list
        // fails every compile outright. Neither notices the case that actually shipped: the pack
        // naming one the compiler never injects. No sample has to use it for the promise to be false.
        var promised = AmbientNamespacesInThePack();
        var injected = AmbientNamespacesInTheCompiler();

        promised.Should().NotBeEmpty("the bullet is parsed out of the pack -- an empty set means the "
            + "wording moved and this guard is no longer reading anything");

        promised.Should().BeEquivalentTo(
            injected,
            "every namespace the pack calls ambient must be one RoslynStrategyCompiler actually "
            + "injects, and every namespace it injects should be one the model is told it has");
    }

    /// <summary>The namespaces the base pack promises are ambient, read out of its own bullet.</summary>
    private static string[] AmbientNamespacesInThePack()
    {
        const string marker = "These are ambient:";
        var start = Pack.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return [];

        // The bullet wraps over several lines and ends where the next one begins.
        var bullet = new StringBuilder();
        foreach (var line in Pack[(start + marker.Length)..].Split('\n'))
        {
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal)) break;
            bullet.Append(line).Append(' ');
        }

        return [.. Regex.Matches(bullet.ToString(), "`([^`]+)`").Select(match => match.Groups[1].Value)];
    }

    /// <summary>The namespaces the compiler injects, read off the constant it injects them from.</summary>
    private static string[] AmbientNamespacesInTheCompiler()
    {
        // Reflected rather than duplicated. A copy of the list in this file would be a third place to
        // forget, which is the failure being guarded against.
        var field = typeof(RoslynStrategyCompiler).GetField(
            "KernelUsings", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull("the guard reads the compiler's own list -- if the constant was "
            + "renamed this test is silently checking nothing");

        var source = (string)field!.GetRawConstantValue()!;

        return
        [
            .. source
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("global using ", StringComparison.Ordinal))
                .Select(line => line["global using ".Length..].TrimEnd(';')),
        ];
    }
}
