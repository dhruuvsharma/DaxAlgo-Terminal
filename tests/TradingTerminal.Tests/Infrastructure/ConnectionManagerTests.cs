using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Ib;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

public sealed class ConnectionManagerTests
{
    [Fact]
    public async Task Reconnects_with_backoff_on_drop()
    {
        var client = new FlakyClient();

        await using var manager = new ConnectionManager(
            client, NullLogger<ConnectionManager>.Instance);
        manager.ConfigureBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        var states = new List<ConnectionState>();
        using var sub = manager.ConnectionState.Subscribe(states.Add);

        await manager.StartAsync();

        // Wait until we've seen both an initial Connected and a Reconnecting after the drop.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (client.ConnectAttempts >= 2 && states.Contains(ConnectionState.Reconnecting))
                break;
            await Task.Delay(50);
        }

        client.ConnectAttempts.Should().BeGreaterThanOrEqualTo(2,
            "the client must attempt at least one reconnect after the first drop");
        states.Should().Contain(ConnectionState.Connected);
        states.Should().Contain(ConnectionState.Reconnecting);

        await manager.StopAsync();
    }

    /// <summary>Connects, immediately disconnects on first attempt, then connects normally on retry.</summary>
    private sealed class FlakyClient : IBrokerClient
    {
        private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
        public int ConnectAttempts;

        public BrokerKind Kind => BrokerKind.InteractiveBrokers;
        public IObservable<ConnectionState> ConnectionState => _state;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref ConnectAttempts);
            _state.OnNext(Core.Domain.ConnectionState.Connecting);
            _state.OnNext(Core.Domain.ConnectionState.Connected);

            if (attempt == 1)
            {
                // Simulate dropping shortly after connecting.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50, ct);
                    _state.OnNext(Core.Domain.ConnectionState.Disconnected);
                }, ct);
            }
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TradableInstrument>>(Array.Empty<TradableInstrument>());

        public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
            Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());

        public IAsyncEnumerable<Bar> SubscribeBarsAsync(
            Contract contract, BarSize barSize, CancellationToken ct = default)
            => EmptyAsync<Bar>(ct);

        public IAsyncEnumerable<Tick> SubscribeTicksAsync(
            Contract contract, CancellationToken ct = default)
            => EmptyAsync<Tick>(ct);

        public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
            Contract contract, int levels = 10, CancellationToken ct = default)
            => EmptyAsync<DepthSnapshot>(ct);

        public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
            Contract contract, CancellationToken ct = default)
            => EmptyAsync<TradeTick>(ct);

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() { _state.Dispose(); return ValueTask.CompletedTask; }
    }
}
