using System.Threading.Channels;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.Execution;

/// <summary>Result returned by the guarded book target-intake seam.</summary>
public readonly record struct ExecutionTargetSubmissionResult(bool IsSuccess, string Message)
{
    public static ExecutionTargetSubmissionResult Success(string message) => new(true, message);

    public static ExecutionTargetSubmissionResult Failure(string message) => new(false, message);
}

/// <summary>
/// Narrow book-bound intake used by target sources. Implementations remain responsible for pause,
/// reconciliation, risk, lease/fencing, coordinator, and execution-mode authorization gates.
/// </summary>
public interface IExecutionBookTargetIntake
{
    ValueTask<ExecutionTargetSubmissionResult> SubmitTargetAsync(
        string bookId,
        TradeIntent intent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Binds one sandbox runtime's virtual book to one execution book.
///
/// <para><b>Enabled by default since 2026-08-17.</b> The virtual book is now the only route a
/// strategy has to execution: a strategy trades its own book, and the execution engine copies that
/// book. It never reaches the OMS directly. That is what makes paper and real trading the same code
/// path — the strategy cannot tell, and cannot behave differently, because it is only ever writing to
/// its own wallet.</para>
///
/// <para><see cref="Enabled"/> remains settable so a test can bind a replicator and assert it stays
/// quiet, but production composition leaves it on.</para>
/// </summary>
public sealed record SandboxExecutionReplicationOptions(
    string BookId,
    string StrategyId,
    bool Enabled = true,
    string PolicyVersion = SandboxExecutionReplicator.DefaultPolicyVersion,
    ScaledMoney EstimatedRoundTripCostPerUnit = default);

/// <summary>One attempted replication and its gated intake result.</summary>
public readonly record struct SandboxExecutionReplicationOutcome(
    TradeIntent? Intent,
    ExecutionTargetSubmissionResult Result);

/// <summary>
/// Coalesces committed sandbox model-portfolio snapshots and maps each changed target 1:1 to a
/// canonical TargetPosition intent. It knows no broker or order adapter; the bound intake owns the
/// complete guarded execution chain.
/// </summary>
public sealed class SandboxExecutionReplicator : IDisposable, IAsyncDisposable
{
    public const string DefaultPolicyVersion = "sandbox-model-portfolio-v1";

    private const byte PriceScale = 8;
    private readonly object _gate = new();
    private readonly object _queueGate = new();
    private readonly IModelPortfolioSource _source;
    private readonly IExecutionBookTargetIntake _intake;
    private readonly SandboxExecutionReplicationOptions _options;
    private readonly Channel<bool> _signals;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;
    private TradeIntent? _lastAcceptedIntent;
    private SandboxExecutionReplicationOutcome? _lastOutcome;
    private IModelPortfolio? _pendingSnapshot;
    private bool _pendingForce;
    private int _disposeStarted;

    public SandboxExecutionReplicator(
        IModelPortfolioSource source,
        IExecutionBookTargetIntake intake,
        SandboxExecutionReplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BookId))
            throw new ArgumentException("A bound execution book id is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.StrategyId))
            throw new ArgumentException("Stable sandbox strategy provenance is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.PolicyVersion))
            throw new ArgumentException("A sandbox replication policy version is required.", nameof(options));
        if (!options.EstimatedRoundTripCostPerUnit.IsValid ||
            options.EstimatedRoundTripCostPerUnit.Coefficient < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Estimated round-trip cost must be an exact non-negative money value.");
        }

        _source = source;
        _intake = intake;
        _options = options with
        {
            BookId = options.BookId.Trim(),
            StrategyId = options.StrategyId.Trim(),
            PolicyVersion = options.PolicyVersion.Trim(),
        };
        _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });

        if (!_options.Enabled)
        {
            _pump = Task.CompletedTask;
            return;
        }

        _source.SnapshotChanged += OnSnapshotChanged;
        _pump = Task.Run(PumpAsync);
        QueueCurrent(force: false);
    }

    /// <summary>Whether this binding was explicitly enabled.</summary>
    public bool IsEnabled => _options.Enabled;

    /// <summary>The latest completed attempt, including closed-gate failures.</summary>
    public SandboxExecutionReplicationOutcome? LastOutcome
    {
        get
        {
            lock (_gate)
                return _lastOutcome;
        }
    }

    /// <summary>Raised after each mapped snapshot reaches the guarded book intake.</summary>
    public event Action<SandboxExecutionReplicationOutcome>? SubmissionCompleted;

    /// <summary>Queues the current committed snapshot, primarily for an explicit retry after resume.</summary>
    public bool ReplicateCurrent()
    {
        if (!_options.Enabled || Volatile.Read(ref _disposeStarted) != 0)
            return false;

        return QueueCurrent(force: true);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        if (_options.Enabled)
            _source.SnapshotChanged -= OnSnapshotChanged;
        _signals.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private void OnSnapshotChanged(IModelPortfolio snapshot)
    {
        if (Volatile.Read(ref _disposeStarted) == 0)
            Queue(snapshot, force: false);
    }

    private async Task PumpAsync()
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
        {
            IModelPortfolio? snapshot;
            bool force;
            lock (_queueGate)
            {
                snapshot = _pendingSnapshot;
                force = _pendingForce;
                _pendingSnapshot = null;
                _pendingForce = false;
            }
            if (snapshot is null)
                continue;
            await ReplicateAsync(snapshot, force, _cancellation.Token).ConfigureAwait(false);
        }
    }

    private async ValueTask ReplicateAsync(
        IModelPortfolio snapshot,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!TryMap(snapshot, out var intent, out var failure))
        {
            Publish(new SandboxExecutionReplicationOutcome(
                null,
                ExecutionTargetSubmissionResult.Failure(failure)));
            return;
        }

        lock (_gate)
        {
            if (!force && _lastAcceptedIntent == intent)
                return;
        }

        ExecutionTargetSubmissionResult result;
        try
        {
            result = await _intake
                .SubmitTargetAsync(_options.BookId, intent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = ExecutionTargetSubmissionResult.Failure(
                $"Sandbox target intake failed closed ({exception.GetType().Name}).");
        }

        lock (_gate)
        {
            if (result.IsSuccess)
                _lastAcceptedIntent = intent;
        }
        Publish(new SandboxExecutionReplicationOutcome(intent, result));
    }

    private bool QueueCurrent(bool force)
    {
        var snapshot = _source.CurrentSnapshot;
        return snapshot is not null && Queue(snapshot, force);
    }

    private bool Queue(IModelPortfolio snapshot, bool force)
    {
        lock (_queueGate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
                return false;
            _pendingSnapshot = snapshot;
            _pendingForce |= force;
            _signals.Writer.TryWrite(true);
            return true;
        }
    }

    private bool TryMap(IModelPortfolio snapshot, out TradeIntent intent, out string failure)
    {
        intent = default;
        failure = string.Empty;
        if (snapshot.Instrument.IsNone)
        {
            failure = "Sandbox replication refused an unresolved instrument.";
            return false;
        }
        if (!TryWholeUnits(snapshot.PositionUnits, out var units))
        {
            failure = "Sandbox replication requires exact whole target units; no re-sizing or rounding is allowed.";
            return false;
        }
        if (!TryPrice(snapshot.ProtectiveStopPrice, out var stop))
        {
            failure = "Sandbox replication refused a non-exact protective-stop price.";
            return false;
        }
        if (!TryPrice(snapshot.ProfitTargetPrice, out var target))
        {
            failure = "Sandbox replication refused a non-exact profit-target price.";
            return false;
        }

        // A resting entry is mirrored to the venue as a real pending order rather than watched
        // locally: the book is still flat and waiting, so the target comes from the armed entry, not
        // from the current position, and the trigger price rides along as the entry condition.
        ScaledPrice? entryLimit = null;
        ScaledPrice? entryStop = null;
        if (snapshot.PendingEntry is { } pending)
        {
            if (!TryWholeUnits(pending.SignedTargetUnits, out units))
            {
                failure = "Sandbox replication requires exact whole pending-entry units.";
                return false;
            }
            if (!TryPrice(pending.TriggerPrice, out var trigger) || trigger is null)
            {
                failure = "Sandbox replication refused a non-exact pending-entry trigger price.";
                return false;
            }
            if (pending.IsStop)
                entryStop = trigger;
            else
                entryLimit = trigger;
        }

        intent = new TradeIntent(
            snapshot.Instrument,
            TradeIntentQuantityMode.TargetPosition,
            ScaledQuantity.FromWhole(units),
            stop,
            target,
            _options.EstimatedRoundTripCostPerUnit,
            _options.StrategyId,
            StrategyNoteId: 0,
            _options.PolicyVersion,
            entryLimit,
            entryStop);
        return true;
    }

    private static bool TryWholeUnits(double value, out long units)
    {
        units = 0;
        if (!double.IsFinite(value) || Math.Truncate(value) != value)
            return false;
        try
        {
            units = checked((long)value);
        }
        catch (OverflowException)
        {
            return false;
        }
        return (double)units == value;
    }

    private static bool TryPrice(double? value, out ScaledPrice? price)
    {
        price = null;
        if (value is null)
            return true;
        if (!double.IsFinite(value.Value) || value.Value <= 0d ||
            !ScaledValueMath.TryQuantizeDouble(value.Value, PriceScale, out var coefficient))
        {
            return false;
        }

        var exact = (double)((decimal)coefficient / 100_000_000m);
        if (exact != value.Value ||
            !ScaledValueMath.TryNarrow(coefficient, PriceScale, out var narrowed, out var scale))
        {
            return false;
        }

        price = new ScaledPrice(narrowed, scale);
        return price.Value.IsValid && price.Value.Coefficient > 0;
    }

    private void Publish(SandboxExecutionReplicationOutcome outcome)
    {
        Action<SandboxExecutionReplicationOutcome>? handlers;
        lock (_gate)
        {
            _lastOutcome = outcome;
            handlers = SubmissionCompleted;
        }

        if (handlers is null)
            return;
        ThreadPool.QueueUserWorkItem(
            static dispatch => InvokeHandlers(dispatch.Handlers, dispatch.Outcome),
            new CallbackDispatch(handlers, outcome),
            preferLocal: false);
    }

    private static void InvokeHandlers(
        Action<SandboxExecutionReplicationOutcome> handlers,
        SandboxExecutionReplicationOutcome outcome)
    {
        foreach (Action<SandboxExecutionReplicationOutcome> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(outcome);
            }
            catch
            {
                // A consumer callback cannot break the serialized replication pump.
            }
        }
    }

    private sealed record CallbackDispatch(
        Action<SandboxExecutionReplicationOutcome> Handlers,
        SandboxExecutionReplicationOutcome Outcome);

}
