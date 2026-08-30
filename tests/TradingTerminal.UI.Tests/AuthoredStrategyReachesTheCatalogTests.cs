using System.Linq;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// From a model's reply to a card in the catalog, with nothing stubbed in between.
///
/// <para>Registration was covered; the step AFTER it was not, and that was the broken one. Nothing in
/// the tree read <c>IStrategyKernelRegistry</c> — the sink wrote to it, the plugin binder wrote to it,
/// DI constructed it, and no reader existed — while the user was told "Registered strategy 'X'. Open
/// it from the catalog." The visualizer half shipped and worked; the strategy half stopped at
/// registration, which is the half people ask for.</para>
///
/// <para>So this drives the real chain: source text through the real Roslyn compiler, the discovered
/// unit through the real sink into the real registry, and the registration into the real catalog row
/// the shell binds to.</para>
/// </summary>
public sealed class AuthoredStrategyReachesTheCatalogTests
{
    /// <summary>What a model returns, using the maths library the prompt now teaches.</summary>
    private const string Source = """
        public sealed class MomentumEdgeKernel : IStrategyKernel
        {
            private readonly Ema _fast = new(5);
            private readonly Ema _slow = new(20);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)),
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 200, unit: "bars"));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
                Task.CompletedTask;

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
            {
                _fast.Update(bar.Close);
                _slow.Update(bar.Close);
                if (_slow.IsReady)
                {
                    context.Book.SetTargetPosition(
                        context.Parameters.GetInstrument("instrument"),
                        _fast.Value > _slow.Value ? 1d : 0d);
                }

                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Momentum", RenderPanelKind.Chart);
                Plot.Waiting(surface, "warming up");
            }
        }
        """;

    [Fact]
    public void A_compiled_kernel_becomes_a_catalog_card_the_shell_can_open()
    {
        var compiled = Compile();

        var kernels = new StrategyKernelRegistry();
        var sink = new AuthoredUnitSink(kernels, new VisualizerRegistry());

        var message = sink.Register(compiled, "momentum-edge", "Momentum edge");
        Assert.Contains("Registered strategy", message, StringComparison.Ordinal);

        // The registry entry the shell reads to build its cards.
        var registration = kernels.Find("momentum-edge");
        Assert.NotNull(registration);

        // The card itself. Backed by the kernel — not an ITradingStrategy, which is the retired
        // contract the plugin factory holds, and not a visualizer, which has no book. Folding it into
        // either would route Open to machinery that cannot run it.
        var card = new StrategyCatalogItemViewModel(registration!);

        Assert.Equal(CatalogItemKind.Strategy, card.Kind);
        Assert.Equal("momentum-edge", card.Id);
        Assert.Equal("Momentum edge", card.Name);
        Assert.NotNull(card.Kernel);
        Assert.Equal("Open", card.PrimaryActionLabel);

        // Quick backtest is a plugin-strategy affordance; the engine was archived, so offering it on
        // an authored kernel would be offering nothing.
        Assert.False(card.HasQuickBacktest);
    }

    [Fact]
    public void The_card_carries_the_data_the_kernel_declared()
    {
        var kernels = new StrategyKernelRegistry();
        new AuthoredUnitSink(kernels, new VisualizerRegistry())
            .Register(Compile(), "momentum-edge", "Momentum edge");

        var card = new StrategyCatalogItemViewModel(kernels.Find("momentum-edge")!);

        // Bars, because that is what the kernel asked for — the pills on the card are how a user knows
        // which brokers can feed it before opening anything.
        Assert.Contains(card.DataRequirementTags, tag => tag.Contains("Bars", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registering_again_replaces_the_card_rather_than_stacking_a_second()
    {
        // Regenerating in Hyperion updates the card. Stacking leaves a second entry shadowing the
        // first depending on lookup order, which is the kind of bug a user reads as "my edit did
        // nothing".
        var kernels = new StrategyKernelRegistry();
        var sink = new AuthoredUnitSink(kernels, new VisualizerRegistry());

        sink.Register(Compile(), "momentum-edge", "Momentum edge");
        sink.Register(Compile(), "momentum-edge", "Momentum edge v2");

        Assert.Single(kernels.All.Where(r => r.Id == "momentum-edge"));
        Assert.Equal("Momentum edge v2", kernels.Find("momentum-edge")!.Descriptor.DisplayName);
    }

    [Fact]
    public void Each_open_builds_a_fresh_instance()
    {
        // Sharing one across windows shares its state: two charts of the same strategy would fight
        // over one history buffer.
        var kernels = new StrategyKernelRegistry();
        new AuthoredUnitSink(kernels, new VisualizerRegistry())
            .Register(Compile(), "momentum-edge", "Momentum edge");

        var registration = kernels.Find("momentum-edge")!;

        Assert.NotSame(registration.Create(), registration.Create());
    }

    [Fact]
    public void The_registry_announces_a_new_card_so_it_appears_without_a_restart()
    {
        // The shell subscribes to this to add the card mid-session. Its absence is why a strategy
        // authored in Hyperion used to need a restart it never actually got, because nothing read the
        // registry at start-up either.
        var kernels = new StrategyKernelRegistry();
        var sink = new AuthoredUnitSink(kernels, new VisualizerRegistry());

        var fired = 0;
        kernels.Changed += (_, _) => fired++;

        sink.Register(Compile(), "momentum-edge", "Momentum edge");

        Assert.Equal(1, fired);
    }

    /// <summary>Real source through the real compiler, and the unit it discovered.</summary>
    private static AuthoredUnit Compile()
    {
        var result = new RoslynStrategyCompiler().Compile(
            new StrategyScript("momentum-edge", "Momentum edge",
                [new StrategyFile("MomentumEdgeKernel.cs", Source)]));

        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Id} {d.Message}")));

        Assert.NotNull(result.Unit);
        return result.Unit!;
    }
}
