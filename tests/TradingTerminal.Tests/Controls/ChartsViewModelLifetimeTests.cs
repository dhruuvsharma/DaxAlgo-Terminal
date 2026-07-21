using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Charts;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.Controls;

public sealed class ChartsViewModelLifetimeTests
{
    [Fact]
    public async Task Dispose_is_idempotent_after_a_chart_reload_created_its_cancellation_source()
    {
        var repository = Substitute.For<IMarketDataRepository>();
        repository.GetHistoricalBarsAsync(
                Arg.Any<Contract>(),
                Arg.Any<BrokerKind>(),
                Arg.Any<BarSize>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>()));

        var selector = Substitute.For<IBrokerSelector>();
        selector.IsConnected(BrokerKind.Simulated).Returns(true);

        var instrument = new TradableInstrument(
            "AAPL — Simulated",
            "Simulated",
            Contract.UsStock("AAPL"),
            BrokerKind.Simulated);

        var viewModel = new ChartsViewModel(
            repository,
            Substitute.For<IMarketDataHub>(),
            Substitute.For<IMarketDataIngest>(),
            selector,
            NullLogger<ChartsViewModel>.Instance,
            new ChartsEmbedOptions(instrument));

        await viewModel.NotifyChartReadyAsync();

        var disposeTwice = () =>
        {
            viewModel.Dispose();
            viewModel.Dispose();
        };

        disposeTwice.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_while_a_reload_is_pending_prevents_that_run_from_resuming()
    {
        var pendingBars = new TaskCompletionSource<IReadOnlyList<Bar>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = Substitute.For<IMarketDataRepository>();
        repository.GetHistoricalBarsAsync(
                Arg.Any<Contract>(),
                Arg.Any<BrokerKind>(),
                Arg.Any<BarSize>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(pendingBars.Task);

        var selector = Substitute.For<IBrokerSelector>();
        selector.IsConnected(BrokerKind.Simulated).Returns(true);

        var instrument = new TradableInstrument(
            "AAPL — Simulated",
            "Simulated",
            Contract.UsStock("AAPL"),
            BrokerKind.Simulated);
        var viewModel = new ChartsViewModel(
            repository,
            Substitute.For<IMarketDataHub>(),
            Substitute.For<IMarketDataIngest>(),
            selector,
            NullLogger<ChartsViewModel>.Instance,
            new ChartsEmbedOptions(instrument));

        var reload = viewModel.NotifyChartReadyAsync();
        viewModel.Dispose();
        pendingBars.TrySetResult(Array.Empty<Bar>());

        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.NotifyChartReadyAsync();

        await repository.Received(1).GetHistoricalBarsAsync(
            Arg.Any<Contract>(),
            Arg.Any<BrokerKind>(),
            Arg.Any<BarSize>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }
}
