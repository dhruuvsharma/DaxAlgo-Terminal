using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;
using TradingTerminal.Execution.InteractiveBrokers;
using TradingTerminal.Execution.Oms;
using Xunit;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// The Interactive Brokers order route against a scripted TWS: which port each environment reaches, the paper-
/// account guard, how an order is spelled, and how an order's state and average fill price are read back.
/// </summary>
public sealed class InteractiveBrokersOrderRouteTests
{
    private sealed class Keys(BrokerCredential credential) : IBrokerCredentialSource
    {
        public BrokerCredential For(BrokerKind broker) => broker == BrokerKind.InteractiveBrokers ? credential : BrokerCredential.None;
    }

    private static Keys Row(int port = 7497, int clientId = 1) => new(new BrokerCredential("127.0.0.1", string.Empty)
    {
        Extra = $"{port}|{clientId}|Paper",
    });

    private sealed class Tws(InteractiveBrokersExecutionEndpoint endpoint, string account) : IInteractiveBrokersExecutionTransport
    {
        private int _nextId = 100;

        public InteractiveBrokersExecutionEndpoint Endpoint { get; } = endpoint;

        public bool IsConnected { get; private set; }

        public int? ClientId { get; private set; }

        public List<InteractiveBrokersOrderRequest> Placed { get; } = [];

        public List<InteractiveBrokersOrderSnapshot> Open { get; } = [];

        public List<InteractiveBrokersOrderSnapshot> Completed { get; } = [];

        public List<InteractiveBrokersPositionSnapshot> Positions { get; } = [];

        public event Action<InteractiveBrokersOrderSnapshot>? OrderUpdated
        {
            add { }
            remove { }
        }

        public event Action<InteractiveBrokersExecutionSnapshot>? ExecutionReceived;

        public event Action<InteractiveBrokersCommissionSnapshot>? CommissionReceived
        {
            add { }
            remove { }
        }

        public event Action<InteractiveBrokersPositionSnapshot>? PositionUpdated
        {
            add { }
            remove { }
        }

        public event Action<InteractiveBrokersOrderError>? OrderError;

        public event Action<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task<InteractiveBrokersSessionSnapshot> ConnectAsync(int clientId, string expectedAccountId, CancellationToken cancellationToken = default)
        {
            ClientId = clientId;
            IsConnected = true;
            return Task.FromResult(new InteractiveBrokersSessionSnapshot(account, _nextId, DateTime.UtcNow, Endpoint.IsPaper));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<InteractiveBrokersNativeCapabilities> DiscoverCapabilitiesAsync(InteractiveBrokersContract contract, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InteractiveBrokersNativeCapabilities(
                [InteractiveBrokersNativeOrderType.Market, InteractiveBrokersNativeOrderType.Limit, InteractiveBrokersNativeOrderType.Stop],
                [InteractiveBrokersNativeTimeInForce.Day, InteractiveBrokersNativeTimeInForce.GoodTillCancelled],
                ["STK"], "STK", ScaledQuantity.FromWhole(1), ScaledQuantity.FromWhole(1), new ScaledPrice(1, 2), true,
                default!, default!, string.Empty, string.Empty, DateTime.UtcNow));

        public int ReserveOrderId() => _nextId++;

        public Task PlaceOrderAsync(InteractiveBrokersOrderRequest request, CancellationToken cancellationToken = default)
        {
            Placed.Add(request);
            return Task.CompletedTask;
        }

        public Task CancelOrderAsync(int orderId, string clientOrderId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ModifyOrderAsync(InteractiveBrokersOrderRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<InteractiveBrokersReconciliationSnapshot> GetReconciliationSnapshotAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InteractiveBrokersReconciliationSnapshot(accountId, [.. Open], [.. Completed], [.. Positions],
                [new InteractiveBrokersCashSnapshot(accountId, "USD", new ScaledMoney(1_000_000, 2), new ScaledMoney(800_000, 2), DateTime.UtcNow)],
                DateTime.UtcNow));

        public void Fill(int orderId, decimal cumulative, decimal average) =>
            ExecutionReceived?.Invoke(new InteractiveBrokersExecutionSnapshot(
                "exec-1", orderId, 0, string.Empty, account, new InteractiveBrokersContract(0, "AAPL", "STK", "SMART", string.Empty, "USD"), "BOT",
                ScaledQuantity.FromWhole((long)cumulative), new ScaledPrice((long)(average * 100), 2), ScaledQuantity.FromWhole((long)cumulative),
                new ScaledPrice((long)(average * 100), 2), string.Empty, DateTime.UtcNow));

        public void Refuse(int orderId, string message) =>
            OrderError?.Invoke(new InteractiveBrokersOrderError(orderId, null, 201, message, null, DateTime.UtcNow));

        public void Dispose() => IsConnected = false;

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private static (InteractiveBrokersOrderRoute Route, List<Tws> Sessions) Route(Keys keys, string account = "DU1234567")
    {
        var sessions = new List<Tws>();
        var route = new InteractiveBrokersOrderRoute(keys, (endpoint, _) =>
        {
            var tws = new Tws(endpoint, account);
            sessions.Add(tws);
            return tws;
        }, TimeProvider.System);
        return (route, sessions);
    }

    private static InteractiveBrokersOrderSnapshot Snapshot(int id, InteractiveBrokersNativeOrderStatus status, long quantity, long filled) =>
        new(id, 0, "daxr-a-1", "DU1234567", new InteractiveBrokersContract(0, "AAPL", "STK", "SMART", string.Empty, "USD"), "BUY",
            InteractiveBrokersNativeOrderType.Market, InteractiveBrokersNativeTimeInForce.Day, status, status.ToString(),
            ScaledQuantity.FromWhole(quantity), ScaledQuantity.FromWhole(filled), ScaledQuantity.FromWhole(quantity - filled),
            null, null, null, null, false, null, null, null, DateTime.UtcNow);

    [Theory]
    [InlineData(7497, RouteEnvironment.Paper, 7497)]
    [InlineData(7497, RouteEnvironment.Live, 7496)]
    [InlineData(4002, RouteEnvironment.Live, 4001)]
    [InlineData(4001, RouteEnvironment.Paper, 4002)]
    public void Each_environment_reaches_the_port_of_the_rows_family(int rowPort, RouteEnvironment environment, int expected) =>
        Assert.Equal(expected, InteractiveBrokersOrderRoute.PortFor(environment, rowPort));

    [Fact]
    public async Task Paper_connects_as_its_own_client_and_refuses_a_live_account()
    {
        var (route, sessions) = Route(Row(clientId: 3));
        await using var _ = route;

        var account = await route.ConnectAsync(RouteEnvironment.Paper, CancellationToken.None);

        Assert.Equal(("DU1234567", "USD", 10_000m, 8_000m), (account.AccountId, account.Currency, account.CashTotal, account.CashAvailable));
        Assert.Equal(23, sessions[0].ClientId);
        Assert.Equal(7497, sessions[0].Endpoint.Port);

        var (liveTws, __) = Route(Row(), account: "U7654321");
        await using var ___ = liveTws;
        var refused = await Assert.ThrowsAsync<BrokerOrderRouteException>(() => liveTws.ConnectAsync(RouteEnvironment.Paper, CancellationToken.None));
        Assert.True(refused.IsRejection);
        Assert.Contains("live account", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_carries_the_reserved_id_and_reads_back_with_its_average_fill()
    {
        var (route, sessions) = Route(Row());
        await using var _ = route;

        var placed = await route.SubmitAsync(RouteEnvironment.Paper,
            new RouteOrderRequest("AAPL", "daxr-a-1", OrderSide.Buy, RouteOrderType.Market, RouteTimeInForce.Day, 10m, null, null),
            CancellationToken.None);

        var tws = sessions.Single();
        var sent = Assert.Single(tws.Placed);
        Assert.Equal(("100", 100), (placed.OrderId, sent.OrderId));
        Assert.Equal(("BUY", "STK", "SMART"), (sent.Side, sent.Contract.SecurityType, sent.Contract.Exchange));
        Assert.Equal(InteractiveBrokersNativeOrderType.Market, sent.OrderType);

        tws.Fill(100, 10m, 190.25m);
        tws.Completed.Add(Snapshot(100, InteractiveBrokersNativeOrderStatus.Filled, 10, 10));
        var read = await route.OrderAsync(RouteEnvironment.Paper, "AAPL", "100", "daxr-a-1", CancellationToken.None);

        Assert.Equal((RouteOrderStatus.Filled, 10m, 190.25m), (read!.Status, read.FilledQuantity, read.AveragePrice));
    }

    [Fact]
    public async Task An_order_tws_refuses_reads_as_rejected_with_its_words()
    {
        var (route, sessions) = Route(Row());
        await using var _ = route;
        await route.SubmitAsync(RouteEnvironment.Paper,
            new RouteOrderRequest("AAPL", "daxr-a-2", OrderSide.Sell, RouteOrderType.Limit, RouteTimeInForce.Day, 5m, 250m, null),
            CancellationToken.None);

        sessions[0].Refuse(100, "Order rejected - reason: insufficient shares");
        var read = await route.OrderAsync(RouteEnvironment.Paper, "AAPL", "100", "daxr-a-2", CancellationToken.None);

        Assert.Equal(RouteOrderStatus.Rejected, read!.Status);
        Assert.Contains("insufficient shares (201)", read.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_currency_pair_is_an_idealpro_cash_contract_and_a_ticker_a_smart_stock()
    {
        var pair = InteractiveBrokersOrderRoute.Contract("eur.usd");
        Assert.Equal(("EUR", "CASH", "IDEALPRO", "USD"), (pair.Symbol, pair.SecurityType, pair.Exchange, pair.Currency));
        var stock = InteractiveBrokersOrderRoute.Contract("AAPL");
        Assert.Equal(("AAPL", "STK", "SMART", "USD"), (stock.Symbol, stock.SecurityType, stock.Exchange, stock.Currency));
    }
}
