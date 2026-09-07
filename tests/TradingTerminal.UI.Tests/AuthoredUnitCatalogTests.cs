using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The step between "registered" and "there is a card": the one nothing tested, in either shell, for
/// either kind.
///
/// <para><b>The bug this was written for.</b> Registering a visualizer answered "Registered visualizer
/// 'X'. Open it from the catalog" and produced no card, because both shells hand-rolled a block that
/// read the kernel registry and only the kernel registry. <see cref="IVisualizerRegistry.Changed"/> —
/// whose own summary says it "is what lets a visualizer authored in Hyperion appear in the catalog
/// without a restart" — had no subscriber anywhere in the tree.</para>
///
/// <para>The strategy half of exactly this had already been found and fixed once. It came back on the
/// other half because the wiring lived inside a view-model no test could construct, so there was
/// nowhere for a guard to go. That is why <see cref="AuthoredUnitCatalog"/> is a seam and why these
/// tests exist: the behaviour is now reachable without a composition root.</para>
/// </summary>
public sealed class AuthoredUnitCatalogTests
{
    private static ObservableCollection<StrategyCatalogItemViewModel> Catalog() => [];

    /// <summary>Inline, so an <c>Application</c> another test constructed cannot leave this marshalling
    /// to a dispatcher that has stopped pumping.</summary>
    private static readonly Action<Action> Inline = run => run();

    private static VisualizerRegistration Visualizer(string id, string name = "A visualizer") =>
        new(new VisualizerDescriptor(id, name, "fixture", DataRequirementTags: ["BARS"]), () => new FakeVisualizer());

    private static StrategyKernelRegistration Kernel(string id, string name = "A strategy") =>
        new(new VisualizerDescriptor(id, name, "fixture", DataRequirementTags: ["BARS"]), () => new FakeKernel());

    // ── the reported bug ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_registered_visualizer_gets_a_card()
    {
        var items = Catalog();
        var visualizers = new VisualizerRegistry();

        using var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: Inline);

        visualizers.Register(Visualizer("flow.pressure", "Order flow pressure"));

        var card = Assert.Single(items);
        Assert.Equal("flow.pressure", card.Id);
        Assert.Equal("Order flow pressure", card.Name);
        Assert.Equal(CatalogItemKind.Visualizer, card.Kind);
        Assert.Equal("Add to chart", card.PrimaryActionLabel);
    }

    [Fact]
    public void A_visualizer_registered_before_the_catalog_was_bound_is_still_shown()
    {
        // An installed pack is bound into the registry at start-up, which may be before or after the
        // window exists. Subscribing without seeding leaves it invisible until something else is
        // registered — which for most users is never.
        var items = Catalog();
        var visualizers = new VisualizerRegistry();
        visualizers.Register(Visualizer("installed.book"));

        using var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: Inline);

        Assert.Equal("installed.book", Assert.Single(items).Id);
    }

    // ── the half that already worked, kept covered ──────────────────────────────────────────────

    [Fact]
    public void A_registered_kernel_gets_a_strategy_card()
    {
        var items = Catalog();
        var kernels = new StrategyKernelRegistry();

        using var binding = AuthoredUnitCatalog.Bind(items, kernels, null, dispatch: Inline);

        kernels.Register(Kernel("momentum", "Momentum edge"));

        var card = Assert.Single(items);
        Assert.Equal(CatalogItemKind.Strategy, card.Kind);
        Assert.Equal("Momentum edge", card.Name);
        Assert.NotNull(card.Kernel);
    }

    [Fact]
    public void Both_registries_feed_the_one_catalog()
    {
        var items = Catalog();
        var kernels = new StrategyKernelRegistry();
        var visualizers = new VisualizerRegistry();

        using var binding = AuthoredUnitCatalog.Bind(items, kernels, visualizers, dispatch: Inline);

        kernels.Register(Kernel("k"));
        visualizers.Register(Visualizer("v"));

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == "k" && i.Kind == CatalogItemKind.Strategy);
        Assert.Contains(items, i => i.Id == "v" && i.Kind == CatalogItemKind.Visualizer);
    }

    // ── what happens on the second registration ─────────────────────────────────────────────────

    [Fact]
    public void Regenerating_a_unit_replaces_its_card_rather_than_stacking_a_second()
    {
        // Regenerating in Hyperion updates the card. A second row shadowing the first depending on
        // lookup order is the kind of bug a user reads as "my edit did nothing".
        var items = Catalog();
        var visualizers = new VisualizerRegistry();

        using var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: Inline);

        visualizers.Register(Visualizer("flow", "Flow"));
        visualizers.Register(Visualizer("flow", "Flow v2"));

        Assert.Equal("Flow v2", Assert.Single(items).Name);
    }

    [Fact]
    public void An_unchanged_card_is_left_alone_so_a_selection_survives()
    {
        // Every registration re-reads both registries. Rebuilding each card unconditionally would swap
        // the object the view binds to, and the row the user had selected would clear itself because
        // something unrelated was authored.
        var items = Catalog();
        var kernels = new StrategyKernelRegistry();
        var visualizers = new VisualizerRegistry();

        using var binding = AuthoredUnitCatalog.Bind(items, kernels, visualizers, dispatch: Inline);

        visualizers.Register(Visualizer("v"));
        var first = items.Single(i => i.Id == "v");

        kernels.Register(Kernel("k"));

        Assert.Same(first, items.Single(i => i.Id == "v"));
    }

    [Fact]
    public void Removing_a_unit_takes_its_card_with_it()
    {
        var items = Catalog();
        var visualizers = new VisualizerRegistry();

        using var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: Inline);

        visualizers.Register(Visualizer("v"));
        visualizers.Remove("v");

        Assert.Empty(items);
    }

    [Fact]
    public void A_card_this_binding_did_not_add_is_never_removed()
    {
        // The Testing profile seeds a fixture visualizer that has a descriptor and no registration
        // behind it, and plugin strategies arrive through the factory. A set difference against the
        // registries would delete both the first time anything was authored — a card vanishing because
        // an unrelated unit appeared.
        var items = Catalog();
        items.Add(new StrategyCatalogItemViewModel(DevCatalogSeed.FixtureVisualizer));

        var visualizers = new VisualizerRegistry();
        using var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: Inline);

        visualizers.Register(Visualizer("v"));
        visualizers.Remove("v");

        Assert.Equal(DevCatalogSeed.FixtureVisualizer.Id, Assert.Single(items).Id);
    }

    // ── the badge, and letting go ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_card_it_adds_is_reported_for_the_unsigned_badge()
    {
        // Nobody signed what the user just authored, so it wears the same DEV badge an unsigned
        // plugin's strategy does — and a visualizer is no more signed than a kernel is.
        var items = Catalog();
        var kernels = new StrategyKernelRegistry();
        var visualizers = new VisualizerRegistry();
        var marked = new List<string>();

        using var binding = AuthoredUnitCatalog.Bind(
            items, kernels, visualizers, markUnsigned: marked.Add, dispatch: Inline);

        kernels.Register(Kernel("k"));
        visualizers.Register(Visualizer("v"));

        Assert.Contains("k", marked);
        Assert.Contains("v", marked);
    }

    [Fact]
    public void Disposing_stops_tracking()
    {
        var items = Catalog();
        var visualizers = new VisualizerRegistry();

        var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: Inline);
        binding.Dispose();

        visualizers.Register(Visualizer("v"));

        Assert.Empty(items);
    }

    [Fact]
    public void A_host_that_composes_neither_registry_binds_to_nothing_rather_than_throwing()
    {
        var items = Catalog();

        using var binding = AuthoredUnitCatalog.Bind(items, null, null, dispatch: Inline);

        Assert.Empty(items);
    }

    [Fact]
    public void A_registration_off_the_owning_thread_is_routed_through_the_dispatcher()
    {
        // Where the marshalling matters: a compile finishes on a worker and registers there, and an
        // ObservableCollection may only be touched by the thread that owns it. What is asserted here is
        // that the change goes through the dispatcher rather than straight at the collection — the
        // dispatcher itself is WPF's, and inline here so the test cannot hang on one that has stopped.
        var items = Catalog();
        var visualizers = new VisualizerRegistry();
        var dispatched = 0;

        using var binding = AuthoredUnitCatalog.Bind(
            items, null, visualizers, dispatch: run => { dispatched++; run(); });

        Task.Run(() => visualizers.Register(Visualizer("v"))).GetAwaiter().GetResult();

        Assert.Single(items);
        Assert.Equal(1, dispatched);
    }

    private sealed class FakeVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = new();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeKernel : IStrategyKernel
    {
        public StrategyParameterSchema Schema { get; } = new();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;
    }
}
