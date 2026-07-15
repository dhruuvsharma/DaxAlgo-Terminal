using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Charts;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.OrderBook;
using TradingTerminal.StrategyComposer;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Strategies;
using TradingTerminal.VolumeFootprint;
using Xunit;

namespace TradingTerminal.Tests.Controls;

/// <summary>
/// The composed default strategy window: <see cref="ComposedStrategyView"/> must turn a descriptor's
/// <see cref="StrategyDataRequirement"/> into exactly the right embedded panels — chart for Bars, book
/// ladder for Depth, footprint for TradeTape, a quote card for L1-only — every panel wearing its
/// Embedded preset with the ML forecaster <b>never constructed</b>, and the strategy's instrument
/// pushed into every panel once setup completes. Runs on <see cref="WpfTestApp"/>'s thread.
/// </summary>
public sealed class ComposedStrategyViewTests
{
    private static readonly Contract TestContract = new("BTCUSDT", "CRYPTO", "BINANCE", "USDT", "BINANCE");

    private sealed class Descriptor(StrategyDataRequirement requirement) : ITradingStrategy
    {
        public string Id => "composedTest";
        public string DisplayName => "Composed Test";
        public string Description => "Composition-mapping fixture.";
        public StrategyDataRequirement DataRequirement { get; } = requirement;
    }

    /// <summary>Panel view-models resolve their pipeline seams from here; with no instrument pinned
    /// (embed options) nothing subscribes, so substitutes are all they need.</summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMarketDataRepository>());
        services.AddSingleton(Substitute.For<IMarketDataHub>());
        services.AddSingleton(Substitute.For<IMarketDataIngest>());
        services.AddSingleton(Substitute.For<IMarketDataStore>());
        services.AddSingleton(Substitute.For<IModelRegistry>());
        services.AddSingleton(Substitute.For<IBrokerSelector>());
        services.AddSingleton(new InMemoryLogSink());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services.BuildServiceProvider();
    }

    /// <summary>Minimal authored-shaped view-model — what the composed view's DataContext really is.</summary>
    private sealed class TestStrategyViewModel : LiveSignalStrategyViewModelBase
    {
        public TestStrategyViewModel(IServiceProvider provider)
            : base(
                "composedTest", "Composed Test",
                new LiveStrategyHostServices(
                    provider.GetRequiredService<IMarketDataRepository>(),
                    provider.GetRequiredService<IMarketDataHub>(),
                    provider.GetRequiredService<IMarketDataIngest>(),
                    provider.GetRequiredService<IMarketDataStore>(),
                    provider.GetRequiredService<IBrokerSelector>(),
                    provider.GetRequiredService<InMemoryLogSink>(),
                    Substitute.For<IInstrumentRegistry>()),
                Substitute.For<INotificationPublisher>(),
                Substitute.For<IClock>(),
                Substitute.For<ISignalGeneratorRouterFactory>(),
                NullLogger<TestStrategyViewModel>.Instance)
        {
        }

        protected override IBacktestStrategy BuildStrategy(Contract contract) =>
            Substitute.For<IBacktestStrategy>();
    }

    [Fact]
    public void Every_declared_data_flag_becomes_its_panel_in_the_embedded_preset()
    {
        WpfTestApp.Run(() =>
        {
            using var provider = BuildProvider();
            using var view = new ComposedStrategyView(
                new Descriptor(StrategyDataRequirement.L1 | StrategyDataRequirement.Bars |
                               StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape),
                provider);

            view.Panels.Should().HaveCount(3);
            var chart = view.Panels.OfType<ChartsPanel>().Single();
            var book = view.Panels.OfType<OrderBookPanel>().Single();
            var footprint = view.Panels.OfType<VolumeFootprintPanel>().Single();

            // The Embedded presets are the whole point: no toolbar to fight the strategy's instrument,
            // and no ML forecaster — not hidden, never constructed.
            chart.Features.Should().BeSameAs(ChartsPanelFeatures.Embedded);
            book.Features.Should().BeSameAs(OrderBookPanelFeatures.Embedded);
            footprint.Features.Should().BeSameAs(VolumeFootprintPanelFeatures.Embedded);
            ((OrderBookViewModel)book.DataContext).MlEnabled.Should().BeFalse();
            ((VolumeFootprintViewModel)footprint.DataContext).MlEnabled.Should().BeFalse();

            // No instrument pinned yet — panels wait for the strategy's pick, quietly.
            ((OrderBookViewModel)book.DataContext).SelectedInstrument.Should().BeNull();
            ((ChartsViewModel)chart.DataContext).SelectedInstrument.Should().BeNull();
        });
    }

    [Fact]
    public void An_L1_only_strategy_gets_the_quote_card_and_no_panels()
    {
        WpfTestApp.Run(() =>
        {
            using var provider = BuildProvider();
            using var view = new ComposedStrategyView(new Descriptor(StrategyDataRequirement.L1), provider);

            view.Panels.Should().BeEmpty();
        });
    }

    [Fact]
    public void A_bars_only_strategy_gets_just_the_chart()
    {
        WpfTestApp.Run(() =>
        {
            using var provider = BuildProvider();
            using var view = new ComposedStrategyView(
                new Descriptor(StrategyDataRequirement.L1 | StrategyDataRequirement.Bars), provider);

            view.Panels.Should().ContainSingle().Which.Should().BeOfType<ChartsPanel>();
        });
    }

    [Fact]
    public void The_strategy_instrument_lands_in_every_panel_once_setup_completes()
    {
        WpfTestApp.Run(() =>
        {
            using var provider = BuildProvider();
            using var view = new ComposedStrategyView(
                new Descriptor(StrategyDataRequirement.L1 | StrategyDataRequirement.Bars |
                               StrategyDataRequirement.Depth),
                provider);
            var vm = new TestStrategyViewModel(provider);
            view.DataContext = vm;

            var instrument = new SignalInstrument("BTC/USDT · Binance", "Crypto", TestContract, BrokerKind.Binance);
            vm.SelectedInstrument = instrument;

            // Not configured yet — the setup screen is still up, so the panels must not move.
            var book = (OrderBookViewModel)view.Panels.OfType<OrderBookPanel>().Single().DataContext;
            var chart = (ChartsViewModel)view.Panels.OfType<ChartsPanel>().Single().DataContext;
            book.SelectedInstrument.Should().BeNull();

            vm.IsConfigured = true;

            book.SelectedInstrument.Should().BeSameAs(instrument, "the book panel takes the strategy's pick as-is");
            chart.SelectedInstrument.Should().NotBeNull();
            chart.SelectedInstrument!.Contract.Symbol.Should().Be("BTCUSDT");
            chart.SelectedInstrument.Broker.Should().Be(BrokerKind.Binance, "the strategy's pick pins the broker");

            vm.Dispose();
        });
    }

    [Fact]
    public void Dispose_is_idempotent_and_detaches_from_the_strategy_view_model()
    {
        WpfTestApp.Run(() =>
        {
            using var provider = BuildProvider();
            var view = new ComposedStrategyView(
                new Descriptor(StrategyDataRequirement.L1 | StrategyDataRequirement.Depth), provider);
            var vm = new TestStrategyViewModel(provider);
            view.DataContext = vm;
            var book = (OrderBookViewModel)view.Panels.OfType<OrderBookPanel>().Single().DataContext;

            view.Dispose();
            var again = () => view.Dispose();
            again.Should().NotThrow();

            // After disposal the view must ignore the strategy — a disposed book VM restarted by a
            // late instrument push would resurrect its subscriptions.
            vm.SelectedInstrument = new SignalInstrument("BTC/USDT · Binance", "Crypto", TestContract, BrokerKind.Binance);
            vm.IsConfigured = true;
            book.SelectedInstrument.Should().BeNull();

            vm.Dispose();
        });
    }
}
