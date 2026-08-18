using TradingTerminal.Sandbox.Portfolio;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>
/// Single-instrument write-side sandbox account backed by <see cref="ModelPortfolioSimulator"/>.
/// The simulator is a single-position engine; basket accounting with shared equity requires a
/// separate portfolio-level account and is intentionally not represented by this type.
/// </summary>
public sealed class ModelPortfolioAccount : IModelPortfolioAccount
{
    private const long AbsolutePriceMode = 1L;
    private const string SingleInstrumentError =
        "ModelPortfolioAccount supports exactly one declared instrument; " +
        "multi-instrument portfolios require a portfolio-level account with shared equity.";

    private readonly InstrumentId _instrument;
    private readonly RecordingVirtualBook _book;
    private readonly ModelPortfolioSimulator _simulator;
    private VirtualTargetIntent? _committedTarget;
    private VirtualTargetIntent? _pendingTarget;
    private bool _windowOpen;

    /// <summary>Creates an account for one canonical instrument.</summary>
    public ModelPortfolioAccount(
        InstrumentId instrument,
        ModelPortfolioAccountConfig? config = null)
        : this(new HashSet<InstrumentId> { instrument }, config)
    {
    }

    /// <summary>
    /// Creates an account from a declared set, rejecting every set whose cardinality is not one.
    /// Multi-instrument portfolios are deliberately deferred because the wrapped simulator cannot
    /// express shared basket equity.
    /// </summary>
    public ModelPortfolioAccount(
        IReadOnlySet<InstrumentId> declaredInstruments,
        ModelPortfolioAccountConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(declaredInstruments);
        if (declaredInstruments.Count != 1)
            throw new NotSupportedException(SingleInstrumentError);

        _instrument = declaredInstruments.Single();
        if (_instrument.IsNone)
            throw new ArgumentException("The declared instrument must be resolved.", nameof(declaredInstruments));

        config ??= new ModelPortfolioAccountConfig();
        var createFault = ModelPortfolioSimulator.TryCreate(
            config.MaxAbsoluteUnits,
            config.RetainedClosedTrips,
            out var simulator);
        if (createFault != ModelPortfolioFault.None || simulator is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                $"The model-portfolio configuration is invalid ({createFault}).");
        }

        _simulator = simulator;
        _book = new RecordingVirtualBook(declaredInstruments);
    }

    /// <inheritdoc />
    public IVirtualBook Book => _book;

    /// <summary>
    /// The latest captured engine fault, or <see cref="ModelPortfolioFault.None"/> after the
    /// latest successful begin, commit, or completion. This single value is the bounded host fault
    /// log; rollback preserves the fault that caused the host to select it.
    /// </summary>
    public ModelPortfolioFault LastFault { get; private set; }

    /// <inheritdoc />
    public SandboxPortfolioSnapshot Snapshot => Project(_simulator.CommittedSnapshot);

    /// <inheritdoc />
    public void BeginBar(double close)
    {
        _pendingTarget = null;
        _book.Reset();
        LastFault = _simulator.BeginOnBar(close);
        _windowOpen = LastFault == ModelPortfolioFault.None;
        if (!_windowOpen)
            _simulator.RollbackCallback();
    }

    /// <inheritdoc />
    public void BeginTick(double bid, double ask, double last)
    {
        _pendingTarget = null;
        _book.Reset();
        LastFault = _simulator.BeginOnTick(bid, ask, last);
        _windowOpen = LastFault == ModelPortfolioFault.None;
        if (!_windowOpen)
            _simulator.RollbackCallback();
    }

    /// <inheritdoc />
    public void ReconcileToTargets()
    {
        try
        {
            if (!_windowOpen || !_book.TryGetTarget(_instrument, out var intent) || intent is null)
                return;

            var fault = _simulator.MpPosition(out var currentUnits);
            if (Capture(fault))
                return;

            if (intent.IsPendingEntry)
            {
                // A resting entry replaces the immediate trade: the book arms it and waits for the
                // price instead of converging now. Only meaningful while flat - the simulator
                // refuses it otherwise rather than quietly turning it into a market order.
                if (currentUnits == 0d)
                {
                    fault = _simulator.MpPendingEntry(
                        intent.EntryTriggerPrice!.Value,
                        intent.TargetUnits,
                        intent.EntryKind == VirtualEntryKind.Stop);
                    if (Capture(fault))
                        return;

                    _pendingTarget = intent;
                    return;
                }

                Capture(ModelPortfolioFault.PendingEntryWhileInPosition);
                return;
            }

            // A plain target cancels any entry still resting: the strategy has changed its mind.
            if (Capture(_simulator.MpCancelPendingEntry()))
                return;

            var delta = intent.TargetUnits - currentUnits;
            if (delta != 0d)
            {
                fault = _simulator.MpMarket(delta, out var fillPrice);
                if (Capture(fault))
                    return;
                if (fillPrice == 0d)
                    return;
            }

            fault = _simulator.MpPosition(out currentUnits);
            if (Capture(fault))
                return;

            if (currentUnits != 0d)
            {
                if (Capture(_simulator.MpCancelExits()))
                    return;

                if (intent.ProtectiveStopPrice is double stopPrice &&
                    Capture(_simulator.MpStop(AbsolutePriceMode, stopPrice)))
                {
                    return;
                }

                if (intent.ProfitTargetPrice is double targetPrice &&
                    Capture(_simulator.MpTarget(AbsolutePriceMode, targetPrice)))
                {
                    return;
                }
            }

            _pendingTarget = intent;
        }
        finally
        {
            _book.Reset();
        }
    }

    /// <inheritdoc />
    public void Commit()
    {
        if (!_windowOpen)
        {
            if (LastFault == ModelPortfolioFault.None)
                LastFault = _simulator.CommitCallback();
            return;
        }

        LastFault = _simulator.CommitCallback();
        _windowOpen = false;
        if (LastFault == ModelPortfolioFault.None)
        {
            _committedTarget = _simulator.CommittedSnapshot.PositionUnits == 0d
                ? null
                : _pendingTarget ?? _committedTarget;
        }
        _pendingTarget = null;
    }

    /// <inheritdoc />
    public void Rollback()
    {
        _simulator.RollbackCallback();
        _windowOpen = false;
        _pendingTarget = null;
        _book.Reset();
    }

    /// <inheritdoc />
    public void Complete()
    {
        LastFault = _simulator.CompleteRun();
        _pendingTarget = null;
        if (LastFault == ModelPortfolioFault.None && _simulator.CommittedSnapshot.PositionUnits == 0d)
            _committedTarget = null;
    }

    private bool Capture(ModelPortfolioFault fault)
    {
        if (fault == ModelPortfolioFault.None)
            return false;

        LastFault = fault;
        return true;
    }

    private SandboxPortfolioSnapshot Project(ModelPortfolioSnapshot snapshot) => new(
        _instrument,
        snapshot.PositionUnits,
        snapshot.PositionQuantity,
        snapshot.AverageEntryPrice,
        snapshot.BarsHeld,
        snapshot.Equity,
        snapshot.RealizedGrossProfitLoss,
        snapshot.CommissionTotal,
        snapshot.SlippageTotal,
        snapshot.EquityPeak,
        snapshot.MaximumDrawdown,
        snapshot.LifetimeClosedTripCount,
        snapshot.LifetimeWinningTripCount,
        snapshot.LifetimeLosingTripCount,
        snapshot.RetainedTradeCount,
        snapshot.Streak,
        snapshot.IsCompleted,
        snapshot.PositionUnits == 0d ? null : _committedTarget?.ProtectiveStopPrice,
        snapshot.PositionUnits == 0d ? null : _committedTarget?.ProfitTargetPrice,
        snapshot.HasPendingEntry
            ? new PendingEntryState(
                snapshot.PendingEntryPrice,
                snapshot.PendingEntryUnits,
                snapshot.PendingEntryIsStop)
            : null);
}
