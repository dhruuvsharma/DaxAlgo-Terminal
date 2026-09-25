using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Execution.Routing;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// The generic adapter every routed broker runs through, against a fake route: what it sends, how broker
/// answers become engine events, and that the live gate is the one the other real adapters use.
/// </summary>
public sealed class RoutedExecutionAdapterTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly InstrumentId Instrument = new(9001);

    private sealed class FakeRoute : IBrokerOrderRoute
    {
        private int _sequence;

        public BrokerKind Broker => BrokerKind.Binance;
        public string DisplayName => "Fake";
        public string RouteId => "fake";
        public string? PaperEnvironmentName { get; init; } = "TESTNET";
        public string AccountId { get; set; } = "acct-1";
        public decimal Position { get; set; }
        public Exception? SubmitFailure { get; set; }
        public List<RouteOrderRequest> Submits { get; } = [];
        public List<string> Cancels { get; } = [];
        public Dictionary<string, RouteOrder> Orders { get; } = [];

        public RouteInstrument Rules { get; set; } = new(
            "BTCUSDT", 0.00001m, 0.00001m, 0.01m, 1, 10_000_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled,
            SupportsReplace: false, "USDT") { BaseAsset = "BTC" };

        public Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct) =>
            Task.FromResult(new RouteAccount(AccountId, "USDT", 1000m, 900m));

        public Task<RouteAccount> AccountAsync(RouteEnvironment environment, CancellationToken ct) => ConnectAsync(environment, ct);

        public Task<RouteInstrument> InstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) => Task.FromResult(Rules);

        public Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
        {
            Submits.Add(request);
            if (SubmitFailure is not null)
                throw SubmitFailure;
            var order = new RouteOrder(
                $"B{++_sequence}", request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce,
                request.Quantity, request.LimitPrice, request.StopPrice, RouteOrderStatus.Working, 0, null, 0, string.Empty, null, Now);
            Orders[order.OrderId] = order;
            return Task.FromResult(order);
        }

        public Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
        {
            Cancels.Add(order.OrderId);
            Orders[order.OrderId] = Orders[order.OrderId] with { Status = RouteOrderStatus.Cancelled };
            return Task.CompletedTask;
        }

        public Task<RouteOrder> ReplaceAsync(RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteOrder>>([.. Orders.Values.Where(order => order.Status is RouteOrderStatus.Working or RouteOrderStatus.PartiallyFilled)]);

        public Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
            Task.FromResult(Orders.GetValueOrDefault(orderId));

        public Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            Task.FromResult(new RoutePosition(symbol, Position));

        public Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            Task.FromResult<RoutePrice?>(new RoutePrice(50_000m, Now));

        /// <summary>Moves order <paramref name="orderId"/> to a new broker state.</summary>
        public void Set(string orderId, RouteOrderStatus status, decimal filled, decimal? average, decimal fee = 0, string feeCurrency = "")
        {
            var order = Orders[orderId];
            Orders[orderId] = order with { Status = status, FilledQuantity = filled, AveragePrice = average, Fee = fee, FeeCurrency = feeCurrency };
        }
    }

    private static RoutedExecutionOptions Options(bool allowLive = false) =>
        new() { AllowLiveExecution = allowLive, PollIntervalMilliseconds = 60_000 };

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(Now);
        return clock;
    }

    private static async Task<(RoutedExecutionAdapter Card, RoutedExecutionAdapter Book, ControllableAdapterEventScheduler Scheduler, List<BrokerAdapterEvent> Events)> BookAsync(FakeRoute route)
    {
        var card = new RoutedExecutionAdapter(route, ExecutionMode.Paper, Options(), hasCredentials: true, Clock());
        await card.ConnectAsync();
        var scheduler = new ControllableAdapterEventScheduler();
        var book = card.CreateBookAdapter(Instrument, "BTCUSDT", scheduler);
        var events = new List<BrokerAdapterEvent>();
        book.EventReceived += events.Add;
        await book.ConnectAsync();
        return (card, book, scheduler, events);
    }

    private static BrokerAdapterCommandResult Submit(RoutedExecutionAdapter adapter, string clientOrderId, long units) =>
        adapter.Submit(new BrokerSubmitCommand(
            OmsTestData.Instruction(clientOrderId, units),
            OmsTestData.Causation(clientOrderId),
            adapter.Capabilities.Version));

    [Fact]
    public async Task A_book_sends_whole_units_as_the_brokers_quantity()
    {
        var route = new FakeRoute();
        var (card, book, scheduler, events) = await BookAsync(route);
        await using var _ = card;
        await using var __ = book;

        Assert.True(book.Session.CanExecute);
        Assert.Equal(new ScaledPrice(1, 2), book.Capabilities.TickSize);
        var result = Submit(book, "c-1", 30);
        Assert.True(result.IsDispatched);

        await WaitFor(() => route.Submits.Count == 1);
        Assert.Equal(0.00030m, route.Submits[0].Quantity);
        Assert.Equal("c-1", route.Submits[0].ClientOrderId);
        Assert.Equal(OrderSide.Buy, route.Submits[0].Side);

        scheduler.RunAll();
        Assert.Contains(events.OfType<BrokerOrderEvent>(), e => e.VenueEvent.Kind == VenueEventKind.Acknowledged);
    }

    [Fact]
    public async Task Cumulative_broker_fills_become_exact_deltas_at_their_incremental_price()
    {
        var route = new FakeRoute();
        var (card, book, scheduler, events) = await BookAsync(route);
        await using var _ = card;
        await using var __ = book;
        Submit(book, "c-2", 30);
        await WaitFor(() => route.Orders.Count == 1);

        route.Set("B1", RouteOrderStatus.PartiallyFilled, 0.00010m, 50_000m);
        await book.RefreshReconciliationAsync();
        route.Set("B1", RouteOrderStatus.Filled, 0.00030m, 50_010m);
        await book.RefreshReconciliationAsync();
        scheduler.RunAll();

        var fills = events.OfType<BrokerExecutionEvent>().Select(e => e.VenueEvent.Fill!.Value).ToList();
        Assert.Equal(2, fills.Count);
        Assert.Equal(ScaledQuantity.FromWhole(10), fills[0].Quantity);
        Assert.Equal(new ScaledPrice(50_000, 0), fills[0].Price);
        Assert.Equal(ScaledQuantity.FromWhole(20), fills[1].Quantity);
        // (50 010 × 0.0003 − 50 000 × 0.0001) ÷ 0.0002 = 50 015.
        Assert.Equal(new ScaledPrice(50_015, 0), fills[1].Price);
        Assert.Equal(ScaledQuantity.FromWhole(30), events.OfType<BrokerPositionEvent>().Last().Position);
    }

    [Fact]
    public async Task A_book_owns_only_what_it_trades_and_adds_back_fees_taken_in_the_coin()
    {
        var route = new FakeRoute { Position = 0.5m };
        var (card, book, scheduler, _) = await BookAsync(route);
        await using var c = card;
        await using var b = book;
        Assert.Equal(0.5m, book.BaselinePosition);

        Submit(book, "c-3", 30);
        await WaitFor(() => route.Orders.Count == 1);
        route.Set("B1", RouteOrderStatus.Filled, 0.00030m, 50_000m, fee: 0.0000003m, feeCurrency: "BTC");
        route.Position = 0.5m + 0.00030m - 0.0000003m;
        await book.RefreshReconciliationAsync();
        scheduler.RunAll();

        var snapshot = book.CaptureReconciliationSnapshot();
        Assert.Equal(ScaledQuantity.FromWhole(30), Assert.Single(snapshot.Positions).Quantity);
        Assert.Equal("USDT", Assert.Single(snapshot.Cash).Currency);
    }

    [Fact]
    public async Task A_refusal_is_a_rejection_and_anything_else_is_an_unknown_outcome()
    {
        var route = new FakeRoute { SubmitFailure = new BrokerOrderRouteException("Account has insufficient balance.", isRejection: true) };
        var (card, book, scheduler, events) = await BookAsync(route);
        await using var _ = card;
        await using var __ = book;

        Submit(book, "c-4", 30);
        await WaitFor(() => route.Submits.Count == 1);
        await WaitFor(() => { scheduler.RunAll(); return events.Count > 0; });
        var rejected = Assert.IsType<BrokerOrderEvent>(Assert.Single(events));
        Assert.Equal(VenueEventKind.Rejected, rejected.VenueEvent.Kind);
        Assert.Contains("insufficient balance", rejected.VenueEvent.Reason);

        route.SubmitFailure = new HttpRequestException("The operation was canceled.");
        Submit(book, "c-5", 30);
        await WaitFor(() => route.Submits.Count == 2);
        await WaitFor(() => { scheduler.RunAll(); return events.Count > 1; });
        Assert.Equal(VenueEventKind.OutcomeUnknown, ((BrokerOrderEvent)events[1]).VenueEvent.Kind);
    }

    [Fact]
    public async Task A_fill_that_is_not_a_whole_number_of_units_stops_for_reconciliation()
    {
        var route = new FakeRoute();
        var (card, book, scheduler, events) = await BookAsync(route);
        await using var _ = card;
        await using var __ = book;
        Submit(book, "c-6", 30);
        await WaitFor(() => route.Orders.Count == 1);

        route.Set("B1", RouteOrderStatus.PartiallyFilled, 0.000105m, 50_000m);
        await book.RefreshReconciliationAsync();
        scheduler.RunAll();

        Assert.Empty(events.OfType<BrokerExecutionEvent>());
        Assert.Contains(events.OfType<BrokerOrderEvent>(), e => e.VenueEvent.Kind == VenueEventKind.OutcomeUnknown);
    }

    [Fact]
    public async Task Cancellation_is_sent_and_confirmed_from_the_brokers_state()
    {
        var route = new FakeRoute();
        var (card, book, scheduler, events) = await BookAsync(route);
        await using var _ = card;
        await using var __ = book;
        var instruction = OmsTestData.Instruction("c-7", 30);
        book.Submit(new BrokerSubmitCommand(instruction, OmsTestData.Causation("c-7"), book.Capabilities.Version));
        await WaitFor(() => route.Orders.Count == 1);
        scheduler.RunAll();

        var cancel = book.Cancel(new BrokerCancelCommand(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId), OmsTestData.Causation("c-7-cancel")));
        Assert.True(cancel.IsDispatched);
        await WaitFor(() => { scheduler.RunAll(); return events.OfType<BrokerOrderEvent>().Any(e => e.VenueEvent.Kind == VenueEventKind.Cancelled); });
        Assert.Equal(["B1"], route.Cancels);
    }

    [Fact]
    public async Task A_broker_connection_without_an_instrument_refuses_every_order()
    {
        var route = new FakeRoute();
        await using var card = new RoutedExecutionAdapter(route, ExecutionMode.Paper, Options(), hasCredentials: true, Clock());
        await card.ConnectAsync();

        Assert.Equal("acct-1", card.NativeAccountId);
        var result = Submit(card, "c-8", 1);
        Assert.False(result.IsDispatched);
        Assert.Empty(route.Submits);
    }

    [Fact]
    public void Paper_is_refused_for_a_broker_with_no_paper_environment()
    {
        var route = new FakeRoute { PaperEnvironmentName = null };
        var error = Assert.Throws<InvalidOperationException>(() =>
            new RoutedExecutionAdapter(route, ExecutionMode.Paper, Options(), hasCredentials: true, Clock()));
        Assert.Contains("no paper", error.Message);
    }

    [Fact]
    public void Live_needs_every_condition_of_the_shared_live_gate()
    {
        var route = new FakeRoute();
        var confirmations = new InMemoryLiveExecutionConfirmationStore();

        Assert.Throws<InvalidOperationException>(() => new RoutedExecutionAdapter(
            route, ExecutionMode.Live, Options(allowLive: false), true, Clock(), confirmationStore: confirmations, expectedAccountId: "acct-1"));
        Assert.Throws<InvalidOperationException>(() => new RoutedExecutionAdapter(
            route, ExecutionMode.Live, Options(allowLive: true), false, Clock(), confirmationStore: confirmations, expectedAccountId: "acct-1"));
        Assert.Throws<InvalidOperationException>(() => new RoutedExecutionAdapter(
            route, ExecutionMode.Live, Options(allowLive: true), true, Clock(), confirmationStore: confirmations, expectedAccountId: "acct-1"));

        confirmations.Save(new LiveExecutionConfirmation("fake", "acct-1", "LIVE", Now, "tester"));
        using var live = new RoutedExecutionAdapter(
            route, ExecutionMode.Live, Options(allowLive: true), true, Clock(), confirmationStore: confirmations, expectedAccountId: "acct-1");
        Assert.Equal("fake-live", live.AdapterId);
        Assert.Equal("LIVE", live.EnvironmentLabel);
    }

    [Fact]
    public async Task Live_refuses_credentials_that_reach_a_different_account_and_direct_commands()
    {
        var route = new FakeRoute { AccountId = "someone-else" };
        var confirmations = new InMemoryLiveExecutionConfirmationStore();
        confirmations.Save(new LiveExecutionConfirmation("fake", "acct-1", "LIVE", Now, "tester"));
        await using var live = new RoutedExecutionAdapter(
            route, ExecutionMode.Live, Options(allowLive: true), true, Clock(), confirmationStore: confirmations, expectedAccountId: "acct-1");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => live.ConnectAsync());
        Assert.Contains("someone-else", error.Message);

        route.AccountId = "acct-1";
        await live.ConnectAsync();
        await using var book = live.CreateBookAdapter(Instrument, "BTCUSDT", new ControllableAdapterEventScheduler());
        await book.ConnectAsync();
        // A LIVE order reaches a broker only through the coordinator's one-use guardrail admission.
        var direct = Submit(book, "c-9", 30);
        Assert.Equal(BrokerAdapterCommandFault.ExecutionUnavailable, direct.Fault);
        Assert.Empty(route.Submits);

        // Revoking the confirmation closes the session at the next command.
        confirmations.Remove("fake", "acct-1");
        var revoked = Submit(book, "c-10", 30);
        Assert.Contains("revoked", revoked.Reason);
        Assert.False(book.Session.CanExecute);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
