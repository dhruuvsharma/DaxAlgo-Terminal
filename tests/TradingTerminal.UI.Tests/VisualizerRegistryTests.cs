using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The registry and the type-derived descriptor: what turns a compiled visualizer into a catalog card
/// the user can actually open. Before these, "Add to chart" had nothing to open.
/// </summary>
public sealed class VisualizerRegistryTests
{
    [Fact]
    public void ATypeThatDeclaresNothingStillProducesAUsableCard()
    {
        // Hyperion emits these. Requiring an attribute or a static would be one more thing for a model
        // to get wrong, and a card with a blank name is not a card.
        var registration = VisualizerDescriptors.FromType(typeof(OrderBookVisualizer));

        Assert.Equal(typeof(OrderBookVisualizer).FullName, registration.Id);
        Assert.Equal("Order Book", registration.Descriptor.DisplayName);
        Assert.Equal(string.Empty, registration.Descriptor.Description);
    }

    [Fact]
    public void DeclaredMetadataWins()
    {
        var registration = VisualizerDescriptors.FromType(typeof(DescribedVisualizer));

        Assert.Equal("acme.described", registration.Id);
        Assert.Equal("Described", registration.Descriptor.DisplayName);
        Assert.Equal("Says what it is.", registration.Descriptor.Description);
    }

    [Fact]
    public void AnExplicitIdOverridesTheDeclaredOne()
    {
        // The installer assigns ids; a pack that ships two builds of the same type must not collide.
        var registration = VisualizerDescriptors.FromType(typeof(DescribedVisualizer), id: "installed.42");

        Assert.Equal("installed.42", registration.Id);
    }

    [Fact]
    public void TheCardAdvertisesTheStreamsTheVisualizerAsked_For()
    {
        var registration = VisualizerDescriptors.FromType(typeof(OrderBookVisualizer));

        Assert.Equal(["L1", "DEPTH"], registration.Descriptor.DataRequirementTags);
    }

    [Fact]
    public void AVisualizerWhoseConstructorThrowsCostsItsTagsAndNothingElse()
    {
        // Reading DataRequirement means constructing one — it is an instance member. A bad constructor
        // must not take down the list the user is looking at; the failure belongs at open time.
        var registration = VisualizerDescriptors.FromType(typeof(ExplodingVisualizer));

        Assert.Null(registration.Descriptor.DataRequirementTags);
        Assert.Equal("Exploding", registration.Descriptor.DisplayName);
    }

    [Fact]
    public void TypesTheHostCannotConstructAreRefused()
    {
        Assert.False(VisualizerDescriptors.CanHost(typeof(IVisualizer)));
        Assert.False(VisualizerDescriptors.CanHost(typeof(AbstractVisualizer)));
        Assert.False(VisualizerDescriptors.CanHost(typeof(NeedsArgumentsVisualizer)));
        Assert.False(VisualizerDescriptors.CanHost(typeof(VisualizerRegistryTests)));
        Assert.True(VisualizerDescriptors.CanHost(typeof(OrderBookVisualizer)));

        Assert.Throws<ArgumentException>(() => VisualizerDescriptors.FromType(typeof(AbstractVisualizer)));
    }

    [Fact]
    public void DiscoveryFindsEveryHostableTypeInAnAssembly()
    {
        var found = VisualizerDescriptors.DiscoverIn(typeof(VisualizerRegistryTests).Assembly);

        Assert.Contains(found, item => item.Id == typeof(OrderBookVisualizer).FullName);
        Assert.Contains(found, item => item.Id == "acme.described");
        Assert.DoesNotContain(found, item => item.Descriptor.DisplayName == "Needs Arguments");
    }

    [Fact]
    public void EachOpenedWindowGetsItsOwnInstance()
    {
        // Two windows on the same visualizer are two independent runs. Sharing one instance would have
        // them fighting over the same state and drawing each other's picture.
        var registration = VisualizerDescriptors.FromType(typeof(OrderBookVisualizer));

        Assert.NotSame(registration.Create(), registration.Create());
    }

    [Fact]
    public void RegisteringTheSameIdTwiceReplacesRatherThanDuplicates()
    {
        // Re-authoring in Hyperion registers the same id again; the catalog must show one card, updated.
        var registry = new VisualizerRegistry();
        var first = VisualizerDescriptors.FromType(typeof(OrderBookVisualizer), id: "same");
        var second = VisualizerDescriptors.FromType(typeof(DescribedVisualizer), id: "same");

        registry.Register(first);
        registry.Register(second);

        Assert.Single(registry.All);
        Assert.Equal("Described", registry.Find("same")!.Descriptor.DisplayName);
    }

    [Fact]
    public void TheCatalogIsToldWhenTheSetChanges()
    {
        // This is what lets a visualizer authored in Hyperion appear without a restart.
        var registry = new VisualizerRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        registry.Register(VisualizerDescriptors.FromType(typeof(OrderBookVisualizer), id: "a"));
        Assert.True(registry.Remove("a"));
        Assert.False(registry.Remove("a"));

        // Two real changes; the removal that removed nothing is not one.
        Assert.Equal(2, changes);
    }

    [Fact]
    public void LookingUpSomethingThatIsNotThereReturnsNullRatherThanThrowing()
    {
        var registry = new VisualizerRegistry();

        Assert.Null(registry.Find("nope"));
        Assert.Null(registry.Find(""));
        Assert.Null(registry.Find(null!));
    }

    [Theory]
    [InlineData("OrderBookVisualizer", "Order Book")]
    [InlineData("VolumeFootprint", "Volume Footprint")]
    [InlineData("OrderBookL2View", "Order Book L2 View")]
    [InlineData("Heatmap", "Heatmap")]
    [InlineData("Visualizer", "Visualizer")]
    [InlineData("", "")]
    public void TypeNamesBecomeReadableLabels(string typeName, string expected) =>
        Assert.Equal(expected, VisualizerDescriptors.Humanise(typeName));

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private class BaseVisualizer : IVisualizer
    {
        public virtual StrategyParameterSchema Schema => StrategyParameterSchema.Empty;

        public virtual StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OrderBookVisualizer : BaseVisualizer
    {
        public override StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 | StrategyDataRequirement.Depth;
    }

    private sealed class DescribedVisualizer : BaseVisualizer
    {
        public static string Id => "acme.described";

        public static string DisplayName => "Described";

        public static string Description => "Says what it is.";
    }

    private sealed class ExplodingVisualizer : BaseVisualizer
    {
        public ExplodingVisualizer() => throw new InvalidOperationException("bad constructor");
    }

    private abstract class AbstractVisualizer : BaseVisualizer;

    private sealed class NeedsArgumentsVisualizer(int _) : BaseVisualizer;
}
