using System.Collections.Frozen;
using System.Threading.Channels;
using TradingTerminal.Sandbox.Portfolio;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>Lifecycle state for one headless sandbox kernel host.</summary>
public enum SandboxStrategyRuntimeState
{
    Idle,
    Running,
    Paused,
    Stopped,
}

/// <summary>
/// Runs one sandboxed SDK kernel against live, scoped hub data and a private model portfolio.
/// Event delivery is serialized through a bounded drop-oldest channel. A failing event is rolled
/// back and skipped; the host remains running and reports the fault through its mediated alert
/// sink. An event without a finite positive reference is still delivered, while its latest target
/// is retained in a bounded per-instrument slot and reconciled on the next priced event. Quotes and
/// depth use a non-crossed bid/ask midpoint, trades use their price, and bars use close. Canonical
/// quotes expose no last-price fallback. Historical warm-up is intentionally deferred.
/// </summary>
public sealed class SandboxStrategyRuntime :
    IStrategyLifecycle,
    IModelPortfolioSource,
    IDisposable,
    IAsyncDisposable
{
    [ThreadStatic]
    private static SandboxStrategyRuntime? _snapshotCallbackRuntime;

    public const int DefaultRetentionBound = ScopedMarketDataView.DefaultRetentionBound;

    private static readonly TimeSpan SnapshotCoalesceInterval = TimeSpan.FromMilliseconds(50);
    private static readonly BarSize[] AllBarSizes = Enum.GetValues<BarSize>();
    private const StrategyDataRequirement SupportedRequirements =
        StrategyDataRequirement.L1 |
        StrategyDataRequirement.Bars |
        StrategyDataRequirement.Depth |
        StrategyDataRequirement.TradeTape;

    private readonly Func<IStrategyKernel> _kernelFactory;
    private readonly StrategyParameterSchema _parameterSchema;
    private readonly IMarketDataHub _hub;
    private readonly IClock _clock;
    private readonly Func<IReadOnlySet<InstrumentId>, IModelPortfolioAccount> _accountFactory;
    private readonly Action<string, string, string> _appendActivityLog;
    private readonly Action<AlertRecord> _showBanner;
    private readonly int _retentionBound;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    /// <summary>
    /// Held across each event's whole consistency window, and taken without waiting by
    /// <see cref="TryDraw"/>.
    ///
    /// <para>Separate from <see cref="_lifecycleGate"/>, which serialises start/pause/resume/stop. This
    /// one serialises a FRAME against a CALLBACK, which is a different question: a kernel mutates its
    /// own fields in <c>OnBarAsync</c> and reads them in <c>Draw</c>, and the two run on different
    /// threads at different rates.</para>
    /// </summary>
    private readonly SemaphoreSlim _drawGate = new(1, 1);

    private long _skippedFrameCount;
    private readonly object _parameterGate = new();
    private readonly StrategyParameters _currentParameters;
    private readonly object _snapshotGate = new();
    private readonly Timer _snapshotTimer;

    private RuntimeSession? _session;
    private IModelPortfolio? _currentSnapshot;
    private bool _parametersLocked;
    private bool _snapshotPublishing;
    private bool _snapshotScheduled;
    private long _snapshotVersion;
    private int _state;
    private int _disposeStarted;
    private long _droppedEventCount;

    public SandboxStrategyRuntime(
        Func<IStrategyKernel> kernelFactory,
        StrategyParameterSchema parameterSchema,
        IReadOnlyDictionary<string, object?>? currentValues,
        IMarketDataHub hub,
        IClock clock,
        Func<IReadOnlySet<InstrumentId>, IModelPortfolioAccount> accountFactory,
        Action<string, string, string> appendActivityLog,
        Action<AlertRecord> showBanner,
        int retentionBound = DefaultRetentionBound)
    {
        ArgumentNullException.ThrowIfNull(kernelFactory);
        ArgumentNullException.ThrowIfNull(parameterSchema);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(accountFactory);
        ArgumentNullException.ThrowIfNull(appendActivityLog);
        ArgumentNullException.ThrowIfNull(showBanner);
        if (retentionBound <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(retentionBound),
                "The market-data retention and event-channel bound must be positive.");

        _kernelFactory = kernelFactory;
        _parameterSchema = parameterSchema;
        _hub = hub;
        _clock = clock;
        _accountFactory = accountFactory;
        _appendActivityLog = appendActivityLog;
        _showBanner = showBanner;
        _retentionBound = retentionBound;
        _currentParameters = new StrategyParameters(parameterSchema, currentValues);
        _snapshotTimer = new Timer(
            static state => ((SandboxStrategyRuntime)state!).PublishCoalescedSnapshot(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>The current lifecycle state.</summary>
    public SandboxStrategyRuntimeState State =>
        (SandboxStrategyRuntimeState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public bool IsRunning => State is SandboxStrategyRuntimeState.Running or SandboxStrategyRuntimeState.Paused;

    /// <inheritdoc />
    public bool IsPaused => State == SandboxStrategyRuntimeState.Paused;

    /// <summary>The fixed maximum number of queued live events.</summary>
    public int QueueCapacity => _retentionBound;

    /// <summary>Total events discarded by the drop-oldest overflow policy across all builds.</summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    /// <summary>The latest committed model-portfolio snapshot, or null before the first build.</summary>
    public IModelPortfolio? CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    /// <summary>
    /// Coalesced model-portfolio updates. Notifications run on a thread-pool timer and never marshal
    /// per market event to a UI thread.
    /// </summary>
    public event Action<IModelPortfolio>? SnapshotChanged;

    /// <summary>Frames skipped because a callback held the gate. Diagnostic only — a skipped frame is
    /// invisible at render cadence.</summary>
    public long SkippedFrameCount => Interlocked.Read(ref _skippedFrameCount);

    /// <summary>
    /// Describes the current frame, if the kernel can be read safely right now.
    ///
    /// <para>A strategy draws exactly like a visualizer — <c>IStrategyKernel.Draw</c> and
    /// <c>IVisualizer.Draw</c> are the same method with the same contract — but until this existed
    /// nothing ever called it. An authored strategy could paint a chart of the signal it acted on, and
    /// the picture went nowhere.</para>
    ///
    /// <para><b>Never waits</b>, for the reason the visualizer runtime gives: this runs on the render
    /// thread while the pump holds the same gate across every callback, so blocking would let any
    /// kernel freeze the window by being slow in <c>OnQuoteAsync</c>. A contended frame is skipped and
    /// the previously composited one stays up, which at render cadence is invisible.</para>
    /// </summary>
    public bool TryDraw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!IsRunning) return false;

        if (!_drawGate.Wait(0))
        {
            Interlocked.Increment(ref _skippedFrameCount);
            return false;
        }

        try
        {
            if (Volatile.Read(ref _session) is not { } session) return false;

            session.Kernel.Draw(surface);
            return true;
        }
        catch (Exception ex)
        {
            // A kernel that throws while drawing loses its picture, not its window or its position.
            ReportDrawFaultSafely(ex);
            return false;
        }
        finally
        {
            _drawGate.Release();
        }
    }

    /// <summary>
    /// The running kernel's declared window layout, with every panel callback gated.
    ///
    /// <para>Same projection, and the same reasons, as the visualizer runtime's: a panel callback
    /// closes over the kernel instance and is invoked from the render thread, so handing the raw tree
    /// over would run author code outside the gate and would pin an instance that
    /// <see cref="ResumeAsync"/> replaces.</para>
    /// </summary>
    public DaxAlgo.Sdk.Layout.UnitLayout GetLayout()
    {
        if (!IsRunning) return DaxAlgo.Sdk.Layout.UnitLayout.Single;
        if (!_drawGate.Wait(0)) return DaxAlgo.Sdk.Layout.UnitLayout.Single;

        DaxAlgo.Sdk.Layout.LayoutNode? root;
        try
        {
            if (Volatile.Read(ref _session) is not { } session)
                return DaxAlgo.Sdk.Layout.UnitLayout.Single;

            root = session.Kernel.Layout.Root;
        }
        catch (Exception ex)
        {
            ReportDrawFaultSafely(ex);
            return DaxAlgo.Sdk.Layout.UnitLayout.Single;
        }
        finally
        {
            _drawGate.Release();
        }

        return root is null
            ? DaxAlgo.Sdk.Layout.UnitLayout.Single
            : DaxAlgo.Sdk.Layout.UnitLayout.Of(Gate(root));
    }

    private DaxAlgo.Sdk.Layout.LayoutNode Gate(DaxAlgo.Sdk.Layout.LayoutNode node) => node switch
    {
        DaxAlgo.Sdk.Layout.PanelNode panel => panel with { Draw = GatedDraw(panel.Draw) },
        DaxAlgo.Sdk.Layout.SplitNode split => split with
        {
            Children = split.Children.Select(Gate).ToArray(),
        },
        _ => node,
    };

    private Action<IRenderSurface> GatedDraw(Action<IRenderSurface> draw) => surface =>
    {
        if (draw is null || !IsRunning) return;

        if (!_drawGate.Wait(0))
        {
            Interlocked.Increment(ref _skippedFrameCount);
            return;
        }

        try
        {
            // Re-read rather than capture: Resume swaps the session.
            if (Volatile.Read(ref _session) is null) return;
            draw(surface);
        }
        catch (Exception ex)
        {
            ReportDrawFaultSafely(ex);
        }
        finally
        {
            _drawGate.Release();
        }
    };

    /// <summary>Reports a draw fault through the running session's mediated alert sink, and never
    /// throws doing it — this is called from a catch on the render thread.</summary>
    private void ReportDrawFaultSafely(Exception ex)
    {
        if (Volatile.Read(ref _session) is not { } session) return;
        ReportFault(session, $"The strategy threw while drawing: {ex.Message}");
    }

    /// <summary>Updates one launch-time value while idle or paused.</summary>
    public void SetParameter(string key, object? value)
    {
        ThrowIfDisposed();
        lock (_parameterGate)
        {
            if (_parametersLocked || State is not (SandboxStrategyRuntimeState.Idle or SandboxStrategyRuntimeState.Paused))
            {
                throw new InvalidOperationException(
                    "Sandbox parameters can be edited only while the runtime is idle or paused.");
            }

            _currentParameters.Set(key, value);
        }
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SandboxStrategyRuntimeState.Idle)
                throw new InvalidOperationException("Run is valid only from the Idle state.");

            var values = LockAndSnapshotParameters();
            try
            {
                var session = await BuildSessionAsync(values, ct).ConfigureAwait(false);
                Volatile.Write(ref _session, session);
                Volatile.Write(ref _state, (int)SandboxStrategyRuntimeState.Running);
                UpdateSnapshot(session.Account.Snapshot);
            }
            catch
            {
                UnlockParameters();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Synchronously pauses the serialized pump after cancelling any in-flight handler.</summary>
    public void Pause() => PauseAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task PauseAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SandboxStrategyRuntimeState.Running)
                throw new InvalidOperationException("Pause is valid only from the Running state.");

            var session = Volatile.Read(ref _session)
                ?? throw new InvalidOperationException("The running sandbox session is unavailable.");

            Volatile.Write(ref _state, (int)SandboxStrategyRuntimeState.Paused);
            StopPump(session);
            await AwaitPumpAsync(session).ConfigureAwait(false);
            UnlockParameters();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SandboxStrategyRuntimeState.Paused)
                throw new InvalidOperationException("Resume is valid only from the Paused state.");

            var values = LockAndSnapshotParameters();
            var previous = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);
            if (previous is not null)
                await TeardownSessionAsync(previous, ct).ConfigureAwait(false);

            try
            {
                var replacement = await BuildSessionAsync(values, ct).ConfigureAwait(false);
                Volatile.Write(ref _session, replacement);
                Volatile.Write(ref _state, (int)SandboxStrategyRuntimeState.Running);
                UpdateSnapshot(replacement.Account.Snapshot);
            }
            catch
            {
                UnlockParameters();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return StopCoreAsync(ct);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        var calledFromSnapshotCallback = ReferenceEquals(_snapshotCallbackRuntime, this);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (calledFromSnapshotCallback)
                _snapshotTimer.Dispose();
            else
                await _snapshotTimer.DisposeAsync().ConfigureAwait(false);
            SnapshotChanged = null;
            GC.SuppressFinalize(this);
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == SandboxStrategyRuntimeState.Stopped)
                return;

            Volatile.Write(ref _state, (int)SandboxStrategyRuntimeState.Stopped);
            var session = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);
            if (session is not null)
            {
                StopPump(session);
                await TeardownSessionAsync(session, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private IReadOnlyDictionary<string, object?> LockAndSnapshotParameters()
    {
        lock (_parameterGate)
        {
            _parametersLocked = true;
            return _currentParameters.ToDictionary();
        }
    }

    private void UnlockParameters()
    {
        lock (_parameterGate)
            _parametersLocked = false;
    }

    private async Task<RuntimeSession> BuildSessionAsync(
        IReadOnlyDictionary<string, object?> parameterValues,
        CancellationToken ct)
    {
        var kernel = _kernelFactory()
            ?? throw new InvalidOperationException("The sandbox kernel factory returned null.");
        IModelPortfolioAccount? account = null;
        ScopedMarketDataView? data = null;
        SandboxStrategyContext? context = null;
        RuntimeSession? session = null;
        var started = false;

        try
        {
            ValidateSchema(kernel.Schema);
            ValidateDataRequirement(kernel.DataRequirement);

            var sandboxParameters = new SandboxParameters(_parameterSchema, parameterValues);
            var instruments = ResolveInstruments(sandboxParameters);
            account = _accountFactory(instruments)
                ?? throw new InvalidOperationException("The model-portfolio account factory returned null.");
            if (!instruments.Contains(account.Snapshot.Instrument))
            {
                throw new InvalidOperationException(
                    "The model-portfolio account instrument is outside the kernel's resolved instrument set.");
            }

            data = new ScopedMarketDataView(
                instruments,
                kernel.DataRequirement,
                _hub,
                _retentionBound);
            var deferredBook = new DeferredVirtualBook(instruments, account.Book);
            var alerts = new MediatedAlertSink(
                kernel.GetType().Name,
                _clock,
                _appendActivityLog,
                _showBanner);
            context = new SandboxStrategyContext(
                data,
                _clock,
                sandboxParameters,
                deferredBook,
                alerts);

            var queue = Channel.CreateBounded<MarketEventEnvelope>(
                new BoundedChannelOptions(_retentionBound)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                },
                static dropped => dropped.Owner.RecordDroppedEvent());

            session = new RuntimeSession(kernel, account, context, deferredBook, instruments, queue);
            await kernel.OnStartAsync(context, ct).ConfigureAwait(false);
            started = true;
            session.KernelStarted = true;
            ct.ThrowIfCancellationRequested();

            session.PumpTask = Task.Run(() => PumpAsync(session), CancellationToken.None);
            Volatile.Write(ref _session, session);
            SubscribeAuthorizedStreams(session, kernel.DataRequirement);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                Volatile.Write(ref _session, null);
                StopPump(session);
                await AwaitPumpAsync(session).ConfigureAwait(false);
            }

            if (started && context is not null)
            {
                try
                {
                    await kernel.OnStopAsync(context, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the build failure while still tearing down every owned resource.
                }
            }

            if (started && account is not null)
            {
                try
                {
                    account.Complete();
                }
                catch
                {
                    // Preserve the build failure while still tearing down every owned resource.
                }
            }

            await TryDisposeOwnedAsync((object?)context ?? data).ConfigureAwait(false);
            await TryDisposeOwnedAsync(account).ConfigureAwait(false);
            await TryDisposeOwnedAsync(kernel).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpAsync(RuntimeSession session)
    {
        try
        {
            await foreach (var item in session.Queue.Reader.ReadAllAsync(session.PumpCancellation.Token)
                               .ConfigureAwait(false))
            {
                if (!IsDelivering(session))
                    continue;

                try
                {
                    await ProcessEventAsync(session, item, session.PumpCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.PumpCancellation.IsCancellationRequested)
                {
                    SafeRollback(session);
                    break;
                }
                catch
                {
                    SafeRollback(session);
                    ReportFault(
                        session,
                        "Sandbox kernel event failed; the event was skipped and its portfolio window rolled back.");
                }
            }
        }
        catch (OperationCanceledException) when (session.PumpCancellation.IsCancellationRequested)
        {
            // Pause/stop cancels the pump so an async kernel handler can end promptly.
        }
        finally
        {
            SafeRollback(session);
        }
    }

    private async Task ProcessEventAsync(
        RuntimeSession session,
        MarketEventEnvelope item,
        CancellationToken ct)
    {
        // The draw gate spans the WHOLE window — deliver, reconcile, commit — not just the callback.
        // A frame taken between the kernel writing its state and the account committing would paint a
        // picture that disagrees with the book, which is the one failure a chart can make that is
        // worse than drawing nothing: it is confidently wrong.
        await _drawGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProcessEventCoreAsync(session, item, ct).ConfigureAwait(false);
        }
        finally
        {
            _drawGate.Release();
        }
    }

    private async Task ProcessEventCoreAsync(
        RuntimeSession session,
        MarketEventEnvelope item,
        CancellationToken ct)
    {
        if (!HasValidReference(item))
        {
            session.Book.BeginDeferredCallback();
            try
            {
                await DeliverAsync(session, item, ct).ConfigureAwait(false);
                session.Book.CommitDeferredCallback();
            }
            catch
            {
                session.Book.RollbackDeferredCallback();
                throw;
            }

            return;
        }

        BeginAccountWindow(session.Account, item);
        if (session.Account.LastFault != ModelPortfolioFault.None)
        {
            session.Account.Rollback();
            session.Book.DiscardPending();
            ReportAccountFault(session);
            return;
        }

        session.Book.OpenWindow();
        await DeliverAsync(session, item, ct).ConfigureAwait(false);
        if (!IsDelivering(session))
        {
            SafeRollback(session);
            return;
        }

        session.Account.ReconcileToTargets();
        if (session.Account.LastFault == ModelPortfolioFault.None)
            session.Account.Commit();

        if (session.Account.LastFault != ModelPortfolioFault.None)
        {
            session.Account.Rollback();
            session.Book.RejectWindow();
            ReportAccountFault(session);
            return;
        }

        session.Book.CommitWindow();
        UpdateSnapshot(session.Account.Snapshot);
    }

    private static Task DeliverAsync(
        RuntimeSession session,
        MarketEventEnvelope item,
        CancellationToken ct) => item.Kind switch
        {
            MarketEventKind.Quote => session.Kernel.OnQuoteAsync((Quote)item.Payload, session.Context, ct),
            MarketEventKind.Trade => session.Kernel.OnTradeAsync((TradePrint)item.Payload, session.Context, ct),
            MarketEventKind.Depth => session.Kernel.OnDepthAsync(
                item.Instrument,
                (DepthSnapshot)item.Payload,
                session.Context,
                ct),
            MarketEventKind.Bar => session.Kernel.OnBarAsync((OhlcvBar)item.Payload, session.Context, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown market event."),
        };

    private static bool HasValidReference(MarketEventEnvelope item) => item.Kind switch
    {
        MarketEventKind.Bar => IsValidPrice(((OhlcvBar)item.Payload).Close),
        MarketEventKind.Trade => IsValidPrice(((TradePrint)item.Payload).Price),
        MarketEventKind.Quote => HasValidBook(
            ((Quote)item.Payload).Bid,
            ((Quote)item.Payload).Ask),
        MarketEventKind.Depth => HasValidBook(
            ((DepthSnapshot)item.Payload).BestBid,
            ((DepthSnapshot)item.Payload).BestAsk),
        _ => false,
    };

    private static void BeginAccountWindow(IModelPortfolioAccount account, MarketEventEnvelope item)
    {
        switch (item.Kind)
        {
            case MarketEventKind.Bar:
                account.BeginBar(((OhlcvBar)item.Payload).Close);
                break;
            case MarketEventKind.Trade:
                account.BeginTick(0d, 0d, ((TradePrint)item.Payload).Price);
                break;
            case MarketEventKind.Quote:
                var quote = (Quote)item.Payload;
                account.BeginTick(quote.Bid, quote.Ask, 0d);
                break;
            case MarketEventKind.Depth:
                var depth = (DepthSnapshot)item.Payload;
                account.BeginTick(depth.BestBid, depth.BestAsk, 0d);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown market event.");
        }
    }

    private static bool IsValidPrice(double value) => double.IsFinite(value) && value > 0d;

    private static bool HasValidBook(double bid, double ask) =>
        IsValidPrice(bid) && IsValidPrice(ask) && ask >= bid;

    private void SubscribeAuthorizedStreams(
        RuntimeSession session,
        StrategyDataRequirement requirement)
    {
        foreach (var instrument in session.Instruments)
        {
            if ((requirement & StrategyDataRequirement.L1) != 0)
            {
                Subscribe(
                    session,
                    _hub.Quotes(instrument),
                    quote =>
                    {
                        if (quote.InstrumentId == instrument)
                            Enqueue(session, MarketEventEnvelope.Quote(this, quote));
                    });
            }

            if ((requirement & StrategyDataRequirement.TradeTape) != 0)
            {
                Subscribe(
                    session,
                    _hub.Trades(instrument),
                    trade =>
                    {
                        if (trade.InstrumentId == instrument)
                            Enqueue(session, MarketEventEnvelope.Trade(this, trade));
                    });
            }

            if ((requirement & StrategyDataRequirement.Bars) != 0)
            {
                foreach (var size in AllBarSizes)
                {
                    Subscribe(
                        session,
                        _hub.Bars(instrument, size),
                        bar =>
                        {
                            if (bar.InstrumentId == instrument && bar.Size == size)
                                Enqueue(session, MarketEventEnvelope.Bar(this, bar));
                        });
                }
            }

            if ((requirement & StrategyDataRequirement.Depth) != 0)
            {
                Subscribe(
                    session,
                    _hub.Depth(instrument),
                    depth => Enqueue(session, MarketEventEnvelope.Depth(this, instrument, depth)));
            }
        }
    }

    private void Subscribe<T>(RuntimeSession session, IObservable<T> source, Action<T> onNext)
    {
        ArgumentNullException.ThrowIfNull(source);
        session.Subscriptions.Add(source.Subscribe(new CallbackObserver<T>(
            onNext,
            () => ReportFault(session, "A sandbox market-data stream faulted and stopped."))));
    }

    private void Enqueue(RuntimeSession session, MarketEventEnvelope item)
    {
        if (IsDelivering(session))
            session.Queue.Writer.TryWrite(item);
    }

    private bool IsDelivering(RuntimeSession session) =>
        State == SandboxStrategyRuntimeState.Running &&
        ReferenceEquals(Volatile.Read(ref _session), session) &&
        !session.PumpCancellation.IsCancellationRequested;

    private void StopPump(RuntimeSession session)
    {
        if (Interlocked.Exchange(ref session.PumpStopped, 1) != 0)
            return;

        foreach (var subscription in session.Subscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch
            {
                ReportFault(session, "A sandbox market-data subscription failed to dispose.");
            }
        }

        session.Subscriptions.Clear();
        session.Queue.Writer.TryComplete();
        try
        {
            session.PumpCancellation.Cancel();
        }
        catch
        {
            ReportFault(
                session,
                "A sandbox kernel cancellation callback failed; host teardown continued.");
        }
    }

    private static async Task AwaitPumpAsync(RuntimeSession session)
    {
        try
        {
            await session.PumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.PumpCancellation.IsCancellationRequested)
        {
            // Expected during pause and stop.
        }
    }

    private async Task TeardownSessionAsync(RuntimeSession session, CancellationToken ct)
    {
        StopPump(session);
        await AwaitPumpAsync(session).ConfigureAwait(false);

        if (session.KernelStarted)
        {
            try
            {
                await session.Kernel.OnStopAsync(session.Context, ct).ConfigureAwait(false);
            }
            catch
            {
                ReportFault(session, "Sandbox kernel stop failed; host teardown continued.");
            }
        }

        try
        {
            session.Account.Complete();
            UpdateSnapshot(session.Account.Snapshot);
            if (session.Account.LastFault != ModelPortfolioFault.None)
                ReportAccountFault(session);
        }
        catch
        {
            ReportFault(session, "Sandbox account completion failed; host teardown continued.");
        }

        session.Book.DiscardPending();
        try
        {
            await DisposeOwnedAsync(session.Context).ConfigureAwait(false);
        }
        catch
        {
            ReportFault(session, "Sandbox context disposal failed; host teardown continued.");
        }

        try
        {
            await DisposeOwnedAsync(session.Account).ConfigureAwait(false);
        }
        catch
        {
            ReportFault(session, "Sandbox account disposal failed; host teardown continued.");
        }

        try
        {
            await DisposeOwnedAsync(session.Kernel).ConfigureAwait(false);
        }
        catch
        {
            ReportFault(session, "Sandbox kernel disposal failed; host teardown continued.");
        }
        finally
        {
            session.PumpCancellation.Dispose();
        }
    }

    private static async ValueTask DisposeOwnedAsync(object? owned)
    {
        if (owned is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (owned is IDisposable disposable)
            disposable.Dispose();
    }

    private static async ValueTask TryDisposeOwnedAsync(object? owned)
    {
        try
        {
            await DisposeOwnedAsync(owned).ConfigureAwait(false);
        }
        catch
        {
            // A prior construction/start exception remains the primary failure.
        }
    }

    private void SafeRollback(RuntimeSession session)
    {
        if (!session.Book.TryRollbackWindow())
            return;

        try
        {
            session.Account.Rollback();
        }
        catch
        {
            ReportFault(session, "Sandbox account rollback failed; the current event was abandoned.");
        }
    }

    private void ReportAccountFault(RuntimeSession session) =>
        ReportFault(
            session,
            $"Sandbox account rejected an event ({session.Account.LastFault}); its window was rolled back.");

    private static void ReportFault(RuntimeSession session, string message)
    {
        try
        {
            session.Context.Alerts.Alert(message, AlertLevel.Error, "sandbox-runtime-fault");
        }
        catch
        {
            // Alert routes are host-owned but must never tear down the serialized event pump.
        }
    }

    private IReadOnlySet<InstrumentId> ResolveInstruments(IParameters parameters)
    {
        var instruments = _parameterSchema.Parameters
            .Where(static parameter => parameter.Kind == ParameterKind.Instrument)
            .Select(parameter => parameters.GetInstrument(parameter.Key))
            .Where(static instrument => !instrument.IsNone)
            .ToFrozenSet();

        if (instruments.Count != 1)
        {
            throw new NotSupportedException(
                "SandboxStrategyRuntime requires exactly one resolved Instrument parameter; " +
                "basket model portfolios are deferred.");
        }

        return instruments;
    }

    private void ValidateSchema(StrategyParameterSchema kernelSchema)
    {
        ArgumentNullException.ThrowIfNull(kernelSchema);
        if (kernelSchema.Parameters.Count != _parameterSchema.Parameters.Count)
            throw new InvalidOperationException("The supplied parameter schema does not match the kernel schema.");

        for (var index = 0; index < kernelSchema.Parameters.Count; index++)
        {
            var kernelParameter = kernelSchema.Parameters[index];
            var suppliedParameter = _parameterSchema.Parameters[index];
            if (!string.Equals(kernelParameter.Key, suppliedParameter.Key, StringComparison.Ordinal) ||
                kernelParameter.Kind != suppliedParameter.Kind)
            {
                throw new InvalidOperationException(
                    "The supplied parameter schema does not match the kernel schema.");
            }
        }
    }

    private static void ValidateDataRequirement(StrategyDataRequirement requirement)
    {
        if ((requirement & ~SupportedRequirements) != 0)
        {
            throw new NotSupportedException(
                $"The kernel declares unsupported market-data flags: {requirement & ~SupportedRequirements}.");
        }
    }

    private void UpdateSnapshot(SandboxPortfolioSnapshot snapshot)
    {
        var boxed = (IModelPortfolio)snapshot;
        lock (_snapshotGate)
        {
            Volatile.Write(ref _currentSnapshot, boxed);
            _snapshotVersion++;
            if (_disposeStarted != 0 || _snapshotScheduled || _snapshotPublishing)
                return;

            ScheduleSnapshotLocked();
        }
    }

    private void PublishCoalescedSnapshot()
    {
        IModelPortfolio? snapshot;
        long publishedVersion;
        lock (_snapshotGate)
        {
            _snapshotScheduled = false;
            if (_disposeStarted != 0 || _snapshotPublishing)
                return;

            _snapshotPublishing = true;
            publishedVersion = _snapshotVersion;
            snapshot = Volatile.Read(ref _currentSnapshot);
        }

        var previousCallbackRuntime = _snapshotCallbackRuntime;
        _snapshotCallbackRuntime = this;
        try
        {
            var handlers = SnapshotChanged;
            if (snapshot is not null && handlers is not null)
            {
                foreach (Action<IModelPortfolio> handler in handlers.GetInvocationList())
                {
                    if (Volatile.Read(ref _disposeStarted) != 0)
                        break;

                    try
                    {
                        handler(snapshot);
                    }
                    catch
                    {
                        try
                        {
                            _appendActivityLog(
                                nameof(SandboxStrategyRuntime),
                                "ERROR",
                                "A sandbox snapshot listener failed; later coalesced updates remain active.");
                        }
                        catch
                        {
                            // A consumer callback and its diagnostic route cannot destabilize the runtime.
                        }
                    }
                }
            }
        }
        finally
        {
            _snapshotCallbackRuntime = previousCallbackRuntime;
            lock (_snapshotGate)
            {
                _snapshotPublishing = false;
                if (_disposeStarted == 0 &&
                    _snapshotVersion != publishedVersion &&
                    !_snapshotScheduled)
                {
                    ScheduleSnapshotLocked();
                }
            }
        }
    }

    private void ScheduleSnapshotLocked()
    {
        _snapshotScheduled = true;
        try
        {
            _snapshotTimer.Change(SnapshotCoalesceInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            _snapshotScheduled = false;
        }
    }

    private void RecordDroppedEvent() => Interlocked.Increment(ref _droppedEventCount);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(SandboxStrategyRuntime));
    }

    private sealed class RuntimeSession(
        IStrategyKernel kernel,
        IModelPortfolioAccount account,
        SandboxStrategyContext context,
        DeferredVirtualBook book,
        IReadOnlySet<InstrumentId> instruments,
        Channel<MarketEventEnvelope> queue)
    {
        public IStrategyKernel Kernel { get; } = kernel;
        public IModelPortfolioAccount Account { get; } = account;
        public SandboxStrategyContext Context { get; } = context;
        public DeferredVirtualBook Book { get; } = book;
        public IReadOnlySet<InstrumentId> Instruments { get; } = instruments;
        public Channel<MarketEventEnvelope> Queue { get; } = queue;
        public CancellationTokenSource PumpCancellation { get; } = new();
        public List<IDisposable> Subscriptions { get; } = new();
        public Task PumpTask { get; set; } = Task.CompletedTask;
        public bool KernelStarted { get; set; }
        public int PumpStopped;
    }

    /// <summary>
    /// Preserves the latest bounded target from an unpriced callback, then forwards it into the
    /// account book after the next valid Begin window. This is required because slice B resets its
    /// recording book at Begin.
    /// </summary>
    private sealed class DeferredVirtualBook : IVirtualBook
    {
        private readonly object _gate = new();
        private readonly HashSet<InstrumentId> _instruments;
        private readonly IVirtualBook _inner;
        private readonly Dictionary<InstrumentId, VirtualTargetIntent> _pending;
        private readonly Dictionary<InstrumentId, VirtualTargetIntent> _deferredCheckpoint;
        private bool _deferredCallbackOpen;
        private bool _windowOpen;

        public DeferredVirtualBook(IReadOnlySet<InstrumentId> instruments, IVirtualBook inner)
        {
            _instruments = new HashSet<InstrumentId>(instruments);
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pending = new Dictionary<InstrumentId, VirtualTargetIntent>(_instruments.Count);
            _deferredCheckpoint = new Dictionary<InstrumentId, VirtualTargetIntent>(_instruments.Count);
        }

        public void SubmitTarget(VirtualTargetIntent intent)
        {
            if (intent is null || !_instruments.Contains(intent.Instrument))
                return;

            lock (_gate)
            {
                if (_windowOpen)
                    _inner.SubmitTarget(intent);
                else
                    _pending[intent.Instrument] = intent;
            }
        }

        public void OpenWindow()
        {
            lock (_gate)
            {
                _windowOpen = true;
                foreach (var intent in _pending.Values)
                    _inner.SubmitTarget(intent);
            }
        }

        public void CommitWindow()
        {
            lock (_gate)
            {
                _windowOpen = false;
                _pending.Clear();
            }
        }

        public void RejectWindow()
        {
            lock (_gate)
            {
                _windowOpen = false;
                _pending.Clear();
            }
        }

        public bool TryRollbackWindow()
        {
            lock (_gate)
            {
                if (!_windowOpen)
                    return false;

                _windowOpen = false;
                return true;
            }
        }

        public void BeginDeferredCallback()
        {
            lock (_gate)
            {
                _deferredCheckpoint.Clear();
                foreach (var pair in _pending)
                    _deferredCheckpoint.Add(pair.Key, pair.Value);
                _deferredCallbackOpen = true;
            }
        }

        public void CommitDeferredCallback()
        {
            lock (_gate)
            {
                _deferredCallbackOpen = false;
                _deferredCheckpoint.Clear();
            }
        }

        public void RollbackDeferredCallback()
        {
            lock (_gate)
            {
                if (!_deferredCallbackOpen)
                    return;

                _pending.Clear();
                foreach (var pair in _deferredCheckpoint)
                    _pending.Add(pair.Key, pair.Value);
                _deferredCallbackOpen = false;
                _deferredCheckpoint.Clear();
            }
        }

        public void DiscardPending()
        {
            lock (_gate)
            {
                _windowOpen = false;
                _deferredCallbackOpen = false;
                _pending.Clear();
                _deferredCheckpoint.Clear();
            }
        }
    }

    private sealed class CallbackObserver<T>(Action<T> onNext, Action onError) : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) => onError();

        public void OnNext(T value) => onNext(value);
    }

    private enum MarketEventKind
    {
        Quote,
        Trade,
        Depth,
        Bar,
    }

    private readonly record struct MarketEventEnvelope(
        SandboxStrategyRuntime Owner,
        MarketEventKind Kind,
        InstrumentId Instrument,
        object Payload)
    {
        public static MarketEventEnvelope Quote(SandboxStrategyRuntime owner, Quote quote) =>
            new(owner, MarketEventKind.Quote, quote.InstrumentId, quote);

        public static MarketEventEnvelope Trade(SandboxStrategyRuntime owner, TradePrint trade) =>
            new(owner, MarketEventKind.Trade, trade.InstrumentId, trade);

        public static MarketEventEnvelope Depth(
            SandboxStrategyRuntime owner,
            InstrumentId instrument,
            DepthSnapshot depth) =>
            new(owner, MarketEventKind.Depth, instrument, depth);

        public static MarketEventEnvelope Bar(SandboxStrategyRuntime owner, OhlcvBar bar) =>
            new(owner, MarketEventKind.Bar, bar.InstrumentId, bar);
    }
}
