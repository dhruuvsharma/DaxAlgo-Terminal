using System.Runtime.CompilerServices;

namespace TradingTerminal.Sandbox.Portfolio;

/// <summary>
/// Implements a deterministic, bounded model portfolio: position, exits, equity sampling,
/// and a fixed-length ring of closed trips.
/// </summary>
public sealed class ModelPortfolioSimulator
{
    private const double StartingEquity = 100_000d;
    private const double UnitNotional = 1_000d;
    private const double BasisPointMultiplier = 0.0001d;

    private readonly int _maxAbsoluteUnits;
    private PortfolioState _committed;
    private PortfolioState _staged;
    private ClosedTrip[] _committedTrips;
    private ClosedTrip[] _stagedTrips;
    private bool _callbackOpen;
    private bool _completed;
    private ModelPortfolioFault _callbackFault;
    private double _callbackReferencePrice;
    private bool _hasValidatedReferencePrice;
    private double _lastValidatedReferencePrice;

    private ModelPortfolioSimulator(int maxAbsoluteUnits, int retainedClosedTrips)
    {
        _maxAbsoluteUnits = maxAbsoluteUnits;
        _committedTrips = new ClosedTrip[retainedClosedTrips];
        _stagedTrips = new ClosedTrip[retainedClosedTrips];
        _committed = new PortfolioState
        {
            LastSampledEquity = StartingEquity,
            EquityPeak = StartingEquity,
        };
        _staged = _committed;
    }

    /// <summary>
    /// Upper bound on a simulator's absolute position size, in units.
    /// </summary>
    /// <remarks>
    /// This simulator owns its own bounds so the portfolio carries no package-format surface and can
    /// be authored in the open-source edition. A delivery format that also declares these limits
    /// (a sealed-package format does, in its manifest) validates its own manifest independently and passes the accepted
    /// values in; the two are deliberately separate checks over the same numbers.
    /// </remarks>
    public const int MaximumAbsoluteUnits = 100;

    /// <summary>Upper bound on the retained closed-trip ring length.</summary>
    /// <remarks>See <see cref="MaximumAbsoluteUnits"/> for why this bound lives here.</remarks>
    public const int MaximumRetainedClosedTrips = 256;

    /// <summary>
    /// Validates the bounded declaration and preallocates both trip rings. A caller's buyer-facing
    /// recommended unit, if it has one, has no execution semantics here.
    /// </summary>
    /// <param name="maxAbsoluteUnits">Declared absolute unit cap, or <see langword="null"/> if the
    /// caller has no declaration.</param>
    /// <param name="retainedClosedTrips">Declared closed-trip ring length, or <see langword="null"/>.</param>
    /// <param name="simulator">The created simulator, or <see langword="null"/> on fault.</param>
    /// <returns>A local fault value; <see cref="ModelPortfolioFault.None"/> on success.</returns>
    public static ModelPortfolioFault TryCreate(
        int? maxAbsoluteUnits,
        int? retainedClosedTrips,
        out ModelPortfolioSimulator? simulator)
    {
        simulator = null;
        if (maxAbsoluteUnits is not (>= 1 and <= MaximumAbsoluteUnits) ||
            retainedClosedTrips is not (>= 1 and <= MaximumRetainedClosedTrips))
        {
            return ModelPortfolioFault.InvalidConfiguration;
        }

        simulator = new ModelPortfolioSimulator(
            maxAbsoluteUnits.Value,
            retainedClosedTrips.Value);
        return ModelPortfolioFault.None;
    }

    /// <summary>Gets the committed core state.</summary>
    public ModelPortfolioSnapshot CommittedSnapshot => new(
        Normalize(_committed.PositionUnits),
        Normalize(_committed.PositionQuantity),
        _committed.PositionUnits == 0d ? 0d : Normalize(_committed.AverageEntryPrice),
        _committed.PositionUnits == 0d ? 0L : _committed.BarsHeld,
        Normalize(_committed.LastSampledEquity),
        Normalize(_committed.RealizedGrossProfitLoss),
        Normalize(_committed.CommissionTotal),
        Normalize(_committed.SlippageTotal),
        Normalize(_committed.EquityPeak),
        Normalize(_committed.MaximumDrawdown),
        _committed.LifetimeClosedTripCount,
        _committed.LifetimeWinningTripCount,
        _committed.LifetimeLosingTripCount,
        _committed.TripRingCount,
        _committed.Streak,
        _completed);

    /// <summary>
    /// Performs callback steps 1, 2, and the core portion of step 4 for an <c>OnBar</c> entrypoint,
    /// then opens the staging window.
    /// </summary>
    public ModelPortfolioFault BeginOnBar(double close)
    {
        var lifecycleFault = CanBeginCallback();
        if (lifecycleFault != ModelPortfolioFault.None)
            return lifecycleFault;
        if (!IsValidReferencePrice(close))
            return ModelPortfolioFault.InvalidReferencePrice;

        return BeginCallback(close, isBar: true);
    }

    /// <summary>
    /// Resolves the quote midpoint, or the last-price fallback, and opens an
    /// <c>OnTick</c> staging window.
    /// </summary>
    public ModelPortfolioFault BeginOnTick(double bid, double ask, double last)
    {
        var lifecycleFault = CanBeginCallback();
        if (lifecycleFault != ModelPortfolioFault.None)
            return lifecycleFault;

        double referencePrice;
        if (IsValidReferencePrice(bid) && IsValidReferencePrice(ask))
        {
            if (ask < bid)
                return ModelPortfolioFault.CrossedQuote;
            if (!TrySubtract(ask, bid, out var spread) ||
                !TryDivide(spread, 2d, out var halfSpread) ||
                !TryAdd(bid, halfSpread, out referencePrice))
            {
                return ModelPortfolioFault.NonFiniteArithmetic;
            }
        }
        else if (IsValidReferencePrice(last))
        {
            referencePrice = last;
        }
        else
        {
            return ModelPortfolioFault.InvalidReferencePrice;
        }

        if (!IsValidReferencePrice(referencePrice))
            return ModelPortfolioFault.InvalidReferencePrice;
        return BeginCallback(referencePrice, isBar: false);
    }

    /// <summary>
    /// Commits all staged model mutations on successful <c>RET</c> and takes the second equity sample
    /// required on a successful return. A poisoned callback is rolled back instead.
    /// </summary>
    public ModelPortfolioFault CommitCallback()
    {
        if (!_callbackOpen)
            return _completed
                ? ModelPortfolioFault.RunCompleted
                : ModelPortfolioFault.InvalidCallbackState;

        if (_callbackFault != ModelPortfolioFault.None)
        {
            var fault = _callbackFault;
            CloseCallbackWindow();
            return fault;
        }

        var candidate = _staged;
        var sampleFault = TrySampleEquity(ref candidate, _callbackReferencePrice);
        if (sampleFault != ModelPortfolioFault.None)
        {
            CloseCallbackWindow();
            return sampleFault;
        }

        _staged = candidate;
        _committed = _staged;
        SwapTripRings();
        CloseCallbackWindow();
        return ModelPortfolioFault.None;
    }

    /// <summary>Discards the open staging window.</summary>
    public void RollbackCallback()
    {
        if (_callbackOpen)
            CloseCallbackWindow();
    }

    /// <summary>
    /// Performs the final liquidation, trip recording, and third equity sample required
    /// to complete a run.
    /// </summary>
    public ModelPortfolioFault CompleteRun()
    {
        if (_completed)
            return ModelPortfolioFault.RunCompleted;
        if (_callbackOpen)
            return ModelPortfolioFault.InvalidCallbackState;

        if (_committed.PositionUnits != 0d)
        {
            if (!_hasValidatedReferencePrice)
                return ModelPortfolioFault.InvalidReferencePrice;

            Array.Copy(_committedTrips, _stagedTrips, _committedTrips.Length);
            _staged = _committed;
            var liquidationFault = ApplyFullClose(
                ref _staged,
                _stagedTrips,
                _lastValidatedReferencePrice);
            if (liquidationFault != ModelPortfolioFault.None)
                return liquidationFault;

            var sampleFault = TrySampleEquity(ref _staged, _lastValidatedReferencePrice);
            if (sampleFault != ModelPortfolioFault.None)
                return sampleFault;

            _committed = _staged;
            SwapTripRings();
        }

        _completed = true;
        return ModelPortfolioFault.None;
    }

    /// <summary>Applies a market order of the given signed unit delta and reports the fill price.</summary>
    public ModelPortfolioFault MpMarket(double signedUnits, out double fillPrice)
    {
        fillPrice = 0d;
        var callbackFault = RequireWritableCallback();
        if (callbackFault != ModelPortfolioFault.None)
            return callbackFault;
        if (!double.IsFinite(signedUnits) || signedUnits == 0d)
            return PoisonCallback(ModelPortfolioFault.InvalidMarketUnits);

        var executionFault = ExecuteMarketOrder(
            ref _staged,
            _stagedTrips,
            signedUnits,
            _callbackReferencePrice,
            out var rejected);
        if (executionFault != ModelPortfolioFault.None)
            return PoisonCallback(executionFault);

        fillPrice = rejected ? 0d : _callbackReferencePrice;
        return ModelPortfolioFault.None;
    }

    /// <summary>Closes the given fraction of the open position and reports the fill price.</summary>
    public ModelPortfolioFault MpClose(double fraction, out double fillPrice)
    {
        fillPrice = 0d;
        var callbackFault = RequireWritableCallback();
        if (callbackFault != ModelPortfolioFault.None)
            return callbackFault;
        if (!double.IsFinite(fraction) || fraction <= 0d || fraction > 1d)
            return PoisonCallback(ModelPortfolioFault.InvalidCloseFraction);
        if (_staged.PositionUnits == 0d)
            return PoisonCallback(ModelPortfolioFault.CloseWhileFlat);

        double signedUnits;
        if (fraction == 1d)
        {
            signedUnits = -_staged.PositionUnits;
        }
        else
        {
            if (!TryMultiply(_staged.PositionUnits, fraction, out var unsignedReduction))
                return PoisonCallback(ModelPortfolioFault.NonFiniteArithmetic);
            signedUnits = -unsignedReduction;
        }

        signedUnits = Normalize(signedUnits);
        var executionFault = ExecuteMarketOrder(
            ref _staged,
            _stagedTrips,
            signedUnits,
            _callbackReferencePrice,
            out var rejected);
        if (executionFault != ModelPortfolioFault.None)
            return PoisonCallback(executionFault);
        if (rejected)
            return PoisonCallback(ModelPortfolioFault.NonFiniteArithmetic);

        fillPrice = _callbackReferencePrice;
        return ModelPortfolioFault.None;
    }

    /// <summary>Declares a protective stop in the given mode.</summary>
    public ModelPortfolioFault MpStop(long mode, double value)
    {
        var callbackFault = RequireWritableCallback();
        if (callbackFault != ModelPortfolioFault.None)
            return callbackFault;
        if (mode is < 1L or > 3L)
            return PoisonCallback(ModelPortfolioFault.InvalidExitMode);
        if (!double.IsFinite(value) || value <= 0d)
            return PoisonCallback(ModelPortfolioFault.InvalidExitValue);
        if (_staged.PositionUnits == 0d)
            return PoisonCallback(ModelPortfolioFault.ExitWhileFlat);

        var candidate = _staged;
        var priceFault = TryResolveExitPrice(candidate, mode, value, isStop: true, out var stopPrice);
        if (priceFault != ModelPortfolioFault.None)
            return PoisonCallback(priceFault);
        if (!double.IsFinite(stopPrice) || stopPrice <= 0d)
            return PoisonCallback(ModelPortfolioFault.NonFiniteArithmetic);
        if (stopPrice == candidate.AverageEntryPrice)
            return PoisonCallback(ModelPortfolioFault.ExitAtEntry);
        if (!IsExitOnCorrectSide(candidate, stopPrice, isStop: true))
            return PoisonCallback(ModelPortfolioFault.ExitOnWrongSide);

        if (!candidate.HasCapturedR)
        {
            var captureFault = TryCaptureR(ref candidate, stopPrice);
            if (captureFault != ModelPortfolioFault.None)
                return PoisonCallback(captureFault);
        }

        ClearTrail(ref candidate);
        candidate.HasStop = true;
        candidate.StopPrice = Normalize(stopPrice);
        _staged = candidate;
        return ModelPortfolioFault.None;
    }

    /// <summary>Declares a profit target in the given mode.</summary>
    public ModelPortfolioFault MpTarget(long mode, double value)
    {
        var callbackFault = RequireWritableCallback();
        if (callbackFault != ModelPortfolioFault.None)
            return callbackFault;
        if (mode is < 1L or > 4L)
            return PoisonCallback(ModelPortfolioFault.InvalidExitMode);
        if (!double.IsFinite(value) || value <= 0d)
            return PoisonCallback(ModelPortfolioFault.InvalidExitValue);
        if (_staged.PositionUnits == 0d)
            return PoisonCallback(ModelPortfolioFault.ExitWhileFlat);
        if (mode == 4L && !_staged.HasCapturedR)
            return PoisonCallback(ModelPortfolioFault.UndefinedR);

        var candidate = _staged;
        var priceFault = TryResolveExitPrice(candidate, mode, value, isStop: false, out var targetPrice);
        if (priceFault != ModelPortfolioFault.None)
            return PoisonCallback(priceFault);
        if (!double.IsFinite(targetPrice) || targetPrice <= 0d)
            return PoisonCallback(ModelPortfolioFault.NonFiniteArithmetic);
        if (targetPrice == candidate.AverageEntryPrice)
            return PoisonCallback(ModelPortfolioFault.ExitAtEntry);
        if (!IsExitOnCorrectSide(candidate, targetPrice, isStop: false))
            return PoisonCallback(ModelPortfolioFault.ExitOnWrongSide);

        candidate.HasTarget = true;
        candidate.TargetPrice = Normalize(targetPrice);
        _staged = candidate;
        return ModelPortfolioFault.None;
    }

    /// <summary>Declares a trailing stop in the given mode, activated at the given R multiple.</summary>
    public ModelPortfolioFault MpTrail(long mode, double value, double activationR)
    {
        var callbackFault = RequireWritableCallback();
        if (callbackFault != ModelPortfolioFault.None)
            return callbackFault;
        if (mode is not 2L and not 3L)
            return PoisonCallback(ModelPortfolioFault.InvalidExitMode);
        if (!double.IsFinite(value) || value <= 0d)
            return PoisonCallback(ModelPortfolioFault.InvalidExitValue);
        if (!double.IsFinite(activationR) || activationR < 0d)
            return PoisonCallback(ModelPortfolioFault.InvalidTrailActivation);
        if (_staged.PositionUnits == 0d)
            return PoisonCallback(ModelPortfolioFault.ExitWhileFlat);
        if (activationR > 0d && !_staged.HasCapturedR)
            return PoisonCallback(ModelPortfolioFault.UndefinedR);

        var candidate = _staged;
        var distanceFault = TryResolveTrailDistance(candidate, mode, value, out var distance);
        if (distanceFault != ModelPortfolioFault.None)
            return PoisonCallback(distanceFault);

        var trailPriceFault = TryCalculateTrailStop(
            candidate.PositionUnits,
            _callbackReferencePrice,
            distance,
            out var effectiveStop);
        if (trailPriceFault != ModelPortfolioFault.None)
            return PoisonCallback(trailPriceFault);

        if (!candidate.HasCapturedR)
        {
            var captureFault = TryCaptureR(ref candidate, effectiveStop);
            if (captureFault != ModelPortfolioFault.None)
                return PoisonCallback(captureFault);
        }

        ClearStop(ref candidate);
        candidate.HasTrail = true;
        candidate.TrailDistance = Normalize(distance);
        candidate.TrailActivationR = Normalize(activationR);
        candidate.TrailHighWaterMark = _callbackReferencePrice;
        candidate.TrailArmed = activationR == 0d;
        _staged = candidate;
        return ModelPortfolioFault.None;
    }

    /// <summary>Cancels any declared stop, target, and trail.</summary>
    public ModelPortfolioFault MpCancelExits()
    {
        var callbackFault = RequireWritableCallback();
        if (callbackFault != ModelPortfolioFault.None)
            return callbackFault;
        if (_staged.PositionUnits == 0d)
            return PoisonCallback(ModelPortfolioFault.ExitWhileFlat);

        var candidate = _staged;
        ClearExitDeclarations(ref candidate);
        _staged = candidate;
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the current signed position, in units.</summary>
    public ModelPortfolioFault MpPosition(out double position)
    {
        position = 0d;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        position = Normalize(state.PositionUnits);
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the average entry price of the open position.</summary>
    public ModelPortfolioFault MpEntry(out double entryPrice)
    {
        entryPrice = 0d;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        entryPrice = state.PositionUnits == 0d ? 0d : Normalize(state.AverageEntryPrice);
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports how many bars the open position has been held.</summary>
    public ModelPortfolioFault MpBarsHeld(out long barsHeld)
    {
        barsHeld = 0L;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        barsHeld = state.PositionUnits == 0d ? 0L : state.BarsHeld;
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the open position's risk multiple (R).</summary>
    public ModelPortfolioFault MpOpenR(out double riskMultiple)
    {
        riskMultiple = 0d;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        if (state.PositionUnits == 0d)
            return ModelPortfolioFault.None;
        if (!state.HasCapturedR)
            return PoisonCallback(ModelPortfolioFault.UndefinedR);

        var referencePrice = _callbackOpen
            ? _callbackReferencePrice
            : _lastValidatedReferencePrice;
        var arithmeticFault = TryCalculateOpenR(state, referencePrice, out riskMultiple);
        if (arithmeticFault != ModelPortfolioFault.None)
        {
            riskMultiple = 0d;
            return PoisonCallback(arithmeticFault);
        }

        riskMultiple = Normalize(riskMultiple);
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the last sampled account equity.</summary>
    public ModelPortfolioFault MpEquity(out double equity)
    {
        equity = 0d;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        if (!_callbackOpen)
        {
            equity = Normalize(state.LastSampledEquity);
            return ModelPortfolioFault.None;
        }

        var arithmeticFault = TryCalculateEquity(state, _callbackReferencePrice, out equity);
        if (arithmeticFault != ModelPortfolioFault.None)
        {
            equity = 0d;
            return PoisonCallback(arithmeticFault);
        }

        equity = Normalize(equity);
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the number of retained closed trips.</summary>
    public ModelPortfolioFault MpTradeCount(out long tradeCount)
    {
        tradeCount = 0L;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        tradeCount = state.TripRingCount;
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the risk multiple (R) of the nth retained closed trip.</summary>
    public ModelPortfolioFault MpTradeR(long n, out double riskMultiple)
    {
        riskMultiple = 0d;
        var tripFault = TryReadTrip(n, out var trip);
        if (tripFault != ModelPortfolioFault.None)
            return tripFault;
        if (!trip.HasR)
            return PoisonCallback(ModelPortfolioFault.UndefinedR);

        if (!TryMultiply(trip.PeakAbsoluteQuantity, trip.R, out var denominator) ||
            denominator <= 0d ||
            !TryDivide(trip.GrossProfitLoss, denominator, out riskMultiple))
        {
            riskMultiple = 0d;
            return PoisonCallback(ModelPortfolioFault.NonFiniteArithmetic);
        }

        riskMultiple = Normalize(riskMultiple);
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports whether the nth retained closed trip recorded a risk multiple.</summary>
    public ModelPortfolioFault MpTradeHasR(long n, out long hasR)
    {
        hasR = 0L;
        var tripFault = TryReadTrip(n, out var trip);
        if (tripFault != ModelPortfolioFault.None)
            return tripFault;
        hasR = trip.HasR ? 1L : 0L;
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the peak absolute units of the nth retained closed trip.</summary>
    public ModelPortfolioFault MpTradeUnits(long n, out double peakAbsoluteUnits)
    {
        peakAbsoluteUnits = 0d;
        var tripFault = TryReadTrip(n, out var trip);
        if (tripFault != ModelPortfolioFault.None)
            return tripFault;
        peakAbsoluteUnits = Normalize(trip.PeakAbsoluteUnits);
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the bars held for the nth retained closed trip.</summary>
    public ModelPortfolioFault MpTradeBars(long n, out long barsHeld)
    {
        barsHeld = 0L;
        var tripFault = TryReadTrip(n, out var trip);
        if (tripFault != ModelPortfolioFault.None)
            return tripFault;
        barsHeld = trip.BarsHeld;
        return ModelPortfolioFault.None;
    }

    /// <summary>Reports the current consecutive win or loss streak.</summary>
    public ModelPortfolioFault MpStreak(out long streak)
    {
        streak = 0L;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        streak = state.Streak;
        return ModelPortfolioFault.None;
    }

    private ModelPortfolioFault BeginCallback(double referencePrice, bool isBar)
    {
        var marked = _committed;
        var sampleFault = TrySampleEquity(ref marked, referencePrice);
        if (sampleFault != ModelPortfolioFault.None)
            return sampleFault;

        _committed = marked;
        _lastValidatedReferencePrice = referencePrice;
        _hasValidatedReferencePrice = true;

        Array.Copy(_committedTrips, _stagedTrips, _committedTrips.Length);
        var afterExit = _committed;
        var exitFault = TryEvaluateExits(
            ref afterExit,
            _stagedTrips,
            referencePrice,
            out var exitTriggered);
        if (exitFault != ModelPortfolioFault.None)
            return exitFault;

        if (exitTriggered)
        {
            _committed = afterExit;
            SwapTripRings();
        }

        var afterStepFour = _committed;
        var stepFourFault = TryAdvanceHostState(ref afterStepFour, referencePrice, isBar);
        if (stepFourFault != ModelPortfolioFault.None)
            return stepFourFault;

        _committed = afterStepFour;
        Array.Copy(_committedTrips, _stagedTrips, _committedTrips.Length);
        _staged = _committed;
        _callbackReferencePrice = referencePrice;
        _callbackFault = ModelPortfolioFault.None;
        _callbackOpen = true;

        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault TryEvaluateExits(
        ref PortfolioState state,
        ClosedTrip[] trips,
        double referencePrice,
        out bool exitTriggered)
    {
        exitTriggered = false;
        if (state.PositionUnits == 0d)
            return ModelPortfolioFault.None;

        if (state.HasStop &&
            IsProtectiveExitTriggered(state.PositionUnits, referencePrice, state.StopPrice))
        {
            var stopFault = ApplyFullClose(ref state, trips, referencePrice);
            if (stopFault != ModelPortfolioFault.None)
                return stopFault;
            exitTriggered = true;
            return ModelPortfolioFault.None;
        }

        if (state.HasTrail && state.TrailArmed)
        {
            var trailFault = TryCalculateTrailStop(
                state.PositionUnits,
                state.TrailHighWaterMark,
                state.TrailDistance,
                out var trailStop);
            if (trailFault != ModelPortfolioFault.None)
                return trailFault;

            if (IsProtectiveExitTriggered(state.PositionUnits, referencePrice, trailStop))
            {
                trailFault = ApplyFullClose(ref state, trips, referencePrice);
                if (trailFault != ModelPortfolioFault.None)
                    return trailFault;
                exitTriggered = true;
                return ModelPortfolioFault.None;
            }
        }

        if (state.HasTarget &&
            IsTargetTriggered(state.PositionUnits, referencePrice, state.TargetPrice))
        {
            var targetFault = ApplyFullClose(ref state, trips, referencePrice);
            if (targetFault != ModelPortfolioFault.None)
                return targetFault;
            exitTriggered = true;
        }

        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault TryAdvanceHostState(
        ref PortfolioState state,
        double referencePrice,
        bool isBar)
    {
        var candidate = state;
        if (candidate.PositionUnits != 0d && candidate.HasTrail)
        {
            candidate.TrailHighWaterMark = candidate.PositionUnits > 0d
                ? Math.Max(candidate.TrailHighWaterMark, referencePrice)
                : Math.Min(candidate.TrailHighWaterMark, referencePrice);

            if (!candidate.TrailArmed)
            {
                var riskFault = TryCalculateOpenR(candidate, referencePrice, out var openR);
                if (riskFault != ModelPortfolioFault.None)
                    return riskFault;
                if (openR >= candidate.TrailActivationR)
                    candidate.TrailArmed = true;
            }
        }

        if (isBar && candidate.PositionUnits != 0d)
        {
            if (candidate.BarsHeld == long.MaxValue)
                return ModelPortfolioFault.CounterOverflow;
            candidate.BarsHeld++;
        }

        state = candidate;
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault TryResolveExitPrice(
        PortfolioState state,
        long mode,
        double value,
        bool isStop,
        out double exitPrice)
    {
        exitPrice = 0d;
        if (mode == 1L)
        {
            exitPrice = value;
            return ModelPortfolioFault.None;
        }

        var distanceFault = TryResolveExitDistance(state, mode, value, out var distance);
        if (distanceFault != ModelPortfolioFault.None)
            return distanceFault;

        var subtract = (state.PositionUnits > 0d) == isStop;
        var priceIsFinite = subtract
            ? TrySubtract(state.AverageEntryPrice, distance, out exitPrice)
            : TryAdd(state.AverageEntryPrice, distance, out exitPrice);
        return priceIsFinite
            ? ModelPortfolioFault.None
            : ModelPortfolioFault.NonFiniteArithmetic;
    }

    private static ModelPortfolioFault TryResolveTrailDistance(
        PortfolioState state,
        long mode,
        double value,
        out double distance) =>
        TryResolveExitDistance(state, mode, value, out distance);

    private static ModelPortfolioFault TryResolveExitDistance(
        PortfolioState state,
        long mode,
        double value,
        out double distance)
    {
        distance = 0d;
        switch (mode)
        {
            case 2L:
                distance = value;
                break;
            case 3L:
                if (!TryMultiply(state.AverageEntryPrice, value, out var percentageNumerator) ||
                    !TryDivide(percentageNumerator, 100d, out distance))
                {
                    return ModelPortfolioFault.NonFiniteArithmetic;
                }

                break;
            case 4L:
                if (!TryMultiply(value, state.CapturedR, out distance))
                    return ModelPortfolioFault.NonFiniteArithmetic;
                break;
            default:
                return ModelPortfolioFault.InvalidExitMode;
        }

        return double.IsFinite(distance) && distance > 0d
            ? ModelPortfolioFault.None
            : ModelPortfolioFault.NonFiniteArithmetic;
    }

    private static ModelPortfolioFault TryCalculateTrailStop(
        double positionUnits,
        double highWaterMark,
        double distance,
        out double stopPrice)
    {
        var resultIsFinite = positionUnits > 0d
            ? TrySubtract(highWaterMark, distance, out stopPrice)
            : TryAdd(highWaterMark, distance, out stopPrice);
        return resultIsFinite
            ? ModelPortfolioFault.None
            : ModelPortfolioFault.NonFiniteArithmetic;
    }

    private static ModelPortfolioFault TryCaptureR(
        ref PortfolioState state,
        double stopPrice)
    {
        if (!TrySubtract(state.AverageEntryPrice, stopPrice, out var signedRisk))
            return ModelPortfolioFault.NonFiniteArithmetic;

        var capturedR = Math.Abs(signedRisk);
        if (!double.IsFinite(capturedR))
            return ModelPortfolioFault.NonFiniteArithmetic;
        if (capturedR <= 0d)
            return ModelPortfolioFault.ExitAtEntry;

        state.HasCapturedR = true;
        state.CapturedR = Normalize(capturedR);
        return ModelPortfolioFault.None;
    }

    private static bool IsExitOnCorrectSide(
        PortfolioState state,
        double exitPrice,
        bool isStop) =>
        state.PositionUnits > 0d
            ? isStop ? exitPrice < state.AverageEntryPrice : exitPrice > state.AverageEntryPrice
            : isStop ? exitPrice > state.AverageEntryPrice : exitPrice < state.AverageEntryPrice;

    private static bool IsProtectiveExitTriggered(
        double positionUnits,
        double referencePrice,
        double stopPrice) =>
        positionUnits > 0d ? referencePrice <= stopPrice : referencePrice >= stopPrice;

    private static bool IsTargetTriggered(
        double positionUnits,
        double referencePrice,
        double targetPrice) =>
        positionUnits > 0d ? referencePrice >= targetPrice : referencePrice <= targetPrice;

    private ModelPortfolioFault CanBeginCallback()
    {
        if (_completed)
            return ModelPortfolioFault.RunCompleted;
        return _callbackOpen
            ? ModelPortfolioFault.InvalidCallbackState
            : ModelPortfolioFault.None;
    }

    private ModelPortfolioFault RequireWritableCallback()
    {
        if (_completed)
            return ModelPortfolioFault.RunCompleted;
        if (!_callbackOpen)
            return ModelPortfolioFault.InvalidCallbackState;
        return _callbackFault;
    }

    private ModelPortfolioFault TryReadState(out PortfolioState state)
    {
        if (_callbackOpen && _callbackFault != ModelPortfolioFault.None)
        {
            state = default;
            return _callbackFault;
        }

        state = _callbackOpen ? _staged : _committed;
        return ModelPortfolioFault.None;
    }

    private ModelPortfolioFault TryReadTrip(long n, out ClosedTrip trip)
    {
        trip = default;
        var readFault = TryReadState(out var state);
        if (readFault != ModelPortfolioFault.None)
            return readFault;
        if (n < 0L || n >= state.TripRingCount)
            return PoisonCallback(ModelPortfolioFault.TradeIndexOutOfRange);

        var trips = _callbackOpen ? _stagedTrips : _committedTrips;
        var index = state.TripRingNextIndex - 1 - (int)n;
        if (index < 0)
            index += trips.Length;
        trip = trips[index];
        return ModelPortfolioFault.None;
    }

    private ModelPortfolioFault PoisonCallback(ModelPortfolioFault fault)
    {
        if (_callbackOpen && _callbackFault == ModelPortfolioFault.None)
            _callbackFault = fault;
        return fault;
    }

    private void CloseCallbackWindow()
    {
        _callbackOpen = false;
        _callbackFault = ModelPortfolioFault.None;
        _callbackReferencePrice = 0d;
    }

    private void SwapTripRings()
    {
        var previousCommitted = _committedTrips;
        _committedTrips = _stagedTrips;
        _stagedTrips = previousCommitted;
    }

    private ModelPortfolioFault ExecuteMarketOrder(
        ref PortfolioState state,
        ClosedTrip[] trips,
        double signedUnits,
        double referencePrice,
        out bool rejected)
    {
        rejected = false;
        if (!TryAdd(state.PositionUnits, signedUnits, out var resultingUnits))
            return ModelPortfolioFault.NonFiniteArithmetic;
        resultingUnits = Normalize(resultingUnits);
        if (Math.Abs(resultingUnits) > _maxAbsoluteUnits)
        {
            rejected = true;
            return ModelPortfolioFault.None;
        }

        if (state.PositionUnits == 0d)
            return ApplyIncrease(ref state, signedUnits, resultingUnits, referencePrice);
        if (HaveSameSign(state.PositionUnits, signedUnits))
            return ApplyIncrease(ref state, signedUnits, resultingUnits, referencePrice);
        if (resultingUnits == 0d)
            return ApplyFullClose(ref state, trips, referencePrice);
        if (HaveSameSign(state.PositionUnits, resultingUnits))
            return ApplyPartialReduction(ref state, signedUnits, resultingUnits, referencePrice);

        var closeFault = ApplyFullClose(ref state, trips, referencePrice);
        if (closeFault != ModelPortfolioFault.None)
            return closeFault;
        return ApplyIncrease(ref state, resultingUnits, resultingUnits, referencePrice);
    }

    private static ModelPortfolioFault ApplyIncrease(
        ref PortfolioState state,
        double addedUnits,
        double resultingUnits,
        double referencePrice)
    {
        if (!TryMultiply(addedUnits, UnitNotional, out var quantityNumerator) ||
            !TryDivide(quantityNumerator, referencePrice, out var quantityChange))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        quantityChange = Normalize(quantityChange);
        var costFault = TryFillCosts(
            quantityChange,
            referencePrice,
            out var commission,
            out var slippage);
        if (costFault != ModelPortfolioFault.None)
            return costFault;

        if (!TryAdd(state.CommissionTotal, commission, out var commissionTotal) ||
            !TryAdd(state.SlippageTotal, slippage, out var slippageTotal) ||
            !TryAdd(state.TripEntryCommission, commission, out var tripEntryCommission) ||
            !TryAdd(state.TripEntrySlippage, slippage, out var tripEntrySlippage))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        double newQuantity;
        double newEntryPrice;
        if (state.PositionUnits == 0d)
        {
            newQuantity = quantityChange;
            newEntryPrice = referencePrice;
        }
        else
        {
            if (!TryMultiply(state.PositionQuantity, state.AverageEntryPrice, out var oldWeightedPrice) ||
                !TryMultiply(quantityChange, referencePrice, out var addedWeightedPrice) ||
                !TryAdd(oldWeightedPrice, addedWeightedPrice, out var weightedPrice) ||
                !TryAdd(state.PositionQuantity, quantityChange, out newQuantity) ||
                newQuantity == 0d ||
                !TryDivide(weightedPrice, newQuantity, out newEntryPrice))
            {
                return ModelPortfolioFault.NonFiniteArithmetic;
            }
        }

        state.PositionUnits = Normalize(resultingUnits);
        state.PositionQuantity = Normalize(newQuantity);
        state.AverageEntryPrice = Normalize(newEntryPrice);
        state.CommissionTotal = Normalize(commissionTotal);
        state.SlippageTotal = Normalize(slippageTotal);
        state.TripEntryCommission = Normalize(tripEntryCommission);
        state.TripEntrySlippage = Normalize(tripEntrySlippage);
        state.TripPeakAbsoluteUnits = Math.Max(
            state.TripPeakAbsoluteUnits,
            Math.Abs(state.PositionUnits));
        state.TripPeakAbsoluteQuantity = Math.Max(
            state.TripPeakAbsoluteQuantity,
            Math.Abs(state.PositionQuantity));
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault ApplyPartialReduction(
        ref PortfolioState state,
        double signedUnits,
        double resultingUnits,
        double referencePrice)
    {
        if (!TryDivide(Math.Abs(signedUnits), Math.Abs(state.PositionUnits), out var fraction) ||
            !TryMultiply(-state.PositionQuantity, fraction, out var quantityChange) ||
            !TryAdd(state.PositionQuantity, quantityChange, out var resultingQuantity))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        var closedQuantity = Normalize(-quantityChange);
        var exitFault = ApplyExitLeg(ref state, closedQuantity, referencePrice);
        if (exitFault != ModelPortfolioFault.None)
            return exitFault;

        state.PositionUnits = Normalize(resultingUnits);
        state.PositionQuantity = Normalize(resultingQuantity);
        state.TripPeakAbsoluteUnits = Math.Max(
            state.TripPeakAbsoluteUnits,
            Math.Abs(state.PositionUnits));
        state.TripPeakAbsoluteQuantity = Math.Max(
            state.TripPeakAbsoluteQuantity,
            Math.Abs(state.PositionQuantity));
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault ApplyFullClose(
        ref PortfolioState state,
        ClosedTrip[] trips,
        double referencePrice)
    {
        var exitFault = ApplyExitLeg(ref state, state.PositionQuantity, referencePrice);
        if (exitFault != ModelPortfolioFault.None)
            return exitFault;

        var tripFault = RecordCompletedTrip(ref state, trips);
        if (tripFault != ModelPortfolioFault.None)
            return tripFault;

        ClearPosition(ref state);
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault ApplyExitLeg(
        ref PortfolioState state,
        double closedQuantity,
        double referencePrice)
    {
        if (!TrySubtract(referencePrice, state.AverageEntryPrice, out var priceChange) ||
            !TryMultiply(closedQuantity, priceChange, out var gross))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        gross = Normalize(gross);
        var costFault = TryFillCosts(
            closedQuantity,
            referencePrice,
            out var commission,
            out var slippage);
        if (costFault != ModelPortfolioFault.None)
            return costFault;

        if (!TryAdd(state.RealizedGrossProfitLoss, gross, out var realizedGross) ||
            !TryAdd(state.TripGrossProfitLoss, gross, out var tripGross) ||
            !TryAdd(state.CommissionTotal, commission, out var commissionTotal) ||
            !TryAdd(state.SlippageTotal, slippage, out var slippageTotal) ||
            !TryAdd(state.TripExitCommission, commission, out var tripExitCommission) ||
            !TryAdd(state.TripExitSlippage, slippage, out var tripExitSlippage))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        state.RealizedGrossProfitLoss = Normalize(realizedGross);
        state.TripGrossProfitLoss = Normalize(tripGross);
        state.CommissionTotal = Normalize(commissionTotal);
        state.SlippageTotal = Normalize(slippageTotal);
        state.TripExitCommission = Normalize(tripExitCommission);
        state.TripExitSlippage = Normalize(tripExitSlippage);
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault RecordCompletedTrip(
        ref PortfolioState state,
        ClosedTrip[] trips)
    {
        if (!TrySubtract(state.TripGrossProfitLoss, state.TripEntryCommission, out var net) ||
            !TrySubtract(net, state.TripEntrySlippage, out net) ||
            !TrySubtract(net, state.TripExitCommission, out net) ||
            !TrySubtract(net, state.TripExitSlippage, out net))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        if (state.LifetimeClosedTripCount == long.MaxValue)
            return ModelPortfolioFault.CounterOverflow;
        var nextClosedTripCount = state.LifetimeClosedTripCount + 1L;
        var nextWinningTripCount = state.LifetimeWinningTripCount;
        var nextLosingTripCount = state.LifetimeLosingTripCount;

        long nextStreak;
        if (net > 0d)
        {
            if (nextWinningTripCount == long.MaxValue)
                return ModelPortfolioFault.CounterOverflow;
            nextWinningTripCount++;

            if (state.Streak > 0L)
            {
                if (state.Streak == long.MaxValue)
                    return ModelPortfolioFault.CounterOverflow;
                nextStreak = state.Streak + 1L;
            }
            else
            {
                nextStreak = 1L;
            }
        }
        else if (net < 0d)
        {
            if (nextLosingTripCount == long.MaxValue)
                return ModelPortfolioFault.CounterOverflow;
            nextLosingTripCount++;

            if (state.Streak < 0L)
            {
                if (state.Streak == long.MinValue)
                    return ModelPortfolioFault.CounterOverflow;
                nextStreak = state.Streak - 1L;
            }
            else
            {
                nextStreak = -1L;
            }
        }
        else
        {
            nextStreak = 0L;
        }

        trips[state.TripRingNextIndex] = new ClosedTrip(
            Normalize(state.TripGrossProfitLoss),
            Normalize(state.TripPeakAbsoluteUnits),
            Normalize(state.TripPeakAbsoluteQuantity),
            state.BarsHeld,
            state.HasCapturedR,
            Normalize(state.CapturedR));
        state.TripRingNextIndex++;
        if (state.TripRingNextIndex == trips.Length)
            state.TripRingNextIndex = 0;
        if (state.TripRingCount < trips.Length)
            state.TripRingCount++;
        state.LifetimeClosedTripCount = nextClosedTripCount;
        state.LifetimeWinningTripCount = nextWinningTripCount;
        state.LifetimeLosingTripCount = nextLosingTripCount;
        state.Streak = nextStreak;
        return ModelPortfolioFault.None;
    }

    private static void ClearPosition(ref PortfolioState state)
    {
        state.PositionUnits = 0d;
        state.PositionQuantity = 0d;
        state.AverageEntryPrice = 0d;
        state.BarsHeld = 0L;
        state.TripGrossProfitLoss = 0d;
        state.TripEntryCommission = 0d;
        state.TripEntrySlippage = 0d;
        state.TripExitCommission = 0d;
        state.TripExitSlippage = 0d;
        state.TripPeakAbsoluteUnits = 0d;
        state.TripPeakAbsoluteQuantity = 0d;
        state.HasCapturedR = false;
        state.CapturedR = 0d;
        ClearExitDeclarations(ref state);
    }

    private static void ClearExitDeclarations(ref PortfolioState state)
    {
        ClearStop(ref state);
        state.HasTarget = false;
        state.TargetPrice = 0d;
        ClearTrail(ref state);
    }

    private static void ClearStop(ref PortfolioState state)
    {
        state.HasStop = false;
        state.StopPrice = 0d;
    }

    private static void ClearTrail(ref PortfolioState state)
    {
        state.HasTrail = false;
        state.TrailDistance = 0d;
        state.TrailActivationR = 0d;
        state.TrailHighWaterMark = 0d;
        state.TrailArmed = false;
    }

    private static ModelPortfolioFault TryFillCosts(
        double quantityChange,
        double referencePrice,
        out double commission,
        out double slippage)
    {
        commission = 0d;
        slippage = 0d;
        if (!TryMultiply(Math.Abs(quantityChange), referencePrice, out var notional) ||
            !TryMultiply(notional, 1d, out var commissionBasis) ||
            !TryMultiply(commissionBasis, BasisPointMultiplier, out commission) ||
            !TryMultiply(notional, 1d, out var slippageBasis) ||
            !TryMultiply(slippageBasis, BasisPointMultiplier, out slippage))
        {
            commission = 0d;
            slippage = 0d;
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        commission = Normalize(commission);
        slippage = Normalize(slippage);
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault TryCalculateOpenR(
        PortfolioState state,
        double referencePrice,
        out double riskMultiple)
    {
        riskMultiple = 0d;
        if (state.PositionUnits == 0d)
            return ModelPortfolioFault.None;
        if (!state.HasCapturedR)
            return ModelPortfolioFault.UndefinedR;
        if (!IsValidReferencePrice(referencePrice))
            return ModelPortfolioFault.InvalidReferencePrice;

        if (!TrySubtract(referencePrice, state.AverageEntryPrice, out var priceChange) ||
            !TryMultiply(state.PositionQuantity, priceChange, out var unrealizedGross) ||
            !TryMultiply(Math.Abs(state.PositionQuantity), state.CapturedR, out var denominator) ||
            denominator <= 0d ||
            !TryDivide(unrealizedGross, denominator, out riskMultiple))
        {
            riskMultiple = 0d;
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        riskMultiple = Normalize(riskMultiple);
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault TrySampleEquity(
        ref PortfolioState state,
        double referencePrice)
    {
        var equityFault = TryCalculateEquity(state, referencePrice, out var equity);
        if (equityFault != ModelPortfolioFault.None)
            return equityFault;

        var peak = Math.Max(state.EquityPeak, equity);
        if (!TrySubtract(peak, equity, out var drawdown))
            return ModelPortfolioFault.NonFiniteArithmetic;

        state.LastSampledEquity = Normalize(equity);
        state.EquityPeak = Normalize(peak);
        state.MaximumDrawdown = Normalize(Math.Max(state.MaximumDrawdown, drawdown));
        return ModelPortfolioFault.None;
    }

    private static ModelPortfolioFault TryCalculateEquity(
        PortfolioState state,
        double referencePrice,
        out double equity)
    {
        equity = 0d;
        double unrealized;
        if (state.PositionUnits == 0d)
        {
            unrealized = 0d;
        }
        else if (!TrySubtract(referencePrice, state.AverageEntryPrice, out var priceChange) ||
                 !TryMultiply(state.PositionQuantity, priceChange, out unrealized))
        {
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        if (!TryAdd(StartingEquity, state.RealizedGrossProfitLoss, out var marked) ||
            !TryAdd(marked, unrealized, out marked) ||
            !TrySubtract(marked, state.CommissionTotal, out marked) ||
            !TrySubtract(marked, state.SlippageTotal, out equity))
        {
            equity = 0d;
            return ModelPortfolioFault.NonFiniteArithmetic;
        }

        equity = Normalize(equity);
        return ModelPortfolioFault.None;
    }

    private static bool IsValidReferencePrice(double value) => double.IsFinite(value) && value > 0d;

    private static bool HaveSameSign(double left, double right) =>
        (left > 0d && right > 0d) || (left < 0d && right < 0d);

    private static double Normalize(double value) => value == 0d ? 0d : value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryAdd(double left, double right, out double result)
    {
        result = left + right;
        return double.IsFinite(result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TrySubtract(double left, double right, out double result)
    {
        result = left - right;
        return double.IsFinite(result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryMultiply(double left, double right, out double result)
    {
        result = left * right;
        return double.IsFinite(result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryDivide(double numerator, double denominator, out double result)
    {
        result = numerator / denominator;
        return double.IsFinite(result);
    }
}
