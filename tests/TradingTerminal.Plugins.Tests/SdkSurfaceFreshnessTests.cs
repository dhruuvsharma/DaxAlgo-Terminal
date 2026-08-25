using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The knowledge Hyperion is given must describe the SDK that actually exists.
///
/// <para>This is the guard the previous pack did not have. Its generator went missing, the markdown
/// was left to be maintained by hand, and it drifted until it taught <c>IBacktestStrategy</c> nine
/// times, <c>IStrategyKernel</c> once, and said nothing at all about <c>IRenderSurface</c> or
/// <c>Draw</c> — so the only contract that puts pixels on screen was invisible to the thing meant to
/// generate against it. A wrong statement in that file reaches a model and lands in somebody's
/// strategy.</para>
///
/// <para>So the surface is reflected from the assembly and this test fails when the committed copy
/// falls behind. It <b>rewrites the file</b> before failing: the fix for a red build here is to
/// review the diff and commit it, which is exactly what should happen when a public contract
/// changes.</para>
/// </summary>
public sealed class SdkSurfaceFreshnessTests
{
    [Fact]
    public void TheCommittedSurfaceMatchesTheSdk()
    {
        var root = RepositoryRoot();

        var rewritten = SdkSurfaceGenerator.WriteTo(root);

        rewritten.Should().BeFalse(
            "the generated SDK surface is stale — it has just been rewritten at "
            + $"{SdkSurfaceGenerator.RelativePath}. Review the diff and commit it.");
    }

    [Fact]
    public void TheSurfaceTeachesTheContractsAnAuthorActuallyImplements()
    {
        // The specific failure this whole mechanism exists to prevent, asserted directly so it cannot
        // regress even if the generator is rewritten.
        var surface = SdkSurfaceGenerator.Generate();

        surface.Should().Contain("IStrategyKernel");
        surface.Should().Contain("IVisualizer");
        surface.Should().Contain("IRenderSurface");

        // "What you implement" is the section a model reads first, so the two live contracts have to
        // be the ones in it — not merely present somewhere further down.
        var implement = Section(surface, "What you implement");
        implement.Should().Contain("IStrategyKernel").And.Contain("IVisualizer");
    }

    [Fact]
    public void TheLegacyContractIsStillReachableFromTheSdkAndThatIsWorthKnowing()
    {
        // A tripwire, not an aspiration. `AuthoredPlugin` discovers `IOrderRoutedStrategy` implementations
        // and `IStrategyEngineFactory.Create` returns one, so the legacy contract is still part of the
        // published surface and a model reading this document will see it. Retiring it from the
        // authoring path is Phase 0 of the Hyperion rework (#44) and belongs with the compiler rework.
        //
        // When that lands, this test fails and should simply be deleted.
        SdkSurfaceGenerator.Generate().Should().Contain("IOrderRoutedStrategy");
    }

    [Fact]
    public void TheSystemPromptCarriesBothHalves()
    {
        // The wiring that makes any of this reach a model. Without the embed, the generator is a file
        // nobody reads.
        var pack = StrategyContextPack.Load();

        pack.SdkSurface.Should().Contain("IRenderSurface");
        pack.Conventions.Should().NotBeEmpty();
        pack.SystemPrompt.Should().Contain(pack.SdkSurface).And.Contain(pack.Conventions);
    }

    [Fact]
    public void TheGeneratedSurfaceLeadsThePrompt()
    {
        // Provider prompt caches key on a stable prefix. The generated surface is the larger and more
        // stable half, so it goes first; putting the hand-edited conventions in front would invalidate
        // the cached prefix every time somebody reworded a sentence.
        var pack = StrategyContextPack.Load();

        pack.SystemPrompt.Should().StartWith(pack.SdkSurface[..200]);
    }

    /// <summary>The body of one `##` section, so a test can assert where something appears rather than
    /// merely that it appears.</summary>
    private static string Section(string surface, string heading)
    {
        var start = surface.IndexOf($"## {heading}", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the surface should have a '{heading}' section");
        var next = surface.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return next < 0 ? surface[start..] : surface[start..next];
    }

    [Fact]
    public void TheDrawingSurfaceIsDescribedWellEnoughToDrawWith()
    {
        // A model cannot produce a picture from a type name. These are the members every visualizer
        // uses, and their absence was the reason nothing generated could draw.
        var surface = SdkSurfaceGenerator.Generate();

        foreach (var member in new[] { "Panel", "Series", "Push", "AxisX", "AxisY", "Text", "Marker", "Theme" })
            surface.Should().Contain(member, $"'{member}' is part of the drawing vocabulary");
    }

    [Fact]
    public void InlineDocumentationIsNotDuplicated()
    {
        // Guards a real bug in the first version: walking DescendantNodes yielded the text inside
        // every <c> element twice, so summaries came out as "the `Draw` Draw method".
        var surface = SdkSurfaceGenerator.Generate();

        surface.Should().NotContain("`Draw` Draw");
        surface.Should().NotContain("`IRenderSurface` IRenderSurface");
    }

    [Fact]
    public void CrossReferencesResolveToMemberNames()
    {
        // Also a real bug caught by reading the output: a cref carries its parameter list, so splitting
        // on dots before stripping it returned the last ARGUMENT TYPE. The surface told authors to
        // "push points inside the scope with `Double)`" — confidently, and wrongly.
        var surface = SdkSurfaceGenerator.Generate();

        surface.Should().NotContain("Double)");
        surface.Should().NotContain("Int32)");
        surface.Should().Contain("with `Push`", "the cref in IRenderSurface.Series should name the method");
    }

    /// <summary>
    /// The repository root, resolved from this file's own compile-time path.
    ///
    /// <para>Walking up from <c>AppContext.BaseDirectory</c> does not work here: the build output is
    /// redirected to <c>C:\DaxAlgoBuild</c>, which is outside the source tree entirely, so the walk
    /// runs to the drive root without ever seeing the solution file. <c>CallerFilePath</c> is baked in
    /// at compile time and points at the source regardless of where the binary lands.</para>
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // <root>/tests/TradingTerminal.Plugins.Tests/SdkSurfaceFreshnessTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        File.Exists(Path.Combine(root, "TradingTerminal.Windows.slnx")).Should().BeTrue(
            $"'{root}' should be the repository root");
        return root;
    }
}
