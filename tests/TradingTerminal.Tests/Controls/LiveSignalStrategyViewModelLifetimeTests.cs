using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using Xunit;

namespace TradingTerminal.Tests.Controls;

public sealed class LiveSignalStrategyViewModelLifetimeTests
{
    [Fact]
    public async Task Stop_during_a_pending_start_does_not_resume_the_stale_run()
    {
        var repository = Substitute.For<IMarketDataRepository>();
        repository.ListInstrumentsAsync().Returns(Task.FromResult<IReadOnlyList<TradableInstrument>>(
        [
            new TradableInstrument(
                "AAPL — Simulated",
                "Simulated",
                Contract.UsStock("AAPL"),
                BrokerKind.Simulated),
        ]));

        var selector = Substitute.For<IBrokerSelector>();
        selector.IsConnected(BrokerKind.Simulated).Returns(true);
        selector.Connected.Returns(new[] { BrokerKind.Simulated });

        var ingest = Substitute.For<IMarketDataIngest>();
        ingest.Resolve(Arg.Any<Contract>(), BrokerKind.Simulated).Returns(new InstrumentId(1));

        var services = new LiveStrategyHostServices(
            repository,
            Substitute.For<IMarketDataHub>(),
            ingest,
            Substitute.For<IMarketDataStore>(),
            selector,
            new InMemoryLogSink(),
            Substitute.For<IInstrumentRegistry>());
        var strategy = new DelayedStartStrategy();
        var viewModel = new TestViewModel(services, strategy)
        {
            SelectedInstrument = new SignalInstrument(
                "AAPL — Simulated",
                "Simulated",
                Contract.UsStock("AAPL"),
                BrokerKind.Simulated),
        };

        var start = viewModel.StartCommand.ExecuteAsync(null);
        await strategy.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.StopCommand.ExecuteAsync(null);
        strategy.StartToken.IsCancellationRequested.Should().BeTrue();

        strategy.Release.TrySetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsStreaming.Should().BeFalse();
        ingest.DidNotReceive().Subscribe(Arg.Any<Contract>(), Arg.Any<BrokerKind>());

        viewModel.Dispose();
        var disposeAgain = () => viewModel.Dispose();
        disposeAgain.Should().NotThrow();
    }

    private sealed class TestViewModel(
        LiveStrategyHostServices services,
        IBacktestStrategy strategy)
        : LiveSignalStrategyViewModelBase(
            "lifetime-test",
            "Lifetime Test",
            services,
            Substitute.For<INotificationPublisher>(),
            Substitute.For<IClock>(),
            new SignalGeneratorRouterFactory(),
            NullLogger<TestViewModel>.Instance)
    {
        protected override IBacktestStrategy BuildStrategy(Contract contract) => strategy;
    }

    private sealed class DelayedStartStrategy : IBacktestStrategy
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken StartToken { get; private set; }

        public async Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            StartToken = ct;
            Started.TrySetResult(true);
            await Release.Task;
        }

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
