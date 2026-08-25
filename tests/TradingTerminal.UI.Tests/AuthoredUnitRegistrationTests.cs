using DaxAlgo.Sandbox.Samples;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Registration — what turns a verified unit into a deliverable one.
///
/// <para>Everything upstream of this was equally true of a unit nobody could open: it compiled, cleared
/// the policy scan, cleared four verification rungs, and drew a live preview. None of that put a card in
/// the catalog, and a strategy the user cannot open has not been delivered.</para>
/// </summary>
public sealed class AuthoredUnitRegistrationTests
{
    private static (AuthoredUnitSink Sink, IStrategyKernelRegistry Kernels, IVisualizerRegistry Visualizers) Build()
    {
        var kernels = new StrategyKernelRegistry();
        var visualizers = new VisualizerRegistry();
        return (new AuthoredUnitSink(kernels, visualizers), kernels, visualizers);
    }

    [Fact]
    public void AStrategyLandsInTheStrategyRegistryAndIsRunnable()
    {
        var (sink, kernels, _) = Build();

        var message = sink.Register(
            new AuthoredUnit(AuthoringKind.Strategy, typeof(MovingAverageCrossKernel)),
            "test.ma-cross",
            "MA Cross");

        Assert.Contains("Registered strategy", message, StringComparison.Ordinal);
        var registration = kernels.Find("test.ma-cross");
        Assert.NotNull(registration);

        // The factory is the point. A descriptor alone gives a card that opens to nothing.
        Assert.IsType<MovingAverageCrossKernel>(registration!.Create());
    }

    [Fact]
    public void AVisualizerLandsInTheVisualizerRegistry()
    {
        var (sink, kernels, visualizers) = Build();

        sink.Register(
            new AuthoredUnit(AuthoringKind.Visualizer, typeof(SpreadBandVisualizer)),
            "test.spread-band",
            null);

        Assert.NotNull(visualizers.Find("test.spread-band"));
        Assert.Empty(kernels.All); // a visualizer must not reach the registry that hands out books
    }

    [Fact]
    public void EachOpenGetsItsOwnInstance()
    {
        // Sharing one instance across windows would share its state: two charts of the same strategy
        // would fight over one history buffer.
        var (sink, kernels, _) = Build();
        sink.Register(new AuthoredUnit(AuthoringKind.Strategy, typeof(MovingAverageCrossKernel)), "id", null);

        var registration = kernels.Find("id")!;

        Assert.NotSame(registration.Create(), registration.Create());
    }

    [Fact]
    public void RegisteringTheSameIdReplacesRatherThanStacks()
    {
        // Regenerating in Hyperion should update the card, not add a second one that shadows the first
        // depending on lookup order.
        var (sink, kernels, _) = Build();

        sink.Register(new AuthoredUnit(AuthoringKind.Strategy, typeof(MovingAverageCrossKernel)), "same", "First");
        sink.Register(new AuthoredUnit(AuthoringKind.Strategy, typeof(MovingAverageCrossKernel)), "same", "Second");

        Assert.Single(kernels.All);
        Assert.Equal("Second", kernels.Find("same")!.Descriptor.DisplayName);
    }

    [Fact]
    public void TheRetiredContractIsRefusedWithTheReplacementNamed()
    {
        var (sink, kernels, _) = Build();

        var message = sink.Register(
            new AuthoredUnit(AuthoringKind.Strategy, typeof(object), UsesRetiredContract: true),
            "old",
            null);

        Assert.Contains("IStrategyKernel", message, StringComparison.Ordinal);
        Assert.Empty(kernels.All);
    }

    [Fact]
    public void RegistrationNeverThrows()
    {
        // The contract. The code compiled and was verified; a registration fault is worth reporting but
        // must not cost the author their session.
        var (sink, _, _) = Build();

        string act() => sink.Register(new AuthoredUnit(AuthoringKind.Strategy, typeof(NoParameterlessCtor)), "x", null);

        var recorded = act();
        Assert.Contains("registration failed", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyIdIsRefusedRatherThanRegisteredUnderNothing()
    {
        var (sink, kernels, _) = Build();

        Assert.Contains(
            "id",
            sink.Register(new AuthoredUnit(AuthoringKind.Strategy, typeof(MovingAverageCrossKernel)), "  ", null),
            StringComparison.Ordinal);
        Assert.Empty(kernels.All);
    }

    // ── the descriptor the card is built from ───────────────────────────────────────────────────

    [Fact]
    public void TheCardSaysWhatDataTheStrategyNeeds()
    {
        // So a user can tell, before opening it, whether their broker can feed it.
        var registration = StrategyKernelDescriptors.FromType(typeof(MovingAverageCrossKernel));

        Assert.Contains("Bars", registration.Descriptor.DataRequirementTags!);
    }

    [Fact]
    public void TheCardTitleIsReadableRatherThanATypeName()
    {
        Assert.Equal(
            "Moving Average Cross",
            StrategyKernelDescriptors.FromType(typeof(MovingAverageCrossKernel)).Descriptor.DisplayName);
    }

    [Fact]
    public void ATypeTheHostCannotConstructIsNotHostable()
    {
        Assert.False(StrategyKernelDescriptors.CanHost(typeof(NoParameterlessCtor)));
        Assert.False(StrategyKernelDescriptors.CanHost(typeof(SpreadBandVisualizer))); // it is a visualizer
        Assert.True(StrategyKernelDescriptors.CanHost(typeof(MovingAverageCrossKernel)));
    }

    private sealed class NoParameterlessCtor(int _) : IStrategyKernel
    {
        public Core.Strategies.Parameters.StrategyParameterSchema Schema { get; } =
            Core.Strategies.Parameters.StrategyParameterSchema.Empty;

        public Core.Strategies.StrategyDataRequirement DataRequirement =>
            Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct) => Task.CompletedTask;
    }
}
