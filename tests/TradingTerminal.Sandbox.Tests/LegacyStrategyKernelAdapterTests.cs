using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using Xunit;

using TradingTerminal.Core.Strategies.Legacy;

namespace TradingTerminal.Sandbox.Tests;

public sealed class LegacyStrategyKernelAdapterTests
{
    private static readonly DateTime Epoch =
        new(2026, 8, 8, 9, 0, 0, DateTimeKind.Utc);

    private static readonly InstrumentId Instrument = new(713);
    private static readonly Contract LegacyContract = Contract.UsStock("LEGACY");

    [Fact]
    public async Task LegacyMarketOrdersBecomeNetTargetsAndSynchronousReferenceFills()
    {
        var strategy = new SequencedLegacyStrategy(LegacyContract);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, SequencedLegacyStrategy.Schema);
        await using var adapter = new LegacyStrategyKernelAdapter(strategy, Instrument);

        Assert.Same(SequencedLegacyStrategy.Schema, adapter.Schema);
        Assert.Equal(
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars,
            adapter.DataRequirement);

        await adapter.OnStartAsync(context, CancellationToken.None);
        for (var index = 1; index <= 6; index++)
        {
            clock.UtcNow = Epoch.AddMinutes(index);
            await adapter.OnBarAsync(Bar(index, close: 100d + index), context, CancellationToken.None);
        }

        Assert.Equal(new[] { 1d, -1d }, book.Targets.Select(target => target.TargetUnits));
        Assert.Equal(
            new[] { 103d, 106d },
            strategy.OrderEvents.Select(evt => evt.LastFillPrice.GetValueOrDefault()));
        Assert.Equal(new[] { OrderSide.Buy, OrderSide.Sell }, strategy.OrderEvents.Select(evt => evt.Side));
        Assert.Equal(new long[] { 1, 2 }, strategy.OrderEvents.Select(evt => evt.LastFillQuantity));
        Assert.All(strategy.OrderEvents, evt => Assert.Equal(OrderState.Filled, evt.State));
        Assert.All(strategy.CallbackObservedBeforeReturn, Assert.True);
        Assert.All(strategy.Results, result => Assert.Equal(OrderState.Filled, result.State));

        await adapter.OnStopAsync(context, CancellationToken.None);

        Assert.True(strategy.EndCalled);
        Assert.True(strategy.Disposed);
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task CanonicalQuoteAndBarMapExactlyToLegacyTickAndBar()
    {
        var strategy = new MappingLegacyStrategy();
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(strategy, Instrument);
        await adapter.OnStartAsync(context, CancellationToken.None);

        var eventTime = Epoch.AddSeconds(3);
        var quote = new Quote(
            Instrument,
            eventTime,
            eventTime.AddMilliseconds(7),
            Bid: 50.25d,
            Ask: 50.75d,
            BidSize: 11,
            AskSize: 13,
            BrokerKind.Simulated,
            Sequence: 42,
            EventTimeApproximate: false);
        var canonicalBar = Bar(4, close: 54.5d);

        await adapter.OnQuoteAsync(quote, context, CancellationToken.None);
        Assert.Equal(50.5d, strategy.Router!.ReferencePrice);
        await adapter.OnBarAsync(canonicalBar, context, CancellationToken.None);

        Assert.Equal(new Tick(eventTime, 50.25d, 50.75d, 11, 13), strategy.LastTick);
        Assert.Equal(
            new Bar(
                canonicalBar.OpenTimeUtc,
                canonicalBar.Open,
                canonicalBar.High,
                canonicalBar.Low,
                canonicalBar.Close,
                canonicalBar.Volume),
            strategy.LastBar);
        Assert.Equal(canonicalBar.Close, strategy.Router.ReferencePrice);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task TradeAndDepthForwardDirectlyAndDeclareTheirOptionalStreams()
    {
        var strategy = new MappingLegacyStrategy();
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(strategy, Instrument);
        await adapter.OnStartAsync(context, CancellationToken.None);

        Assert.Equal(
            StrategyDataRequirement.L1 |
            StrategyDataRequirement.Bars |
            StrategyDataRequirement.Depth |
            StrategyDataRequirement.TradeTape,
            adapter.DataRequirement);

        var trade = new TradePrint(
            Instrument,
            Epoch.AddSeconds(5),
            Epoch.AddSeconds(5).AddMilliseconds(1),
            Price: 77.25d,
            Size: 4,
            AggressorSide.Buy,
            BrokerKind.Simulated,
            Sequence: 9,
            EventTimeApproximate: false);
        var depth = new DepthSnapshot(
            Epoch.AddSeconds(6),
            Bids: [new DepthLevel(77d, 10)],
            Asks: [new DepthLevel(78d, 12)]);

        await adapter.OnTradeAsync(trade, context, CancellationToken.None);
        Assert.Same(trade, strategy.LastTrade);
        Assert.Equal(77.25d, strategy.Router!.ReferencePrice);

        await adapter.OnDepthAsync(Instrument, depth, context, CancellationToken.None);
        Assert.Same(depth, strategy.LastDepth);
        Assert.Equal(77.5d, strategy.Router.ReferencePrice);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task StopCallsLegacyEndAndAllowsItsFlattenOrderBeforeDisposal()
    {
        var strategy = new FlatteningLegacyStrategy(LegacyContract);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        await adapter.OnBarAsync(Bar(1, close: 125.5d), context, CancellationToken.None);
        await adapter.OnStopAsync(context, CancellationToken.None);

        Assert.True(strategy.EndCalled);
        Assert.True(strategy.Disposed);
        Assert.Equal(new[] { 1d, 0d }, book.Targets.Select(target => target.TargetUnits));
        Assert.Equal(
            new[] { 125.5d, 125.5d },
            strategy.OrderEvents.Select(evt => evt.LastFillPrice.GetValueOrDefault()));
    }

    [Fact]
    public async Task ConditionalLegacyOrderUsesImmediateFillAndMapsItsPriceFields()
    {
        var strategy = new ConditionalOrderLegacyStrategy(LegacyContract);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        var exception = await Record.ExceptionAsync(
            () => adapter.OnBarAsync(Bar(1, close: 100d), context, CancellationToken.None));

        Assert.Null(exception);
        Assert.NotNull(strategy.Result);
        Assert.Equal(OrderState.Filled, strategy.Result.State);
        var fill = Assert.Single(strategy.OrderEvents);
        Assert.Equal(OrderState.Filled, fill.State);
        Assert.Equal(100d, fill.LastFillPrice.GetValueOrDefault());
        var target = Assert.Single(book.Targets);
        Assert.Equal(1d, target.TargetUnits);
        Assert.Equal(95d, target.ProtectiveStopPrice.GetValueOrDefault());
        Assert.Equal(110d, target.ProfitTargetPrice.GetValueOrDefault());
        Assert.Empty(alerts.Alerts);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task NonExactLegacyQuantityIsRejectedWithReasonWithoutThrowing()
    {
        var strategy = new SingleMarketOrderLegacyStrategy(
            LegacyContract,
            quantity: PositionTrackingOrderRouter.MaxExactTargetUnits + 1);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        var exception = await Record.ExceptionAsync(
            () => adapter.OnBarAsync(Bar(1, close: 100d), context, CancellationToken.None));

        Assert.Null(exception);
        Assert.NotNull(strategy.Result);
        Assert.Equal(OrderState.Rejected, strategy.Result.State);
        Assert.Contains("exact integer range", strategy.Result.RejectReason);
        Assert.Equal(OrderState.Rejected, Assert.Single(strategy.OrderEvents).State);
        Assert.Empty(book.Targets);
        Assert.Contains(alerts.Alerts, alert => alert.Level == AlertLevel.Warning);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task CancelRemovesTheOrdersNetContributionAndProtectionIntent()
    {
        var strategy = new CancellingLegacyStrategy(LegacyContract);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        await adapter.OnBarAsync(Bar(1, close: 100d), context, CancellationToken.None);

        Assert.Equal(new[] { 2d, 0d }, book.Targets.Select(target => target.TargetUnits));
        Assert.Equal(95d, book.Targets[0].ProtectiveStopPrice.GetValueOrDefault());
        Assert.Equal(110d, book.Targets[0].ProfitTargetPrice.GetValueOrDefault());
        Assert.Null(book.Targets[1].ProtectiveStopPrice);
        Assert.Null(book.Targets[1].ProfitTargetPrice);
        Assert.Equal(
            new[] { OrderState.Filled, OrderState.Cancelled },
            strategy.OrderEvents.Select(evt => evt.State));
        Assert.Equal(0, strategy.Router!.NetPosition);
        Assert.Empty(alerts.Alerts);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task ReentrantFillCallbacksPreserveEveryDeclarativeTargetTransition()
    {
        var strategy = new ReentrantLegacyStrategy(LegacyContract);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        await adapter.OnBarAsync(Bar(1, close: 150d), context, CancellationToken.None);

        Assert.Equal(new[] { 1d, 0d }, book.Targets.Select(target => target.TargetUnits));
        Assert.Equal(
            new[] { "reentrant-entry", "reentrant-offset" },
            strategy.OrderEvents.Select(evt => evt.ClientOrderId));
        Assert.All(strategy.OrderEvents, evt => Assert.Equal(OrderState.Filled, evt.State));

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task VirtualBookRejectionBecomesARejectedOrderInsteadOfEscaping()
    {
        var strategy = new SingleMarketOrderLegacyStrategy(LegacyContract);
        var book = new RejectingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        var exception = await Record.ExceptionAsync(
            () => adapter.OnBarAsync(Bar(1, close: 200d), context, CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(1, book.Attempts);
        Assert.NotNull(strategy.Result);
        Assert.Equal(OrderState.Rejected, strategy.Result.State);
        Assert.Contains("virtual book rejected", strategy.Result.RejectReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderState.Rejected, Assert.Single(strategy.OrderEvents).State);
        Assert.Equal(0, strategy.Router!.NetPosition);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task InvalidCurrentReferenceRejectsRatherThanFillingAtAnEarlierPrice()
    {
        var strategy = new SingleMarketOrderLegacyStrategy(LegacyContract, submitOnBar: 2);
        var book = new RecordingVirtualBook();
        var clock = new LegacyTestClock(Epoch);
        var alerts = new RecordingAlertSink();
        var context = CreateContext(book, clock, alerts, StrategyParameterSchema.Empty);
        await using var adapter = new LegacyStrategyKernelAdapter(
            strategy,
            Instrument,
            StrategyParameterSchema.Empty);

        await adapter.OnStartAsync(context, CancellationToken.None);
        await adapter.OnBarAsync(Bar(1, close: 99d), context, CancellationToken.None);
        await adapter.OnBarAsync(Bar(2, close: double.NaN), context, CancellationToken.None);

        Assert.NotNull(strategy.Result);
        Assert.Equal(OrderState.Rejected, strategy.Result.State);
        Assert.Contains("No valid market reference price", strategy.Result.RejectReason);
        Assert.Null(strategy.Router!.ReferencePrice);
        Assert.Empty(book.Targets);

        await adapter.OnStopAsync(context, CancellationToken.None);
    }

    private static LegacyTestContext CreateContext(
        IVirtualBook book,
        LegacyTestClock clock,
        RecordingAlertSink alerts,
        StrategyParameterSchema schema) =>
        new(
            new LegacyMarketDataView(Instrument),
            clock,
            new SandboxParameters(schema),
            book,
            alerts);

    private static OhlcvBar Bar(int minute, double close) =>
        new(
            Instrument,
            BarSize.OneMinute,
            Epoch.AddMinutes(minute),
            Open: close - 1d,
            High: close + 2d,
            Low: close - 2d,
            Close: close,
            Volume: 1000 + minute,
            BrokerKind.Simulated,
            IsFinal: true);

    private sealed class SequencedLegacyStrategy(Contract contract) : IOrderRoutedStrategy, IDisposable
    {
        public static StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Int("size", "Size", @default: 1, min: 1, max: 10));

        private int _barCount;
        private int _sequence;

        public List<OrderEvent> OrderEvents { get; } = [];
        public List<OrderResult> Results { get; } = [];
        public List<bool> CallbackObservedBeforeReturn { get; } = [];
        public bool EndCalled { get; private set; }
        public bool Disposed { get; private set; }

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task OnBarAsync(
            Bar bar,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            _barCount++;
            var order = _barCount switch
            {
                3 => new OrderRequest(
                    $"sequence-{++_sequence}",
                    contract,
                    OrderSide.Buy,
                    OrderType.Market,
                    Quantity: 1),
                6 => new OrderRequest(
                    $"sequence-{++_sequence}",
                    contract,
                    OrderSide.Sell,
                    OrderType.Market,
                    Quantity: 2),
                _ => null,
            };

            if (order is null)
                return;

            var callbacksBefore = OrderEvents.Count;
            Results.Add(await router.PlaceOrderAsync(order, ct));
            CallbackObservedBeforeReturn.Add(OrderEvents.Count == callbacksBefore + 1);
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
        {
            OrderEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            EndCalled = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class MappingLegacyStrategy : IOrderRoutedStrategy
    {
        public PositionTrackingOrderRouter? Router { get; private set; }
        public Tick? LastTick { get; private set; }
        public Bar? LastBar { get; private set; }
        public TradePrint? LastTrade { get; private set; }
        public DepthSnapshot? LastDepth { get; private set; }

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            Router = Assert.IsType<PositionTrackingOrderRouter>(router);
            return Task.CompletedTask;
        }

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
        {
            LastTick = tick;
            return Task.CompletedTask;
        }

        public Task OnBarAsync(Bar bar, IClock clock, IOrderRouter router, CancellationToken ct)
        {
            LastBar = bar;
            return Task.CompletedTask;
        }

        public Task OnTradeAsync(
            TradePrint trade,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            LastTrade = trade;
            return Task.CompletedTask;
        }

        public Task OnDepthAsync(
            DepthSnapshot depth,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            LastDepth = depth;
            return Task.CompletedTask;
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FlatteningLegacyStrategy(Contract contract) : IOrderRoutedStrategy, IDisposable
    {
        private bool _entered;

        public List<OrderEvent> OrderEvents { get; } = [];
        public bool EndCalled { get; private set; }
        public bool Disposed { get; private set; }

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task OnBarAsync(
            Bar bar,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            if (_entered)
                return;

            _entered = true;
            await router.PlaceOrderAsync(
                new OrderRequest("flatten-entry", contract, OrderSide.Buy, OrderType.Market, 1),
                ct);
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
        {
            OrderEvents.Add(evt);
            return Task.CompletedTask;
        }

        public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            EndCalled = true;
            await router.PlaceOrderAsync(
                new OrderRequest("flatten-exit", contract, OrderSide.Sell, OrderType.Market, 1),
                ct);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ConditionalOrderLegacyStrategy(Contract contract) : IOrderRoutedStrategy
    {
        public OrderResult? Result { get; private set; }
        public List<OrderEvent> OrderEvents { get; } = [];

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task OnBarAsync(
            Bar bar,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            Result = await router.PlaceOrderAsync(
                new OrderRequest(
                    "conditional-stop-limit",
                    contract,
                    OrderSide.Buy,
                    OrderType.StopLimit,
                    Quantity: 1,
                    LimitPrice: 110d,
                    StopPrice: 95d),
                ct);
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
        {
            OrderEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class CancellingLegacyStrategy(Contract contract) : IOrderRoutedStrategy
    {
        public PositionTrackingOrderRouter? Router { get; private set; }
        public List<OrderEvent> OrderEvents { get; } = [];

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            Router = Assert.IsType<PositionTrackingOrderRouter>(router);
            return Task.CompletedTask;
        }

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task OnBarAsync(
            Bar bar,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            await router.PlaceOrderAsync(
                new OrderRequest(
                    "cancel-target",
                    contract,
                    OrderSide.Buy,
                    OrderType.Market,
                    Quantity: 2,
                    LimitPrice: 110d,
                    StopPrice: 95d),
                ct);
            await router.CancelOrderAsync("cancel-target", ct);
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
        {
            OrderEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ReentrantLegacyStrategy(Contract contract) : IOrderRoutedStrategy
    {
        private IOrderRouter? _router;
        private bool _offsetSubmitted;

        public List<OrderEvent> OrderEvents { get; } = [];

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            _router = router;
            return Task.CompletedTask;
        }

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task OnBarAsync(
            Bar bar,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            await router.PlaceOrderAsync(
                new OrderRequest("reentrant-entry", contract, OrderSide.Buy, OrderType.Market, 1),
                ct);
        }

        public async Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
        {
            OrderEvents.Add(evt);
            if (_offsetSubmitted || evt.State != OrderState.Filled)
                return;

            _offsetSubmitted = true;
            await _router!.PlaceOrderAsync(
                new OrderRequest("reentrant-offset", contract, OrderSide.Sell, OrderType.Market, 1),
                ct);
        }

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class SingleMarketOrderLegacyStrategy(
        Contract contract,
        int submitOnBar = 1,
        long quantity = 1) : IOrderRoutedStrategy
    {
        private int _barCount;

        public PositionTrackingOrderRouter? Router { get; private set; }
        public OrderResult? Result { get; private set; }
        public List<OrderEvent> OrderEvents { get; } = [];

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
        {
            Router = Assert.IsType<PositionTrackingOrderRouter>(router);
            return Task.CompletedTask;
        }

        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task OnBarAsync(
            Bar bar,
            IClock clock,
            IOrderRouter router,
            CancellationToken ct)
        {
            if (++_barCount != submitOnBar)
                return;

            Result = await router.PlaceOrderAsync(
                new OrderRequest("single-market", contract, OrderSide.Buy, OrderType.Market, quantity),
                ct);
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
        {
            OrderEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingVirtualBook : IVirtualBook
    {
        public List<VirtualTargetIntent> Targets { get; } = [];

        public void SubmitTarget(VirtualTargetIntent intent) => Targets.Add(intent);
    }

    private sealed class RejectingVirtualBook : IVirtualBook
    {
        public int Attempts { get; private set; }

        public void SubmitTarget(VirtualTargetIntent intent)
        {
            Attempts++;
            throw new InvalidOperationException("Rejected by test book.");
        }
    }

    private sealed class RecordingAlertSink : IAlertSink
    {
        public List<RecordedAlert> Alerts { get; } = [];

        public void Alert(string message, AlertLevel level, string? dedupeKey = null) =>
            Alerts.Add(new RecordedAlert(message, level, dedupeKey));
    }

    private sealed record RecordedAlert(string Message, AlertLevel Level, string? DedupeKey);

    private sealed class LegacyTestClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }

    private sealed class LegacyTestContext(
        IMarketDataView data,
        IClock clock,
        IParameters parameters,
        IVirtualBook book,
        IAlertSink alerts) : IStrategyRuntimeContext
    {
        public IMarketDataView Data { get; } = data;
        public IClock Clock { get; } = clock;
        public IParameters Parameters { get; } = parameters;
        public IVirtualBook Book { get; } = book;
        public IAlertSink Alerts { get; } = alerts;
    }

    private sealed class LegacyMarketDataView(InstrumentId instrument) : IMarketDataView
    {
        public IReadOnlySet<InstrumentId> Instruments { get; } =
            new HashSet<InstrumentId> { instrument };

        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 |
            StrategyDataRequirement.Bars |
            StrategyDataRequirement.Depth |
            StrategyDataRequirement.TradeTape;

        public IReadOnlyList<OhlcvBar> RecentBars(
            InstrumentId instrument,
            BarSize size,
            int maxCount) => [];

        public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount) => [];

        public DepthSnapshot? LatestDepth(InstrumentId instrument) => null;

        public IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int maxCount) => [];
    }
}
