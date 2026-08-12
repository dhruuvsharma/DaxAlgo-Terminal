using System.Runtime.CompilerServices;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;

namespace TradingTerminal.Execution.Tests;

public sealed class SignalBacktestRunnerTests
{
    private static readonly DateTime StartUtc = new(2026, 8, 4, 9, 15, 0, DateTimeKind.Utc);
    private static readonly InstrumentId Instrument = new(91);
    private static readonly Contract Contract = Contract.UsStock("SIGTEST");

    [Fact]
    public async Task SignalOnlyKernel_ProducesClosedTradeAndBacktestReport()
    {
        var policy = CreatePolicy(maximumUnits: 1);
        var decisions = new List<SignalExecutionDecision>();
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(0, bid: 99, ask: 100),
            Quote(1, bid: 100, ask: 101)));

        var report = await runner.RunAsync(
            Spec(),
            new FirstQuoteLongSignalKernel(),
            "signal-only.integration",
            policy,
            UnitDefinition.ConservativeDefault,
            decisions.Add);

        var trade = Assert.Single(report.Trades);
        Assert.Equal(Instrument, trade.Instrument);
        Assert.Equal(OrderSide.Buy, trade.Side);
        Assert.Equal(1, trade.Quantity);
        Assert.Equal(101d, trade.EntryPrice);
        Assert.Equal(100d, trade.ExitPrice);
        Assert.Equal(-1d, trade.GrossPnl);
        Assert.Equal(99_999d, report.Summary.EndingEquity);

        var rawSignal = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<StrategySignalEvent>>(report.Signals));
        Assert.Equal(StrategySignalKind.Long, rawSignal.Signal.Kind);
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision =>
        {
            Assert.True(decision.IsAccepted);
            Assert.Equal("signal-policy-v1", decision.Intent!.Value.PolicyVersion);
        });
        Assert.Equal(2, runner.LastRiskDecisions.Count);
        Assert.All(runner.LastRiskDecisions, decision => Assert.True(decision.IsAccepted));
    }

    [Fact]
    public async Task BuyerCapRejection_IsObservableAndSubmitsNoPartialTrade()
    {
        var policy = CreatePolicy(maximumUnits: 5);
        var decisions = new List<SignalExecutionDecision>();
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(0, bid: 99, ask: 100),
            Quote(1, bid: 100, ask: 101)));

        var report = await runner.RunAsync(
            Spec(),
            new FirstQuoteLongSignalKernel(),
            "signal-only.capped",
            policy,
            UnitDefinition.FixedContracts(10),
            decisions.Add);

        Assert.Empty(report.Trades);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<StrategySignalEvent>>(report.Signals));
        Assert.Equal(SignalExecutionFault.BuyerUnitCapExceeded, decisions[0].Fault);
        Assert.Equal(ScaledQuantity.FromWhole(10), decisions[0].CandidateTargetUnits);
        Assert.Null(decisions[0].Intent);
    }

    [Fact]
    public async Task WrappedSignalKernel_CannotBypassPolicyWithDirectOrder()
    {
        var kernel = new DirectOrderAttemptKernel();
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(0, bid: 99, ask: 100),
            Quote(1, bid: 100, ask: 101)));

        var report = await runner.RunAsync(
            Spec(),
            kernel,
            "signal-only.no-bypass",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault);

        Assert.Equal(OrderState.Rejected, kernel.Result?.State);
        Assert.Contains("emit StrategySignal", kernel.Result?.RejectReason, StringComparison.Ordinal);
        Assert.Empty(report.Trades);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<StrategySignalEvent>>(report.Signals));
    }

    [Fact]
    public async Task ProtectiveIntent_FailsBeforeAnyOrderBecauseFrozenRouterHasNoOcoContract()
    {
        var options = new SignalExecutionPolicyOptions(
            SignalCostAssumptions.Zero,
            new BuyerExecutionCaps(ScaledQuantity.FromWhole(20)),
            AttachSizingRiskAsProtectiveStop: true);
        var creation = SignalExecutionPolicy.TryCreate("signal-policy-v1", options, out var policy);
        Assert.Equal(SignalExecutionFault.None, creation);
        var runner = new SignalBacktestRunner(new ArrayFeed(Quote(0, bid: 99, ask: 100)));

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(
            Spec(),
            new FirstQuoteLongSignalKernel(),
            "signal-only.protected",
            Assert.IsType<SignalExecutionPolicy>(policy),
            UnitDefinition.FixedCashRisk(new ScaledMoney(10_000, 2), new ScaledPrice(500, 2))));

        Assert.Contains("bracket/OCO", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSignal_IsObservedAsFaultAndDoesNotAbortRun()
    {
        var decisions = new List<SignalExecutionDecision>();
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(0, bid: 99, ask: 100),
            Quote(1, bid: 100, ask: 101)));

        var report = await runner.RunAsync(
            Spec(),
            new InvalidSignalKernel(),
            "signal-only.invalid",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault,
            decisions.Add);

        Assert.Equal(SignalExecutionFault.InvalidSignal, decisions[0].Fault);
        Assert.Null(decisions[0].Intent);
        Assert.Empty(report.Trades);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<StrategySignalEvent>>(report.Signals));
    }

    [Fact]
    public async Task LaterSignal_CancelsUnfilledTargetAndConvergesToFlat()
    {
        var decisions = new List<SignalExecutionDecision>();
        var runner = new SignalBacktestRunner(new ArrayFeed(Quote(0, bid: 99, ask: 100)));

        var report = await runner.RunAsync(
            Spec(),
            new LongThenFlatSignalKernel(),
            "signal-only.replace",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault,
            decisions.Add);

        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<StrategySignalEvent>>(report.Signals).Count);
        Assert.Equal(ScaledQuantity.FromWhole(1), decisions[0].Intent!.Value.SignedUnits);
        Assert.Equal(ScaledQuantity.Zero, decisions[1].Intent!.Value.SignedUnits);
        Assert.Empty(report.Trades);
    }

    [Fact]
    public async Task DistinctSymbolPortfolio_ProducesTradePerCanonicalInstrument()
    {
        var secondInstrument = new InstrumentId(92);
        var secondContract = Contract.UsStock("SIGTWO");
        var universe = Universe.Of(
            new InstrumentSpec(Instrument, Contract, TickSize: 0.01, ContractMultiplier: 1),
            new InstrumentSpec(secondInstrument, secondContract, TickSize: 0.01, ContractMultiplier: 1));
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(Instrument, 0, bid: 99, ask: 100),
            Quote(Instrument, 1, bid: 100, ask: 101),
            Quote(secondInstrument, 2, bid: 199, ask: 200),
            Quote(secondInstrument, 3, bid: 200, ask: 201)));

        var report = await runner.RunAsync(
            Spec(universe),
            new FirstQuotePerInstrumentLongSignalKernel(),
            "signal-only.portfolio",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault);

        Assert.Equal(2, report.Trades.Count);
        Assert.Contains(report.Trades, trade => trade.Instrument == Instrument);
        Assert.Contains(report.Trades, trade => trade.Instrument == secondInstrument);
    }

    [Fact]
    public async Task DuplicateSymbols_AreRejectedBeforeAmbiguousRouting()
    {
        var duplicate = new Contract("sigtest", "STK", "OTHER", "USD", "OTHER");
        var universe = Universe.Of(
            new InstrumentSpec(Instrument, Contract),
            new InstrumentSpec(new InstrumentId(92), duplicate));
        var runner = new SignalBacktestRunner(new ArrayFeed());

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(
            Spec(universe),
            new FirstQuoteLongSignalKernel(),
            "signal-only.ambiguous",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault));

        Assert.Contains("unique contract symbols", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerDisposesWrappedKernel()
    {
        var kernel = new DisposableSignalKernel();
        var runner = new SignalBacktestRunner(new ArrayFeed());

        _ = await runner.RunAsync(
            Spec(),
            kernel,
            "signal-only.dispose",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault);

        Assert.True(kernel.IsDisposed);
    }

    [Fact]
    public async Task RetainedSignalProxy_IsRevokedAfterRun()
    {
        var kernel = new RetainedSignalSinkKernel();
        var runner = new SignalBacktestRunner(new ArrayFeed());
        var report = await runner.RunAsync(
            Spec(),
            kernel,
            "signal-only.revoke",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault);
        var signals = Assert.IsAssignableFrom<IReadOnlyList<StrategySignalEvent>>(report.Signals);
        Assert.Empty(signals);

        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.Sink!.EmitSignalAsync(
            new StrategySignal(StrategySignalKind.Long, 1d)));
        Assert.Empty(signals);
    }

    [Fact]
    public async Task DailyLossBreach_MidRunIsObservedAndReplayContinues()
    {
        var riskEngine = CreateRiskEngine(dailyLossLimit: 5);
        var riskDecisions = new List<RiskDecisionRecord>();
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(0, bid: 99, ask: 100),
            Quote(1, bid: 100, ask: 101),
            Quote(2, bid: 89, ask: 90),
            Quote(3, bid: 90, ask: 91)));

        var report = await runner.RunAsync(
            Spec(),
            new EnterExitThenReenterSignalKernel(),
            "signal-only.daily-loss",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault,
            riskEngine,
            observeRiskDecision: riskDecisions.Add);

        var trade = Assert.Single(report.Trades);
        Assert.Equal(101d, trade.EntryPrice);
        Assert.Equal(89d, trade.ExitPrice);
        Assert.Equal(-12d, trade.GrossPnl);
        Assert.Equal(4, report.Summary.EventsProcessed);
        Assert.Equal(99_988d, report.Summary.EndingEquity);
        Assert.Equal(4, riskDecisions.Count);
        Assert.Equal(RiskDecisionOutcome.Rejected, riskDecisions[2].Outcome);
        Assert.False(riskDecisions[2].IsAccepted);
        Assert.Equal(RiskReasonCode.DailyLossLimitExceeded, riskDecisions[2].ReasonCodes);
        Assert.Equal(803, riskDecisions[2].Input.Intent.StrategyNoteId);
        Assert.Equal(ScaledQuantity.FromWhole(1), riskDecisions[2].Input.Intent.SignedUnits);
        Assert.Equal(new ScaledMoney(-12, 0), riskDecisions[2].Input.DailyRealizedPnl);
        Assert.Equal(ScaledMoney.Zero, riskDecisions[2].Input.DailyMarkToMarketPnl);
        Assert.Equal(RiskReasonCode.DailyLossLimitExceeded, riskDecisions[3].ReasonCodes);
        Assert.Equal(riskDecisions, riskEngine.Decisions);
    }

    [Fact]
    public async Task TradeMarkToMarketLoss_IsRejectedBeforeALaterQuote()
    {
        var riskEngine = CreateRiskEngine(dailyLossLimit: 5);
        var riskDecisions = new List<RiskDecisionRecord>();
        var runner = new SignalBacktestRunner(new ArrayFeed(
            Quote(0, bid: 99, ask: 100),
            Quote(1, bid: 100, ask: 101),
            Trade(2, price: 90),
            Quote(3, bid: 90, ask: 91)));

        var report = await runner.RunAsync(
            Spec(),
            new EnterThenReverseOnTradeSignalKernel(),
            "signal-only.trade-mtm",
            CreatePolicy(maximumUnits: 1),
            UnitDefinition.ConservativeDefault,
            riskEngine,
            observeRiskDecision: riskDecisions.Add);

        Assert.Empty(report.Trades);
        Assert.Equal(4, report.Summary.EventsProcessed);
        Assert.Equal(3, riskDecisions.Count);
        Assert.True(riskDecisions[0].IsAccepted);
        Assert.Equal(RiskDecisionOutcome.Rejected, riskDecisions[1].Outcome);
        Assert.Equal(RiskReasonCode.DailyLossLimitExceeded, riskDecisions[1].ReasonCodes);
        Assert.Equal(ScaledMoney.Zero, riskDecisions[1].Input.DailyRealizedPnl);
        Assert.Equal(new ScaledMoney(-11, 0), riskDecisions[1].Input.DailyMarkToMarketPnl);
        Assert.Equal(804, riskDecisions[1].Input.Intent.StrategyNoteId);
        Assert.Equal(ScaledQuantity.FromWhole(-1), riskDecisions[1].Input.Intent.SignedUnits);
        Assert.Equal(99_989.5d, report.Summary.EndingEquity);
    }

    private static RunSpec Spec(Universe? universe = null) => new(
        universe ?? Universe.Single(new InstrumentSpec(Instrument, Contract, TickSize: 0.01, ContractMultiplier: 1)),
        new DataSpec(FromUtc: StartUtc, ToUtc: StartUtc.AddMinutes(1)),
        StartingCash: 100_000d);

    private static MarketEvent Quote(int second, double bid, double ask)
    {
        var tick = new Tick(StartUtc.AddSeconds(second), bid, ask, 10, 10);
        return MarketEvent.OfQuote(Instrument, tick);
    }

    private static MarketEvent Quote(InstrumentId instrument, int second, double bid, double ask)
    {
        var tick = new Tick(StartUtc.AddSeconds(second), bid, ask, 10, 10);
        return MarketEvent.OfQuote(instrument, tick);
    }

    private static MarketEvent Trade(int second, double price)
    {
        var timestamp = StartUtc.AddSeconds(second);
        var trade = new TradePrint(
            Instrument,
            timestamp,
            timestamp,
            price,
            1,
            AggressorSide.Sell,
            BrokerKind.Simulated,
            second,
            EventTimeApproximate: false);
        return MarketEvent.OfTrade(Instrument, trade);
    }

    private static SignalExecutionPolicy CreatePolicy(long maximumUnits)
    {
        var options = new SignalExecutionPolicyOptions(
            SignalCostAssumptions.Zero,
            new BuyerExecutionCaps(ScaledQuantity.FromWhole(maximumUnits)));
        var fault = SignalExecutionPolicy.TryCreate("signal-policy-v1", options, out var policy);
        Assert.Equal(SignalExecutionFault.None, fault);
        return Assert.IsType<SignalExecutionPolicy>(policy);
    }

    private static RiskEngine CreateRiskEngine(long dailyLossLimit)
    {
        var limits = new RiskLimits(
            ScaledQuantity.FromWhole(100),
            new ScaledMoney(1_000_000, 0),
            ScaledQuantity.FromWhole(100),
            new ScaledMoney(1_000_000, 0),
            new ScaledMoney(dailyLossLimit, 0));
        var fault = RiskPolicy.TryCreate("backtest-risk", "1", limits, out var policy);
        Assert.Equal(RiskPolicyFault.None, fault);
        return new RiskEngine(Assert.IsType<RiskPolicy>(policy));
    }

    private sealed class FirstQuoteLongSignalKernel : IStrategyKernel
    {
        private int _quotes;

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            _quotes++;
            return _quotes == 1
                ? ((IStrategySignalSink)ctx.Router).EmitSignalAsync(
                    new StrategySignal(StrategySignalKind.Long, 1d, 701), ct)
                : Task.CompletedTask;
        }
    }

    private sealed class DirectOrderAttemptKernel : IStrategyKernel
    {
        public OrderResult? Result { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task OnQuoteAsync(
            InstrumentId instrument,
            Tick quote,
            IStrategyContext ctx,
            CancellationToken ct)
        {
            if (Result is not null)
                return;
            Result = await ctx.Router.PlaceOrderAsync(
                new OrderRequest("BYPASS", Contract, OrderSide.Buy, OrderType.Market, 1),
                ct);
        }
    }

    private sealed class InvalidSignalKernel : IStrategyKernel
    {
        private bool _emitted;

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            if (_emitted)
                return Task.CompletedTask;
            _emitted = true;
            return ((IStrategySignalSink)ctx.Router).EmitSignalAsync(
                new StrategySignal((StrategySignalKind)99, 1d), ct);
        }
    }

    private sealed class LongThenFlatSignalKernel : IStrategyKernel
    {
        private bool _emitted;

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task OnQuoteAsync(
            InstrumentId instrument,
            Tick quote,
            IStrategyContext ctx,
            CancellationToken ct)
        {
            if (_emitted)
                return;
            _emitted = true;
            var sink = (IStrategySignalSink)ctx.Router;
            await sink.EmitSignalAsync(new StrategySignal(StrategySignalKind.Long, 1d), ct);
            await sink.EmitSignalAsync(new StrategySignal(StrategySignalKind.Flat, 1d), ct);
        }
    }

    private sealed class FirstQuotePerInstrumentLongSignalKernel : IStrategyKernel
    {
        private readonly HashSet<InstrumentId> _emitted = [];

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct) =>
            _emitted.Add(instrument)
                ? ((IStrategySignalSink)ctx.Router).EmitSignalAsync(
                    new StrategySignal(StrategySignalKind.Long, 1d), ct)
                : Task.CompletedTask;
    }

    private sealed class EnterExitThenReenterSignalKernel : IStrategyKernel
    {
        private int _quotes;

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            _quotes++;
            var signal = _quotes switch
            {
                1 => new StrategySignal(StrategySignalKind.Long, 1d, 801),
                2 => new StrategySignal(StrategySignalKind.Flat, 1d, 802),
                3 => new StrategySignal(StrategySignalKind.Long, 1d, 803),
                _ => (StrategySignal?)null,
            };
            return signal is { } value
                ? ((IStrategySignalSink)ctx.Router).EmitSignalAsync(value, ct)
                : Task.CompletedTask;
        }
    }

    private sealed class EnterThenReverseOnTradeSignalKernel : IStrategyKernel
    {
        private bool _entered;
        private bool _reversed;

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            if (_entered)
                return Task.CompletedTask;
            _entered = true;
            return ((IStrategySignalSink)ctx.Router).EmitSignalAsync(
                new StrategySignal(StrategySignalKind.Long, 1d, 804 - 1), ct);
        }

        public Task OnTradeAsync(
            InstrumentId instrument,
            TradePrint trade,
            IStrategyContext ctx,
            CancellationToken ct)
        {
            if (_reversed)
                return Task.CompletedTask;
            _reversed = true;
            return ((IStrategySignalSink)ctx.Router).EmitSignalAsync(
                new StrategySignal(StrategySignalKind.Short, 1d, 804), ct);
        }
    }

    private sealed class DisposableSignalKernel : IStrategyKernel, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RetainedSignalSinkKernel : IStrategyKernel
    {
        public IStrategySignalSink? Sink { get; private set; }

        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
        {
            Sink = (IStrategySignalSink)ctx.Router;
            return Task.CompletedTask;
        }
    }

    private sealed class ArrayFeed(params MarketEvent[] events) : IMarketDataFeed
    {
        public async IAsyncEnumerable<MarketEvent> StreamAsync(
            RunSpec spec,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _ = spec;
            foreach (var marketEvent in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return marketEvent;
            }
            await Task.CompletedTask;
        }
    }
}
