using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Execution;

/// <summary>
/// Backtest-only composition root for the first unified-execution increment. The execution kernel is
/// private to this runner and can only be installed into the existing <see cref="BacktestEngine"/>;
/// no live router, broker adapter, or OMS capability is referenced.
/// </summary>
public sealed class SignalBacktestRunner
{
    private readonly IMarketDataFeed _feed;
    private RiskEngine? _lastRiskEngine;

    /// <summary>Creates a runner over the caller's deterministic historical feed.</summary>
    public SignalBacktestRunner(IMarketDataFeed feed) =>
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));

    /// <summary>
    /// Gets the append-only risk records from the most recently started run. This keeps rejections
    /// observable for the source-compatible overload that creates its own per-run risk engine.
    /// </summary>
    public IReadOnlyList<RiskDecisionRecord> LastRiskDecisions =>
        _lastRiskEngine?.Decisions ?? Array.Empty<RiskDecisionRecord>();

    /// <summary>
    /// Runs a signal-only kernel through the exact policy and the existing simulated order book.
    /// <paramref name="observeDecision"/> receives every accepted or rejected policy result, including
    /// buyer-cap rejections that intentionally submit no order.
    /// </summary>
    public Task<BacktestReport> RunAsync(
        RunSpec spec,
        IStrategyKernel signalKernel,
        string strategyId,
        SignalExecutionPolicy policy,
        UnitDefinition unitDefinition,
        Action<SignalExecutionDecision>? observeDecision = null,
        CancellationToken ct = default)
    {
        var riskEngine = CreateDefaultRiskEngine();
        return RunCoreAsync(
            spec,
            signalKernel,
            strategyId,
            policy,
            unitDefinition,
            riskEngine,
            observeDecision,
            observeRiskDecision: null,
            ct);
    }

    /// <summary>
    /// Runs a signal-only kernel through sizing policy, the supplied versioned ADR D6 pre-trade risk
    /// engine, and only then the simulated order book. Every risk result is retained by
    /// <paramref name="riskEngine"/> and optionally copied to <paramref name="observeRiskDecision"/>.
    /// </summary>
    public Task<BacktestReport> RunAsync(
        RunSpec spec,
        IStrategyKernel signalKernel,
        string strategyId,
        SignalExecutionPolicy policy,
        UnitDefinition unitDefinition,
        RiskEngine riskEngine,
        Action<SignalExecutionDecision>? observeDecision = null,
        Action<RiskDecisionRecord>? observeRiskDecision = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(riskEngine);
        return RunCoreAsync(
            spec,
            signalKernel,
            strategyId,
            policy,
            unitDefinition,
            riskEngine,
            observeDecision,
            observeRiskDecision,
            ct);
    }

    private Task<BacktestReport> RunCoreAsync(
        RunSpec spec,
        IStrategyKernel signalKernel,
        string strategyId,
        SignalExecutionPolicy policy,
        UnitDefinition unitDefinition,
        RiskEngine riskEngine,
        Action<SignalExecutionDecision>? observeDecision,
        Action<RiskDecisionRecord>? observeRiskDecision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(signalKernel);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(policy);
        _lastRiskEngine = riskEngine;

        var kernel = new BacktestSignalExecutionKernel(
            signalKernel,
            strategyId,
            policy,
            unitDefinition,
            riskEngine,
            observeDecision,
            observeRiskDecision);
        return new BacktestEngine(_feed).RunAsync(spec, kernel, ct);
    }

    private static RiskEngine CreateDefaultRiskEngine()
    {
        var limits = new RiskLimits(
            ScaledQuantity.FromWhole(long.MaxValue),
            new ScaledMoney(long.MaxValue, 0),
            ScaledQuantity.FromWhole(long.MaxValue),
            new ScaledMoney(long.MaxValue, 0),
            new ScaledMoney(long.MaxValue, 0));
        var fault = RiskPolicy.TryCreate("backtest-default", "1", limits, out var policy);
        if (fault != RiskPolicyFault.None || policy is null)
            throw new InvalidOperationException($"The built-in backtest risk policy is invalid ({fault}).");
        return new RiskEngine(policy);
    }
}

/// <summary>
/// Internal backtest adapter: consumes <see cref="IStrategySignalSink"/> emissions, applies the
/// buyer policy, computes target-to-actual deltas, and lowers accepted integral targets through the
/// public <see cref="IOrderRouter"/> already backed by <c>SimulatedOrderBook</c>.
/// </summary>
internal sealed class BacktestSignalExecutionKernel : IStrategyKernel, IAsyncDisposable
{
    private const byte BoundaryScale = 6;
    private const string ProtectiveOrdersUnsupported =
        "Protective stop/target intent requires linked bracket/OCO semantics that the frozen public IOrderRouter does not provide.";

    private readonly IStrategyKernel _signalKernel;
    private readonly string _strategyId;
    private readonly SignalExecutionPolicy _policy;
    private readonly UnitDefinition _unitDefinition;
    private readonly RiskEngine _riskEngine;
    private readonly Action<SignalExecutionDecision>? _observeDecision;
    private readonly Action<RiskDecisionRecord>? _observeRiskDecision;
    private readonly SignalCaptureRouter _captureRouter;
    private readonly Dictionary<InstrumentId, ScaledPrice> _lastReferencePrices = [];
    private readonly Dictionary<InstrumentId, PendingOrder> _pendingByInstrument = [];
    private readonly Dictionary<string, InstrumentId> _pendingByClientId = new(StringComparer.Ordinal);
    private IStrategyContext? _venueContext;
    private SignalKernelContext? _signalContext;
    private InstrumentId _activeInstrument;
    private ScaledPrice _activeReferencePrice;
    private bool _hasActiveReferencePrice;
    private ScaledMoney _runStartingEquity;
    private ScaledMoney _runStartingMarkToMarket;
    private ScaledMoney _dailyOpeningEquity;
    private ScaledMoney _dailyOpeningMarkToMarket;
    private ScaledMoney _lastObservedEquity;
    private ScaledMoney _lastObservedMarkToMarket;
    private DateOnly _riskDay;
    private bool _hasRunRiskBaseline;
    private bool _hasRiskDay;
    private bool _hasDailyOpeningRiskBaseline;
    private bool _hasLastRiskObservation;
    private long _nextOrderSequence;
    private int _disposed;

    internal BacktestSignalExecutionKernel(
        IStrategyKernel signalKernel,
        string strategyId,
        SignalExecutionPolicy policy,
        UnitDefinition unitDefinition,
        RiskEngine riskEngine,
        Action<SignalExecutionDecision>? observeDecision,
        Action<RiskDecisionRecord>? observeRiskDecision)
    {
        _signalKernel = signalKernel;
        _strategyId = strategyId;
        _policy = policy;
        _unitDefinition = unitDefinition;
        _riskEngine = riskEngine;
        _observeDecision = observeDecision;
        _observeRiskDecision = observeRiskDecision;
        _captureRouter = new SignalCaptureRouter(ApplySignalAsync);
    }

    public async Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
    {
        ThrowIfDisposed();
        ValidateRouterAddressableUniverse(ctx.Universe);
        _venueContext = ctx;
        _captureRouter.Bind(ctx.Router);
        _signalContext = new SignalKernelContext(ctx, _captureRouter);
        _activeInstrument = ctx.Universe.Primary.Id;
        _hasActiveReferencePrice = false;
        _hasRunRiskBaseline = TryPortfolioRiskValues(
            ctx,
            out _runStartingEquity,
            out _runStartingMarkToMarket);
        _lastObservedEquity = _runStartingEquity;
        _lastObservedMarkToMarket = _runStartingMarkToMarket;
        _hasLastRiskObservation = _hasRunRiskBaseline;
        _hasRiskDay = false;
        _hasDailyOpeningRiskBaseline = false;

        _lastReferencePrices.EnsureCapacity(ctx.Universe.Instruments.Count);
        _pendingByInstrument.EnsureCapacity(ctx.Universe.Instruments.Count);
        _pendingByClientId.EnsureCapacity(ctx.Universe.Instruments.Count);
        for (var index = 0; index < ctx.Universe.Instruments.Count; index++)
            _lastReferencePrices[ctx.Universe.Instruments[index].Id] = default;

        await _signalKernel.OnStartAsync(_signalContext, ct).ConfigureAwait(false);
    }

    public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
    {
        PrepareMarketCallback(quote.TimestampUtc, instrument, TryMidpoint(quote.Bid, quote.Ask, out var price), price);
        return _signalKernel.OnQuoteAsync(instrument, quote, RequireSignalContext(), ct);
    }

    public Task OnTradeAsync(InstrumentId instrument, TradePrint trade, IStrategyContext ctx, CancellationToken ct)
    {
        PrepareMarketCallback(trade.EventTimeUtc, instrument, TryPrice(trade.Price, out var price), price);
        return _signalKernel.OnTradeAsync(instrument, trade, RequireSignalContext(), ct);
    }

    public Task OnDepthAsync(InstrumentId instrument, DepthSnapshot depth, IStrategyContext ctx, CancellationToken ct)
    {
        var hasPrice = depth.BestBid > 0d && depth.BestAsk > 0d
            ? TryMidpoint(depth.BestBid, depth.BestAsk, out var price)
            : TryPrice(depth.BestBid > 0d ? depth.BestBid : depth.BestAsk, out price);
        PrepareMarketCallback(depth.TimestampUtc, instrument, hasPrice, price);
        return _signalKernel.OnDepthAsync(instrument, depth, RequireSignalContext(), ct);
    }

    public Task OnBarAsync(InstrumentId instrument, OhlcvBar bar, IStrategyContext ctx, CancellationToken ct)
    {
        PrepareMarketCallback(bar.OpenTimeUtc, instrument, TryPrice(bar.Close, out var price), price);
        return _signalKernel.OnBarAsync(instrument, bar, RequireSignalContext(), ct);
    }

    public Task OnOrderEventAsync(OrderEvent evt, IStrategyContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (evt.State is not (OrderState.Filled or OrderState.Cancelled or OrderState.Rejected))
            return Task.CompletedTask;
        if (!_pendingByClientId.Remove(evt.ClientOrderId, out var instrument))
            return Task.CompletedTask;
        if (_pendingByInstrument.TryGetValue(instrument, out var pending) &&
            string.Equals(pending.ClientOrderId, evt.ClientOrderId, StringComparison.Ordinal))
        {
            _pendingByInstrument.Remove(instrument);
        }
        return Task.CompletedTask;
    }

    public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
    {
        ThrowIfDisposed();
        _activeInstrument = ctx.Universe.Primary.Id;
        _hasActiveReferencePrice = _lastReferencePrices.TryGetValue(_activeInstrument, out _activeReferencePrice) &&
            _activeReferencePrice.Coefficient > 0;
        await _signalKernel.OnEndAsync(RequireSignalContext(), ct).ConfigureAwait(false);

        // Host-owned end-of-run liquidation makes every accepted open target observable in the
        // completed BacktestReport, matching the model portfolio's complete-run semantics.
        for (var index = 0; index < ctx.Universe.Instruments.Count; index++)
        {
            var instrument = ctx.Universe.Instruments[index].Id;
            if (_pendingByInstrument.TryGetValue(instrument, out var pending) && pending.TargetUnits == 0)
                continue;

            _activeInstrument = instrument;
            _hasActiveReferencePrice = _lastReferencePrices.TryGetValue(instrument, out _activeReferencePrice) &&
                _activeReferencePrice.Coefficient > 0;
            var flat = new StrategySignal(StrategySignalKind.Flat, 1d);
            await ApplySignalAsync(flat, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _captureRouter.Unbind();
        _venueContext = null;
        _signalContext = null;
        if (_signalKernel is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_signalKernel is IDisposable disposable)
            disposable.Dispose();
    }

    private void PrepareMarketCallback(DateTime timestampUtc, InstrumentId instrument, bool hasPrice, ScaledPrice price)
    {
        ThrowIfDisposed();
        _activeInstrument = instrument;
        _activeReferencePrice = price;
        _hasActiveReferencePrice = hasPrice;
        if (hasPrice)
            _lastReferencePrices[instrument] = price;
        ObserveRiskDay(timestampUtc, RequireVenueContext());
    }

    private async Task ApplySignalAsync(StrategySignal signal, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var ctx = RequireVenueContext();
        var instrumentSpec = ctx.Universe.Find(_activeInstrument);
        var hasEquity = ScaledValueMath.TryQuantizeDouble(ctx.Portfolio.Equity, BoundaryScale, out var equity);
        long multiplier = 0;
        var hasMultiplier = instrumentSpec is not null &&
            ScaledValueMath.TryQuantizeDouble(instrumentSpec.ContractMultiplier, BoundaryScale, out multiplier);

        var inputs = new SignalExecutionInputs(
            _activeInstrument,
            _hasActiveReferencePrice ? _activeReferencePrice : default,
            hasEquity ? new ScaledMoney(equity, BoundaryScale) : default,
            ScaledQuantity.FromWhole(ctx.Portfolio.PositionOf(_activeInstrument).Quantity),
            hasMultiplier ? new ScaledRatio(multiplier, BoundaryScale) : default);
        var decision = _policy.Evaluate(_strategyId, signal, _unitDefinition, inputs);
        _observeDecision?.Invoke(decision);
        if (!decision.IsAccepted)
            return;

        var intent = decision.Intent!.Value;
        if (intent.ProtectiveStopPrice.HasValue || intent.ProfitTargetPrice.HasValue)
            throw new NotSupportedException(ProtectiveOrdersUnsupported);
        if (intent.QuantityMode != TradeIntentQuantityMode.TargetPosition ||
            !intent.SignedUnits.TryGetWholeUnits(out var targetUnits))
        {
            throw new InvalidOperationException("The backtest adapter accepts only integral target-position intents.");
        }
        if (instrumentSpec is null)
            throw new InvalidOperationException($"Intent instrument {intent.Instrument} is outside the run universe.");

        var riskInput = BuildRiskInput(ctx, intent, instrumentSpec);
        var riskDecision = _riskEngine.Evaluate(riskInput);
        _observeRiskDecision?.Invoke(riskDecision);
        if (!riskDecision.IsAccepted)
            return;

        if (_pendingByInstrument.TryGetValue(intent.Instrument, out var previous))
        {
            if (previous.TargetUnits == targetUnits)
                return;
            await ctx.Router.CancelOrderAsync(previous.ClientOrderId, ct).ConfigureAwait(false);
            _pendingByInstrument.Remove(intent.Instrument);
            _pendingByClientId.Remove(previous.ClientOrderId);
        }

        var actualUnits = ctx.Portfolio.PositionOf(intent.Instrument).Quantity;
        var delta = (Int128)targetUnits - actualUnits;
        if (delta == 0)
            return;
        if (delta < long.MinValue || delta > long.MaxValue)
            throw new OverflowException("Target-to-actual order delta exceeds the public router quantity range.");

        var deltaUnits = (long)delta;
        var quantity = deltaUnits < 0 ? -deltaUnits : deltaUnits;
        if (quantity <= 0)
            throw new OverflowException("Order quantity cannot be represented as a positive Int64 value.");

        var clientOrderId = $"SIG-{intent.Instrument.Value}-{++_nextOrderSequence:D12}";
        var pending = new PendingOrder(clientOrderId, targetUnits);
        _pendingByInstrument[intent.Instrument] = pending;
        _pendingByClientId[clientOrderId] = intent.Instrument;

        var result = await ctx.Router.PlaceOrderAsync(
            new OrderRequest(
                clientOrderId,
                instrumentSpec.Contract,
                deltaUnits > 0 ? OrderSide.Buy : OrderSide.Sell,
                OrderType.Market,
                quantity),
            ct).ConfigureAwait(false);
        if (result.State == OrderState.Rejected)
        {
            _pendingByInstrument.Remove(intent.Instrument);
            _pendingByClientId.Remove(clientOrderId);
        }
    }

    private RiskInputSnapshot BuildRiskInput(
        IStrategyContext ctx,
        in TradeIntent intent,
        InstrumentSpec instrumentSpec)
    {
        var position = ctx.Portfolio.PositionOf(intent.Instrument);
        var hasMultiplier = ScaledValueMath.TryQuantizeDouble(
            instrumentSpec.ContractMultiplier,
            BoundaryScale,
            out var multiplierCoefficient);
        var multiplier = hasMultiplier
            ? new ScaledRatio(multiplierCoefficient, BoundaryScale)
            : default;
        var hasGrossExposure = TryGrossExposure(ctx, out var grossExposure);
        var hasDailyPnl = TryDailyPnl(ctx, out var realizedPnl, out var markToMarketPnl);
        var complete = _hasActiveReferencePrice &&
                       hasMultiplier && multiplier.Coefficient > 0 &&
                       hasGrossExposure &&
                       hasDailyPnl &&
                       _hasRiskDay;
        return new RiskInputSnapshot(
            intent,
            ScaledQuantity.FromWhole(position.Quantity),
            _hasActiveReferencePrice ? _activeReferencePrice : default,
            multiplier,
            grossExposure,
            realizedPnl,
            markToMarketPnl,
            _riskDay,
            complete);
    }

    private bool TryGrossExposure(IStrategyContext ctx, out ScaledMoney grossExposure)
    {
        grossExposure = ScaledMoney.Zero;
        foreach (var position in ctx.Portfolio.OpenPositions)
        {
            if (!_lastReferencePrices.TryGetValue(position.Instrument, out var referencePrice) ||
                referencePrice.Coefficient <= 0)
            {
                return false;
            }

            var spec = ctx.Universe.Find(position.Instrument);
            if (spec is null ||
                !ScaledValueMath.TryQuantizeDouble(spec.ContractMultiplier, BoundaryScale, out var multiplier) ||
                multiplier <= 0 ||
                !RiskMath.TryExposureMoney(
                    position.Quantity < 0 ? -(Int128)position.Quantity : position.Quantity,
                    referencePrice,
                    new ScaledRatio(multiplier, BoundaryScale),
                    out var instrumentExposure) ||
                !RiskMath.TryAddMoney(grossExposure, instrumentExposure, out grossExposure))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryDailyPnl(
        IStrategyContext ctx,
        out ScaledMoney realizedPnl,
        out ScaledMoney markToMarketPnl)
    {
        realizedPnl = default;
        markToMarketPnl = default;
        if (!_hasRiskDay || !_hasDailyOpeningRiskBaseline ||
            !TryPortfolioRiskValues(ctx, out var currentEquity, out var currentMarkToMarket) ||
            !RiskMath.TrySubtractMoney(currentEquity, _dailyOpeningEquity, out var totalPnl) ||
            !RiskMath.TrySubtractMoney(
                currentMarkToMarket,
                _dailyOpeningMarkToMarket,
                out markToMarketPnl) ||
            !RiskMath.TrySubtractMoney(totalPnl, markToMarketPnl, out realizedPnl))
        {
            return false;
        }
        return true;
    }

    private void ObserveRiskDay(DateTime timestampUtc, IStrategyContext ctx)
    {
        var day = DateOnly.FromDateTime(timestampUtc);
        if (!_hasRiskDay)
        {
            _riskDay = day;
            _hasRiskDay = true;
            if (_hasRunRiskBaseline)
            {
                _dailyOpeningEquity = _runStartingEquity;
                _dailyOpeningMarkToMarket = _runStartingMarkToMarket;
            }
            _hasDailyOpeningRiskBaseline = _hasRunRiskBaseline;
        }
        else if (day != _riskDay)
        {
            _riskDay = day;
            if (_hasLastRiskObservation)
            {
                _dailyOpeningEquity = _lastObservedEquity;
                _dailyOpeningMarkToMarket = _lastObservedMarkToMarket;
            }
            _hasDailyOpeningRiskBaseline = _hasLastRiskObservation;
        }

        if (TryPortfolioRiskValues(ctx, out var currentEquity, out var currentMarkToMarket))
        {
            _lastObservedEquity = currentEquity;
            _lastObservedMarkToMarket = currentMarkToMarket;
            _hasLastRiskObservation = true;
        }
        else
        {
            _hasLastRiskObservation = false;
        }
    }

    private bool TryPortfolioRiskValues(
        IStrategyContext ctx,
        out ScaledMoney equity,
        out ScaledMoney markToMarket)
    {
        equity = default;
        markToMarket = default;
        if (!ScaledValueMath.TryQuantizeDouble(ctx.Portfolio.Equity, BoundaryScale, out var equityCoefficient))
            return false;

        var publicMarkToMarket = ScaledMoney.Zero;
        var riskMarkToMarket = ScaledMoney.Zero;
        foreach (var position in ctx.Portfolio.OpenPositions)
        {
            if (!ScaledValueMath.TryQuantizeDouble(
                    position.UnrealizedPnl,
                    BoundaryScale,
                    out var publicPositionMark) ||
                !RiskMath.TryAddMoney(
                    publicMarkToMarket,
                    new ScaledMoney(publicPositionMark, BoundaryScale),
                    out publicMarkToMarket) ||
                !_lastReferencePrices.TryGetValue(position.Instrument, out var referencePrice) ||
                referencePrice.Coefficient <= 0 ||
                !ScaledValueMath.TryQuantizeDouble(position.AveragePrice, BoundaryScale, out var averagePrice) ||
                averagePrice <= 0)
            {
                return false;
            }

            var spec = ctx.Universe.Find(position.Instrument);
            if (spec is null ||
                !ScaledValueMath.TryQuantizeDouble(spec.ContractMultiplier, BoundaryScale, out var multiplier) ||
                multiplier <= 0 ||
                !RiskMath.TryMarkToMarketMoney(
                    referencePrice,
                    new ScaledPrice(averagePrice, BoundaryScale),
                    position.Quantity,
                    new ScaledRatio(multiplier, BoundaryScale),
                    out var positionRiskMark) ||
                !RiskMath.TryAddMoney(riskMarkToMarket, positionRiskMark, out riskMarkToMarket))
            {
                return false;
            }
        }

        var publicEquity = new ScaledMoney(equityCoefficient, BoundaryScale);
        if (!RiskMath.TrySubtractMoney(publicEquity, publicMarkToMarket, out var realizedEquity) ||
            !RiskMath.TryAddMoney(realizedEquity, riskMarkToMarket, out equity))
        {
            return false;
        }
        markToMarket = riskMarkToMarket;
        return true;
    }

    private static bool TryMidpoint(double bid, double ask, out ScaledPrice price)
    {
        price = default;
        if (!ScaledValueMath.TryQuantizeDouble(bid, BoundaryScale, out var bidCoefficient) ||
            !ScaledValueMath.TryQuantizeDouble(ask, BoundaryScale, out var askCoefficient) ||
            bidCoefficient <= 0 || askCoefficient < bidCoefficient ||
            !ScaledValueMath.TryRoundRatioToLong((Int128)bidCoefficient + askCoefficient, 2, out var midpoint))
        {
            return false;
        }
        price = new ScaledPrice(midpoint, BoundaryScale);
        return true;
    }

    private static void ValidateRouterAddressableUniverse(Universe universe)
    {
        if (universe.Instruments.Count == 0)
            throw new ArgumentException("A signal backtest requires at least one instrument.", nameof(universe));

        for (var left = 0; left < universe.Instruments.Count; left++)
        {
            var leftSymbol = universe.Instruments[left].Contract.Symbol;
            if (string.IsNullOrWhiteSpace(leftSymbol))
                throw new NotSupportedException("Every signal-backtest instrument requires a non-empty contract symbol.");
            for (var right = left + 1; right < universe.Instruments.Count; right++)
            {
                if (string.Equals(
                    leftSymbol,
                    universe.Instruments[right].Contract.Symbol,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        $"Signal backtests require unique contract symbols because the frozen backtest router resolves orders by symbol; duplicate '{leftSymbol}' is ambiguous.");
                }
            }
        }
    }

    private static bool TryPrice(double source, out ScaledPrice price)
    {
        price = default;
        if (!ScaledValueMath.TryQuantizeDouble(source, BoundaryScale, out var coefficient) || coefficient <= 0)
            return false;
        price = new ScaledPrice(coefficient, BoundaryScale);
        return true;
    }

    private IStrategyContext RequireVenueContext() => _venueContext ??
        throw new InvalidOperationException("The backtest signal adapter has not started.");

    private SignalKernelContext RequireSignalContext() => _signalContext ??
        throw new InvalidOperationException("The backtest signal adapter has not started.");

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(BacktestSignalExecutionKernel));
    }

    private readonly record struct PendingOrder(string ClientOrderId, long TargetUnits);
}

internal sealed class SignalKernelContext : IStrategyContext
{
    private readonly IStrategyContext _inner;

    internal SignalKernelContext(IStrategyContext inner, IOrderRouter router)
    {
        _inner = inner;
        Router = router;
    }

    public IClock Clock => _inner.Clock;
    public IOrderRouter Router { get; }
    public IPortfolioView Portfolio => _inner.Portfolio;
    public Universe Universe => _inner.Universe;
    public StrategyParameters Parameters => _inner.Parameters;
}

/// <summary>
/// Gives a signal-only kernel its public router-shaped host without exposing the execution router's
/// order methods or real fill events. Signal publication is preserved for BacktestReport.Signals.
/// </summary>
internal sealed class SignalCaptureRouter : IOrderRouter, IStrategySignalSink
{
    private const string DirectOrdersRejected =
        "Direct orders are disabled inside the signal execution adapter; emit StrategySignal instead.";
    private readonly Func<StrategySignal, CancellationToken, Task> _applySignal;
    private readonly SilentObservable<OrderEvent> _silentEvents = new();
    private IOrderRouter? _venueRouter;

    internal SignalCaptureRouter(Func<StrategySignal, CancellationToken, Task> applySignal) =>
        _applySignal = applySignal;

    public IObservable<OrderEvent> OrderEvents => _silentEvents;

    internal void Bind(IOrderRouter venueRouter) => _venueRouter = venueRouter;

    internal void Unbind() => _venueRouter = null;

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new OrderResult(
            request.ClientOrderId,
            null,
            OrderState.Rejected,
            DirectOrdersRejected));
    }

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task EmitSignalAsync(StrategySignal signal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(signal.Kind) || !double.IsFinite(signal.Strength) ||
            signal.Strength is < 0d or > 1d || signal.NoteId < 0)
        {
            return _applySignal(signal, ct);
        }

        var venue = _venueRouter ?? throw new InvalidOperationException("The signal host has not started.");
        if (venue is not IStrategySignalSink sink)
            throw new InvalidOperationException("The backtest router does not expose the strategy-signal sink.");

        var publish = sink.EmitSignalAsync(signal, ct);
        if (publish.IsCompletedSuccessfully)
            return _applySignal(signal, ct);
        return PublishThenApplyAsync(sink, signal, publish, ct);
    }

    private async Task PublishThenApplyAsync(
        IStrategySignalSink sink,
        StrategySignal signal,
        Task publish,
        CancellationToken ct)
    {
        _ = sink;
        await publish.ConfigureAwait(false);
        await _applySignal(signal, ct).ConfigureAwait(false);
    }
}

internal sealed class SilentObservable<T> : IObservable<T>
{
    private readonly SilentSubscription _subscription = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _subscription;
    }

    private sealed class SilentSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
