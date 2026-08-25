using DaxAlgo.Sandbox.Samples;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The live preview Hyperion shows instead of code.
///
/// <para>Reading generated C# is a poor way to review a strategy: most people cannot, and the ones who
/// can still cannot tell from the source whether the axes are sensible or whether it drew anything at
/// all. These pin the part that matters most — that the pane <b>always says something</b>. An
/// unexplained empty rectangle is read as a broken application, not as a unit that draws nothing.</para>
/// </summary>
public sealed class AuthoredUnitPreviewTests
{
    private static AuthoredUnit Unit<T>(AuthoringKind kind) => new(kind, typeof(T));

    [Fact]
    public void TheSampleVisualizerProducesAFrame()
    {
        var preview = AuthoredUnitPreview.Create(Unit<SpreadBandVisualizer>(AuthoringKind.Visualizer));

        preview.IsDrawable.Should().BeTrue(preview.Summary);
        preview.Draw.Should().NotBeNull();

        // The frame is real, not merely non-null.
        var surface = new RecordingRenderSurface();
        preview.Draw!(surface);
        surface.IsBlank.Should().BeFalse();
        surface.SeriesNames.Should().NotBeEmpty();
    }

    [Fact]
    public void TheSampleStrategyPreviewsAndReportsWhatItDid()
    {
        // The summary is the difference between "here is a picture" and "here is a picture, and it took
        // three positions to draw it" — which is what tells an author the logic ran at all.
        var preview = AuthoredUnitPreview.Create(Unit<MovingAverageCrossKernel>(AuthoringKind.Strategy));

        preview.IsDrawable.Should().BeTrue(preview.Summary);
        preview.TargetsSubmitted.Should().BeGreaterThan(0, "the preview series crosses");
        preview.Summary.Should().Contain("position");
    }

    [Fact]
    public void ASignalOnlyStrategyIsExplainedRatherThanShownBlank()
    {
        // Drawing nothing is legitimate for a strategy. The pane must say so, because a blank rectangle
        // with no caption is indistinguishable from a broken host.
        var preview = AuthoredUnitPreview.Create(Unit<SilentStrategy>(AuthoringKind.Strategy));

        preview.IsDrawable.Should().BeFalse();
        preview.Draw.Should().BeNull();
        preview.Summary.Should().Contain("draws nothing");
    }

    [Fact]
    public void AVisualizerThatPaintsNothingSaysWhyThatIsWrong()
    {
        // Not legitimate here: a visualizer that draws nothing has no other purpose.
        var preview = AuthoredUnitPreview.Create(Unit<SilentVisualizer>(AuthoringKind.Visualizer));

        preview.IsDrawable.Should().BeFalse();
        preview.Summary.Should().Contain("no other purpose");
    }

    [Fact]
    public void AUnitThatThrowsNamesTheExceptionRatherThanDisappearing()
    {
        var preview = AuthoredUnitPreview.Create(Unit<ThrowsOnStart>(AuthoringKind.Strategy));

        preview.IsDrawable.Should().BeFalse();
        preview.Summary.Should().Contain("InvalidOperationException").And.Contain("no data yet");
    }

    [Fact]
    public void TheRetiredContractIsExplainedRatherThanAttempted()
    {
        var preview = AuthoredUnitPreview.Create(
            new AuthoredUnit(AuthoringKind.Strategy, typeof(object), UsesRetiredContract: true));

        preview.IsDrawable.Should().BeFalse();
        preview.Summary.Should().Contain("IStrategyKernel");
    }

    [Fact]
    public void EveryOutcomeCarriesASummary()
    {
        // The one invariant the pane depends on. There is no path that leaves the caption empty.
        AuthoredUnit[] units =
        [
            Unit<SpreadBandVisualizer>(AuthoringKind.Visualizer),
            Unit<MovingAverageCrossKernel>(AuthoringKind.Strategy),
            Unit<SilentStrategy>(AuthoringKind.Strategy),
            Unit<SilentVisualizer>(AuthoringKind.Visualizer),
            Unit<ThrowsOnStart>(AuthoringKind.Strategy),
            new(AuthoringKind.Strategy, typeof(object), UsesRetiredContract: true),
        ];

        foreach (var unit in units)
            AuthoredUnitPreview.Create(unit).Summary.Should().NotBeNullOrWhiteSpace();
    }

    // ── stand-ins ───────────────────────────────────────────────────────────────────────────────

    private sealed class SilentStrategy : IStrategyKernel
    {
        public Core.Strategies.Parameters.StrategyParameterSchema Schema { get; } =
            Core.Strategies.Parameters.StrategyParameterSchema.Empty;

        public Core.Strategies.StrategyDataRequirement DataRequirement =>
            Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SilentVisualizer : IVisualizer
    {
        public Core.Strategies.Parameters.StrategyParameterSchema Schema { get; } =
            Core.Strategies.Parameters.StrategyParameterSchema.Empty;

        public Core.Strategies.StrategyDataRequirement DataRequirement =>
            Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(IVisualizerContext c, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowsOnStart : IStrategyKernel
    {
        public Core.Strategies.Parameters.StrategyParameterSchema Schema { get; } =
            Core.Strategies.Parameters.StrategyParameterSchema.Empty;

        public Core.Strategies.StrategyDataRequirement DataRequirement =>
            Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct) =>
            throw new InvalidOperationException("there is no data yet");
    }
}
