using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class BacktestEngineLifecycleTests
{
    private static readonly InstrumentId Id = new(1);
    private static readonly Contract TestContract = Contract.UsStock("TEST");
    private static readonly DateTime Start = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static RunSpec Spec(DataSpec? data = null, ExecutionSpec? execution = null) => new(
        Universe.Single(new InstrumentSpec(Id, TestContract, TickSize: 0.01, ContractMultiplier: 1.0)),
        data ?? new DataSpec(),
        Execution: execution,
        StartingCash: 100_000d);

    private static MarketEvent Quote(int seconds, double bid = 100, double ask = 101) =>
        MarketEvent.OfQuote(Id, new Tick(Start.AddSeconds(seconds), bid, ask, 10, 10));

    [Fact]
    public async Task Reentrant_order_callback_is_awaited_before_next_market_callback()
    {
        var kernel = new ReentrantCallbackKernel();
        var engine = new BacktestEngine(new InMemoryMarketDataFeed(new[] { Quote(1) }));

        Func<Task> run = () => engine.RunAsync(Spec(), kernel);

        var assertion = await run.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("callback failed");
        kernel.Calls.Should().Equal("working", "cancelled");
        kernel.QuoteObserved.Should().BeFalse();
    }

    [Fact]
    public async Task OnEnd_market_close_fills_at_last_quote_and_delivers_callbacks()
    {
        var kernel = new EndCloseKernel();
        var events = new[]
        {
            Quote(1, bid: 99, ask: 101),
            Quote(2, bid: 100, ask: 102),
        };

        var report = await new BacktestEngine(new InMemoryMarketDataFeed(events)).RunAsync(Spec(), kernel);

        kernel.PositionWasOpenOnEnd.Should().BeTrue();
        kernel.PositionWasFlatInCloseFillCallback.Should().BeTrue();
        kernel.Events.Should().Equal(
            "entry:Working",
            "entry:Filled",
            "close:Working",
            "close:Filled");

        var trade = report.Trades.Should().ContainSingle().Which;
        trade.EntryPrice.Should().Be(102);
        trade.ExitPrice.Should().Be(100);
        trade.ExitUtc.Should().Be(Start.AddSeconds(2));
        report.Summary.EndingEquity.Should().Be(99_998d);
    }

    [Theory]
    [InlineData(ModelingMode.EveryTickFromBars, FillModelKind.L1Touch, 0d, "Modeling mode")]
    [InlineData(ModelingMode.BarClose, FillModelKind.L1Touch, 0d, "Modeling mode")]
    [InlineData(ModelingMode.BarOpen, FillModelKind.L1Touch, 0d, "Modeling mode")]
    [InlineData(ModelingMode.RealTicks, FillModelKind.MidPrice, 0d, "Fill model")]
    [InlineData(ModelingMode.RealTicks, FillModelKind.NextBarOpen, 0d, "Fill model")]
    [InlineData(ModelingMode.RealTicks, FillModelKind.L1Touch, 1d, "Execution latency")]
    public async Task Unsupported_run_options_are_rejected_before_start(
        ModelingMode modeling,
        FillModelKind fillModel,
        double latencyMs,
        string messagePrefix)
    {
        var spec = Spec(
            new DataSpec(Modeling: modeling),
            new ExecutionSpec(FillModel: fillModel, LatencyMs: latencyMs));
        var kernel = new TrackingDisposableKernel();
        var engine = new BacktestEngine(new InMemoryMarketDataFeed(Array.Empty<MarketEvent>()));

        Func<Task> run = () => engine.RunAsync(spec, kernel);

        var assertion = await run.Should().ThrowAsync<NotSupportedException>();
        assertion.Which.Message.Should().StartWith(messagePrefix);
        kernel.Started.Should().BeFalse();
        kernel.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_disposes_kernel_without_masking_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var kernel = new CancellingDisposableKernel(cts);
        var engine = new BacktestEngine(new InMemoryMarketDataFeed(new[] { Quote(1) }));

        Func<Task> run = () => engine.RunAsync(Spec(), kernel, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        kernel.Disposed.Should().BeTrue();
    }

    private sealed class ReentrantCallbackKernel : IStrategyKernel
    {
        public List<string> Calls { get; } = new();
        public bool QuoteObserved { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) =>
            ctx.Router.PlaceOrderAsync(new OrderRequest(
                "order", TestContract, OrderSide.Buy, OrderType.Limit, 1, LimitPrice: 1), ct);

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            QuoteObserved = true;
            return Task.CompletedTask;
        }

        public async Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct)
        {
            if (evt.State == OrderState.Working)
            {
                Calls.Add("working");
                await ctx.Router.CancelOrderAsync(evt.ClientOrderId, ct);
                return;
            }

            if (evt.State == OrderState.Cancelled)
            {
                Calls.Add("cancelled");
                throw new InvalidOperationException("callback failed");
            }
        }
    }

    private sealed class EndCloseKernel : IStrategyKernel
    {
        private bool _entrySubmitted;

        public List<string> Events { get; } = new();
        public bool PositionWasOpenOnEnd { get; private set; }
        public bool PositionWasFlatInCloseFillCallback { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            if (_entrySubmitted)
                return;

            _entrySubmitted = true;
            await ctx.Router.PlaceOrderAsync(
                new OrderRequest("entry", TestContract, OrderSide.Buy, OrderType.Market, 1), ct);
        }

        public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
        {
            PositionWasOpenOnEnd = ctx.Portfolio.PositionOf(Id).Quantity == 1;
            await ctx.Router.PlaceOrderAsync(
                new OrderRequest("close", TestContract, OrderSide.Sell, OrderType.Market, 1), ct);
        }

        public Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct)
        {
            Events.Add($"{evt.ClientOrderId}:{evt.State}");
            if (evt.ClientOrderId == "close" && evt.State == OrderState.Filled)
                PositionWasFlatInCloseFillCallback = ctx.Portfolio.PositionOf(Id).IsFlat;
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingDisposableKernel : IStrategyKernel, IAsyncDisposable
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingDisposableKernel : IStrategyKernel, IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public CancellingDisposableKernel(CancellationTokenSource cts) => _cts = cts;

        public bool Disposed { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
            throw new InvalidOperationException("dispose failed");
        }
    }
}
