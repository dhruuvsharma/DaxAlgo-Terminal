using System.Collections.Frozen;
using System.Threading.Channels;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Sandbox;

/// <summary>Lifecycle state for one automatically hosted sandbox visualizer.</summary>
public enum SandboxVisualizerRuntimeState
{
    Idle,
    Running,
    Paused,
    Stopped,
}

/// <summary>
/// Runs one sandboxed visualizer over its declared instruments and market-data streams. Delivery is
/// serialized through a bounded drop-oldest channel; slow visualizers therefore shed the oldest
/// queued work instead of growing memory without bound. Event faults are reported through the
/// visualizer's mediated alert sink and do not terminate the pump.
/// </summary>
public sealed class SandboxVisualizerRuntime :
    IVisualizerLifecycle,
    IDisposable,
    IAsyncDisposable
{
    private static readonly AsyncLocal<CallbackScope?> CurrentCallback = new();

    public const int DefaultRetentionBound = ScopedMarketDataView.DefaultRetentionBound;

    private static readonly BarSize[] AllBarSizes = Enum.GetValues<BarSize>();
    private const StrategyDataRequirement SupportedRequirements =
        StrategyDataRequirement.L1 |
        StrategyDataRequirement.Bars |
        StrategyDataRequirement.Depth |
        StrategyDataRequirement.TradeTape;

    private readonly Func<IVisualizer> _visualizerFactory;
    private readonly IReadOnlyDictionary<string, object?>? _initialParameterValues;
    private readonly IMarketDataHub _hub;
    private readonly IClock _clock;
    private readonly Action<string, string, string> _appendActivityLog;
    private readonly Action<AlertRecord> _showBanner;
    private readonly int _retentionBound;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _drawGate = new(1, 1);
    private readonly object _parameterGate = new();
    private readonly object _disposeGate = new();

    private RuntimeSession? _session;
    private StrategyParameterSchema? _parameterSchema;
    private StrategyParameters? _currentParameters;
    private Task? _disposeTask;
    private bool _parametersLocked;
    private int _state;
    private int _disposeStarted;
    private long _droppedEventCount;
    private long _skippedFrameCount;
    private long _drawFaultCount;

    public SandboxVisualizerRuntime(
        Func<IVisualizer> visualizerFactory,
        IReadOnlyDictionary<string, object?>? currentValues,
        IMarketDataHub hub,
        IClock clock,
        Action<string, string, string> appendActivityLog,
        Action<AlertRecord> showBanner,
        int retentionBound = DefaultRetentionBound,
        Action<string, string>? offerTakeAway = null)
    {
        ArgumentNullException.ThrowIfNull(visualizerFactory);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(appendActivityLog);
        ArgumentNullException.ThrowIfNull(showBanner);
        if (retentionBound <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionBound),
                "The market-data retention and event-channel bound must be positive.");
        }

        _offerTakeAway = offerTakeAway;
        _visualizerFactory = visualizerFactory;
        _initialParameterValues = currentValues is null
            ? null
            : new Dictionary<string, object?>(currentValues, StringComparer.Ordinal);
        _hub = hub;
        _clock = clock;
        _appendActivityLog = appendActivityLog;
        _showBanner = showBanner;
        _retentionBound = retentionBound;
    }

    /// <summary>The current lifecycle state.</summary>
    public SandboxVisualizerRuntimeState State =>
        (SandboxVisualizerRuntimeState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public bool IsRunning =>
        (State is SandboxVisualizerRuntimeState.Running or SandboxVisualizerRuntimeState.Paused) &&
        Volatile.Read(ref _session) is not null;

    /// <inheritdoc />
    public bool IsPaused => State == SandboxVisualizerRuntimeState.Paused;

    /// <summary>The fixed maximum number of queued market-data events.</summary>
    public int QueueCapacity => _retentionBound;

    /// <summary>Total events discarded by the drop-oldest policy across all visualizer builds.</summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    /// <summary>Frames the visualizer threw out of. Announced once; counted every time.</summary>
    public long DrawFaultCount => Interlocked.Read(ref _drawFaultCount);

    /// <summary>Frames skipped because the visualizer was mid-event when the host tried to draw.</summary>
    public long SkippedFrameCount => Interlocked.Read(ref _skippedFrameCount);

    /// <summary>
    /// Asks the running visualizer to describe the current frame, and reports whether it did.
    ///
    /// <para><b>Never waits.</b> This is called from the render thread, and the pump holds the same
    /// gate across each market-data callback; blocking here would hand an arbitrary visualizer the
    /// ability to freeze the window by being slow in <c>OnQuoteAsync</c>. A contended frame is simply
    /// skipped — the previously composited frame stays on screen, which at render cadence is
    /// invisible, whereas a stalled UI thread is not.</para>
    ///
    /// <para>Returns <see langword="false"/> when there is nothing to draw (not running, disposed, or
    /// contended) so the host can leave the surface alone rather than paint an empty one.</para>
    /// </summary>
    public bool TryDraw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!IsRunning || Volatile.Read(ref _disposeStarted) != 0)
            return false;

        if (!_drawGate.Wait(0))
        {
            Interlocked.Increment(ref _skippedFrameCount);
            return false;
        }

        try
        {
            if (Volatile.Read(ref _session) is not { } session)
                return false;

            session.Visualizer.Draw(surface);
            return true;
        }
        catch (Exception ex)
        {
            // A visualizer that throws while drawing loses its picture, not its window. The renderer
            // keeps whatever it had; reporting it here is what makes the fault findable.
            ReportDrawFault(ex);
            return false;
        }
        finally
        {
            _drawGate.Release();
        }
    }

    /// <summary>
    /// The running visualizer's declared window layout, with every panel callback gated.
    ///
    /// <para><b>The unit's own <c>Layout</c> must not be handed to the host directly</b>, and that is
    /// the whole reason this method exists rather than a property. A <c>PanelNode</c> carries an
    /// <c>Action&lt;IRenderSurface&gt;</c> that closes over the visualizer instance; the layout host
    /// invokes it straight from the render thread, so passing the raw tree would run author code
    /// outside <see cref="_drawGate"/> — the exact protection <see cref="TryDraw"/> exists for — while
    /// a market-data callback is halfway through mutating the same state. It would also pin the
    /// <i>old</i> instance: <see cref="ResumeAsync"/> builds a replacement session, and a captured
    /// callback would keep drawing the one that was torn down.</para>
    ///
    /// <para>So each panel is re-wrapped: take the gate without waiting, re-read the live session, and
    /// skip the frame if either is unavailable. Same contract as <see cref="TryDraw"/>, per panel.</para>
    ///
    /// <para>Returns <see cref="DaxAlgo.Sdk.Layout.UnitLayout.Single"/> when the unit declares no
    /// layout, is not running, or throws while being asked — the single-panel default is always a
    /// usable window, and a layout is not worth a failure.</para>
    /// </summary>
    public DaxAlgo.Sdk.Layout.UnitLayout GetLayout()
    {
        if (!IsRunning || Volatile.Read(ref _disposeStarted) != 0)
            return DaxAlgo.Sdk.Layout.UnitLayout.Single;

        if (!_drawGate.Wait(0))
            return DaxAlgo.Sdk.Layout.UnitLayout.Single;

        DaxAlgo.Sdk.Layout.LayoutNode? root;
        try
        {
            if (Volatile.Read(ref _session) is not { } session)
                return DaxAlgo.Sdk.Layout.UnitLayout.Single;

            root = session.Visualizer.Layout.Root;
        }
        catch (Exception ex)
        {
            // Reading Layout runs author code. A unit that throws describing its window keeps the
            // default one rather than losing it.
            ReportDrawFault(ex);
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

    /// <summary>Rebuilds a layout tree with every panel's callback wrapped in the draw gate.</summary>
    private DaxAlgo.Sdk.Layout.LayoutNode Gate(DaxAlgo.Sdk.Layout.LayoutNode node) => node switch
    {
        DaxAlgo.Sdk.Layout.PanelNode panel => panel with { Draw = GatedDraw(panel.Draw) },
        DaxAlgo.Sdk.Layout.SplitNode split => split with
        {
            Children = split.Children.Select(Gate).ToArray(),
        },
        _ => node,
    };

    /// <summary>One panel's callback, holding the same non-blocking gate <see cref="TryDraw"/> takes.</summary>
    private Action<IRenderSurface> GatedDraw(Action<IRenderSurface> draw) => surface =>
    {
        if (draw is null || !IsRunning || Volatile.Read(ref _disposeStarted) != 0)
            return;

        if (!_drawGate.Wait(0))
        {
            Interlocked.Increment(ref _skippedFrameCount);
            return;
        }

        try
        {
            // Re-read rather than capture: Resume swaps the session, and a panel bound to the old one
            // would go on painting a visualizer that has been torn down.
            if (Volatile.Read(ref _session) is null) return;
            draw(surface);
        }
        catch (Exception ex)
        {
            ReportDrawFault(ex);
        }
        finally
        {
            _drawGate.Release();
        }
    };

    /// <summary>Updates one launch-time value while the visualizer is paused.</summary>
    public void SetParameter(string key, object? value)
    {
        ThrowIfDisposed();
        lock (_parameterGate)
        {
            if (_parametersLocked ||
                State != SandboxVisualizerRuntimeState.Paused ||
                _currentParameters is null)
            {
                throw new InvalidOperationException(
                    "Visualizer parameters can be edited only after Pause has completed.");
            }

            _currentParameters.Set(key, value);
        }
    }

    /// <summary>Builds and automatically starts a fresh visualizer.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfLifecycleReentrant(nameof(StartAsync));
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SandboxVisualizerRuntimeState.Idle)
                throw new InvalidOperationException("Start is valid only from the Idle state.");

            try
            {
                var session = await BuildSessionAsync(parameterValues: null, initializeParameters: true, ct)
                    .ConfigureAwait(false);
                Volatile.Write(ref _session, session);
                Volatile.Write(ref _state, (int)SandboxVisualizerRuntimeState.Running);
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

    /// <summary>Synchronously pauses the serialized event pump.</summary>
    public void Pause() => PauseAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task PauseAsync(CancellationToken ct = default)
    {
        ThrowIfLifecycleReentrant(nameof(PauseAsync));
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SandboxVisualizerRuntimeState.Running)
                throw new InvalidOperationException("Pause is valid only from the Running state.");

            var session = Volatile.Read(ref _session)
                ?? throw new InvalidOperationException("The running visualizer session is unavailable.");

            Volatile.Write(ref _state, (int)SandboxVisualizerRuntimeState.Paused);
            StopPump(session);
            await AwaitPumpAsync(session).ConfigureAwait(false);
            UnlockParameters();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Synchronously rebuilds and resumes the paused visualizer.</summary>
    public void Resume() => ResumeAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        ThrowIfLifecycleReentrant(nameof(ResumeAsync));
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SandboxVisualizerRuntimeState.Paused)
                throw new InvalidOperationException("Resume is valid only from the Paused state.");

            var values = LockAndSnapshotParameters();
            var previous = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);

            try
            {
                if (previous is not null)
                    await TeardownSessionAsync(previous, ct).ConfigureAwait(false);

                var replacement = await BuildSessionAsync(values, initializeParameters: false, ct)
                    .ConfigureAwait(false);
                Volatile.Write(ref _session, replacement);
                Volatile.Write(ref _state, (int)SandboxVisualizerRuntimeState.Running);
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
        ThrowIfLifecycleReentrant(nameof(StopAsync));
        ThrowIfDisposed();
        return StopCoreAsync(ct);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        ThrowIfLifecycleReentrant(nameof(DisposeAsync));
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposeStarted, 1);
        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == SandboxVisualizerRuntimeState.Stopped)
                return;

            Volatile.Write(ref _state, (int)SandboxVisualizerRuntimeState.Stopped);
            var session = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);
            if (session is not null)
                await TeardownSessionAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<RuntimeSession> BuildSessionAsync(
        IReadOnlyDictionary<string, object?>? parameterValues,
        bool initializeParameters,
        CancellationToken ct)
    {
        var visualizer = InvokeCallback(_visualizerFactory)
            ?? throw new InvalidOperationException("The sandbox visualizer factory returned null.");
        SandboxVisualizerContext? context = null;
        RuntimeSession? session = null;
        var started = false;

        try
        {
            var visualizerSchema = InvokeCallback(() => visualizer.Schema);
            if (initializeParameters)
                parameterValues = InitializeParametersAndLock(visualizerSchema);
            else
                ValidateSchema(visualizerSchema);

            var dataRequirement = InvokeCallback(() => visualizer.DataRequirement);
            ValidateDataRequirement(dataRequirement);
            var schema = _parameterSchema
                ?? throw new InvalidOperationException("The visualizer parameter schema is unavailable.");
            var values = parameterValues
                ?? throw new InvalidOperationException("The visualizer parameter values are unavailable.");
            var sandboxParameters = new SandboxParameters(schema, values);
            var instruments = ResolveInstruments(sandboxParameters);

            context = SandboxVisualizerContextFactory.Create(
                instruments,
                dataRequirement,
                _hub,
                _clock,
                schema,
                values,
                visualizer.GetType().Name,
                _appendActivityLog,
                _showBanner,
                _retentionBound);

            // Attached after construction: the sink has to be able to ask this runtime whether an
            // action is in flight, which it cannot do before the runtime has a context to give it.
            context.Export = new MediatedExport(this);

            var queue = Channel.CreateBounded<MarketEventEnvelope>(
                new BoundedChannelOptions(_retentionBound)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                },
                static dropped => dropped.Owner.RecordDroppedEvent());

            session = new RuntimeSession(
                visualizer,
                context,
                instruments,
                dataRequirement,
                queue);
            await InvokeCallbackAsync(() => visualizer.OnStartAsync(context, ct)).ConfigureAwait(false);
            started = true;
            session.VisualizerStarted = true;
            ct.ThrowIfCancellationRequested();

            session.PumpTask = Task.Run(() => PumpAsync(session), CancellationToken.None);
            SubscribeAuthorizedStreams(session);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                StopPump(session);
                await TryAwaitPumpAsync(session).ConfigureAwait(false);
            }

            if (started && context is not null)
            {
                try
                {
                    await InvokeCallbackAsync(() =>
                            visualizer.OnStopAsync(context, CancellationToken.None))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the build failure while still tearing down every owned resource.
                }
            }

            await TryDisposeOwnedAsync(context).ConfigureAwait(false);
            await TryDisposeCallbackOwnedAsync(visualizer).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The verbs the running unit declared, sanitised. Empty when nothing is running or the unit
    /// declared a malformed list.
    /// </summary>
    public IReadOnlyList<UnitAction> Actions =>
        Volatile.Read(ref _session) is { } session
            ? UnitAction.Sanitise(InvokeCallback(() => session.Visualizer.Actions))
            : [];

    /// <summary>
    /// Runs one declared action on the unit.
    ///
    /// <para><b>Under <c>_drawGate</c>, which is the whole point.</b> The pump holds that gate across
    /// every data callback so a frame never sees half-mutated state; an action almost always touches
    /// the same fields, and running it off the render thread without the gate would race the pump in
    /// exactly the way the gate exists to prevent. Taking it here means an author has one threading
    /// rule for every callback rather than a special case for buttons.</para>
    ///
    /// <para>Returns false when there is nothing running or the id was never declared — a stale button
    /// press after a restart is an ordinary event, not a fault.</para>
    /// </summary>
    public async Task<bool> InvokeActionAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();

        var session = Volatile.Read(ref _session);
        if (session is null || !IsRunning) return false;
        if (!Actions.Any(action => string.Equals(action.Id, id, StringComparison.Ordinal))) return false;

        await _drawGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // isAction: the export sink accepts an offer only inside this scope, which is what ties a
            // take-away to the button the viewer pressed.
            await InvokeCallbackAsync(
                    () => session.Visualizer.OnActionAsync(id, session.Context, ct), isAction: true)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A button that throws must not take the window down, for the same reason a data callback
            // that throws does not: the unit is untrusted and the user came for a picture.
            ReportFault(session, $"Sandbox visualizer action '{id}' failed; the window is unaffected.");
            return false;
        }
        finally
        {
            _drawGate.Release();
        }
    }

    /// <summary>Where an accepted offer goes, or null when the host offers no take-away at all.</summary>
    private readonly Action<string, string>? _offerTakeAway;

    /// <summary>The last offer accepted, so a unit cannot thrash the destination.</summary>
    private DateTime _lastOfferUtc = DateTime.MinValue;

    /// <summary>How close together two accepted offers may be.</summary>
    private static readonly TimeSpan OfferInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Mediates a unit's take-away offer.
    ///
    /// <para><b>Only while an action is running.</b> That is the whole safety argument: a unit cannot
    /// offer from a data callback, so nothing reaches the viewer that they did not ask for by pressing
    /// a button. The sandbox still denies files and the network — the unit produces content and never
    /// learns where it goes.</para>
    /// </summary>
    private sealed class MediatedExport(SandboxVisualizerRuntime runtime) : IUnitExport
    {
        public bool Offer(string label, string text)
        {
            if (runtime._offerTakeAway is not { } deliver) return false;
            if (CurrentCallback.Value is not { } scope || !scope.IsActiveFor(runtime) || !scope.IsAction)
                return false;

            if (string.IsNullOrEmpty(text) || text.Length > ExportLimits.MaxTextLength) return false;

            var now = DateTime.UtcNow;
            if (now - runtime._lastOfferUtc < OfferInterval) return false;
            runtime._lastOfferUtc = now;

            var trimmed = string.IsNullOrWhiteSpace(label)
                ? "Take-away"
                : label.Length > ExportLimits.MaxLabelLength
                    ? label[..ExportLimits.MaxLabelLength]
                    : label;

            try
            {
                deliver(trimmed, text);
                return true;
            }
            catch
            {
                // The destination failing is the host's problem, not a fault in the unit.
                return false;
            }
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
                    // Held across the callback so a frame never sees state a handler is halfway
                    // through mutating. TryDraw takes the same gate without waiting, so a visualizer
                    // in the middle of an event costs a skipped frame, never a stalled UI thread.
                    await _drawGate.WaitAsync(session.PumpCancellation.Token).ConfigureAwait(false);
                    try
                    {
                        await InvokeCallbackAsync(() =>
                                DeliverAsync(session, item, session.PumpCancellation.Token))
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        _drawGate.Release();
                    }
                }
                catch (OperationCanceledException) when (session.PumpCancellation.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    ReportFault(session, "Sandbox visualizer event failed; the event was skipped.");
                }
            }
        }
        catch (OperationCanceledException) when (session.PumpCancellation.IsCancellationRequested)
        {
            // Pause and stop cancel the pump so an asynchronous handler can end promptly.
        }
    }

    private static Task DeliverAsync(
        RuntimeSession session,
        MarketEventEnvelope item,
        CancellationToken ct) => item.Kind switch
        {
            MarketEventKind.Quote =>
                session.Visualizer.OnQuoteAsync((Quote)item.Payload, session.Context, ct),
            MarketEventKind.Trade =>
                session.Visualizer.OnTradeAsync((TradePrint)item.Payload, session.Context, ct),
            MarketEventKind.Depth => session.Visualizer.OnDepthAsync(
                item.Instrument,
                (DepthSnapshot)item.Payload,
                session.Context,
                ct),
            MarketEventKind.Bar =>
                session.Visualizer.OnBarAsync((OhlcvBar)item.Payload, session.Context, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown market event."),
        };

    private void SubscribeAuthorizedStreams(RuntimeSession session)
    {
        foreach (var instrument in session.Instruments)
        {
            if ((session.DataRequirement & StrategyDataRequirement.L1) != 0)
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

            if ((session.DataRequirement & StrategyDataRequirement.TradeTape) != 0)
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

            if ((session.DataRequirement & StrategyDataRequirement.Bars) != 0)
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

            if ((session.DataRequirement & StrategyDataRequirement.Depth) != 0)
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
            () => ReportFault(session, "A sandbox visualizer market-data stream faulted and stopped."))));
    }

    private void Enqueue(RuntimeSession session, MarketEventEnvelope item)
    {
        if (IsDelivering(session))
            session.Queue.Writer.TryWrite(item);
    }

    private bool IsDelivering(RuntimeSession session) =>
        State == SandboxVisualizerRuntimeState.Running &&
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
                ReportFault(session, "A sandbox visualizer market-data subscription failed to dispose.");
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
                "A sandbox visualizer cancellation callback failed; host teardown continued.");
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

    private static async Task TryAwaitPumpAsync(RuntimeSession session)
    {
        try
        {
            await AwaitPumpAsync(session).ConfigureAwait(false);
        }
        catch
        {
            // A construction failure remains primary.
        }
    }

    private async Task TeardownSessionAsync(RuntimeSession session, CancellationToken ct)
    {
        StopPump(session);
        try
        {
            await AwaitPumpAsync(session).ConfigureAwait(false);
        }
        catch
        {
            ReportFault(session, "The sandbox visualizer event pump failed during teardown.");
        }

        if (session.VisualizerStarted)
        {
            try
            {
                await InvokeCallbackAsync(() =>
                        session.Visualizer.OnStopAsync(session.Context, ct))
                    .ConfigureAwait(false);
            }
            catch
            {
                ReportFault(session, "Sandbox visualizer stop failed; host teardown continued.");
            }
        }

        try
        {
            await DisposeOwnedAsync(session.Context).ConfigureAwait(false);
        }
        catch
        {
            ReportFault(session, "Sandbox visualizer context disposal failed; host teardown continued.");
        }

        try
        {
            await InvokeCallbackAsync(async () =>
                    await DisposeOwnedAsync(session.Visualizer).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch
        {
            ReportFault(session, "Sandbox visualizer disposal failed; host teardown continued.");
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
            // A prior construction/start failure remains primary.
        }
    }

    private async ValueTask TryDisposeCallbackOwnedAsync(object? owned)
    {
        try
        {
            await InvokeCallbackAsync(async () =>
                    await DisposeOwnedAsync(owned).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch
        {
            // A prior construction/start failure remains primary.
        }
    }

    private IReadOnlyDictionary<string, object?> InitializeParametersAndLock(
        StrategyParameterSchema visualizerSchema)
    {
        ArgumentNullException.ThrowIfNull(visualizerSchema);
        lock (_parameterGate)
        {
            if (_parameterSchema is null)
            {
                var initialized = new StrategyParameters(visualizerSchema, _initialParameterValues);
                _parameterSchema = visualizerSchema;
                _currentParameters = initialized;
            }
            else
            {
                ValidateSchemaCore(visualizerSchema, _parameterSchema);
            }

            _parametersLocked = true;
            return _currentParameters!.ToDictionary();
        }
    }

    private IReadOnlyDictionary<string, object?> LockAndSnapshotParameters()
    {
        lock (_parameterGate)
        {
            _parametersLocked = true;
            return (_currentParameters
                    ?? throw new InvalidOperationException("The visualizer parameters are unavailable."))
                .ToDictionary();
        }
    }

    private void UnlockParameters()
    {
        lock (_parameterGate)
            _parametersLocked = false;
    }

    private IReadOnlySet<InstrumentId> ResolveInstruments(IParameters parameters)
    {
        var schema = _parameterSchema
            ?? throw new InvalidOperationException("The visualizer parameter schema is unavailable.");
        var instruments = schema.Parameters
            .Where(static parameter => parameter.Kind == ParameterKind.Instrument)
            .Select(parameter => parameters.GetInstrument(parameter.Key))
            .Where(static instrument => !instrument.IsNone)
            .ToFrozenSet();

        if (instruments.Count == 0)
        {
            throw new NotSupportedException(
                "SandboxVisualizerRuntime requires at least one resolved Instrument parameter.");
        }

        return instruments;
    }

    private void ValidateSchema(StrategyParameterSchema visualizerSchema)
    {
        ArgumentNullException.ThrowIfNull(visualizerSchema);
        var expected = _parameterSchema
            ?? throw new InvalidOperationException("The visualizer parameter schema is unavailable.");
        ValidateSchemaCore(visualizerSchema, expected);
    }

    private static void ValidateSchemaCore(
        StrategyParameterSchema visualizerSchema,
        StrategyParameterSchema expected)
    {
        if (visualizerSchema.Parameters.Count != expected.Parameters.Count)
            throw new InvalidOperationException("The replacement visualizer parameter schema has changed.");

        for (var index = 0; index < visualizerSchema.Parameters.Count; index++)
        {
            var visualizerParameter = visualizerSchema.Parameters[index];
            var expectedParameter = expected.Parameters[index];
            if (!Equivalent(visualizerParameter, expectedParameter))
            {
                throw new InvalidOperationException("The replacement visualizer parameter schema has changed.");
            }
        }
    }

    private static bool Equivalent(StrategyParameter actual, StrategyParameter expected) =>
        string.Equals(actual.Key, expected.Key, StringComparison.Ordinal) &&
        string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal) &&
        actual.Kind == expected.Kind &&
        Equals(actual.Default, expected.Default) &&
        actual.Min == expected.Min &&
        actual.Max == expected.Max &&
        actual.Step == expected.Step &&
        Equivalent(actual.Choices, expected.Choices) &&
        string.Equals(actual.Description, expected.Description, StringComparison.Ordinal) &&
        string.Equals(actual.Group, expected.Group, StringComparison.Ordinal) &&
        string.Equals(actual.Unit, expected.Unit, StringComparison.Ordinal);

    private static bool Equivalent(IReadOnlyList<string>? actual, IReadOnlyList<string>? expected) =>
        ReferenceEquals(actual, expected) ||
        actual is not null &&
        expected is not null &&
        actual.SequenceEqual(expected, StringComparer.Ordinal);

    private static void ValidateDataRequirement(StrategyDataRequirement requirement)
    {
        if ((requirement & ~SupportedRequirements) != 0)
        {
            throw new NotSupportedException(
                $"The visualizer declares unsupported market-data flags: {requirement & ~SupportedRequirements}.");
        }
    }

    private async Task InvokeCallbackAsync(Func<Task> callback, bool isAction = false)
    {
        var previous = CurrentCallback.Value;
        var current = new CallbackScope(this, isAction);
        CurrentCallback.Value = current;
        try
        {
            await callback().ConfigureAwait(false);
        }
        finally
        {
            current.Clear();
            CurrentCallback.Value = previous;
        }
    }

    private T InvokeCallback<T>(Func<T> callback)
    {
        var previous = CurrentCallback.Value;
        var current = new CallbackScope(this);
        CurrentCallback.Value = current;
        try
        {
            return callback();
        }
        finally
        {
            current.Clear();
            CurrentCallback.Value = previous;
        }
    }

    private void ReportFault(RuntimeSession session, string message)
    {
        var previous = CurrentCallback.Value;
        var current = new CallbackScope(this);
        CurrentCallback.Value = current;
        try
        {
            session.Context.Alerts.Alert(message, AlertLevel.Error, "sandbox-visualizer-runtime-fault");
        }
        catch
        {
            // Host-owned alert routes must never destabilize the serialized event pump or teardown.
        }
        finally
        {
            current.Clear();
            CurrentCallback.Value = previous;
        }
    }

    /// <summary>
    /// Reports a fault raised while drawing. Only the FIRST one is announced: <c>Draw</c> runs every
    /// frame, so a visualizer that throws in it throws sixty times a second, and repeating the alert
    /// would bury the log it is meant to appear in.
    /// </summary>
    private void ReportDrawFault(Exception fault)
    {
        if (Interlocked.Increment(ref _drawFaultCount) != 1L)
            return;

        try
        {
            _appendActivityLog(
                "Visualizer",
                "Error",
                $"The visualizer failed while drawing and its picture was dropped: {fault.Message}");
        }
        catch
        {
            // The activity log is host-owned; a failure there must not escape onto the render thread.
        }
    }

    private void RecordDroppedEvent() => Interlocked.Increment(ref _droppedEventCount);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(SandboxVisualizerRuntime));
    }

    private void ThrowIfLifecycleReentrant(string operation)
    {
        if (CurrentCallback.Value?.IsActiveFor(this) == true)
        {
            throw new InvalidOperationException(
                $"{operation} cannot be called from a visualizer or mediated-alert callback.");
        }
    }

    private sealed class CallbackScope(SandboxVisualizerRuntime runtime, bool isAction = false)
    {
        private SandboxVisualizerRuntime? _runtime = runtime;

        /// <summary>True when this callback is a user-pressed action rather than a data event. Read by
        /// the export sink, which is the one capability that needs a gesture behind it.</summary>
        public bool IsAction { get; } = isAction;

        public bool IsActiveFor(SandboxVisualizerRuntime candidate) =>
            ReferenceEquals(Volatile.Read(ref _runtime), candidate);

        public void Clear() => Volatile.Write(ref _runtime, null);
    }

    private sealed class RuntimeSession(
        IVisualizer visualizer,
        SandboxVisualizerContext context,
        IReadOnlySet<InstrumentId> instruments,
        StrategyDataRequirement dataRequirement,
        Channel<MarketEventEnvelope> queue)
    {
        public IVisualizer Visualizer { get; } = visualizer;
        public SandboxVisualizerContext Context { get; } = context;
        public IReadOnlySet<InstrumentId> Instruments { get; } = instruments;
        public StrategyDataRequirement DataRequirement { get; } = dataRequirement;
        public Channel<MarketEventEnvelope> Queue { get; } = queue;
        public CancellationTokenSource PumpCancellation { get; } = new();
        public List<IDisposable> Subscriptions { get; } = new();
        public Task PumpTask { get; set; } = Task.CompletedTask;
        public bool VisualizerStarted { get; set; }
        public int PumpStopped;
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
        SandboxVisualizerRuntime Owner,
        MarketEventKind Kind,
        InstrumentId Instrument,
        object Payload)
    {
        public static MarketEventEnvelope Quote(SandboxVisualizerRuntime owner, Quote quote) =>
            new(owner, MarketEventKind.Quote, quote.InstrumentId, quote);

        public static MarketEventEnvelope Trade(SandboxVisualizerRuntime owner, TradePrint trade) =>
            new(owner, MarketEventKind.Trade, trade.InstrumentId, trade);

        public static MarketEventEnvelope Depth(
            SandboxVisualizerRuntime owner,
            InstrumentId instrument,
            DepthSnapshot depth) =>
            new(owner, MarketEventKind.Depth, instrument, depth);

        public static MarketEventEnvelope Bar(SandboxVisualizerRuntime owner, OhlcvBar bar) =>
            new(owner, MarketEventKind.Bar, bar.InstrumentId, bar);
    }
}
