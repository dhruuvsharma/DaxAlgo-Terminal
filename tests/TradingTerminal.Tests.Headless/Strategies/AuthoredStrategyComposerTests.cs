using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

/// <summary>
/// The default-UI composer contract: an authored strategy that ships a kernel + descriptor +
/// view-model but <b>no view</b> still becomes a catalog card — the host composes its live window from
/// the descriptor's <see cref="ITradingStrategy.DataRequirement"/>. Pinned here end-to-end and headless
/// (the composer itself is faked; the WPF composition is covered by the ComposedStrategyView tests):
/// <list type="number">
/// <item>the compiler reports the trio as composable, and no longer demands a view;</item>
/// <item>the in-session installer registers the card with the composer's view;</item>
/// <item>the SDK's plugin bootstrap — the restart path — registers the same composed factory, so a
/// composed strategy survives a restart identically to a hand-written one;</item>
/// <item>hosts with no composer degrade the way they always did: backtest-only, with a reason.</item>
/// </list>
/// </summary>
public sealed class AuthoredStrategyComposerTests
{
    /// <summary>Kernel + descriptor + live view-model, no view — the shape the AI builder's pack now
    /// tells the model to write. Mirrors the pack's own examples.</summary>
    private const string TrioSource = """
        public sealed class ComposedStratKernel : IBacktestStrategy
        {
            private readonly Contract _contract;
            public ComposedStratKernel(Contract contract) { _contract = contract; }
            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        }

        public sealed class ComposedStratDescriptor : ITradingStrategy
        {
            public string Id => "composedStrat";
            public string DisplayName => "Composed Strat";
            public string Description => "A trio without a view — the host composes the window.";
            public StrategyDataRequirement DataRequirement =>
                StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth;
        }

        public sealed class ComposedStratViewModel : LiveSignalStrategyViewModelBase
        {
            public ComposedStratViewModel(
                LiveStrategyHostServices services,
                INotificationPublisher notifications,
                IClock clock,
                ISignalGeneratorRouterFactory routerFactory,
                ILogger<ComposedStratViewModel> logger)
                : base("composedStrat", "Composed Strat", services, notifications, clock, routerFactory, logger)
            {
            }

            protected override StrategyDataRequirement DataRequirement =>
                StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth;

            protected override IBacktestStrategy BuildStrategy(Contract contract) => new ComposedStratKernel(contract);
        }
        """;

    private static StrategyCompileResult CompileTrio() =>
        new RoslynStrategyCompiler().Compile(new StrategyScript("composedStrat", "Composed Strat", TrioSource));

    /// <summary>The headless DI graph an authored view-model resolves against — same shape the ported
    /// strategy-resolution tests use (real pipeline seams, simulated broker, no WPF).</summary>
    private static IServiceProvider BuildGraph(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder().Build();
        services.AddSingleton(config);
        services.AddLogging();
        services.AddSingleton<InMemoryLogSink>();

        services.AddTradingTerminalInfrastructure();
        services.AddMarketDataPipeline(config);
        services.AddNotifications(config);

        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();
        services.AddSingleton(sp => new LiveStrategyHostServices(
            sp.GetRequiredService<IMarketDataRepository>(),
            sp.GetRequiredService<IMarketDataHub>(),
            sp.GetRequiredService<IMarketDataIngest>(),
            sp.GetRequiredService<IMarketDataStore>(),
            sp.GetRequiredService<IBrokerSelector>(),
            sp.GetRequiredService<InMemoryLogSink>(),
            sp.GetRequiredService<IInstrumentRegistry>()));

        extra?.Invoke(services);

        // Not disposed on purpose: the headless graph's Rx-backed singletons throw on synchronous
        // teardown, which is irrelevant to these checks (same note as the resolution tests).
        return services.BuildServiceProvider();
    }

    /// <summary>Stands in for the WPF composer; records what it was asked to compose.</summary>
    private sealed class FakeComposer : IAuthoredStrategyViewComposer
    {
        public ITradingStrategy? Composed { get; private set; }
        public object ComposeView(ITradingStrategy descriptor)
        {
            Composed = descriptor;
            return new FakeView();
        }
    }

    /// <summary>A "view" the reflection-based DataContext binder can work with, minus WPF.</summary>
    private sealed class FakeView
    {
        public object? DataContext { get; set; }
    }

    private sealed class FakeRegistrar(IServiceCollection services) : IPluginRegistrar
    {
        public IServiceCollection Services { get; } = services;
        public PluginContext Context => new("Composed Strat", string.Empty, SdkInfo.Version);
    }

    // ── 1. the compiler's report ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_trio_without_a_view_is_composable_and_missing_nothing()
    {
        var result = CompileTrio();

        result.Success.Should().BeTrue("compile diagnostics: {0}", string.Join("; ", result.Diagnostics));
        result.Authored.Should().NotBeNull();
        result.Authored!.CanComposeLiveWindow.Should().BeTrue("descriptor + view-model are all a card needs now");
        result.Authored.HasLiveWindow.Should().BeFalse("the author wrote no view");
        result.Authored.MissingForCatalog.Should().BeEmpty("a view is no longer a catalog requirement");
    }

    [Fact]
    public void A_bare_kernel_is_told_it_lacks_descriptor_and_view_model_but_not_a_view()
    {
        const string kernelOnly = """
            public sealed class LoneKernel : IBacktestStrategy
            {
                private readonly Contract _c;
                public LoneKernel(Contract c) { _c = c; }
                public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        var result = new RoslynStrategyCompiler().Compile(new StrategyScript("lone", "Lone", kernelOnly));

        result.Success.Should().BeTrue();
        result.Authored!.CanComposeLiveWindow.Should().BeFalse();
        result.Authored.MissingForCatalog.Should().HaveCount(2)
            .And.NotContain(m => m.Contains("view (", StringComparison.OrdinalIgnoreCase),
                "the host supplies the window; only the descriptor and view-model are the author's job");
    }

    // ── 2. the in-session installer ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_installer_puts_a_viewless_trio_in_the_catalog_through_the_composer()
    {
        var compiled = CompileTrio();
        var provider = BuildGraph();
        var catalog = new StrategyFactory(provider, [], []);
        var composer = new FakeComposer();
        var installer = new AuthoredStrategyInstaller(
            provider, Substitute.For<IBacktestStrategyRegistry>(), catalog, composer: composer);

        var install = installer.Install(
            new StrategyScript("composedStrat", "Composed Strat", TrioSource), compiled);

        install.Registered.Should().BeTrue();
        install.InCatalog.Should().BeTrue("descriptor + view-model + a registered composer make a card");
        install.Message.Should().Contain("host-composed");

        // Opening the card must produce the composer's view with the authored VM bound to it.
        var host = catalog.Create("composedStrat");
        composer.Composed.Should().NotBeNull();
        composer.Composed!.Id.Should().Be("composedStrat");
        composer.Composed.DataRequirement.Should().Be(
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth,
            "the composer decides the window's panels off exactly this value");
        host.View.Should().BeOfType<FakeView>();
        ((FakeView)host.View).DataContext.Should().BeSameAs(host.ViewModel);
        host.ViewModel.Should().BeAssignableTo<LiveSignalStrategyViewModelBase>();
    }

    [Fact]
    public void The_installer_without_a_composer_keeps_the_old_backtest_only_behaviour()
    {
        var compiled = CompileTrio();
        var provider = BuildGraph();
        var installer = new AuthoredStrategyInstaller(
            provider, Substitute.For<IBacktestStrategyRegistry>(), new StrategyFactory(provider, [], []));

        var install = installer.Install(
            new StrategyScript("composedStrat", "Composed Strat", TrioSource), compiled);

        install.Registered.Should().BeTrue();
        install.InCatalog.Should().BeFalse("no composer, no view, no card that throws when clicked");
        install.Message.Should().Contain("no strategy view composer");
    }

    // ── 3. the restart path (SDK plugin bootstrap) ───────────────────────────────────────────────────

    [Fact]
    public void The_plugin_bootstrap_registers_a_composed_view_factory_for_the_next_start()
    {
        var compiled = CompileTrio();
        var composer = new FakeComposer();

        var provider = BuildGraph(services =>
        {
            services.AddSingleton<IAuthoredStrategyViewComposer>(composer);
            AuthoredPluginBootstrap.Register(
                new FakeRegistrar(services), compiled.Authored!.Assembly, "composedStrat", "Composed Strat");
        });

        var registration = provider.GetRequiredService<StrategyFactoryRegistration>();
        registration.StrategyId.Should().Be("composedStrat");

        var view = registration.ViewFactory(provider);
        view.Should().BeOfType<FakeView>("the factory resolves the host's composer at open time");
        composer.Composed!.Id.Should().Be("composedStrat");

        registration.ViewModelFactory(provider).Should().BeAssignableTo<LiveSignalStrategyViewModelBase>();
    }

    [Fact]
    public void The_plugin_bootstrap_in_a_composerless_host_fails_at_open_with_a_reason()
    {
        var compiled = CompileTrio();

        var provider = BuildGraph(services =>
            AuthoredPluginBootstrap.Register(
                new FakeRegistrar(services), compiled.Authored!.Assembly, "composedStrat", "Composed Strat"));

        var registration = provider.GetRequiredService<StrategyFactoryRegistration>();
        var open = () => registration.ViewFactory(provider);

        open.Should().Throw<InvalidOperationException>()
            .WithMessage("*no strategy view composer*", "a headless host never opens windows, but the " +
                "failure must still name the actual problem");
    }
}
