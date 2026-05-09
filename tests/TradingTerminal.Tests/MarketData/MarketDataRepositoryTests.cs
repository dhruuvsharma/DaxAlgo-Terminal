using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Tests.TestSupport;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class MarketDataRepositoryTests
{
    [Fact]
    public async Task When_disconnected_subscribe_throws()
    {
        var client = Substitute.For<IBrokerClient>();
        client.Kind.Returns(BrokerKind.InteractiveBrokers);
        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        client.ConnectionState.Returns(state);

        // SubscribeBarsAsync throws synchronously when not connected — emulates Fake/Real client guard.
        client.SubscribeBarsAsync(Arg.Any<Contract>(), Arg.Any<BarSize>(), Arg.Any<CancellationToken>())
              .Returns(_ => throw new InvalidOperationException("Not connected."));

        var selector = new SingleClientSelector(client);
        var connection = new ConnectionManager(selector, NullLogger<ConnectionManager>.Instance);

        var repo = new MarketDataRepository(
            selector, connection, new ImmediateDispatcher(),
            NullLogger<MarketDataRepository>.Instance);

        var act = async () =>
        {
            await foreach (var _ in repo.SubscribeBarsAsync(
                Contract.UsStock("NVDA"), BarSize.ThreeMinutes, CancellationToken.None))
            {
                /* unreachable */
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
                  .WithMessage("*Not connected*");
    }

    private sealed class SingleClientSelector : IBrokerSelector
    {
        public SingleClientSelector(IBrokerClient client)
        {
            Active = client;
            ActiveMode = new BrokerConnectionMode(client.Kind, false, "Test", "Test");
        }

        public BrokerKind ActiveKind => Active.Kind;
        public IBrokerClient Active { get; }
        public BrokerConnectionMode ActiveMode { get; }
        public event EventHandler? ActiveChanged { add { } remove { } }
        public void SetActive(BrokerKind kind) { /* test selector — single client */ }
    }
}
