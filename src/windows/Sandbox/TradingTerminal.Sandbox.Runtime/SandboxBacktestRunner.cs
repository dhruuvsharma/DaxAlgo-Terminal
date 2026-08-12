using TradingTerminal.Sandbox.Portfolio;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>
/// Replays one instrument's historical bars through a fresh sandbox kernel and model
/// portfolio. Bars are processed sequentially without a live event pump or wall-clock access.
/// </summary>
public sealed class SandboxBacktestRunner
{
    public const int DefaultRetentionBound = ScopedMarketDataView.DefaultRetentionBound;

    private readonly Func<IStrategyKernel> _kernelFactory;
    private readonly IReadOnlyDictionary<string, object?> _parameterValues;
    private readonly InstrumentId _instrument;
    private readonly BarSize _barSize;
    private readonly ModelPortfolioAccountConfig _accountConfig;
    private readonly int _retentionBound;

    public SandboxBacktestRunner(
        Func<IStrategyKernel> kernelFactory,
        IReadOnlyDictionary<string, object?>? parameterValues,
        InstrumentId instrument,
        BarSize barSize,
        ModelPortfolioAccountConfig? accountConfig = null,
        int retentionBound = DefaultRetentionBound)
    {
        ArgumentNullException.ThrowIfNull(kernelFactory);
        if (instrument.IsNone)
            throw new ArgumentException("The backtest instrument must be resolved.", nameof(instrument));
        if (!Enum.IsDefined(barSize))
            throw new ArgumentOutOfRangeException(nameof(barSize), barSize, "Unknown bar size.");
        if (retentionBound <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionBound),
                "The market-data retention bound must be positive.");
        }

        _kernelFactory = kernelFactory;
        _parameterValues = parameterValues is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(parameterValues, StringComparer.Ordinal);
        _instrument = instrument;
        _barSize = barSize;
        _accountConfig = accountConfig ?? new ModelPortfolioAccountConfig();
        _retentionBound = retentionBound;
    }

    /// <summary>Runs a caller-supplied, deterministic historical bar list.</summary>
    public Task<SandboxBacktestResult> RunAsync(
        IReadOnlyList<OhlcvBar> bars,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return RunPreparedAsync(PrepareBars(bars), ct);
    }

    /// <summary>Runs a caller-supplied historical bar sequence after materializing it once.</summary>
    public Task<SandboxBacktestResult> RunAsync(
        IEnumerable<OhlcvBar> bars,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return RunPreparedAsync(PrepareBars(bars), ct);
    }

    /// <summary>
    /// Reads a date range from the shared historical store and runs it through the same deterministic
    /// in-memory path as caller-supplied bars.
    /// </summary>
    public async Task<SandboxBacktestResult> RunAsync(
        IMarketDataStore store,
        DateTime fromUtc,
        DateTime toUtc,
        BrokerKind? source = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        RequireUtc(fromUtc, nameof(fromUtc));
        RequireUtc(toUtc, nameof(toUtc));
        if (toUtc <= fromUtc)
            throw new ArgumentException("The historical range must end after it starts.", nameof(toUtc));

        var bars = new List<OhlcvBar>();
        await foreach (var bar in store
                           .ReadBarsAsync(_instrument, _barSize, fromUtc, toUtc, source, ct)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            bars.Add(bar);
        }

        return await RunPreparedAsync(PrepareBars(bars), ct).ConfigureAwait(false);
    }

    private async Task<SandboxBacktestResult> RunPreparedAsync(
        IReadOnlyList<OhlcvBar> bars,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var kernel = _kernelFactory()
            ?? throw new InvalidOperationException("The sandbox kernel factory returned null.");
        var clock = new BacktestClock(
            bars.Count == 0 ? DateTime.UnixEpoch : bars[0].OpenTimeUtc);
        var hub = new SynchronousMarketDataHub();
        var capturedAlerts = new List<AlertRecord>();
        var equityCurve = new List<SandboxBacktestPoint>(bars.Count);
        ModelPortfolioAccount? account = null;
        BacktestVirtualBook? book = null;
        ScopedMarketDataView? data = null;
        SandboxStrategyContext? context = null;
        var started = false;
        var completionAttempted = false;
        var stopAttempted = false;
        Exception? runFailure = null;

        try
        {
            ValidateDataRequirement(kernel.DataRequirement);
            var schema = kernel.Schema
                ?? throw new InvalidOperationException("The sandbox kernel returned a null parameter schema.");
            var parameters = new SandboxParameters(schema, _parameterValues);
            ValidateInstrumentParameters(schema, parameters);
            var instruments = new HashSet<InstrumentId> { _instrument };
            account = new ModelPortfolioAccount(_instrument, _accountConfig);
            book = new BacktestVirtualBook(instruments, account.Book);
            data = new ScopedMarketDataView(
                instruments,
                kernel.DataRequirement,
                hub,
                _retentionBound);
            var alerts = new MediatedAlertSink(
                kernel.GetType().Name,
                clock,
                static (_, _, _) => { },
                capturedAlerts.Add);
            context = new SandboxStrategyContext(
                data,
                clock,
                parameters,
                book,
                alerts);

            await kernel.OnStartAsync(context, ct).ConfigureAwait(false);
            started = true;
            ct.ThrowIfCancellationRequested();

            foreach (var bar in bars)
            {
                ct.ThrowIfCancellationRequested();
                clock.AdvanceTo(bar.OpenTimeUtc);
                hub.PublishBar(bar);

                account.BeginBar(bar.Close);
                if (account.LastFault != ModelPortfolioFault.None)
                {
                    var fault = account.LastFault;
                    account.Rollback();
                    book.DiscardPending();
                    ReportBarAccountFault(fault, alerts);
                }
                else
                {
                    book.OpenWindow();
                    try
                    {
                        await kernel.OnBarAsync(bar, context, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (book.TryRollbackWindow())
                            account.Rollback();
                        throw;
                    }

                    account.ReconcileToTargets();
                    if (account.LastFault == ModelPortfolioFault.None)
                        account.Commit();

                    if (account.LastFault != ModelPortfolioFault.None)
                    {
                        var fault = account.LastFault;
                        account.Rollback();
                        book.RejectWindow();
                        ReportBarAccountFault(fault, alerts);
                    }
                    else
                    {
                        book.CommitWindow();
                    }
                }

                equityCurve.Add(new SandboxBacktestPoint(bar.OpenTimeUtc, account.Snapshot));
            }

            completionAttempted = true;
            account.Complete();
            if (account.LastFault != ModelPortfolioFault.None)
                ReportCompletionFault(account.LastFault, alerts);
            var finalSnapshot = account.Snapshot;

            stopAttempted = true;
            await kernel.OnStopAsync(context, ct).ConfigureAwait(false);
            book.DiscardPending();

            return new SandboxBacktestResult(equityCurve, finalSnapshot, capturedAlerts);
        }
        catch (Exception ex)
        {
            runFailure = ex;
            throw;
        }
        finally
        {
            if (started && account is not null && !completionAttempted)
            {
                try
                {
                    account.Rollback();
                    account.Complete();
                }
                catch
                {
                    // Preserve the primary run failure while still attempting kernel teardown.
                }
            }

            if (started && context is not null && !stopAttempted)
            {
                try
                {
                    await kernel.OnStopAsync(context, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the primary run failure while disposing all owned resources.
                }
            }

            book?.DiscardPending();
            var cleanupFailures = new List<Exception>(4);
            try
            {
                if (context is not null)
                    context.Dispose();
                else
                    data?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(ex);
            }

            await CaptureDisposalFailureAsync(account, cleanupFailures).ConfigureAwait(false);
            await CaptureDisposalFailureAsync(kernel, cleanupFailures).ConfigureAwait(false);
            try
            {
                hub.Dispose();
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(ex);
            }

            if (runFailure is null && cleanupFailures.Count > 0)
                throw new AggregateException("One or more backtest resources failed to dispose.", cleanupFailures);
        }
    }

    private OhlcvBar[] PrepareBars(IEnumerable<OhlcvBar> source)
    {
        var bars = new List<OhlcvBar>();
        foreach (var bar in source)
        {
            if (bar is null)
                throw new ArgumentException("The historical feed cannot contain null bars.", nameof(source));
            if (bar.InstrumentId != _instrument)
            {
                throw new ArgumentException(
                    $"Bar instrument {bar.InstrumentId} does not match backtest instrument {_instrument}.",
                    nameof(source));
            }

            if (bar.Size != _barSize)
            {
                throw new ArgumentException(
                    $"Bar size {bar.Size} does not match backtest size {_barSize}.",
                    nameof(source));
            }

            RequireUtc(bar.OpenTimeUtc, nameof(source));
            bars.Add(bar);
        }

        return bars
            .OrderBy(static bar => bar.OpenTimeUtc)
            .ToArray();
    }

    private static void ValidateDataRequirement(StrategyDataRequirement requirement)
    {
        if (requirement != StrategyDataRequirement.Bars)
        {
            throw new NotSupportedException(
                "SandboxBacktestRunner accepts bar-only kernels; every declared data requirement " +
                "must be satisfied by the historical feed.");
        }
    }

    private void ValidateInstrumentParameters(
        StrategyParameterSchema schema,
        IParameters parameters)
    {
        foreach (var parameter in schema.Parameters.Where(
                     static parameter => parameter.Kind == ParameterKind.Instrument))
        {
            var configured = parameters.GetInstrument(parameter.Key);
            if (configured != _instrument)
            {
                throw new InvalidOperationException(
                    $"Instrument parameter '{parameter.Key}' resolves to {configured}; " +
                    $"the backtest is scoped to {_instrument}.");
            }
        }
    }

    private static void ReportBarAccountFault(ModelPortfolioFault fault, IAlertSink alerts) =>
        alerts.Alert(
            $"Sandbox account rejected a backtest bar ({fault}); its window was rolled back.",
            AlertLevel.Error,
            "sandbox-backtest-fault");

    private static void ReportCompletionFault(ModelPortfolioFault fault, IAlertSink alerts) =>
        alerts.Alert(
            $"Sandbox account completion failed ({fault}).",
            AlertLevel.Error,
            "sandbox-backtest-completion-fault");

    private static void RequireUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Historical timestamps must use DateTimeKind.Utc.", parameterName);
    }

    private static async ValueTask DisposeOwnedAsync(object? owned)
    {
        switch (owned)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static async ValueTask CaptureDisposalFailureAsync(
        object? owned,
        ICollection<Exception> failures)
    {
        try
        {
            await DisposeOwnedAsync(owned).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private sealed class BacktestClock(DateTime initialUtc) : IClock
    {
        private DateTime _utcNow = initialUtc;

        public DateTime UtcNow => _utcNow;

        public void AdvanceTo(DateTime timestampUtc)
        {
            if (timestampUtc < _utcNow)
                throw new InvalidOperationException("The deterministic backtest clock cannot move backwards.");

            _utcNow = timestampUtc;
        }
    }

    /// <summary>
    /// Preserves the latest bounded target submitted outside a priced account window (notably from
    /// <c>OnStartAsync</c>) and forwards it after the next successful <c>BeginBar</c>.
    /// </summary>
    private sealed class BacktestVirtualBook : IVirtualBook
    {
        private readonly object _gate = new();
        private readonly HashSet<InstrumentId> _instruments;
        private readonly IVirtualBook _inner;
        private readonly Dictionary<InstrumentId, VirtualTargetIntent> _pending;
        private bool _windowOpen;

        public BacktestVirtualBook(IReadOnlySet<InstrumentId> instruments, IVirtualBook inner)
        {
            _instruments = new HashSet<InstrumentId>(instruments);
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pending = new Dictionary<InstrumentId, VirtualTargetIntent>(_instruments.Count);
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

        public void DiscardPending()
        {
            lock (_gate)
            {
                _windowOpen = false;
                _pending.Clear();
            }
        }
    }

    /// <summary>
    /// A hot in-memory hub whose publishers invoke current observers inline. It stores no market
    /// data; the bounded <see cref="ScopedMarketDataView"/> owns all retained history.
    /// </summary>
    private sealed class SynchronousMarketDataHub : IMarketDataHub, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<InstrumentId, SynchronousStream<Quote>> _quotes = new();
        private readonly Dictionary<InstrumentId, SynchronousStream<TradePrint>> _trades = new();
        private readonly Dictionary<BarStreamKey, SynchronousStream<OhlcvBar>> _bars = new();
        private readonly Dictionary<InstrumentId, SynchronousStream<DepthSnapshot>> _depth = new();
        private int _disposed;

        public IObservable<Quote> Quotes(InstrumentId instrumentId) =>
            GetOrAdd(_quotes, instrumentId);

        public IObservable<TradePrint> Trades(InstrumentId instrumentId) =>
            GetOrAdd(_trades, instrumentId);

        public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) =>
            GetOrAdd(_bars, new BarStreamKey(instrumentId, size));

        public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) =>
            GetOrAdd(_depth, instrumentId);

        public void PublishQuote(Quote quote) =>
            GetOrAdd(_quotes, quote.InstrumentId).Publish(quote);

        public void PublishTrade(TradePrint trade) =>
            GetOrAdd(_trades, trade.InstrumentId).Publish(trade);

        public void PublishBar(OhlcvBar bar) =>
            GetOrAdd(_bars, new BarStreamKey(bar.InstrumentId, bar.Size)).Publish(bar);

        public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) =>
            GetOrAdd(_depth, instrumentId).Publish(snapshot);

        public void Dispose()
        {
            SynchronousStreamBase[] streams;
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                streams = _quotes.Values.Cast<SynchronousStreamBase>()
                    .Concat(_trades.Values)
                    .Concat(_bars.Values)
                    .Concat(_depth.Values)
                    .ToArray();
                _quotes.Clear();
                _trades.Clear();
                _bars.Clear();
                _depth.Clear();
            }

            foreach (var stream in streams)
                stream.Dispose();
        }

        private SynchronousStream<TValue> GetOrAdd<TKey, TValue>(
            Dictionary<TKey, SynchronousStream<TValue>> streams,
            TKey key)
            where TKey : notnull
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (streams.TryGetValue(key, out var stream))
                    return stream;

                stream = new SynchronousStream<TValue>();
                streams.Add(key, stream);
                return stream;
            }
        }

        private readonly record struct BarStreamKey(InstrumentId Instrument, BarSize Size);
    }

    private abstract class SynchronousStreamBase : IDisposable
    {
        public abstract void Dispose();
    }

    private sealed class SynchronousStream<T> : SynchronousStreamBase, IObservable<T>
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, IObserver<T>> _observers = new();
        private long _nextId;
        private int _disposed;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var id = ++_nextId;
                _observers.Add(id, observer);
                return new Subscription(this, id);
            }
        }

        public void Publish(T value)
        {
            IObserver<T>[] observers;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                observers = _observers.Values.ToArray();
            }

            foreach (var observer in observers)
                observer.OnNext(value);
        }

        public override void Dispose()
        {
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _observers.Clear();
            }
        }

        private void Remove(long id)
        {
            lock (_gate)
                _observers.Remove(id);
        }

        private sealed class Subscription(SynchronousStream<T> owner, long id) : IDisposable
        {
            private SynchronousStream<T>? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(id);
        }
    }
}
