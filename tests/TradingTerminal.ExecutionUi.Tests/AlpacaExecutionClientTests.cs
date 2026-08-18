using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.ExecutionUi.Tests;

[CollectionDefinition("Execution client", DisableParallelization = true)]
public sealed class ExecutionClientCollection;

[Collection("Execution client")]
public sealed class AlpacaExecutionClientTests
{
    private static readonly DateTime TimestampUtc = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ConnectCreateAndLimitOrder_FlowsThroughOmsIntoLedgerAndPosition()
    {
        await using var harness = new Harness(TimestampUtc);
        using var client = new InProcessExecutionClient([harness.Adapter]);

        var disconnected = Assert.Single(client.GetSnapshot().Adapters, item => item.Id == "alpaca-paper");
        Assert.Equal("PAPER", disconnected.EnvironmentLabel);
        Assert.Equal(BrokerKind.Alpaca, disconnected.LoginBroker);
        Assert.True(disconnected.CanConnect);

        var connected = await client.ConnectAdapterAsync(
            new ExecutionAdapterConnectRequest("alpaca-paper", "unit-test-key", "unit-test-secret"));
        Assert.True(connected.IsSuccess, connected.Message);
        var card = Assert.Single(client.GetSnapshot().Adapters, item => item.Id == "alpaca-paper");
        Assert.True(card.IsConnected);
        Assert.DoesNotContain("unit-test-secret", card.StatusDetail, StringComparison.Ordinal);

        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Paper Test",
            "alpaca-paper",
            Array.AsReadOnly(["Manual verification"])));
        Assert.True(created.IsSuccess, created.Message);
        var book = Assert.Single(client.GetSnapshot().Books, item => item.Name == "Paper Test");
        var instrument = Assert.Single(book.TradableInstruments);
        Assert.True(book.AdmissionOpen);
        Assert.True(book.SupportsKill);

        var sent = await client.SubmitManualOrderAsync(new ExecutionManualOrderRequest(
            book.Id,
            instrument.Instrument,
            instrument.Symbol,
            ExecutionManualOrderSide.Buy,
            ScaledQuantity.FromWhole(2),
            ExecutionManualOrderType.Limit,
            new ScaledPrice(10_025, 2)));
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(harness.Scheduler.RunAll() > 0);

        var request = Assert.Single(harness.Transport.Submits);
        Assert.StartsWith("daxt-", request.ClientOrderId, StringComparison.Ordinal);
        Assert.InRange(request.ClientOrderId.Length, 1, 48);
        Assert.Equal(ScaledQuantity.FromWhole(2), request.Quantity);
        Assert.Equal(new ScaledPrice(10_025, 2), request.LimitPrice);
        Assert.Equal("day", request.TimeInForce);

        harness.Source.Publish(harness.Transport.Filled(request, TimestampUtc.AddSeconds(2)));
        Assert.True(harness.Scheduler.RunAll() > 0);

        var afterFill = Assert.Single(client.GetSnapshot().Books, item => item.Name == "Paper Test");
        Assert.Contains(afterFill.Orders, item => item.State == "Filled");
        Assert.Contains(afterFill.LedgerEvents, item => item.Message.StartsWith("FILL", StringComparison.Ordinal));
        Assert.Equal("+2", Assert.Single(afterFill.Positions).RealQuantity);
        Assert.Equal(1, afterFill.OpenRealPositionCount);
    }

    [Fact]
    public async Task MarketOrder_WithStaleReferenceTrade_FailsBeforeTransportSubmit()
    {
        await using var harness = new Harness(TimestampUtc)
        {
            LatestTradeTimestampUtc = TimestampUtc.AddMinutes(-1),
        };
        using var client = new InProcessExecutionClient([harness.Adapter]);
        Assert.True((await client.ConnectAdapterAsync(
            new ExecutionAdapterConnectRequest("alpaca-paper", "unit-test-key", "unit-test-secret"))).IsSuccess);
        Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Stale Market",
            "alpaca-paper",
            Array.AsReadOnly(["Manual verification"])))).IsSuccess);
        var book = Assert.Single(client.GetSnapshot().Books, item => item.Name == "Stale Market");
        var instrument = Assert.Single(book.TradableInstruments);

        var result = await client.SubmitManualOrderAsync(new ExecutionManualOrderRequest(
            book.Id,
            instrument.Instrument,
            instrument.Symbol,
            ExecutionManualOrderSide.Buy,
            ScaledQuantity.FromWhole(1),
            ExecutionManualOrderType.Market,
            null));

        Assert.False(result.IsSuccess);
        Assert.Contains("newer than 15 seconds", result.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.Submits);
    }

    [Fact]
    public async Task Kill_StopsIntakeAndCancelsWorkingOrderThroughGuardedOmsPath()
    {
        await using var harness = new Harness(TimestampUtc);
        using var client = new InProcessExecutionClient([harness.Adapter]);
        Assert.True((await client.ConnectAdapterAsync(
            new ExecutionAdapterConnectRequest("alpaca-paper", "unit-test-key", "unit-test-secret"))).IsSuccess);
        Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Kill Test",
            "alpaca-paper",
            Array.AsReadOnly(["Manual verification"])))).IsSuccess);
        var book = Assert.Single(client.GetSnapshot().Books, item => item.Name == "Kill Test");
        var instrument = Assert.Single(book.TradableInstruments);
        var submitted = await client.SubmitManualOrderAsync(new ExecutionManualOrderRequest(
            book.Id,
            instrument.Instrument,
            instrument.Symbol,
            ExecutionManualOrderSide.Buy,
            ScaledQuantity.FromWhole(1),
            ExecutionManualOrderType.Limit,
            new ScaledPrice(10_025, 2)));
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.True(harness.Scheduler.RunAll() > 0);

        var killed = await client.KillAsync(book.Id);

        Assert.True(killed.IsSuccess, killed.Message);
        Assert.Contains("cancellation", killed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Transport.CancelledOrderIds);
        Assert.True(Assert.Single(client.GetSnapshot().Books, item => item.Id == book.Id).IsIntakePaused);
    }

    [Fact]
    public async Task AuthenticationFailure_LeavesErrorCardAndNoBookPath()
    {
        await using var harness = new Harness(TimestampUtc);
        harness.Transport.AccountFailure = new InvalidOperationException("deterministic authentication denial");
        using var client = new InProcessExecutionClient([harness.Adapter]);

        var result = await client.ConnectAdapterAsync(
            new ExecutionAdapterConnectRequest("alpaca-paper", "unit-test-key", "unit-test-secret"));

        Assert.False(result.IsSuccess);
        var card = Assert.Single(client.GetSnapshot().Adapters, item => item.Id == "alpaca-paper");
        Assert.Equal(ExecutionConnectionStatus.Error, card.Status);
        Assert.Equal("Authentication error", card.StatusLabel);
        Assert.False(card.CanCreateBook);
        Assert.DoesNotContain("unit-test-secret", card.StatusDetail, StringComparison.Ordinal);
        // No books: the console no longer seeds fabricated demo books, and a failed/unauthorized
        // adapter must not create one either.
        Assert.Empty(client.GetSnapshot().Books);
    }

    [Fact]
    public async Task EnabledSandboxReplicationCannotCreateOrUseLiveRouteWhenAuthorizationIsMissing()
    {
        await using var harness = new Harness(TimestampUtc);
        var confirmations = new InMemoryLiveExecutionConfirmationStore();
        var ownerOptions = new AlpacaExecutionOptions
        {
            Enabled = true,
            AllowLiveExecution = false,
            MarketDataBaseUrl = AlpacaExecutionOptions.DataBaseUrl,
            Symbol = "AAPL",
            CanonicalInstrumentId = 7101,
            MaximumTrackedOrders = 32,
            PollIntervalMilliseconds = 100,
        };
        using var client = new InProcessExecutionClient(
            [harness.Adapter],
            confirmations,
            alpacaOptions: ownerOptions,
            executionClock: new FixedClock(TimestampUtc));
        var source = new ManualPortfolioSource();
        await using var replicator = new SandboxExecutionReplicator(
            source,
            client,
            new SandboxExecutionReplicationOptions(
                "unauthorized-live-book",
                "sandbox-live-refusal",
                Enabled: true));
        var replication = new TaskCompletionSource<SandboxExecutionReplicationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        replicator.SubmissionCompleted += outcome => replication.TrySetResult(outcome);

        var modeChange = await client.SetExecutionModeAsync(new ExecutionModeChangeRequest(
            "alpaca-paper",
            "paper-account-ui-test",
            ExecutionMode.Live,
            LiveExecutionConfirmation.RequiredAcknowledgement,
            keyId: "real-live-key",
            secretKey: "real-live-secret"));
        source.Publish(new SandboxPortfolioSnapshot(
            new InstrumentId(7101),
            2d,
            2d,
            100.25d,
            1,
            100_000d,
            0d,
            0d,
            0d,
            100_000d,
            0d,
            0,
            0,
            0,
            0,
            0,
            false,
            90d,
            110d));
        var outcome = await replication.Task.WaitAsync(TestTimeouts.Deadlock);

        Assert.False(modeChange.IsSuccess);
        Assert.Contains("authorization gate", modeChange.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(outcome.Result.IsSuccess);
        Assert.False(client.GetSnapshot().HasLiveExecution);
        Assert.Equal(ExecutionMode.Paper, Assert.Single(
            client.GetSnapshot().Adapters,
            item => item.Id == "alpaca-paper").Mode);
        Assert.Null(confirmations.Read(AlpacaExecutionOptions.BrokerId, "paper-account-ui-test"));
        Assert.Empty(harness.Transport.Submits);
        // No books: the console no longer seeds fabricated demo books, and a failed/unauthorized
        // adapter must not create one either.
        Assert.Empty(client.GetSnapshot().Books);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly FixedClock _clock;

        internal Harness(DateTime timestampUtc)
        {
            _clock = new FixedClock(timestampUtc);
            var options = Options();
            Transport = new MockTransport(AlpacaExecutionEndpointGate.Resolve(options), this);
            Source = new ManualTradeUpdateSource();
            Scheduler = new ControllableAdapterEventScheduler();
            Adapter = new AlpacaExecutionAdapter(options, Transport, Source, _clock, Scheduler);
        }

        internal DateTime LatestTradeTimestampUtc { get; set; } = TimestampUtc;

        internal MockTransport Transport { get; }

        internal ManualTradeUpdateSource Source { get; }

        internal ControllableAdapterEventScheduler Scheduler { get; }

        internal AlpacaExecutionAdapter Adapter { get; }

        public async ValueTask DisposeAsync() => await Adapter.DisposeAsync();

        private static AlpacaExecutionOptions Options() => new()
        {
            Enabled = true,
            BaseUrl = AlpacaExecutionOptions.PaperBaseUrl,
            MarketDataBaseUrl = AlpacaExecutionOptions.DataBaseUrl,
            Symbol = "AAPL",
            CanonicalInstrumentId = 7101,
            MaximumTrackedOrders = 32,
            PollIntervalMilliseconds = 100,
        };
    }

    private sealed class ManualPortfolioSource : IModelPortfolioSource
    {
        public IModelPortfolio? CurrentSnapshot { get; private set; }

        public event Action<IModelPortfolio>? SnapshotChanged;

        internal void Publish(IModelPortfolio snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }
    }

    private sealed class FixedClock(DateTime timestampUtc) : IClock
    {
        public DateTime UtcNow { get; } = timestampUtc;
    }

    private sealed class ManualTradeUpdateSource : IAlpacaTradeUpdateSource
    {
        public bool IsRunning { get; private set; }

        public event Action<AlpacaOrderSnapshot>? OrderUpdated;

        public event Action<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task StartAsync(IAlpacaExecutionTransport transport, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        internal void Publish(AlpacaOrderSnapshot order) => OrderUpdated?.Invoke(order);
    }

    private sealed class MockTransport(AlpacaExecutionEndpoint endpoint, Harness harness) : IAlpacaExecutionTransport
    {
        private bool _connected;

        public AlpacaExecutionEndpoint Endpoint { get; } = endpoint;

        public bool IsConnected => _connected;

        internal List<AlpacaSubmitRequest> Submits { get; } = [];

        internal List<string> CancelledOrderIds { get; } = [];

        internal Exception? AccountFailure { get; set; }

        public Task ConnectAsync(string keyId, string secretKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secretKey))
                throw new InvalidOperationException("The deterministic mock requires credentials.");
            _connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _connected = false;
            return Task.CompletedTask;
        }

        public Task<AlpacaAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            AccountFailure is not null
                ? Task.FromException<AlpacaAccountSnapshot>(AccountFailure)
                : Task.FromResult(new AlpacaAccountSnapshot(
                "paper-account-ui-test",
                "ACTIVE",
                "USD",
                new ScaledMoney(10_000_000, 2),
                new ScaledMoney(20_000_000, 2),
                false,
                false));

        public Task<AlpacaAssetSnapshot> GetAssetAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AlpacaAssetSnapshot(
                "AAPL",
                "us_equity",
                true,
                true,
                new ScaledQuantity(1, 3),
                new ScaledQuantity(1, 3),
                new ScaledPrice(1, 2)));

        public Task<AlpacaLatestTrade?> GetLatestTradeAsync(
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AlpacaLatestTrade?>(new AlpacaLatestTrade(
                new ScaledPrice(10_025, 2),
                harness.LatestTradeTimestampUtc));

        public Task<AlpacaOrderSnapshot> SubmitOrderAsync(
            AlpacaSubmitRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Submits.Add(request);
            return Task.FromResult(Order(request, "new", ScaledQuantity.Zero, null, TimestampUtc));
        }

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelledOrderIds.Add(orderId);
            var request = Assert.Single(Submits);
            harness.Source.Publish(Order(
                request,
                "canceled",
                ScaledQuantity.Zero,
                null,
                TimestampUtc.AddSeconds(1)));
            _ = harness.Scheduler.RunAll();
            return Task.CompletedTask;
        }

        public Task<AlpacaOrderSnapshot> ReplaceOrderAsync(
            string orderId,
            AlpacaReplaceRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AlpacaOrderSnapshot?> GetOrderByIdAsync(
            string orderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AlpacaOrderSnapshot?>(null);

        public Task<AlpacaOrderSnapshot?> GetOrderByClientIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AlpacaOrderSnapshot?>(null);

        public Task<IReadOnlyList<AlpacaOrderSnapshot>> GetOrdersAsync(
            AlpacaOrderStatusFilter status,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AlpacaOrderSnapshot> orders = Submits
                .Where(request => status == AlpacaOrderStatusFilter.Closed
                    ? CancelledOrderIds.Contains("paper-order-ui-001", StringComparer.Ordinal)
                    : !CancelledOrderIds.Contains("paper-order-ui-001", StringComparer.Ordinal))
                .Select(request => Order(
                    request,
                    status == AlpacaOrderStatusFilter.Closed ? "canceled" : "new",
                    ScaledQuantity.Zero,
                    null,
                    TimestampUtc))
                .ToArray();
            return Task.FromResult(orders);
        }

        public Task<IReadOnlyList<AlpacaPositionSnapshot>> GetPositionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlpacaPositionSnapshot>>(Array.Empty<AlpacaPositionSnapshot>());

        public ValueTask DisposeAsync()
        {
            _connected = false;
            return ValueTask.CompletedTask;
        }

        internal AlpacaOrderSnapshot Filled(AlpacaSubmitRequest request, DateTime updatedAtUtc) =>
            Order(request, "filled", request.Quantity, new ScaledPrice(10_025, 2), updatedAtUtc);

        private static AlpacaOrderSnapshot Order(
            AlpacaSubmitRequest request,
            string status,
            ScaledQuantity filledQuantity,
            ScaledPrice? filledAveragePrice,
            DateTime updatedAtUtc) => new(
                "paper-order-ui-001",
                request.ClientOrderId,
                request.Symbol,
                "us_equity",
                request.Side,
                request.OrderType,
                request.TimeInForce,
                status,
                request.Quantity,
                filledQuantity,
                filledAveragePrice,
                request.LimitPrice,
                request.StopPrice,
                updatedAtUtc);
    }
}
