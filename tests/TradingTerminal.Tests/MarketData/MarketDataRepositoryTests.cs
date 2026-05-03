using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
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
        var ib = Substitute.For<IIbClient>();
        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        ib.ConnectionState.Returns(state);

        // SubscribeBarsAsync throws synchronously when not connected — emulates FakeIbClient/RealIbClient guard.
        ib.SubscribeBarsAsync(Arg.Any<Contract>(), Arg.Any<BarSize>(), Arg.Any<CancellationToken>())
          .Returns(_ => throw new InvalidOperationException("Not connected."));

        var connection = new ConnectionManager(
            ib,
            Options.Create(new InteractiveBrokersOptions()),
            NullLogger<ConnectionManager>.Instance);

        var repo = new MarketDataRepository(
            ib, connection, new ImmediateDispatcher(),
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
}
