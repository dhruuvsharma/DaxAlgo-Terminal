using System.Collections.ObjectModel;
using System.Linq;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The visualizer half of <see cref="AuthoredStrategyReachesTheCatalogTests"/>, and the reason it
/// exists: registering a visualizer said "Registered visualizer 'X'. Open it from the catalog" and no
/// card appeared.
///
/// <para>The strategy half had had precisely this bug and had been fixed; its test file then recorded,
/// in its own summary, that "the visualizer half shipped and worked". It had not. Registration worked
/// — which is all anything tested — and the step after it, the one the sentence promises, existed for
/// neither kind until the strategy fix, and for visualizers not even then.</para>
///
/// <para>So this drives the whole chain with nothing stubbed: source text through the real Roslyn
/// compiler, the discovered unit through the real sink into the real registry, and the registry into
/// the real catalog collection a shell binds its list to.</para>
/// </summary>
public sealed class AuthoredVisualizerReachesTheCatalogTests
{
    /// <summary>
    /// What a model returns for "show me buying pressure".
    ///
    /// <para>Named <c>PressureKernel</c> on purpose: the humanised type name is "Pressure Kernel", and
    /// the author called it "Order flow pressure". The two have to be told apart below.</para>
    /// </summary>
    private const string Source = """
        public sealed class PressureKernel : IVisualizer
        {
            private readonly System.Collections.Generic.List<double> _closes = new(64);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 200, unit: "bars"));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) =>
                Task.CompletedTask;

            public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
            {
                _closes.Add(bar.Close);
                if (_closes.Count > 240) _closes.RemoveAt(0);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Pressure", RenderPanelKind.Chart);
                if (_closes.Count == 0) { Plot.Waiting(surface); return; }

                Series.Draw(surface, "Close", _closes);
            }
        }
        """;

    [Fact]
    public void A_compiled_visualizer_becomes_a_catalog_card()
    {
        // The whole reported bug, end to end. Everything up to the last two lines passed before the
        // fix; the catalog stayed empty.
        var visualizers = new VisualizerRegistry();
        var items = new ObservableCollection<StrategyCatalogItemViewModel>();
        using var binding = AuthoredUnitCatalog.Bind(items, null, visualizers, dispatch: run => run());

        var message = new AuthoredUnitSink(new StrategyKernelRegistry(), visualizers)
            .Register(Compile(), "flow-pressure", "Order flow pressure");

        Assert.Contains("Registered visualizer", message, StringComparison.Ordinal);
        Assert.NotNull(visualizers.Find("flow-pressure"));

        var card = Assert.Single(items);
        Assert.Equal("flow-pressure", card.Id);
        Assert.Equal(CatalogItemKind.Visualizer, card.Kind);
        Assert.Equal("Add to chart", card.PrimaryActionLabel);
    }

    [Fact]
    public void The_card_is_titled_what_the_author_called_it()
    {
        // The sink took a display name, used it in the message, and did not pass it on — so the
        // sentence said "Registered visualizer 'Order flow pressure'" and the card underneath read
        // "Pressure Kernel". PluginUnitBinder's own documentation records this exact mistake from the
        // strategy side, where a unit called "Shelf momentum" appeared in the catalog as "Restart".
        var visualizers = new VisualizerRegistry();

        new AuthoredUnitSink(new StrategyKernelRegistry(), visualizers)
            .Register(Compile(), "flow-pressure", "Order flow pressure");

        Assert.Equal("Order flow pressure", visualizers.Find("flow-pressure")!.Descriptor.DisplayName);
    }

    [Fact]
    public void The_card_carries_the_data_the_visualizer_declared()
    {
        var visualizers = new VisualizerRegistry();
        new AuthoredUnitSink(new StrategyKernelRegistry(), visualizers)
            .Register(Compile(), "flow-pressure", "Order flow pressure");

        var card = new StrategyCatalogItemViewModel(visualizers.Find("flow-pressure")!.Descriptor);

        // Bars, because that is what it asked for — the pills are how a user knows which brokers can
        // feed it before opening anything.
        Assert.Contains(card.DataRequirementTags, tag => tag.Contains("Bars", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Each_open_builds_a_fresh_instance()
    {
        // Sharing one across windows shares its state: two charts of the same visualizer would fight
        // over one history buffer.
        var visualizers = new VisualizerRegistry();
        new AuthoredUnitSink(new StrategyKernelRegistry(), visualizers)
            .Register(Compile(), "flow-pressure", "Order flow pressure");

        var registration = visualizers.Find("flow-pressure")!;

        Assert.NotSame(registration.Create(), registration.Create());
    }

    /// <summary>Real source through the real compiler, and the unit it discovered.</summary>
    private static AuthoredUnit Compile()
    {
        var result = new RoslynStrategyCompiler().Compile(
            new StrategyScript("flow-pressure", "Order flow pressure",
                [new StrategyFile("PressureKernel.cs", Source)]));

        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Id} {d.Message}")));

        Assert.NotNull(result.Unit);
        return result.Unit!;
    }
}
