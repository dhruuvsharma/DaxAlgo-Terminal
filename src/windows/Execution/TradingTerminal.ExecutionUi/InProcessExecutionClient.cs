using System.Globalization;
using System.Security.Cryptography;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.CTrader;
using TradingTerminal.Execution.InteractiveBrokers;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Execution.Service;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// Default console backing: one fully in-process, lease-fenced OMS runtime per active book. Sample
/// books construct only <see cref="SimulatedExecutionAdapter"/>; an explicitly registered and
/// authenticated live-safety-gated broker adapter can be attached without changing the default composition.
/// </summary>
public sealed class InProcessExecutionClient : IExecutionClient, IExecutionBookTargetIntake
{
    private const int MaximumBooks = 12;
    private static readonly SemaphoreSlim ExecutionModeChangeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly List<BookEntry> _books;
    private readonly List<IBrokerExecutionAdapter> _registeredAdapters;
    private readonly HashSet<IBrokerExecutionAdapter> _ownedAdapters = [];
    private readonly Dictionary<IBrokerExecutionAdapter, IAsyncDisposable> _ownedSchedulers = [];
    private readonly ILiveExecutionConfirmationStore? _liveConfirmationStore;
    private readonly CTraderExecutionOptions? _cTraderOptions;
    private readonly AlpacaExecutionOptions? _alpacaOptions;
    private readonly InteractiveBrokersExecutionOptions? _interactiveBrokersOptions;
    private readonly Func<InteractiveBrokersExecutionEndpoint, TimeSpan, IInteractiveBrokersExecutionTransport>?
        _interactiveBrokersTransportFactory;
    private readonly IClock? _executionClock;
    private readonly IExecutionLeaseStore _executionLeaseStore;
    private readonly IExecutionModeStatusPublisher? _modeStatusPublisher;
    private readonly Dictionary<string, string> _adapterConnectionErrors = new(StringComparer.Ordinal);
    private string _interactiveBrokersAccountId;
    private string? _lastOperationMessage;
    private int _newBookSequence = 3;
    private int _bookCreationInProgress;
    private int _disposed;

    public InProcessExecutionClient(
        IEnumerable<IBrokerExecutionAdapter>? registeredAdapters = null,
        ILiveExecutionConfirmationStore? liveConfirmationStore = null,
        CTraderExecutionOptions? cTraderOptions = null,
        AlpacaExecutionOptions? alpacaOptions = null,
        InteractiveBrokersExecutionOptions? interactiveBrokersOptions = null,
        Func<InteractiveBrokersExecutionEndpoint, TimeSpan, IInteractiveBrokersExecutionTransport>?
            interactiveBrokersTransportFactory = null,
        IClock? executionClock = null,
        IExecutionLeaseStore? executionLeaseStore = null,
        ExecutionModeStatusProjection? executionModeStatus = null)
    {
        _liveConfirmationStore = liveConfirmationStore;
        _cTraderOptions = cTraderOptions;
        _alpacaOptions = alpacaOptions;
        _interactiveBrokersOptions = interactiveBrokersOptions;
        _interactiveBrokersTransportFactory = interactiveBrokersTransportFactory;
        _interactiveBrokersAccountId = interactiveBrokersOptions?.AccountId ?? string.Empty;
        _executionClock = executionClock;
        _executionLeaseStore = executionLeaseStore ?? new ExecutionConsoleLeaseStore();
        _registeredAdapters = (registeredAdapters ?? [])
            .DistinctBy(adapter => $"{adapter.BrokerId}|{adapter.Account.AccountId.Value}")
            .ToList();
        _modeStatusPublisher = executionModeStatus?.CreatePublisher();
        _books = [];
        try
        {
            foreach (var adapter in _registeredAdapters)
                adapter.EventReceived += OnRegisteredAdapterEvent;
            // No seeded books. The console used to start with two fabricated demo books (Alpha,
            // Beta) carrying invented strategies, instruments attributed to a "Simulated" adapter,
            // hardcoded prices and a fake P&L history, plus a third permanently-unavailable one.
            // Removed 2026-08-18: the console shows real state or nothing. Create a book to begin.
        }
        catch
        {
            foreach (var book in _books)
                book.Runtime?.Dispose();
            _books.Clear();
            foreach (var adapter in _registeredAdapters)
                adapter.EventReceived -= OnRegisteredAdapterEvent;
            _modeStatusPublisher?.Dispose();
            throw;
        }
        PublishExecutionModeStatus();
    }

    public event EventHandler? SnapshotInvalidated;

    public ExecutionConsoleSnapshot GetSnapshot()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            var books = _books.Select(entry => entry.BuildReadModel()).ToArray();
            var readOnlyBooks = Array.AsReadOnly(books);
            return new ExecutionConsoleSnapshot(
                BuildAdapterReadModels(),
                readOnlyBooks,
                ExecutionAnalyticsProjector.Aggregate(readOnlyBooks),
                DateTime.UtcNow,
                _lastOperationMessage);
        }
    }

    public ValueTask<ExecutionCommandResult> SetIntakePausedAsync(
        string bookId,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionCommandResult result;
        lock (_gate)
        {
            if (FindBook(bookId) is not { Runtime: not null } entry)
            {
                result = ExecutionCommandResult.Failure("The selected book has no local execution-adapter lease.");
            }
            else if (!entry.TryBeginOperation())
            {
                result = ExecutionCommandResult.Failure(
                    "Intake state cannot change while another command is active for this book.");
            }
            else
            {
                try
                {
                    result = entry.SetPaused(paused);
                }
                finally
                {
                    entry.EndOperation();
                }
            }
            _lastOperationMessage = result.Message;
        }
        Invalidate();
        return ValueTask.FromResult(result);
    }

    public ValueTask<ExecutionCommandResult> ReconcileAsync(
        string bookId,
        CancellationToken cancellationToken = default)
        => ReconcileCoreAsync(bookId, cancellationToken);

    private async ValueTask<ExecutionCommandResult> ReconcileCoreAsync(
        string bookId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        BookEntry? entry;
        lock (_gate)
            entry = FindBook(bookId);
        ExecutionCommandResult result;
        if (entry?.Runtime is null)
        {
            result = ExecutionCommandResult.Failure("Reconciliation is unavailable because this book is held elsewhere.");
        }
        else if (!entry.TryBeginOperation())
        {
            result = ExecutionCommandResult.Failure("Reconciliation is unavailable while another command is active for this book.");
        }
        else
        {
            try
            {
                result = await entry.Runtime.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                entry.EndOperation();
            }
        }
        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result;
    }

    public ValueTask<ExecutionCommandResult> KillAsync(
        string bookId,
        CancellationToken cancellationToken = default)
        => KillCoreAsync(bookId, cancellationToken);

    private async ValueTask<ExecutionCommandResult> KillCoreAsync(
        string bookId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        BookEntry? entry;
        IBookRuntime? runtime;
        lock (_gate)
        {
            entry = FindBook(bookId);
            if (entry?.Runtime is null)
            {
                runtime = null;
            }
            else
            {
                entry.IsPaused = true;
                runtime = entry.Runtime;
            }
        }
        ExecutionCommandResult result;
        if (runtime is null || entry is null)
        {
            result = ExecutionCommandResult.Failure("Kill refused: this book has no local execution-adapter lease.");
        }
        else if (!entry.TryBeginOperation())
        {
            result = ExecutionCommandResult.Failure(
                "Kill stopped new intake but refused to race another active book command; retry after it completes.");
        }
        else
        {
            try
            {
                result = await runtime.KillAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                entry.EndOperation();
            }
        }
        if (!result.IsSuccess && runtime is not null)
        {
            result = ExecutionCommandResult.Failure(
                $"{result.Message} New order intake remains paused.");
        }
        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result;
    }

    public async ValueTask<ExecutionCommandResult> SetExecutionModeAsync(
        ExecutionModeChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        await ExecutionModeChangeGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            return await SetExecutionModeCoreAsync(request, linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            ExecutionModeChangeGate.Release();
        }
    }

    private async ValueTask<ExecutionCommandResult> SetExecutionModeCoreAsync(
        ExecutionModeChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(request.Mode))
            return ModeChangeFailure("The requested execution mode is invalid.");

        IBrokerExecutionAdapter? current;
        lock (_gate)
            current = FindRegisteredAdapter(request.AdapterId);
        if (current is null)
            return ModeChangeFailure("The selected broker connection is not registered; execution mode was unchanged.");
        if (string.Equals(current.BrokerId, "simulated", StringComparison.Ordinal))
            return ModeChangeFailure("The Simulated adapter is always PAPER and cannot be switched to LIVE.");
        if (current.Mode == request.Mode)
            return ModeChangeSuccess($"{AdapterDisplayName(current)} is already {current.Mode.ToString().ToUpperInvariant()}.");
        if (current.Session.IsDataConnected || current.Session.IsExecutionAuthenticated)
        {
            return ModeChangeFailure(
                "Disconnect the broker before changing execution mode; the existing connection remains unchanged.");
        }

        lock (_gate)
        {
            if (_books.Any(book => ReferenceEquals(book.Runtime?.Adapter, current)))
            {
                return ModeChangeFailure(
                    "Execution mode cannot change while a book owns this broker connection; the existing mode remains active.");
            }
        }

        var accountId = ResolveModeAccountId(current, request);
        if (!IsValidModeAccountId(accountId))
            return ModeChangeFailure("An exact bounded broker account ID is required before execution mode can change.");

        var persistedLiveConfirmation = false;
        if (request.Mode == ExecutionMode.Live)
        {
            if (!string.Equals(
                    request.TypedConfirmation,
                    LiveExecutionConfirmation.RequiredAcknowledgement,
                    StringComparison.Ordinal))
            {
                return ModeChangeFailure("LIVE execution requires the exact typed acknowledgement LIVE.");
            }
            if (_liveConfirmationStore is null)
                return ModeChangeFailure("No persistent live-confirmation store is available; the connection remains PAPER.");

            try
            {
                _liveConfirmationStore.Save(new LiveExecutionConfirmation(
                    current.BrokerId,
                    accountId,
                    request.TypedConfirmation,
                    DateTime.UtcNow,
                    CurrentConfirmingIdentity()));
                persistedLiveConfirmation = true;
            }
            catch (Exception exception)
            {
                return ModeChangeFailure($"LIVE confirmation could not be persisted; the connection remains PAPER: {SafeReason(exception)}");
            }
        }

        OwnedAdapter replacement;
        try
        {
            replacement = CreateAdapterForMode(current, request, accountId);
        }
        catch (Exception exception)
        {
            if (persistedLiveConfirmation)
                TryRemoveLiveConfirmation(current.BrokerId, accountId);
            return ModeChangeFailure(
                $"{request.Mode.ToString().ToUpperInvariant()} mode was refused by the authorization gate; " +
                $"the existing connection remains {current.Mode.ToString().ToUpperInvariant()}: {SafeReason(exception)}");
        }

        var installed = false;
        lock (_gate)
        {
            var index = _registeredAdapters.IndexOf(current);
            if (index >= 0 &&
                Volatile.Read(ref _disposed) == 0 &&
                !cancellationToken.IsCancellationRequested &&
                !current.Session.IsDataConnected &&
                !_books.Any(book => ReferenceEquals(book.Runtime?.Adapter, current)))
            {
                current.EventReceived -= OnRegisteredAdapterEvent;
                replacement.Adapter.EventReceived += OnRegisteredAdapterEvent;
                _registeredAdapters[index] = replacement.Adapter;
                _ownedAdapters.Add(replacement.Adapter);
                if (replacement.Scheduler is not null)
                    _ownedSchedulers[replacement.Adapter] = replacement.Scheduler;
                if (replacement.Adapter is InteractiveBrokersExecutionAdapter)
                    _interactiveBrokersAccountId = accountId;
                _adapterConnectionErrors.Remove(AdapterKey(current));
                installed = true;
            }
        }

        if (!installed)
        {
            try
            {
                await DisposeOwnedAdapterAsync(replacement.Adapter, replacement.Scheduler).ConfigureAwait(false);
            }
            catch
            {
                // The replacement was never registered, connected, or book-bound. Keep the
                // concurrent-change failure as the operator-visible result and revoke authorization.
            }
            finally
            {
                if (persistedLiveConfirmation)
                    TryRemoveLiveConfirmation(current.BrokerId, accountId);
            }
            return ModeChangeFailure("The broker connection changed concurrently; execution mode was left unchanged.");
        }

        if (request.Mode == ExecutionMode.Paper)
            TryRemoveLiveConfirmation(current.BrokerId, accountId);
        string? cleanupWarning = null;
        try
        {
            await DisposeIfOwnedAsync(current).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The prior adapter was disconnected, unbound, removed from the registry, and had its
            // event handler detached before disposal. Surface the installed mode truth even if its
            // now-unreachable resources report a cleanup fault.
            cleanupWarning = $" Prior disconnected-adapter cleanup reported: {SafeReason(exception)}";
        }
        return ModeChangeSuccess(
            $"{AdapterDisplayName(replacement.Adapter)} is now {request.Mode.ToString().ToUpperInvariant()}; " +
            (request.Mode == ExecutionMode.Live
                ? "the persisted confirmation and all authorization gates passed before the live endpoint was constructed."
                : "the live confirmation was revoked and the paper endpoint was constructed.") +
            cleanupWarning);
    }

    public async ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
        string adapterId,
        CancellationToken cancellationToken = default) =>
        await ConnectAdapterAsync(
            new ExecutionAdapterConnectRequest(adapterId),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
        ExecutionAdapterConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var operationToken = linkedCancellation.Token;
        operationToken.ThrowIfCancellationRequested();
        var adapterId = request.AdapterId?.Trim() ?? string.Empty;
        ExecutionCommandResult result;
        if (string.Equals(adapterId, "simulated", StringComparison.Ordinal))
        {
            result = ExecutionCommandResult.Success("The in-process Simulated adapter is already connected.");
        }
        else if (FindRegisteredAdapter(adapterId) is CTraderExecutionAdapter cTrader)
        {
            var environment = cTrader.Mode == ExecutionMode.Live ? "LIVE" : "DEMO";
            await ExecutionModeChangeGate.WaitAsync(operationToken).ConfigureAwait(false);
            try
            {
                cTrader = await ReconfigureCTraderForConnectAsync(cTrader, request, operationToken)
                    .ConfigureAwait(false);
                adapterId = AdapterKey(cTrader);
                var connection = await cTrader.ConnectAsync(operationToken).ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                result = connection.IsSuccess
                    ? ExecutionCommandResult.Success(
                        $"Connected and execution-authenticated the cTrader {environment} account.")
                    : ExecutionCommandResult.Failure(
                        $"cTrader {environment} connection failed closed: {connection.Reason ?? connection.Fault.ToString()}.");
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                // A closing console must not leave a just-completed DEMO connection behind.
                await cTrader.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                result = ExecutionCommandResult.Failure(
                    $"cTrader {environment} authentication failed. Verify the OAuth credentials and exact execution account ID; no order path was opened.");
            }
            finally
            {
                ExecutionModeChangeGate.Release();
            }
        }
        else if (FindRegisteredAdapter(adapterId) is AlpacaExecutionAdapter alpaca)
        {
            var environment = alpaca.Mode.ToString().ToUpperInvariant();
            try
            {
                if (string.IsNullOrWhiteSpace(request.KeyId) && string.IsNullOrWhiteSpace(request.SecretKey))
                    await alpaca.ConnectAsync(operationToken).ConfigureAwait(false);
                else if (!string.IsNullOrWhiteSpace(request.KeyId) && !string.IsNullOrWhiteSpace(request.SecretKey))
                {
                    await alpaca.ConnectAsync(
                            request.KeyId.Trim(),
                            request.SecretKey.Trim(),
                            operationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    result = ExecutionCommandResult.Failure(
                        $"Alpaca {environment} authentication requires both a key ID and secret. Nothing was sent.");
                    goto FinishConnect;
                }

                operationToken.ThrowIfCancellationRequested();
                result = alpaca.Session.CanExecute
                    ? ExecutionCommandResult.Success($"Connected and execution-authenticated the Alpaca {environment} account.")
                    : ExecutionCommandResult.Failure(
                        $"Alpaca {environment} connected without execution certification; order routing remains blocked.");
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                await alpaca.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                result = ExecutionCommandResult.Failure(
                    $"Alpaca {environment} authentication failed. Verify the credentials and exact account; no order path was opened.");
            }
        }
        else if (FindRegisteredAdapter(adapterId) is InteractiveBrokersExecutionAdapter interactiveBrokers)
        {
            await ExecutionModeChangeGate.WaitAsync(operationToken).ConfigureAwait(false);
            try
            {
                interactiveBrokers = await ReconfigureInteractiveBrokersForConnectAsync(
                        interactiveBrokers,
                        request,
                        operationToken)
                    .ConfigureAwait(false);
                var environment = interactiveBrokers.Mode.ToString().ToUpperInvariant();
                await interactiveBrokers.ConnectAsync(operationToken).ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                result = interactiveBrokers.Session.CanExecute
                    ? ExecutionCommandResult.Success(
                        $"Connected and execution-authenticated the Interactive Brokers {environment} account.")
                    : ExecutionCommandResult.Failure(
                        $"Interactive Brokers {environment} connected without execution certification; order routing remains blocked.");
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                await interactiveBrokers.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                result = ExecutionCommandResult.Failure(
                    "Interactive Brokers authentication failed. Verify TWS or IB Gateway, the exact mode port, unique client ID, and account ID; no order path was opened.");
            }
            finally
            {
                ExecutionModeChangeGate.Release();
            }
        }
        else
        {
            result = ExecutionCommandResult.Failure(
                "No connectable execution adapter is registered for this broker. No network path was created.");
        }

FinishConnect:
        lock (_gate)
        {
            if (result.IsSuccess)
                _adapterConnectionErrors.Remove(adapterId);
            else if (FindRegisteredAdapter(adapterId) is
                     CTraderExecutionAdapter or AlpacaExecutionAdapter or InteractiveBrokersExecutionAdapter)
                _adapterConnectionErrors[adapterId] = result.Message;
            _lastOperationMessage = result.Message;
        }
        Invalidate();
        return result;
    }

    public async ValueTask<ExecutionCommandResult> DisconnectAdapterAsync(
        string adapterId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var operationToken = linkedCancellation.Token;
        operationToken.ThrowIfCancellationRequested();
        ExecutionCommandResult result;
        if (string.Equals(adapterId, "simulated", StringComparison.Ordinal))
        {
            result = ExecutionCommandResult.Failure(
                "The default in-process Simulated adapter is owned by its books and cannot be disconnected here.");
        }
        else if (FindRegisteredAdapter(adapterId) is CTraderExecutionAdapter cTrader)
        {
            await cTrader.DisconnectAsync(operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            result = ExecutionCommandResult.Success(
                $"Disconnected the cTrader {(cTrader.Mode == ExecutionMode.Live ? "LIVE" : "DEMO")} execution adapter.");
        }
        else if (FindRegisteredAdapter(adapterId) is AlpacaExecutionAdapter alpaca)
        {
            await alpaca.DisconnectAsync(operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            result = ExecutionCommandResult.Success(
                $"Disconnected the Alpaca {alpaca.Mode.ToString().ToUpperInvariant()} execution adapter.");
        }
        else if (FindRegisteredAdapter(adapterId) is InteractiveBrokersExecutionAdapter interactiveBrokers)
        {
            await interactiveBrokers.DisconnectAsync(operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            result = ExecutionCommandResult.Success(
                $"Disconnected the Interactive Brokers {interactiveBrokers.Mode.ToString().ToUpperInvariant()} execution adapter.");
        }
        else
        {
            result = ExecutionCommandResult.Failure("The selected execution adapter is not registered.");
        }

        lock (_gate)
        {
            _adapterConnectionErrors.Remove(adapterId);
            _lastOperationMessage = result.Message;
        }
        Invalidate();
        return result;
    }

    public async ValueTask<ExecutionCommandResult> CreateBookAsync(
        ExecutionBookCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _bookCreationInProgress, 1, 0) != 0)
            return ExecutionCommandResult.Failure("Another book creation is already in progress.");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        try
        {
            return await CreateBookCoreAsync(request, linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _bookCreationInProgress, 0);
        }
    }

    private async ValueTask<ExecutionCommandResult> CreateBookCoreAsync(
        ExecutionBookCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionCommandResult? validationFailure = null;
        BookConfiguration? configuration = null;
        IBookRuntime? runtime = null;
        lock (_gate)
        {
            var name = request.Name?.Trim() ?? string.Empty;
            var adapterId = request.AdapterId?.Trim() ?? string.Empty;
            var symbol = request.Symbol?.Trim() ?? string.Empty;
            var strategies = request.Strategies
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (name.Length is < 2 or > 48)
            {
                validationFailure = ExecutionCommandResult.Failure("Book names must contain between 2 and 48 characters.");
            }
            else if (_books.Any(book => string.Equals(book.Configuration.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                validationFailure = ExecutionCommandResult.Failure($"A book named '{name}' already exists.");
            }
            else if (strategies.Length == 0)
            {
                validationFailure = ExecutionCommandResult.Failure("Bind at least one strategy before creating the book.");
            }
            else if (request.Instrument.IsNone != (symbol.Length == 0))
            {
                validationFailure = ExecutionCommandResult.Failure(
                    "A model-bound book requires both one resolved instrument and its symbol.");
            }
            else if (symbol.Length > 32)
            {
                validationFailure = ExecutionCommandResult.Failure("Book instrument symbols are capped at 32 characters.");
            }
            else if (!IsAdapterAvailable(adapterId))
            {
                validationFailure = ExecutionCommandResult.Failure(
                    "Select an available registered execution adapter. Unavailable broker cards cannot create books.");
            }
            else if (_books.Count >= MaximumBooks)
            {
                validationFailure = ExecutionCommandResult.Failure(
                    $"The in-memory console is capped at {MaximumBooks} books.");
            }
            else
            {
                _newBookSequence++;
                var id = $"book-{_newBookSequence}";
                var isSimulated = string.Equals(adapterId, "simulated", StringComparison.Ordinal);
                configuration = BookConfiguration.New(
                    id,
                    name,
                    adapterId,
                    isSimulated ? "Simulated" : AdapterDisplayName(adapterId),
                    strategies,
                    request.Instrument,
                    symbol);
                runtime = isSimulated
                    ? InProcessBookRuntime.CreateEmpty(id, _executionLeaseStore)
                    : FindRegisteredAdapter(adapterId) is AlpacaExecutionAdapter alpaca
                        ? new AlpacaBookRuntime(id, alpaca, _executionLeaseStore)
                        : null;
            }
        }

        ExecutionCommandResult result;
        if (validationFailure is { } failure)
        {
            result = failure;
        }
        else if (configuration is null || runtime is null)
        {
            result = ExecutionCommandResult.Failure(
                "The selected execution adapter could not create an attached book runtime.");
        }
        else
        {
            try
            {
                if (runtime is AlpacaBookRuntime alpacaRuntime)
                {
                    var initialized = await alpacaRuntime.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    if (!initialized.IsSuccess)
                    {
                        runtime.Dispose();
                        runtime = null;
                        result = initialized;
                    }
                    else
                    {
                        result = ExecutionCommandResult.Success(
                            $"Created book '{configuration.Name}' on the connected Alpaca {alpacaRuntime.Adapter.Mode.ToString().ToUpperInvariant()} account.");
                    }
                }
                else
                {
                    result = ExecutionCommandResult.Success(
                        $"Created book '{configuration.Name}' on its own in-process Simulated adapter lease.");
                }
            }
            catch
            {
                runtime?.Dispose();
                throw;
            }

            if (runtime is not null && result.IsSuccess)
            {
                var added = false;
                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) == 0 && !cancellationToken.IsCancellationRequested)
                    {
                        _books.Add(new BookEntry(configuration, runtime));
                        added = true;
                    }
                }
                if (!added)
                {
                    runtime.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException(nameof(InProcessExecutionClient));
                }
            }
        }
        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result;
    }

    public async ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(
        ExecutionManualOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var operationToken = linkedCancellation.Token;
        operationToken.ThrowIfCancellationRequested();
        BookEntry? entry;
        lock (_gate)
            entry = FindBook(request.BookId);

        ExecutionCommandResult result;
        if (entry?.Runtime is null)
        {
            result = ExecutionCommandResult.Failure(
                "Order refused because the selected book has no attached execution runtime.");
        }
        else if (!entry.TryBeginOperation())
        {
            result = ExecutionCommandResult.Failure(
                "Order refused because another command is active for the selected book.");
        }
        else
        {
            try
            {
                bool paused;
                lock (_gate)
                    paused = entry.IsPaused;
                result = paused
                    ? ExecutionCommandResult.Failure("Order refused because intake is paused for the selected book.")
                    : await entry.Runtime
                        .SubmitManualOrderAsync(entry.Configuration, request, operationToken)
                        .ConfigureAwait(false);
            }
            finally
            {
                entry.EndOperation();
            }
        }

        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionTargetSubmissionResult> SubmitTargetAsync(
        string bookId,
        TradeIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bookId))
            return ExecutionTargetSubmissionResult.Failure("Sandbox target refused because no execution book is bound.");
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var operationToken = linkedCancellation.Token;
        operationToken.ThrowIfCancellationRequested();
        BookEntry? entry;
        lock (_gate)
            entry = FindBook(bookId.Trim());

        ExecutionCommandResult result;
        if (entry?.Runtime is null)
        {
            result = ExecutionCommandResult.Failure(
                "Sandbox target refused because the bound book has no attached execution runtime.");
        }
        else if (!entry.Configuration.Strategies.Any(strategy =>
                     string.Equals(strategy, intent.StrategyId, StringComparison.Ordinal)))
        {
            result = ExecutionCommandResult.Failure(
                "Sandbox target refused because its strategy id is not explicitly bound to this book.");
        }
        else if (!entry.TryBeginOperation())
        {
            result = ExecutionCommandResult.Failure(
                "Sandbox target refused because another command is active for the bound book.");
        }
        else
        {
            try
            {
                bool paused;
                lock (_gate)
                    paused = entry.IsPaused;
                result = paused
                    ? ExecutionCommandResult.Failure(
                        "Sandbox target refused because intake is paused for the bound book.")
                    : await entry.Runtime
                        .SubmitTargetAsync(entry.Configuration, intent, operationToken)
                        .ConfigureAwait(false);
            }
            finally
            {
                entry.EndOperation();
            }
        }

        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result.IsSuccess
            ? ExecutionTargetSubmissionResult.Success(result.Message)
            : ExecutionTargetSubmissionResult.Failure(result.Message);
    }

    private BookEntry? FindBook(string bookId) =>
        _books.FirstOrDefault(book => string.Equals(book.Configuration.Id, bookId, StringComparison.Ordinal));

    private IBrokerExecutionAdapter? FindRegisteredAdapter(string adapterId) =>
        _registeredAdapters.FirstOrDefault(adapter =>
            string.Equals(AdapterKey(adapter), adapterId, StringComparison.Ordinal));

    private bool IsAdapterAvailable(string adapterId) =>
        string.Equals(adapterId, "simulated", StringComparison.Ordinal) ||
        FindRegisteredAdapter(adapterId) is AlpacaExecutionAdapter alpaca &&
        alpaca.Session.CanExecute &&
        !_books.Any(book => string.Equals(book.Configuration.AdapterId, adapterId, StringComparison.Ordinal));

    private string AdapterDisplayName(string adapterId) =>
        FindRegisteredAdapter(adapterId) switch
        {
            { } adapter => AdapterDisplayName(adapter),
            _ => adapterId,
        };

    private static string AdapterDisplayName(IBrokerExecutionAdapter adapter) => adapter switch
    {
        CTraderExecutionAdapter => $"cTrader {(adapter.Mode == ExecutionMode.Live ? "LIVE" : "DEMO")}",
        AlpacaExecutionAdapter => $"Alpaca {adapter.Mode.ToString().ToUpperInvariant()}",
        InteractiveBrokersExecutionAdapter =>
            $"Interactive Brokers {adapter.Mode.ToString().ToUpperInvariant()}",
        _ => adapter.Account.AdapterId.Value,
    };

    private string ResolveModeAccountId(
        IBrokerExecutionAdapter current,
        ExecutionModeChangeRequest request)
    {
        if (current is CTraderExecutionAdapter)
            return TryParseCanonicalCTraderAccountId(request.AccountId, out var accountId)
                ? accountId.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

        return request.AccountId.Trim();
    }

    private OwnedAdapter CreateAdapterForMode(
        IBrokerExecutionAdapter current,
        ExecutionModeChangeRequest request,
        string accountId) => current switch
    {
        CTraderExecutionAdapter => CreateCTraderAdapter(
            request.Mode,
            request.OAuthClientId,
            request.OAuthClientSecret,
            request.OAuthAccessToken,
            accountId),
        AlpacaExecutionAdapter => CreateAlpacaAdapter(request, accountId),
        InteractiveBrokersExecutionAdapter => CreateInteractiveBrokersAdapter(
            request.Mode,
            request.Host,
            request.Port,
            request.ClientId,
            accountId),
        _ => throw new InvalidOperationException(
            "Only the cTrader, Alpaca, and Interactive Brokers connections support mode reconstruction."),
    };

    private OwnedAdapter CreateCTraderAdapter(
        ExecutionMode mode,
        string oauthClientId,
        string oauthClientSecret,
        string oauthAccessToken,
        string accountId)
    {
        var source = _cTraderOptions ??
            throw new InvalidOperationException("The cTrader owner configuration is unavailable.");
        var clock = _executionClock ??
            throw new InvalidOperationException("The execution clock is unavailable.");
        var clientId = oauthClientId?.Trim() ?? string.Empty;
        var clientSecret = oauthClientSecret?.Trim() ?? string.Empty;
        var accessToken = oauthAccessToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "cTrader execution requires an OAuth client ID, client secret, and access token.");
        }
        if (!TryParseCanonicalCTraderAccountId(accountId, out var ctidTraderAccountId))
            throw new InvalidOperationException("cTrader execution requires one canonical positive account ID.");

        var options = new CTraderExecutionOptions
        {
            Enabled = source.Enabled,
            Mode = mode,
            AllowLiveExecution = source.AllowLiveExecution,
            Host = mode == ExecutionMode.Live
                ? CTraderExecutionOptions.LiveHost
                : CTraderExecutionOptions.DemoHost,
            Port = source.Port,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            CtidTraderAccountId = ctidTraderAccountId,
            SymbolId = source.SymbolId,
            CanonicalInstrumentId = source.CanonicalInstrumentId,
            MaximumCommandsPerSecond = source.MaximumCommandsPerSecond,
            RequestTimeoutMilliseconds = source.RequestTimeoutMilliseconds,
            CompletedOrderLookbackDays = source.CompletedOrderLookbackDays,
        };
        var endpoint = CTraderExecutionEndpointGate.Resolve(options, _liveConfirmationStore);
        var scheduler = new CTraderSerializedEventScheduler();
        var transport = new CTraderTlsExecutionTransport(endpoint);
        try
        {
            return new OwnedAdapter(
                new CTraderExecutionAdapter(options, transport, clock, scheduler, _liveConfirmationStore),
                scheduler);
        }
        catch
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private OwnedAdapter CreateAlpacaAdapter(ExecutionModeChangeRequest request, string accountId)
    {
        var source = _alpacaOptions ??
            throw new InvalidOperationException("The Alpaca owner configuration is unavailable.");
        var clock = _executionClock ??
            throw new InvalidOperationException("The execution clock is unavailable.");
        var keyId = string.IsNullOrWhiteSpace(request.KeyId) ? source.KeyId : request.KeyId.Trim();
        var secretKey = string.IsNullOrWhiteSpace(request.SecretKey) ? source.SecretKey : request.SecretKey;
        var options = new AlpacaExecutionOptions
        {
            Enabled = source.Enabled,
            Mode = request.Mode,
            AllowLiveExecution = source.AllowLiveExecution,
            BaseUrl = request.Mode == ExecutionMode.Live
                ? AlpacaExecutionOptions.LiveBaseUrl
                : AlpacaExecutionOptions.PaperBaseUrl,
            MarketDataBaseUrl = source.MarketDataBaseUrl,
            KeyId = keyId,
            SecretKey = secretKey,
            ExpectedAccountId = accountId,
            Symbol = source.Symbol,
            CanonicalInstrumentId = source.CanonicalInstrumentId,
            PollIntervalMilliseconds = source.PollIntervalMilliseconds,
            MaximumTrackedOrders = source.MaximumTrackedOrders,
            MaximumCommandsPerMinute = source.MaximumCommandsPerMinute,
            RequestTimeoutMilliseconds = source.RequestTimeoutMilliseconds,
        };
        var endpoint = AlpacaExecutionEndpointGate.Resolve(options, _liveConfirmationStore);
        var transport = new AlpacaHttpExecutionTransport(
            endpoint,
            TimeSpan.FromMilliseconds(options.RequestTimeoutMilliseconds));
        var updates = new AlpacaPollingTradeUpdateSource(
            TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds),
            options.MaximumTrackedOrders);
        var scheduler = new AlpacaSerializedEventScheduler();
        try
        {
            return new OwnedAdapter(new AlpacaExecutionAdapter(
                options,
                transport,
                updates,
                clock,
                scheduler,
                _liveConfirmationStore));
        }
        catch
        {
            updates.DisposeAsync().AsTask().GetAwaiter().GetResult();
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private OwnedAdapter CreateInteractiveBrokersAdapter(
        ExecutionMode mode,
        string host,
        int port,
        int clientId,
        string accountId)
    {
        var source = _interactiveBrokersOptions ??
            throw new InvalidOperationException("The Interactive Brokers owner configuration is unavailable.");
        var clock = _executionClock ??
            throw new InvalidOperationException("The execution clock is unavailable.");
        var exactPort = ResolveInteractiveBrokersModePort(port, mode);
        var options = new InteractiveBrokersExecutionOptions
        {
            Enabled = source.Enabled,
            Mode = mode,
            AllowLiveExecution = source.AllowLiveExecution,
            Host = host.Trim(),
            Port = exactPort,
            ClientId = clientId,
            AccountId = accountId,
            Symbol = source.Symbol,
            SecurityType = source.SecurityType,
            Exchange = source.Exchange,
            PrimaryExchange = source.PrimaryExchange,
            Currency = source.Currency,
            ContractId = source.ContractId,
            CanonicalInstrumentId = source.CanonicalInstrumentId,
            OutsideRegularTradingHours = source.OutsideRegularTradingHours,
            MaximumCommandsPerSecond = source.MaximumCommandsPerSecond,
            RequestTimeoutMilliseconds = source.RequestTimeoutMilliseconds,
            MaximumTrackedOrders = source.MaximumTrackedOrders,
        };
        var endpoint = InteractiveBrokersExecutionEndpointGate.Resolve(options, _liveConfirmationStore);
        var timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMilliseconds);
        var scheduler = new InteractiveBrokersSerializedEventScheduler();
        var transport = _interactiveBrokersTransportFactory?.Invoke(endpoint, timeout) ??
                        InteractiveBrokersExecutionTransportFactory.CreateDefault(
                            endpoint,
                            timeout,
                            options.MaximumTrackedOrders);
        try
        {
            return new OwnedAdapter(new InteractiveBrokersExecutionAdapter(
                options,
                transport,
                clock,
                scheduler,
                _liveConfirmationStore));
        }
        catch
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static int ResolveInteractiveBrokersModePort(int port, ExecutionMode mode) => port switch
    {
        InteractiveBrokersExecutionOptions.TwsPaperPort or InteractiveBrokersExecutionOptions.TwsLivePort =>
            mode == ExecutionMode.Live
                ? InteractiveBrokersExecutionOptions.TwsLivePort
                : InteractiveBrokersExecutionOptions.TwsPaperPort,
        InteractiveBrokersExecutionOptions.GatewayPaperPort or InteractiveBrokersExecutionOptions.GatewayLivePort =>
            mode == ExecutionMode.Live
                ? InteractiveBrokersExecutionOptions.GatewayLivePort
                : InteractiveBrokersExecutionOptions.GatewayPaperPort,
        _ => throw new InvalidOperationException(
            "Interactive Brokers accepts only TWS ports 7497/7496 or Gateway ports 4002/4001."),
    };

    private async ValueTask<CTraderExecutionAdapter> ReconfigureCTraderForConnectAsync(
        CTraderExecutionAdapter current,
        ExecutionAdapterConnectRequest request,
        CancellationToken cancellationToken)
    {
        if (current.Session.IsDataConnected || current.Session.IsExecutionAuthenticated)
            throw new InvalidOperationException("Disconnect cTrader before changing its execution credentials.");
        if (!TryParseCanonicalCTraderAccountId(request.AccountId, out var ctidTraderAccountId))
            throw new InvalidOperationException("cTrader execution requires one canonical positive account ID.");

        var replacement = CreateCTraderAdapter(
            current.Mode,
            request.OAuthClientId,
            request.OAuthClientSecret,
            request.OAuthAccessToken,
            ctidTraderAccountId.ToString(CultureInfo.InvariantCulture));
        var installed = false;
        lock (_gate)
        {
            var index = _registeredAdapters.IndexOf(current);
            if (index >= 0 &&
                Volatile.Read(ref _disposed) == 0 &&
                !cancellationToken.IsCancellationRequested &&
                !current.Session.IsDataConnected &&
                !_books.Any(book => ReferenceEquals(book.Runtime?.Adapter, current)))
            {
                current.EventReceived -= OnRegisteredAdapterEvent;
                replacement.Adapter.EventReceived += OnRegisteredAdapterEvent;
                _registeredAdapters[index] = replacement.Adapter;
                _ownedAdapters.Add(replacement.Adapter);
                if (replacement.Scheduler is not null)
                    _ownedSchedulers[replacement.Adapter] = replacement.Scheduler;
                _adapterConnectionErrors.Remove(AdapterKey(current));
                installed = true;
            }
        }

        if (!installed)
        {
            await DisposeOwnedAdapterAsync(replacement.Adapter, replacement.Scheduler).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "The cTrader connection changed concurrently; no transport was opened.");
        }

        await DisposeIfOwnedAsync(current).ConfigureAwait(false);
        return (CTraderExecutionAdapter)replacement.Adapter;
    }

    private async ValueTask<InteractiveBrokersExecutionAdapter> ReconfigureInteractiveBrokersForConnectAsync(
        InteractiveBrokersExecutionAdapter current,
        ExecutionAdapterConnectRequest request,
        CancellationToken cancellationToken)
    {
        if (current.Session.IsDataConnected || current.Session.IsExecutionAuthenticated)
            throw new InvalidOperationException("Disconnect Interactive Brokers before changing its connection settings.");
        var host = request.Host?.Trim() ?? string.Empty;
        var accountId = request.AccountId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host) ||
            !string.Equals(host, request.Host, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Interactive Brokers requires one exact trimmed host.");
        }
        if (!IsValidModeAccountId(accountId) ||
            !string.Equals(accountId, request.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Interactive Brokers requires one exact bounded account ID.");
        }
        if (request.ClientId < 0)
            throw new InvalidOperationException("Interactive Brokers client ID cannot be negative.");
        var exactPort = ResolveInteractiveBrokersModePort(request.Port, current.Mode);
        if (exactPort != request.Port)
        {
            throw new InvalidOperationException(
                $"Interactive Brokers {current.Mode} mode requires its exact TWS or Gateway port.");
        }

        var replacement = CreateInteractiveBrokersAdapter(
            current.Mode,
            host,
            exactPort,
            request.ClientId,
            accountId);
        var installed = false;
        lock (_gate)
        {
            var index = _registeredAdapters.IndexOf(current);
            if (index >= 0 &&
                Volatile.Read(ref _disposed) == 0 &&
                !cancellationToken.IsCancellationRequested &&
                !current.Session.IsDataConnected &&
                !_books.Any(book => ReferenceEquals(book.Runtime?.Adapter, current)))
            {
                current.EventReceived -= OnRegisteredAdapterEvent;
                replacement.Adapter.EventReceived += OnRegisteredAdapterEvent;
                _registeredAdapters[index] = replacement.Adapter;
                _ownedAdapters.Add(replacement.Adapter);
                if (replacement.Scheduler is not null)
                    _ownedSchedulers[replacement.Adapter] = replacement.Scheduler;
                _interactiveBrokersAccountId = accountId;
                _adapterConnectionErrors.Remove(AdapterKey(current));
                installed = true;
            }
        }

        if (!installed)
        {
            await DisposeOwnedAdapterAsync(replacement.Adapter, replacement.Scheduler).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "The Interactive Brokers connection changed concurrently; no socket was opened.");
        }

        await DisposeIfOwnedAsync(current).ConfigureAwait(false);
        return (InteractiveBrokersExecutionAdapter)replacement.Adapter;
    }

    private ExecutionCommandResult ModeChangeFailure(string message)
    {
        var result = ExecutionCommandResult.Failure(message);
        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result;
    }

    private ExecutionCommandResult ModeChangeSuccess(string message)
    {
        var result = ExecutionCommandResult.Success(message);
        lock (_gate)
            _lastOperationMessage = result.Message;
        Invalidate();
        return result;
    }

    private void TryRemoveLiveConfirmation(string brokerId, string accountId)
    {
        if (_liveConfirmationStore is null)
            return;
        try
        {
            _liveConfirmationStore.Remove(brokerId, accountId);
        }
        catch
        {
            // Failing to revoke is reported by the next live construction gate; this connection is
            // already PAPER or unchanged and therefore cannot route a live order from this failure.
        }
    }

    private async ValueTask DisposeIfOwnedAsync(IBrokerExecutionAdapter adapter)
    {
        IAsyncDisposable? scheduler = null;
        bool owned;
        lock (_gate)
        {
            owned = _ownedAdapters.Remove(adapter);
            if (_ownedSchedulers.Remove(adapter, out var registeredScheduler))
                scheduler = registeredScheduler;
        }
        if (owned)
            await DisposeOwnedAdapterAsync(adapter, scheduler).ConfigureAwait(false);
    }

    private static async ValueTask DisposeOwnedAdapterAsync(
        IBrokerExecutionAdapter adapter,
        IAsyncDisposable? scheduler)
    {
        if (adapter is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (adapter is IDisposable disposable)
            disposable.Dispose();
        if (scheduler is not null)
            await scheduler.DisposeAsync().ConfigureAwait(false);
    }

    private static string CurrentConfirmingIdentity()
    {
        var userName = Environment.UserName?.Trim() ?? string.Empty;
        var domain = Environment.UserDomainName?.Trim() ?? string.Empty;
        var identity = domain.Length == 0 ? userName : $"{domain}\\{userName}";
        if (string.IsNullOrWhiteSpace(identity) ||
            identity.Length > LiveExecutionConfirmation.MaximumConfirmingIdentityLength)
        {
            throw new InvalidOperationException("The current confirming identity is unavailable or unbounded.");
        }
        return identity;
    }

    private static bool TryParseCanonicalCTraderAccountId(string? value, out long accountId)
    {
        accountId = 0;
        return !string.IsNullOrWhiteSpace(value) &&
               long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out accountId) &&
               accountId > 0 &&
               string.Equals(value, accountId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool IsValidModeAccountId(string accountId) =>
        !string.IsNullOrWhiteSpace(accountId) &&
        accountId.Length <= LiveExecutionConfirmation.MaximumAccountIdLength &&
        string.Equals(accountId, accountId.Trim(), StringComparison.Ordinal);

    private static string SafeReason(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 512 ? message : message[..512];
    }

    private sealed record OwnedAdapter(
        IBrokerExecutionAdapter Adapter,
        IAsyncDisposable? Scheduler = null);

    private IReadOnlyList<ExecutionAdapterReadModel> BuildAdapterReadModels()
    {
        var cards = new List<ExecutionAdapterReadModel>();
        var simulatedRuntimes = _books
            .Select(book => book.Runtime)
            .OfType<InProcessBookRuntime>()
            .ToArray();
        if (simulatedRuntimes.Length > 0)
        {
            var capabilities = simulatedRuntimes[0].Adapter.Capabilities;
            cards.Add(new ExecutionAdapterReadModel(
                "simulated",
                "Simulated",
                $"{simulatedRuntimes.Length} in-process book account{(simulatedRuntimes.Length == 1 ? string.Empty : "s")}",
                ExecutionConnectionStatus.Connected,
                "Connected",
                "In-process, deterministic, and network-free.",
                ExecutionTone.Positive,
                IsRegistered: true,
                CanConnect: false,
                CanDisconnect: false,
                CanCreateBook: true,
                IsDemoOnly: true,
                "No credentials",
                "Owned locally by the execution console; no host, key, secret, or socket is used.",
                CapabilityLabels(capabilities),
                EnvironmentLabel: "TEST",
                Mode: ExecutionMode.Paper,
                BrokerAccountId: "simulated"));
        }

        foreach (var adapter in _registeredAdapters
                     .OrderBy(adapter => adapter.Account.AdapterId.Value, StringComparer.Ordinal))
        {
            cards.Add(BuildRegisteredAdapterReadModel(adapter));
        }

        if (_registeredAdapters.All(adapter => adapter is not CTraderExecutionAdapter))
        {
            cards.Add(new ExecutionAdapterReadModel(
                "ctrader-openapi-demo",
                "cTrader PAPER",
                "No registered execution account",
                ExecutionConnectionStatus.AdapterUnavailable,
                "Adapter unavailable",
                "The cTrader adapter was not registered by this host; PAPER remains the safe default.",
                ExecutionTone.Neutral,
                IsRegistered: false,
                CanConnect: false,
                CanDisconnect: false,
                CanCreateBook: false,
                IsDemoOnly: true,
                "OAuth from local configuration",
                "Client ID, secret, access token, and account stay in local options. LIVE additionally requires owner opt-in and persisted typed confirmation.",
                Array.AsReadOnly(["PAPER default", "LIVE gated", "Fail-closed"]),
                EnvironmentLabel: "PAPER",
                Mode: ExecutionMode.Paper));
        }

        return Array.AsReadOnly(cards.ToArray());
    }

    private ExecutionAdapterReadModel BuildRegisteredAdapterReadModel(IBrokerExecutionAdapter adapter)
    {
        var session = adapter.Session;
        var isCTrader = adapter is CTraderExecutionAdapter;
        var isAlpaca = adapter is AlpacaExecutionAdapter;
        var isInteractiveBrokers = adapter is InteractiveBrokersExecutionAdapter;
        BrokerKind? loginBroker = adapter switch
        {
            CTraderExecutionAdapter => BrokerKind.CTrader,
            AlpacaExecutionAdapter => BrokerKind.Alpaca,
            InteractiveBrokersExecutionAdapter => BrokerKind.InteractiveBrokers,
            _ => null,
        };
        var adapterId = AdapterKey(adapter);
        var hasConnectionError = _adapterConnectionErrors.TryGetValue(adapterId, out var connectionError);
        var status = hasConnectionError
            ? ExecutionConnectionStatus.Error
            : session.CanExecute
            ? ExecutionConnectionStatus.Connected
            : session.Health == ExecutionSessionHealth.Disconnected
                ? ExecutionConnectionStatus.NotConfigured
                : ExecutionConnectionStatus.Error;
        var tone = status switch
        {
            ExecutionConnectionStatus.Connected => ExecutionTone.Positive,
            ExecutionConnectionStatus.Error => ExecutionTone.Warning,
            _ => ExecutionTone.Neutral,
        };
        var statusLabel = status switch
        {
            ExecutionConnectionStatus.Connected => "Connected",
            ExecutionConnectionStatus.Error => isAlpaca
                ? "Authentication error"
                : isInteractiveBrokers ? "Connection error" : session.Health.ToString(),
            _ => "Not connected",
        };
        return new ExecutionAdapterReadModel(
            adapterId,
            AdapterDisplayName(adapter),
            (isAlpaca || isInteractiveBrokers) && !session.CanExecute
                ? $"{adapter.Mode.ToString().ToUpperInvariant()} account not authenticated"
                : adapter.Account.AccountId.Value,
            status,
            statusLabel,
            hasConnectionError
                ? connectionError!
                : session.CanExecute
                ? "Execution authenticated and certified by the registered adapter."
                : "The registered adapter is fail-closed until the shared Login form credentials authenticate and certify execution.",
            tone,
            IsRegistered: true,
            CanConnect: isCTrader && session.Health == ExecutionSessionHealth.Disconnected ||
                        isAlpaca && !session.CanExecute ||
                        isInteractiveBrokers && session.Health == ExecutionSessionHealth.Disconnected,
            CanDisconnect: (isCTrader || isAlpaca || isInteractiveBrokers) && session.IsDataConnected,
            CanCreateBook: isAlpaca && session.CanExecute &&
                           !_books.Any(book => string.Equals(book.Configuration.AdapterId, adapterId, StringComparison.Ordinal)),
            IsDemoOnly: adapter.Mode == ExecutionMode.Paper,
            loginBroker is not null ? "Shared Login form" : "Host-owned configuration",
            isCTrader
                ? $"Enter cTrader OAuth credentials in the shared Login form and the exact execution account ID separately. The console uses the gated {(adapter.Mode == ExecutionMode.Live ? "LIVE" : "DEMO")} endpoint and never persists credentials."
                : isAlpaca
                    ? $"Enter Alpaca {adapter.Mode.ToString().ToUpperInvariant()} credentials in the shared Login form and the exact execution account ID separately. This console does not persist credentials."
                    : isInteractiveBrokers
                        ? "Enter the host, exact mode port, and unique client ID in the shared Login form, plus the exact execution account ID separately. The console never persists credentials."
                : "Connection settings are owned by the registered adapter; no book client is attached and the console never constructs one.",
            CapabilityLabels(adapter.Capabilities),
            EnvironmentLabel: isCTrader
                ? adapter.Mode == ExecutionMode.Live ? "LIVE" : "DEMO"
                : isAlpaca || isInteractiveBrokers ? adapter.Mode.ToString().ToUpperInvariant() : string.Empty,
            Mode: adapter.Mode,
            BrokerAccountId: adapter is AlpacaExecutionAdapter { NativeAccountId: { } nativeAccountId }
                ? nativeAccountId
                : isInteractiveBrokers && !string.IsNullOrWhiteSpace(_interactiveBrokersAccountId)
                    ? _interactiveBrokersAccountId
                    : adapter.Account.AccountId.Value,
            LoginBroker: loginBroker);
    }

    private static string AdapterKey(IBrokerExecutionAdapter adapter) =>
        adapter switch
        {
            AlpacaExecutionAdapter alpaca => alpaca.AdapterId,
            InteractiveBrokersExecutionAdapter interactiveBrokers => interactiveBrokers.AdapterId,
            _ => $"{adapter.Account.AdapterId.Value}|{adapter.Account.AccountId.Value}",
        };

    private static IReadOnlyList<string> CapabilityLabels(BrokerExecutionCapabilities capabilities) =>
        Array.AsReadOnly(
        [
            $"Orders: {capabilities.CanonicalCapabilities.OrderTypes}",
            $"TIF: {capabilities.CanonicalCapabilities.TimeInForce}",
            $"Replace: {capabilities.ReplaceSemantics}",
            capabilities.SupportsFractionalQuantity ? "Fractional quantity" : "Whole quantity",
            $"Rate: {capabilities.RateLimit.MaximumCommands}/{capabilities.RateLimit.Window.TotalSeconds:0.#}s",
        ]);

    private void OnRegisteredAdapterEvent(BrokerAdapterEvent _) => Invalidate();

    private void Invalidate()
    {
        PublishExecutionModeStatus();
        SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void PublishExecutionModeStatus()
    {
        if (_modeStatusPublisher is null)
            return;

        bool hasLiveExecution;
        try
        {
            lock (_gate)
            {
                hasLiveExecution = _registeredAdapters.Any(adapter => adapter.Mode == ExecutionMode.Live) ||
                                   _books.Any(book => book.Runtime?.Adapter.Mode == ExecutionMode.Live);
            }
        }
        catch
        {
            hasLiveExecution = true;
        }
        _modeStatusPublisher.Publish(hasLiveExecution);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _disposeCancellation.Cancel();
            lock (_gate)
            {
                foreach (var book in _books)
                    book.Runtime?.Dispose();
                _books.Clear();
            }
            foreach (var adapter in _registeredAdapters)
                adapter.EventReceived -= OnRegisteredAdapterEvent;
            foreach (var adapter in _ownedAdapters.ToArray())
                DisposeIfOwnedAsync(adapter).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _modeStatusPublisher?.Dispose();
            _disposeCancellation.Dispose();
        }
    }

    private interface IBookRuntime : IDisposable
    {
        BrokerExecutionAccount Account { get; }

        IBrokerExecutionAdapter Adapter { get; }

        ExecutionBookReadModel BuildReadModel(BookConfiguration configuration, bool isPaused);

        ValueTask<ExecutionCommandResult> ReconcileAsync(CancellationToken cancellationToken);

        ValueTask<ExecutionCommandResult> KillAsync(CancellationToken cancellationToken);

        ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(
            BookConfiguration configuration,
            ExecutionManualOrderRequest request,
            CancellationToken cancellationToken);

        ValueTask<ExecutionCommandResult> SubmitTargetAsync(
            BookConfiguration configuration,
            TradeIntent intent,
            CancellationToken cancellationToken);
    }

    private sealed class BookEntry(
        BookConfiguration configuration,
        IBookRuntime? runtime)
    {
        private int _operationInProgress;

        internal BookConfiguration Configuration { get; } = configuration;

        internal IBookRuntime? Runtime { get; } = runtime;

        internal bool IsPaused { get; set; }

        internal bool TryBeginOperation() =>
            Interlocked.CompareExchange(ref _operationInProgress, 1, 0) == 0;

        internal void EndOperation() => Volatile.Write(ref _operationInProgress, 0);

        internal ExecutionCommandResult SetPaused(bool paused)
        {
            IsPaused = paused;
            return ExecutionCommandResult.Success(
                paused
                    ? $"Paused new-order intake for book “{Configuration.Name}”."
                    : $"Resumed new-order intake for book “{Configuration.Name}”; engine gates still apply.");
        }

        internal ExecutionBookReadModel BuildReadModel() =>
            Runtime is null
                ? Configuration.BuildUnavailableReadModel(IsPaused)
                : Runtime.BuildReadModel(Configuration, IsPaused);
    }

    private sealed class InProcessBookRuntime : IBookRuntime
    {
        private const int LedgerViewCapacity = 96;
        private const int OperationalHistoryCapacity = 500;
        private const int OperationalTableCapacity = 500;
        private const int MaximumResyncBatchesPerRefresh = 4;
        private const int ServiceBatchCapacity = 256;
        private const int MaximumTargetOrders = 500;

        private readonly string _bookId;
        private readonly MutableExecutionClock _clock;
        private readonly InMemoryOrderEventStore _ledger;
        private readonly InMemoryReconciliationCaseStore _caseStore;
        private readonly RiskEngine _risk;
        private readonly DeterministicSimulatedVenue _venue;
        private readonly ControllableAdapterEventScheduler _scheduler;
        private readonly SimulatedExecutionAdapter _adapter;
        private readonly ExecutionLease _lease;
        private readonly OrderManagementService _oms;
        private readonly ReconciliationEngine _reconciliation;
        private readonly ExecutionCoordinator _coordinator;
        private readonly ExecutionServiceEngine _engine;
        private readonly Queue<ExecutionLedgerEventReadModel> _ledgerView = new();
        private readonly Queue<TradingTerminal.Execution.Oms.OrderEvent> _historyView = new();
        private readonly Dictionary<string, DateTime> _pendingAcknowledgements = new(StringComparer.Ordinal);
        private long _outboxCursor;
        private long _requestSequence;
        private int _orderCount;
        private int _filledOrderCount;
        private int _rejectCount;
        private int _cancelCount;
        private int _unknownOutcomeCount;
        private int _acknowledgementCount;
        private int _targetOrderCount;
        private double _totalAcknowledgementLatencyMilliseconds;
        private bool _disposed;

        private InProcessBookRuntime(
            string bookId,
            IEnumerable<VenueSubmitPlan> plans,
            DateTime seedStartUtc,
            IExecutionLeaseStore executionLeaseStore)
        {
            _bookId = bookId;
            _clock = new MutableExecutionClock(seedStartUtc);
            _ledger = new InMemoryOrderEventStore();
            _caseStore = new InMemoryReconciliationCaseStore();
            _risk = CreateRiskEngine(bookId);
            _venue = new DeterministicSimulatedVenue(_clock, plans);
            _scheduler = new ControllableAdapterEventScheduler();
            var account = new BrokerExecutionAccount(
                new ExecutionAdapterId("simulated"),
                new BrokerAccountId($"execution-console-{bookId}"));
            var session = new BrokerExecutionSession(
                account,
                ExecutionSessionHealth.Healthy,
                IsDataConnected: true,
                IsExecutionAuthenticated: true,
                IsExecutionCertified: true,
                _clock.UtcNow);
            _adapter = new SimulatedExecutionAdapter(_venue, _clock, _scheduler, session);
            var acquired = ExecutionLease.Acquire(
                account,
                executionLeaseStore,
                _clock,
                new ExecutionLeaseId($"console-{bookId}-{Guid.NewGuid():N}"));
            if (!acquired.IsSuccess || acquired.Lease is null)
            {
                throw new InvalidOperationException(
                    $"The in-process simulated lease for '{bookId}' could not be acquired: {acquired.Reason}");
            }
            _lease = acquired.Lease;
            _oms = new OrderManagementService(_ledger, _risk, _venue, _clock);
            _reconciliation = new ReconciliationEngine(_oms, _caseStore, _clock);
            ExecutionCoordinator? coordinator = null;
            try
            {
                coordinator = new ExecutionCoordinator(
                    _oms,
                    [_adapter],
                    _reconciliation,
                    [_lease]);
                _engine = new ExecutionServiceEngine(_ledger, _oms, coordinator, _scheduler, _lease);
                _coordinator = coordinator;
            }
            catch
            {
                coordinator?.Dispose();
                _lease.Dispose();
                throw;
            }
        }

        internal static InProcessBookRuntime CreateEmpty(
            string bookId,
            IExecutionLeaseStore executionLeaseStore) =>
            new(bookId, Array.Empty<VenueSubmitPlan>(), DateTime.UtcNow, executionLeaseStore);

        public BrokerExecutionAccount Account => _adapter.Account;

        public IBrokerExecutionAdapter Adapter => _adapter;

        public ExecutionBookReadModel BuildReadModel(BookConfiguration configuration, bool isPaused)
        {
            RefreshLedgerView();
            var observedAtUtc = DateTime.UtcNow;
            var adapterSnapshot = _adapter.CaptureReconciliationSnapshot();
            var realPositions = adapterSnapshot.Positions.ToDictionary(
                position => position.Instrument.Value,
                position => ToDecimal(position.Quantity));
            var positions = configuration.Instruments
                .Select(instrument => BuildPosition(configuration.Name, instrument, realPositions))
                .ToArray();
            var orders = _oms.ReadAllProjections()
                .OrderByDescending(projection => LastEventTime(projection.ClientOrderId))
                .Take(OperationalTableCapacity)
                .Select(projection => BuildOrder(configuration, projection))
                .ToArray();
            var materialCases = LatestMaterialCases();
            var cases = BuildReconciliationCases(configuration, materialCases);
            var (longExposure, shortExposure) = CalculateExposure(configuration, realPositions);
            var quality = BuildExecutionQuality(materialCases.Count);
            var analytics = ExecutionAnalyticsProjector.BuildBook(
                configuration.Id,
                configuration.Name,
                configuration.OpeningEquity,
                configuration.AnalyticsHistory,
                realPositions.Values.Count(quantity => quantity != 0m),
                longExposure,
                shortExposure,
                quality,
                observedAtUtc);
            var period = analytics.Period(ExecutionTimeRange.ThirtyDays);
            var admissionOpen = !isPaused &&
                                _lease.CanAdmitNewOrders &&
                                _reconciliation.CanAdmitNewOrders(_adapter.Account);

            return new ExecutionBookReadModel(
                configuration.Id,
                configuration.Name,
                configuration.AdapterId,
                configuration.AdapterName,
                configuration.Strategies,
                period.Metrics.NetProfitAndLossDisplay,
                period.Metrics.ProfitAndLossTone,
                new ExecutionLeaseReadModel(
                    ExecutionLeaseStatus.Held,
                    _lease.Grant.FencingToken.Value,
                    "local simulated writer"),
                isPaused,
                admissionOpen,
                realPositions.Values.Count(quantity => quantity != 0m),
                Array.AsReadOnly(positions),
                Array.AsReadOnly(orders),
                BuildHistory(configuration, _historyView.ToArray()),
                cases,
                BuildRisk(configuration),
                Array.AsReadOnly(_ledgerView.Reverse().ToArray()),
                analytics)
            {
                TradableInstruments = Array.AsReadOnly(configuration.Instruments
                    .Select(instrument => new ExecutionTradableInstrumentReadModel(
                        new InstrumentId(instrument.InstrumentId),
                        instrument.Symbol))
                    .ToArray()),
            };
        }

        public ValueTask<ExecutionCommandResult> ReconcileAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Reconcile());
        }

        public ValueTask<ExecutionCommandResult> KillAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(FlattenAll());
        }

        public ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(
            BookConfiguration configuration,
            ExecutionManualOrderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SubmitManualOrder(configuration, request));
        }

        public ValueTask<ExecutionCommandResult> SubmitTargetAsync(
            BookConfiguration configuration,
            TradeIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SubmitTarget(configuration, intent));
        }

        internal ExecutionCommandResult Reconcile()
        {
            ThrowIfDisposed();
            _clock.SetTo(DateTime.UtcNow);
            _adapter.ClearReconciliationSnapshotInjection();
            var exchange = _engine.Handle(new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                NextRequestId("reconcile"),
                ExecutionServiceRequestKind.Reconcile,
                _adapter.Account,
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken,
                ReconciliationTrigger: ReconciliationTrigger.OperatorRequest));
            if (!exchange.Response.IsSuccess)
            {
                return ExecutionCommandResult.Failure(
                    $"Reconciliation failed closed: {exchange.Response.Reason ?? exchange.Response.Fault.ToString()}.");
            }

            var open = LatestMaterialCases().Count(item => item.Status != ReconciliationCaseStatus.Resolved);
            return ExecutionCommandResult.Success(
                open == 0
                    ? "Reconciliation completed against exact Simulated-adapter evidence; admission is open."
                    : $"Reconciliation completed with {open} material case(s) still open; admission remains blocked.");
        }

        internal ExecutionCommandResult FlattenAll()
        {
            ThrowIfDisposed();
            _clock.SetTo(DateTime.UtcNow);
            if (!_lease.CanAdmitNewOrders || !_reconciliation.CanAdmitNewOrders(_adapter.Account))
            {
                return ExecutionCommandResult.Failure(
                    "Flatten refused by the reconciliation/lease admission gate. Reconcile the book first.");
            }

            foreach (var projection in _oms.ReadAllProjections().Where(IsCancellable))
            {
                var cancel = _engine.Handle(new ExecutionServiceRequest(
                    ExecutionServiceProtocol.CurrentVersion,
                    NextRequestId("flatten-cancel"),
                    ExecutionServiceRequestKind.Cancel,
                    _adapter.Account,
                    _lease.Grant.LeaseId,
                    _lease.Grant.FencingToken,
                    Cancel: new ExecutionCancelRequest(projection.ClientOrderId)));
                if (!cancel.Response.IsSuccess)
                {
                    return ExecutionCommandResult.Failure(
                        $"Flatten stopped safely while cancelling {projection.ClientOrderId}: " +
                        $"{cancel.Response.Reason ?? cancel.Response.Fault.ToString()}.");
                }
            }

            var positions = _adapter.CaptureReconciliationSnapshot().Positions
                .Where(position => position.Quantity.Coefficient != 0)
                .ToArray();
            if (positions.Length == 0)
                return ExecutionCommandResult.Success("The Simulated adapter already reports a flat book.");

            foreach (var position in positions)
            {
                if (!position.Quantity.TryGetWholeUnits(out var currentUnits))
                    return ExecutionCommandResult.Failure("Flatten refused: a simulated position is not a whole exact unit.");

                var clientOrderId = $"{BookPrefix()}-flatten-{position.Instrument.Value}";
                var referencePrice = ReferencePrice(position.Instrument.Value);
                if (BookPrefix().StartsWith("book-", StringComparison.Ordinal))
                {
                    var plan = FilledPlan(
                        clientOrderId,
                        Math.Abs(currentUnits),
                        referencePrice,
                        ScaledMoney.Zero);
                    if (!_venue.TryAddSubmitPlan(plan))
                    {
                        return ExecutionCommandResult.Failure(
                            $"Flatten stopped safely because the Simulated plan for {position.Instrument} could not be reserved.");
                    }
                }
                var instruction = CreateInstruction(
                    clientOrderId,
                    position.Instrument.Value,
                    currentUnits,
                    targetUnits: 0,
                    CanonicalOrderType.Market,
                    referencePrice);
                var response = Submit(
                    instruction,
                    RiskSnapshot(instruction, currentUnits, referencePrice, grossExposure: 200_000));
                if (!response.IsSuccess)
                {
                    return ExecutionCommandResult.Failure(
                        $"Flatten stopped safely for instrument {position.Instrument}: " +
                        $"{response.Reason ?? response.Fault.ToString()}.");
                }
                if (response.State != OrderLifecycleState.Filled)
                {
                    return ExecutionCommandResult.Failure(
                        $"Flatten is incomplete for instrument {position.Instrument}: the simulated order " +
                        $"ended in {response.State?.ToString() ?? "an unknown state"} rather than Filled.");
                }
            }

            var remaining = _adapter.CaptureReconciliationSnapshot().Positions
                .Where(position => position.Quantity.Coefficient != 0)
                .ToArray();
            if (remaining.Length != 0)
            {
                return ExecutionCommandResult.Failure(
                    $"Flatten is incomplete: the Simulated adapter still reports {remaining.Length} open " +
                    $"position(s) ({string.Join(", ", remaining.Select(position => position.Instrument.Value))}).");
            }

            return ExecutionCommandResult.Success(
                $"Verified {positions.Length} position(s) flat against the Simulated-adapter snapshot; intake is paused.");
        }

        private ExecutionCommandResult SubmitManualOrder(
            BookConfiguration configuration,
            ExecutionManualOrderRequest request)
        {
            ThrowIfDisposed();
            _clock.SetTo(DateTime.UtcNow);
            var configuredInstrument = configuration.Instruments.FirstOrDefault(
                item => item.InstrumentId == request.Instrument.Value);
            if (configuredInstrument is null ||
                !string.Equals(configuredInstrument.Symbol, request.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return ExecutionCommandResult.Failure(
                    "Order refused because the instrument is not configured for the selected book.");
            }
            if (!request.Quantity.TryGetWholeUnits(out var requestedUnits) || requestedUnits <= 0)
                return ExecutionCommandResult.Failure("Order quantity must be a positive whole number.");
            if (!request.HasWellFormedPriceTerms)
                return ExecutionCommandResult.Failure("Price terms do not match the selected order type.");

            var snapshot = _adapter.CaptureReconciliationSnapshot();
            var position = snapshot.Positions.FirstOrDefault(item => item.Instrument == request.Instrument)?.Quantity ??
                           ScaledQuantity.Zero;
            if (!position.TryGetWholeUnits(out _))
                return ExecutionCommandResult.Failure("The current position is not an exact whole quantity.");
            var referencePrice = request.LimitPrice ?? request.StopPrice ?? ReferencePrice(request.Instrument.Value);
            var instruction = CreateManualInstruction(request);
            var response = Submit(
                instruction,
                RiskSnapshot(instruction, position.TryGetWholeUnits(out var current) ? current : 0, referencePrice, 0));
            return response.IsSuccess
                ? ExecutionCommandResult.Success(
                    $"Order {instruction.Identity.ClientOrderId} reached {response.State?.ToString() ?? "the OMS"} through the book's execution route.")
                : ExecutionCommandResult.Failure(
                    $"Order failed closed: {response.Reason ?? response.Fault.ToString()}.");
        }

        private ExecutionCommandResult SubmitTarget(
            BookConfiguration configuration,
            TradeIntent intent)
        {
            ThrowIfDisposed();
            _clock.SetTo(DateTime.UtcNow);
            if (intent.QuantityMode != TradeIntentQuantityMode.TargetPosition)
                return ExecutionCommandResult.Failure("Sandbox replication accepts only TargetPosition intents.");
            var configuredInstrument = configuration.Instruments.FirstOrDefault(
                item => item.InstrumentId == intent.Instrument.Value);
            if (configuredInstrument is null)
            {
                return ExecutionCommandResult.Failure(
                    "Sandbox target refused because its single instrument is not bound to this book.");
            }
            if (!intent.SignedUnits.TryGetWholeUnits(out var targetUnits))
                return ExecutionCommandResult.Failure("Sandbox target must contain an exact whole-unit position.");

            var reconciled = Reconcile();
            if (!reconciled.IsSuccess)
                return ExecutionCommandResult.Failure($"Sandbox target {reconciled.Message}");

            var snapshot = _adapter.CaptureReconciliationSnapshot();
            var position = snapshot.Positions.FirstOrDefault(item => item.Instrument == intent.Instrument)?.Quantity ??
                           ScaledQuantity.Zero;
            if (!position.TryGetWholeUnits(out var currentUnits))
                return ExecutionCommandResult.Failure("The current position is not an exact whole quantity.");
            if (currentUnits == targetUnits)
                return ExecutionCommandResult.Success("The Simulated book already matches the sandbox target.");

            long delta;
            try
            {
                delta = checked(targetUnits - currentUnits);
            }
            catch (OverflowException)
            {
                return ExecutionCommandResult.Failure("Sandbox target delta cannot be represented safely.");
            }
            if (delta == long.MinValue)
                return ExecutionCommandResult.Failure("Sandbox target delta cannot be represented safely.");
            if (!TryReserveTargetOrderSlot())
            {
                return ExecutionCommandResult.Failure(
                    $"The bounded target runtime reached its {MaximumTargetOrders}-order limit.");
            }
            if (!TryCalculateGrossExposure(snapshot, out var grossExposure))
                return ExecutionCommandResult.Failure("The exact simulated gross exposure cannot be represented safely.");

            var referencePrice = ReferencePrice(intent.Instrument.Value);
            var sequence = ++_requestSequence;
            var clientOrderId = new ClientOrderId($"{BookPrefix()}-sandbox-{sequence}");
            var instruction = CreateTargetInstruction(clientOrderId, intent, delta);
            var plan = new VenueSubmitPlan(
                clientOrderId,
                VenueSubmitOutcome.Accepted,
                [new FillExecution(
                    ScaledQuantity.FromWhole(Math.Abs(delta)),
                    referencePrice,
                    ScaledMoney.Zero,
                    LiquidityFlag.Taker)]);
            if (!_venue.TryAddSubmitPlan(plan))
                return ExecutionCommandResult.Failure("Sandbox target refused because its Simulated plan id collided.");

            var response = Submit(
                instruction,
                RiskSnapshot(instruction, currentUnits, referencePrice, grossExposure));
            if (!response.IsSuccess)
            {
                return ExecutionCommandResult.Failure(
                    $"Sandbox target failed closed: {response.Reason ?? response.Fault.ToString()}.");
            }

            var actual = _adapter.CaptureReconciliationSnapshot().Positions
                .FirstOrDefault(item => item.Instrument == intent.Instrument)?.Quantity ?? ScaledQuantity.Zero;
            return actual.TryGetWholeUnits(out var actualUnits) && actualUnits == targetUnits
                ? ExecutionCommandResult.Success(
                    $"Sandbox target converged the Simulated book to {targetUnits} unit(s) through guarded OMS.")
                : ExecutionCommandResult.Failure(
                    "Sandbox target dispatch completed without exact Simulated position convergence.");
        }

        private CanonicalOrderInstruction CreateTargetInstruction(
            ClientOrderId clientOrderId,
            TradeIntent intent,
            long delta)
        {
            var identity = new OrderIdentity(
                new IntentId($"intent-{clientOrderId.Value}"),
                null,
                new LegId($"leg-{clientOrderId.Value}"),
                clientOrderId,
                null,
                null,
                new CorrelationId($"correlation-{clientOrderId.Value}"),
                new CausationId($"cause-{clientOrderId.Value}"),
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken);
            // A strategy that armed a resting entry gets a real resting order, not a market order:
            // the intent's entry condition is what the venue must see.
            var terms = new CanonicalOrderTerms(
                delta > 0 ? OrderSide.Buy : OrderSide.Sell,
                CanonicalOrderInstruction.EntryOrderTypeOf(intent),
                CanonicalTimeInForce.Day,
                ScaledQuantity.FromWhole(Math.Abs(delta)),
                intent.EntryLimitPrice,
                intent.EntryStopPrice);
            return new CanonicalOrderInstruction(identity, intent, terms);
        }

        private CanonicalOrderInstruction CreateManualInstruction(ExecutionManualOrderRequest request)
        {
            if (!request.Quantity.TryGetWholeUnits(out var quantity) || quantity <= 0)
                throw new ArgumentException("A positive whole quantity is required.", nameof(request));
            var sequence = ++_requestSequence;
            var clientOrderId = new ClientOrderId($"{BookPrefix()}-manual-{sequence}");
            var signedUnits = request.Side == ExecutionManualOrderSide.Buy ? quantity : -quantity;
            var intent = new TradeIntent(
                request.Instrument,
                TradeIntentQuantityMode.Delta,
                ScaledQuantity.FromWhole(signedUnits),
                null,
                null,
                ScaledMoney.Zero,
                $"execution-console.{BookPrefix()}.manual",
                sequence,
                _risk.CurrentPolicy.PolicyVersion);
            var identity = new OrderIdentity(
                new IntentId($"intent-{clientOrderId.Value}"),
                null,
                new LegId($"leg-{clientOrderId.Value}"),
                clientOrderId,
                null,
                null,
                new CorrelationId($"correlation-{clientOrderId.Value}"),
                new CausationId($"cause-{clientOrderId.Value}"),
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken);
            var terms = new CanonicalOrderTerms(
                request.Side == ExecutionManualOrderSide.Buy ? OrderSide.Buy : OrderSide.Sell,
                request.CanonicalOrderType,
                CanonicalTimeInForce.Day,
                request.Quantity,
                request.LimitPrice,
                request.StopPrice);
            return new CanonicalOrderInstruction(identity, intent, terms);
        }

        private void SubmitSeed(
            CanonicalOrderInstruction instruction,
            long currentUnits,
            long grossExposure)
        {
            var response = Submit(
                instruction,
                RiskSnapshot(instruction, currentUnits, ReferencePrice(instruction.TradeIntent.Instrument.Value), grossExposure));
            if (!response.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Seed order '{instruction.Identity.ClientOrderId}' failed: {response.Reason ?? response.Fault.ToString()}.");
            }
            _clock.Advance(TimeSpan.FromSeconds(24));
        }

        private ExecutionServiceResponse Submit(
            CanonicalOrderInstruction instruction,
            RiskInputSnapshot riskInput)
        {
            var exchange = _engine.Handle(new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                NextRequestId("submit"),
                ExecutionServiceRequestKind.Submit,
                _adapter.Account,
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken,
                Submit: new ExecutionSubmitRequest(instruction, riskInput)));
            return exchange.Response;
        }

        private CanonicalOrderInstruction CreateInstruction(
            string clientOrderId,
            int instrumentId,
            long currentUnits,
            long targetUnits,
            CanonicalOrderType orderType,
            ScaledPrice referencePrice)
        {
            var delta = checked(targetUnits - currentUnits);
            if (delta == 0)
                throw new ArgumentException("A sample instruction requires a non-zero delta.", nameof(targetUnits));

            var clientId = new ClientOrderId(clientOrderId);
            var intent = new TradeIntent(
                new InstrumentId(instrumentId),
                TradeIntentQuantityMode.TargetPosition,
                ScaledQuantity.FromWhole(targetUnits),
                null,
                null,
                ScaledMoney.Zero,
                $"execution-console.{BookPrefix()}",
                ++_requestSequence,
                _risk.CurrentPolicy.PolicyVersion);
            var identity = new OrderIdentity(
                new IntentId($"intent-{clientOrderId}"),
                null,
                new LegId($"leg-{clientOrderId}"),
                clientId,
                null,
                null,
                new CorrelationId($"correlation-{clientOrderId}"),
                new CausationId($"cause-{clientOrderId}"),
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken);
            var terms = new CanonicalOrderTerms(
                delta > 0 ? OrderSide.Buy : OrderSide.Sell,
                orderType,
                CanonicalTimeInForce.Day,
                ScaledQuantity.FromWhole(Math.Abs(delta)),
                orderType is CanonicalOrderType.Limit or CanonicalOrderType.StopLimit ? referencePrice : null,
                orderType is CanonicalOrderType.Stop or CanonicalOrderType.StopLimit ? referencePrice : null);
            return new CanonicalOrderInstruction(identity, intent, terms);
        }

        private RiskInputSnapshot RiskSnapshot(
            CanonicalOrderInstruction instruction,
            long currentUnits,
            ScaledPrice referencePrice,
            long grossExposure) => RiskSnapshot(
                instruction,
                currentUnits,
                referencePrice,
                new ScaledMoney(grossExposure, 0));

        private RiskInputSnapshot RiskSnapshot(
            CanonicalOrderInstruction instruction,
            long currentUnits,
            ScaledPrice referencePrice,
            ScaledMoney grossExposure) =>
            new(
                instruction.TradeIntent,
                ScaledQuantity.FromWhole(currentUnits),
                referencePrice,
                new ScaledRatio(1, 0),
                grossExposure,
                new ScaledMoney(BookPrefix() == "alpha" ? -318 : -120, 0),
                ScaledMoney.Zero,
                DateOnly.FromDateTime(_clock.UtcNow));

        private bool TryCalculateGrossExposure(
            BrokerReconciliationSnapshot snapshot,
            out ScaledMoney grossExposure)
        {
            try
            {
                var total = snapshot.Positions.Aggregate(
                    0m,
                    (current, item) => current +
                        Math.Abs(ToDecimal(item.Quantity)) * ToDecimal(ReferencePrice(item.Instrument.Value)));
                grossExposure = Money(total);
                return grossExposure.IsValid && grossExposure.Coefficient >= 0;
            }
            catch (OverflowException)
            {
                grossExposure = default;
                return false;
            }
        }

        private bool TryReserveTargetOrderSlot()
        {
            while (true)
            {
                var current = Volatile.Read(ref _targetOrderCount);
                if (current >= MaximumTargetOrders)
                    return false;
                if (Interlocked.CompareExchange(ref _targetOrderCount, current + 1, current) == current)
                    return true;
            }
        }

        private OrderCommandContext Context(string clientOrderId, string operation) =>
            new(
                new CausationId($"console:{clientOrderId}:{operation}"),
                new DeduplicationKey($"console:{clientOrderId}:{operation}"));

        private string NextRequestId(string operation) =>
            $"console:{BookPrefix()}:{operation}:{++_requestSequence}";

        private string BookPrefix() => _bookId;

        private void RefreshLedgerView()
        {
            for (var batch = 0; batch < MaximumResyncBatchesPerRefresh; batch++)
            {
                var exchange = _engine.Handle(new ExecutionServiceRequest(
                    ExecutionServiceProtocol.CurrentVersion,
                    NextRequestId("resync"),
                    ExecutionServiceRequestKind.Resync,
                    _adapter.Account,
                    _lease.Grant.LeaseId,
                    _lease.Grant.FencingToken,
                    _outboxCursor));
                if (!exchange.Response.IsSuccess)
                    return;

                foreach (var item in exchange.Events)
                {
                    _ledgerView.Enqueue(ToReadModel(item.Event));
                    while (_ledgerView.Count > LedgerViewCapacity)
                        _ledgerView.Dequeue();
                    TrackOperationalEvent(item.Event);
                }
                _outboxCursor = exchange.Response.LastOutboxSequence;
                if (exchange.Events.Count < ServiceBatchCapacity)
                    break;
            }
        }

        private void TrackOperationalEvent(TradingTerminal.Execution.Oms.OrderEvent item)
        {
            _historyView.Enqueue(item);
            while (_historyView.Count > OperationalHistoryCapacity)
                _historyView.Dequeue();

            switch (item.Kind)
            {
                case OrderEventKind.DraftCreated:
                    _orderCount++;
                    break;
                case OrderEventKind.FillReceived when item.StateBefore != OrderLifecycleState.PartiallyFilled:
                    _filledOrderCount++;
                    break;
                case OrderEventKind.RiskRejected:
                case OrderEventKind.ValidationRejected:
                case OrderEventKind.VenueRejected:
                    _rejectCount++;
                    break;
                case OrderEventKind.CancelRequested:
                    _cancelCount++;
                    break;
                case OrderEventKind.OutcomeUnknown:
                    _unknownOutcomeCount++;
                    break;
                case OrderEventKind.SubmissionRecorded:
                    _pendingAcknowledgements[item.AggregateId.Value] = item.OccurredAtUtc;
                    break;
                case OrderEventKind.VenueAcknowledged:
                    if (_pendingAcknowledgements.Remove(item.AggregateId.Value, out var submittedAt) &&
                        item.OccurredAtUtc >= submittedAt)
                    {
                        _acknowledgementCount++;
                        _totalAcknowledgementLatencyMilliseconds +=
                            (item.OccurredAtUtc - submittedAt).TotalMilliseconds;
                    }
                    break;
            }

            if (item.StateAfter is OrderLifecycleState.Filled or
                OrderLifecycleState.Cancelled or
                OrderLifecycleState.Rejected or
                OrderLifecycleState.Expired or
                OrderLifecycleState.Reconciled)
            {
                _pendingAcknowledgements.Remove(item.AggregateId.Value);
            }
        }

        private ExecutionPositionReadModel BuildPosition(
            string bookName,
            BookInstrumentConfiguration instrument,
            IReadOnlyDictionary<int, decimal> realPositions)
        {
            realPositions.TryGetValue(instrument.InstrumentId, out var real);
            var delta = real - instrument.TargetQuantity;
            var isFlat = real == 0m;
            return new ExecutionPositionReadModel(
                bookName,
                instrument.Symbol,
                real > 0m ? "LONG" : real < 0m ? "SHORT" : "FLAT",
                real > 0m ? ExecutionTone.Positive : real < 0m ? ExecutionTone.Negative : ExecutionTone.Neutral,
                instrument.ConfiguredRoute,
                FormatSigned(instrument.ModelUnits, "0.0"),
                FormatSigned(instrument.TargetQuantity, "0.###"),
                FormatSigned(real, "0.###"),
                FormatSigned(delta, "0.###"),
                delta != 0m,
                isFlat ? "-" : instrument.AveragePrice,
                instrument.LastPrice,
                isFlat ? "$0.00" : instrument.UnrealizedProfitAndLoss,
                instrument.RealizedProfitAndLoss,
                isFlat ? ExecutionTone.Neutral : instrument.ProfitAndLossTone);
        }

        private ExecutionOrderReadModel BuildOrder(
            BookConfiguration configuration,
            OrderProjection projection)
        {
            var instrument = configuration.Instruments.FirstOrDefault(
                item => item.InstrumentId == projection.Instruction.TradeIntent.Instrument.Value);
            var sideIsBuy = projection.Terms.Side == OrderSide.Buy;
            var lastEvent = _oms.ReadEvents(projection.ClientOrderId).Last();
            return new ExecutionOrderReadModel(
                configuration.Name,
                projection.ClientOrderId.Value,
                instrument?.Symbol ?? projection.Instruction.TradeIntent.Instrument.ToString(),
                sideIsBuy ? "BUY" : "SELL",
                sideIsBuy ? ExecutionTone.Positive : ExecutionTone.Negative,
                FormatQuantity(ToDecimal(projection.Terms.Quantity)),
                projection.Terms.OrderType.ToString(),
                projection.State.ToString(),
                StateTone(projection.State),
                instrument?.ConfiguredRoute ?? configuration.AdapterName,
                FormatAge(DateTime.UtcNow - lastEvent.OccurredAtUtc),
                lastEvent.OccurredAtUtc);
        }

        private static (decimal Long, decimal Short) CalculateExposure(
            BookConfiguration configuration,
            IReadOnlyDictionary<int, decimal> realPositions)
        {
            var longExposure = 0m;
            var shortExposure = 0m;
            foreach (var instrument in configuration.Instruments)
            {
                if (!realPositions.TryGetValue(instrument.InstrumentId, out var quantity))
                    continue;
                var exposure = quantity * instrument.ReferencePrice;
                if (exposure > 0m)
                    longExposure += exposure;
                else
                    shortExposure += exposure;
            }
            return (longExposure, shortExposure);
        }

        private ExecutionQualityReadModel BuildExecutionQuality(int reconciliationCaseCount)
        {
            // The OMS ledger has no owner-defined arrival-price/slippage benchmark. Report n/a
            // instead of converting exact fill prices into an invented performance claim.
            return new ExecutionQualityReadModel(
                _orderCount,
                _filledOrderCount,
                _rejectCount,
                _cancelCount,
                reconciliationCaseCount,
                _unknownOutcomeCount,
                SlippageObservationCount: 0,
                TotalSlippageTicks: 0d,
                _acknowledgementCount,
                _totalAcknowledgementLatencyMilliseconds);
        }

        private IReadOnlyList<ExecutionHistoryReadModel> BuildHistory(
            BookConfiguration configuration,
            IReadOnlyList<TradingTerminal.Execution.Oms.OrderEvent> events)
        {
            var projections = _oms.ReadAllProjections()
                .ToDictionary(item => item.ClientOrderId);
            var ledgerRows = events.Select(item =>
            {
                projections.TryGetValue(item.AggregateId, out var projection);
                var instrumentId = projection?.Instruction.TradeIntent.Instrument.Value;
                var instrument = configuration.Instruments.FirstOrDefault(candidate =>
                    candidate.InstrumentId == instrumentId);
                var fill = item.Fill;
                return new ExecutionHistoryReadModel(
                    item.OccurredAtUtc,
                    configuration.Name,
                    instrument?.Symbol ?? (instrumentId?.ToString(CultureInfo.InvariantCulture) ?? "-"),
                    SplitWords(item.Kind.ToString()),
                    string.IsNullOrWhiteSpace(item.Reason) ? item.Source.ToString() : item.Reason,
                    fill is { } exactFill ? FormatQuantity(ToDecimal(exactFill.Quantity)) : "-",
                    fill is { } pricedFill ? FormatQuantity(ToDecimal(pricedFill.Price)) : "-",
                    "-",
                    LedgerTone(item.Kind));
            });
            return Array.AsReadOnly(ledgerRows
                .OrderByDescending(item => item.OccurredAtUtc)
                .ToArray());
        }

        private static ExecutionTone LedgerTone(OrderEventKind kind) => kind switch
        {
            OrderEventKind.FillReceived or OrderEventKind.VenueAcknowledged => ExecutionTone.Positive,
            OrderEventKind.RiskRejected or OrderEventKind.ValidationRejected or OrderEventKind.VenueRejected => ExecutionTone.Negative,
            OrderEventKind.ReconciliationStarted or OrderEventKind.OutcomeUnknown => ExecutionTone.Warning,
            _ => ExecutionTone.Neutral,
        };

        private static IReadOnlyList<ExecutionReconciliationReadModel> BuildReconciliationCases(
            BookConfiguration configuration,
            IReadOnlyList<ReconciliationCase> materialCases)
        {
            var items = materialCases
                .OrderBy(item => item.Status == ReconciliationCaseStatus.Resolved)
                .ThenByDescending(item => item.OpenedAtUtc)
                .Take(4)
                .Select(item =>
                {
                    var subject = item.SubjectKind == ReconciliationSubjectKind.Order
                        ? $"order {item.ClientOrderId?.Value}"
                        : ResolveSubject(configuration, item.SubjectKey);
                    return new ExecutionReconciliationReadModel(
                        subject,
                        CaseDetail(item),
                        SplitWords(item.Kind.ToString()),
                        CaseTone(item.Kind),
                        item.Status.ToString());
                })
                .ToArray();
            return Array.AsReadOnly(items);
        }

        private IReadOnlyList<ReconciliationCase> LatestMaterialCases() =>
            _caseStore.Read(_adapter.Account)
                .GroupBy(item => item.CaseId)
                .Select(group => group.Last())
                .Where(item => item.IsMaterial)
                .ToArray();

        private ExecutionRiskReadModel BuildRisk(BookConfiguration configuration)
        {
            var decisions = _risk.Decisions;
            var limits = _risk.CurrentPolicy.Limits;
            var maxOrder = decisions.Count == 0 ? 0m : decisions.Max(item => Math.Abs(ToDecimal(item.OrderNotional)));
            var maxExposure = decisions.Count == 0 ? 0m : decisions.Max(item => Math.Abs(ToDecimal(item.ExposureAfter.GrossExposure)));
            var dailyLoss = decisions.Count == 0
                ? 0m
                : decisions.Max(item => Math.Abs(
                    ToDecimal(item.Input.DailyRealizedPnl) + ToDecimal(item.Input.DailyMarkToMarketPnl)));
            var orderLimit = ToDecimal(limits.MaximumOrderNotional);
            var exposureLimit = ToDecimal(limits.MaximumGrossExposure);
            var lossLimit = ToDecimal(limits.DailyLossLimit);
            var usage = new[]
            {
                RiskUsage("Per-order notional", maxOrder, orderLimit),
                RiskUsage("Book exposure", maxExposure, exposureLimit),
                RiskUsage("Daily loss limit", dailyLoss, lossLimit),
            };
            return new ExecutionRiskReadModel(
                Array.AsReadOnly(usage),
                configuration.EscalationLine);
        }

        private static ExecutionRiskUsageReadModel RiskUsage(string label, decimal used, decimal limit)
        {
            var percentage = limit <= 0m ? 100d : Math.Clamp((double)(used / limit * 100m), 0d, 100d);
            var tone = percentage >= 90d
                ? ExecutionTone.Negative
                : percentage >= 65d
                    ? ExecutionTone.Warning
                    : ExecutionTone.Positive;
            return new ExecutionRiskUsageReadModel(
                label,
                $"{FormatCompactMoney(used)} / {FormatCompactMoney(limit)}",
                percentage,
                tone);
        }

        private DateTime LastEventTime(ClientOrderId clientOrderId) =>
            _oms.ReadEvents(clientOrderId).Last().OccurredAtUtc;

        private static bool IsCancellable(OrderProjection projection) =>
            projection.State is OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled;

        private static ExecutionTone StateTone(OrderLifecycleState state) => state switch
        {
            OrderLifecycleState.Filled or OrderLifecycleState.Working or OrderLifecycleState.Reconciled => ExecutionTone.Positive,
            OrderLifecycleState.Rejected or OrderLifecycleState.Expired or OrderLifecycleState.Unknown => ExecutionTone.Negative,
            OrderLifecycleState.Armed or OrderLifecycleState.Reconciling or OrderLifecycleState.PendingCancel or OrderLifecycleState.PendingReplace => ExecutionTone.Warning,
            OrderLifecycleState.Releasing or OrderLifecycleState.Acknowledging => ExecutionTone.Info,
            _ => ExecutionTone.Neutral,
        };

        private static ExecutionTone CaseTone(ReconciliationCaseKind kind) => kind switch
        {
            ReconciliationCaseKind.QuantityMismatch or ReconciliationCaseKind.PriceMismatch => ExecutionTone.Warning,
            ReconciliationCaseKind.BrokerMissing or ReconciliationCaseKind.LocallyMissing or ReconciliationCaseKind.ManualException => ExecutionTone.Negative,
            ReconciliationCaseKind.DuplicateCandidate => ExecutionTone.Accent,
            _ => ExecutionTone.Neutral,
        };

        private static string CaseDetail(ReconciliationCase item) => item.Kind switch
        {
            ReconciliationCaseKind.QuantityMismatch => "local ledger and Simulated-adapter quantities differ",
            ReconciliationCaseKind.BrokerMissing => "local order is absent from the Simulated-adapter snapshot",
            ReconciliationCaseKind.LocallyMissing => "Simulated-adapter subject is absent from the local ledger",
            ReconciliationCaseKind.PriceMismatch => "local and Simulated-adapter exact prices differ",
            _ => "operator evidence review required",
        };

        private static string ResolveSubject(BookConfiguration configuration, string subjectKey)
        {
            var instrument = configuration.Instruments.FirstOrDefault(item =>
                subjectKey.Contains(item.InstrumentId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            return instrument is null ? subjectKey : $"{instrument.Symbol} · {instrument.ConfiguredRoute}";
        }

        private static ExecutionLedgerEventReadModel ToReadModel(TradingTerminal.Execution.Oms.OrderEvent item)
        {
            var message = item.Kind switch
            {
                OrderEventKind.DraftCreated => $"DRAFT {item.AggregateId}",
                OrderEventKind.RiskAccepted => $"VALIDATE {item.AggregateId}",
                OrderEventKind.RiskRejected or OrderEventKind.ValidationRejected => $"REJECT {item.AggregateId} {item.Reason}",
                OrderEventKind.Prepared => $"PREPARE {item.AggregateId}",
                OrderEventKind.Armed => $"ARM {item.AggregateId}",
                OrderEventKind.SendStarted => $"RELEASE {item.AggregateId}",
                OrderEventKind.SubmissionRecorded => $"DISPATCH {item.AggregateId} Simulated",
                OrderEventKind.VenueAcknowledged => $"ACK {item.AggregateId} Simulated",
                OrderEventKind.FillReceived when item.Fill is { } fill =>
                    $"FILL {item.AggregateId} {FormatQuantity(ToDecimal(fill.Quantity))} @{FormatQuantity(ToDecimal(fill.Price))}",
                OrderEventKind.VenueRejected => $"REJECT {item.AggregateId} {item.Reason}",
                OrderEventKind.CancelRequested => $"CANCEL {item.AggregateId} requested",
                OrderEventKind.CancelConfirmed => $"CANCEL {item.AggregateId} confirmed",
                _ => $"{SplitWords(item.Kind.ToString()).ToUpperInvariant()} {item.AggregateId}",
            };
            var tone = item.Kind switch
            {
                OrderEventKind.FillReceived or OrderEventKind.VenueAcknowledged => ExecutionTone.Positive,
                OrderEventKind.RiskRejected or OrderEventKind.ValidationRejected or OrderEventKind.VenueRejected => ExecutionTone.Negative,
                OrderEventKind.ReconciliationStarted or OrderEventKind.OutcomeUnknown => ExecutionTone.Warning,
                _ => ExecutionTone.Neutral,
            };
            var hash = item.EventHash.Length > 7 ? $"{item.EventHash[..7]}…" : item.EventHash;
            return new ExecutionLedgerEventReadModel(
                item.OccurredAtUtc,
                item.OccurredAtUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                message.Trim(),
                hash,
                tone);
        }

        private static RiskEngine CreateRiskEngine(string bookId)
        {
            var limits = new RiskLimits(
                ScaledQuantity.FromWhole(100_000),
                new ScaledMoney(250_000, 0),
                ScaledQuantity.FromWhole(100_000),
                new ScaledMoney(500_000, 0),
                new ScaledMoney(5_000, 0));
            var fault = RiskPolicy.TryCreate(
                $"execution-console-{bookId}",
                "1",
                limits,
                out var policy);
            if (fault != RiskPolicyFault.None || policy is null)
                throw new InvalidOperationException($"The console simulation risk policy is invalid: {fault}.");
            return new RiskEngine(policy);
        }

        private static VenueSubmitPlan FilledPlan(
            string clientOrderId,
            long quantity,
            ScaledPrice price,
            ScaledMoney fee) =>
            new(
                new ClientOrderId(clientOrderId),
                VenueSubmitOutcome.Accepted,
                [new FillExecution(ScaledQuantity.FromWhole(quantity), price, fee, LiquidityFlag.Taker)]);

        private ScaledPrice ReferencePrice(int instrumentId) => instrumentId switch
        {
            1001 => Price(61_842.5m),
            1002 => Price(1.08214m),
            1003 => Price(3_004.8m),
            2001 => Price(18_420m),
            2002 => Price(2_410m),
            _ => Price(100m),
        };

        private static ScaledPrice Price(decimal value)
        {
            var bits = decimal.GetBits(value);
            var scale = (byte)((bits[3] >> 16) & 0x7F);
            var coefficient = value * DecimalPower(scale);
            return new ScaledPrice(decimal.ToInt64(coefficient), scale);
        }

        private static ScaledMoney Money(decimal value)
        {
            var bits = decimal.GetBits(value);
            var scale = (byte)((bits[3] >> 16) & 0x7F);
            var coefficient = value * DecimalPower(scale);
            return new ScaledMoney(decimal.ToInt64(coefficient), scale);
        }

        private static decimal DecimalPower(byte scale)
        {
            var value = 1m;
            for (var index = 0; index < scale; index++)
                value *= 10m;
            return value;
        }

        private static decimal ToDecimal(ScaledQuantity value) => ToDecimal(value.Coefficient, value.Scale);

        private static decimal ToDecimal(ScaledPrice value) => ToDecimal(value.Coefficient, value.Scale);

        private static decimal ToDecimal(ScaledMoney value) => ToDecimal(value.Coefficient, value.Scale);

        private static decimal ToDecimal(long coefficient, byte scale) => coefficient / DecimalPower(scale);

        private static string FormatQuantity(decimal value) =>
            value.ToString(value == decimal.Truncate(value) ? "N0" : "N3", CultureInfo.InvariantCulture);

        private static string FormatSigned(decimal value, string format) =>
            value == 0m
                ? 0m.ToString(format, CultureInfo.InvariantCulture)
                : $"{(value > 0m ? "+" : "−")}{Math.Abs(value).ToString(format, CultureInfo.InvariantCulture)}";

        private static string FormatCompactMoney(decimal value)
        {
            var absolute = Math.Abs(value);
            return absolute >= 1_000m
                ? $"${absolute / 1_000m:0.#}k"
                : $"${absolute:0}";
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            if (age.TotalSeconds < 60)
                return $"{Math.Floor(age.TotalSeconds):0}s";
            if (age.TotalMinutes < 60)
                return $"{Math.Floor(age.TotalMinutes):0}m";
            return $"{Math.Floor(age.TotalHours):0}h";
        }

        private static string SplitWords(string value) =>
            string.Concat(value.Select((character, index) =>
                index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _coordinator.Dispose();
            _lease.Dispose();
            _ledgerView.Clear();
            _historyView.Clear();
            _pendingAcknowledgements.Clear();
        }

        private enum PredispatchStage
        {
            Draft,
            Validated,
            Armed,
        }
    }

    /// <summary>
    /// UI-owned control plane for one explicitly connected, authorization-gated Alpaca account. The registered
    /// adapter remains transport owner; this runtime owns only the bounded OMS/ledger/lease graph.
    /// </summary>
    private sealed class AlpacaBookRuntime : IBookRuntime
    {
        private const int LedgerViewCapacity = 96;
        private const int OperationalTableCapacity = 500;
        private const int MaximumManualOrders = 500;
        private static readonly TimeSpan MaximumReferencePriceAge = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan KillCancellationTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan KillCancellationPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly string _bookId;
        private readonly AlpacaExecutionAdapter _adapter;
        private readonly MutableExecutionClock _clock;
        private readonly InMemoryOrderEventStore _ledger;
        private readonly InMemoryReconciliationCaseStore _caseStore;
        private readonly RiskEngine _risk;
        private readonly ExecutionLease _lease;
        private readonly OrderManagementService _oms;
        private readonly ReconciliationEngine _reconciliation;
        private readonly ExecutionCoordinator _coordinator;
        private readonly ExecutionServiceEngine _engine;
        private readonly string _clientOrderNamespace =
            Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        private long _requestSequence;
        private int _submittedOrderCount;
        private bool _disposed;

        private string EnvironmentLabel => _adapter.Mode.ToString().ToUpperInvariant();

        private string RouteLabel => $"Alpaca {EnvironmentLabel}";

        private string OrderLabel => _adapter.Mode == ExecutionMode.Live ? "LIVE order" : "Paper order";

        internal AlpacaBookRuntime(
            string bookId,
            AlpacaExecutionAdapter adapter,
            IExecutionLeaseStore executionLeaseStore)
        {
            _bookId = bookId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            if (!_adapter.Session.CanExecute)
                throw new InvalidOperationException($"The {RouteLabel} adapter is not execution-authenticated.");

            var openingSnapshot = _adapter.CaptureReconciliationSnapshot();
            var initialTime = openingSnapshot.CapturedAtUtc > DateTime.UnixEpoch
                ? openingSnapshot.CapturedAtUtc
                : _adapter.Session.ObservedAtUtc;
            _clock = new MutableExecutionClock(initialTime);
            _ledger = new InMemoryOrderEventStore();
            _caseStore = new InMemoryReconciliationCaseStore();
            _risk = CreateRiskEngine($"{bookId}-alpaca-{EnvironmentLabel.ToLowerInvariant()}");
            var omsVenue = new DeterministicSimulatedVenue(
                _clock,
                _adapter.Capabilities.CanonicalCapabilities,
                Array.Empty<VenueSubmitPlan>());
            var scheduler = new ControllableAdapterEventScheduler();

            var acquired = ExecutionLease.Acquire(
                _adapter.Account,
                executionLeaseStore,
                _clock,
                new ExecutionLeaseId($"console-{bookId}-alpaca-{Guid.NewGuid():N}"));
            if (!acquired.IsSuccess || acquired.Lease is null)
            {
                throw new InvalidOperationException(
                    $"The {RouteLabel} execution lease for '{bookId}' could not be acquired: {acquired.Reason}");
            }
            _lease = acquired.Lease;
            _oms = new OrderManagementService(_ledger, _risk, omsVenue, _clock);
            if (openingSnapshot.Cash.Count != 1 ||
                openingSnapshot.Cash[0] is not { } openingCash ||
                string.IsNullOrWhiteSpace(openingCash.Currency) ||
                !openingCash.Total.IsValid ||
                !openingCash.Available.IsValid ||
                openingCash.ObservedAtUtc.Kind != DateTimeKind.Utc)
            {
                _lease.Dispose();
                throw new InvalidOperationException(
                    $"The {RouteLabel} account did not provide one valid exact opening-cash snapshot.");
            }
            _reconciliation = new ReconciliationEngine(
                _oms,
                _caseStore,
                _clock,
                new ReconciliationCashBasis(
                    openingCash.Currency,
                    openingCash.Total,
                    openingCash.Available,
                    CompareAvailable: false));

            ExecutionCoordinator? coordinator = null;
            _adapter.EventReceived += OnAdapterClockEvent;
            try
            {
                coordinator = new ExecutionCoordinator(
                    _oms,
                    [_adapter],
                    _reconciliation,
                    [_lease]);
                _engine = new ExecutionServiceEngine(_ledger, _oms, coordinator, scheduler, _lease);
                _coordinator = coordinator;
            }
            catch
            {
                _adapter.EventReceived -= OnAdapterClockEvent;
                coordinator?.Dispose();
                _lease.Dispose();
                throw;
            }
        }

        public BrokerExecutionAccount Account => _adapter.Account;

        public IBrokerExecutionAdapter Adapter => _adapter;

        internal async ValueTask<ExecutionCommandResult> InitializeAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _adapter.RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
            var result = RunReconciliation(ReconciliationTrigger.OperatorRequest);
            if (!result.IsSuccess)
                return result;

            var open = LatestMaterialCases().Count(item => item.Status != ReconciliationCaseStatus.Resolved);
            return ExecutionCommandResult.Success(
                open == 0
                    ? $"Attached the {RouteLabel} account and completed exact startup reconciliation."
                    : $"Attached the {RouteLabel} account with {open} material reconciliation case(s); order admission remains blocked.");
        }

        public ExecutionBookReadModel BuildReadModel(BookConfiguration configuration, bool isPaused)
        {
            ThrowIfDisposed();
            var observedAtUtc = DateTime.UtcNow;
            var snapshot = _adapter.CaptureReconciliationSnapshot();
            var position = snapshot.Positions.FirstOrDefault(item => item.Instrument == _adapter.Instrument)?.Quantity ??
                           ScaledQuantity.Zero;
            var quantity = ToDecimal(position);
            var reference = _adapter.LatestReferencePrice is { Coefficient: > 0 } price
                ? ToDecimal(price)
                : 0m;
            var exposure = _adapter.LatestReferencePrice is { IsValid: true, Coefficient: > 0 } exposurePrice &&
                           TryCalculateExposure(position, exposurePrice, out var exactExposure)
                ? (quantity < 0m ? -1m : 1m) * ToDecimal(exactExposure)
                : 0m;
            var positions = Array.AsReadOnly(
            [
                new ExecutionPositionReadModel(
                    configuration.Name,
                    _adapter.Symbol,
                    quantity > 0m ? "LONG" : quantity < 0m ? "SHORT" : "FLAT",
                    quantity > 0m ? ExecutionTone.Positive : quantity < 0m ? ExecutionTone.Negative : ExecutionTone.Neutral,
                    RouteLabel,
                    "-",
                    "-",
                    FormatSigned(quantity, "0.###"),
                    "-",
                    HasDivergence: false,
                    "-",
                    reference > 0m ? FormatQuantity(reference) : "-",
                    "$0.00",
                    "$0.00",
                    ExecutionTone.Neutral),
            ]);

            var projections = _oms.ReadAllProjections()
                .OrderByDescending(projection => LastEventTime(projection.ClientOrderId))
                .Take(OperationalTableCapacity)
                .ToArray();
            var orders = Array.AsReadOnly(projections.Select(projection =>
            {
                var lastEvent = _oms.ReadEvents(projection.ClientOrderId).Last();
                var buy = projection.Terms.Side == OrderSide.Buy;
                return new ExecutionOrderReadModel(
                    configuration.Name,
                    projection.ClientOrderId.Value,
                    _adapter.Symbol,
                    buy ? "BUY" : "SELL",
                    buy ? ExecutionTone.Positive : ExecutionTone.Negative,
                    FormatQuantity(ToDecimal(projection.Terms.Quantity)),
                    projection.Terms.OrderType.ToString(),
                    projection.State.ToString(),
                    StateTone(projection.State),
                    RouteLabel,
                    FormatAge(observedAtUtc - lastEvent.OccurredAtUtc),
                    lastEvent.OccurredAtUtc);
            }).ToArray());

            var outbox = _ledger.ReadOutbox();
            var history = BuildHistory(configuration, outbox);
            var cases = BuildReconciliationCases(LatestMaterialCases());
            var quality = BuildExecutionQuality(projections, outbox, cases.Count);
            var openingEquity = snapshot.Cash.Count == 1
                ? ToDecimal(snapshot.Cash[0].Total)
                : configuration.OpeningEquity;
            var analytics = ExecutionAnalyticsProjector.BuildBook(
                configuration.Id,
                configuration.Name,
                openingEquity,
                Array.Empty<ExecutionTradeHistoryPoint>(),
                quantity == 0m ? 0 : 1,
                exposure > 0m ? exposure : 0m,
                exposure < 0m ? exposure : 0m,
                quality,
                observedAtUtc);
            var period = analytics.Period(ExecutionTimeRange.ThirtyDays);
            var admissionOpen = !isPaused &&
                                _adapter.Session.CanExecute &&
                                _lease.CanAdmitNewOrders &&
                                _reconciliation.CanAdmitNewOrders(_adapter.Account) &&
                                Volatile.Read(ref _submittedOrderCount) < MaximumManualOrders;

            return new ExecutionBookReadModel(
                configuration.Id,
                configuration.Name,
                configuration.AdapterId,
                configuration.AdapterName,
                configuration.Strategies,
                period.Metrics.NetProfitAndLossDisplay,
                period.Metrics.ProfitAndLossTone,
                new ExecutionLeaseReadModel(
                    ExecutionLeaseStatus.Held,
                    _lease.Grant.FencingToken.Value,
                    $"local {RouteLabel} writer"),
                isPaused,
                admissionOpen,
                quantity == 0m ? 0 : 1,
                positions,
                orders,
                history,
                cases,
                BuildRisk(),
                BuildLedger(outbox),
                analytics,
                _adapter.Mode)
            {
                TradableInstruments = Array.AsReadOnly(
                [
                    new ExecutionTradableInstrumentReadModel(_adapter.Instrument, _adapter.Symbol),
                ]),
                SupportsKill = true,
            };
        }

        public async ValueTask<ExecutionCommandResult> ReconcileAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _adapter.RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
            return RunReconciliation(ReconciliationTrigger.OperatorRequest);
        }

        public async ValueTask<ExecutionCommandResult> KillAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_adapter.Session.CanExecute)
                return ExecutionCommandResult.Failure($"Kill refused because {RouteLabel} is disconnected or uncertified.");

            await _adapter.RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
            var reconciled = RunReconciliation(ReconciliationTrigger.OperatorRequest);
            if (!reconciled.IsSuccess ||
                !_lease.CanAdmitNewOrders ||
                !_reconciliation.CanAdmitNewOrders(_adapter.Account))
            {
                return ExecutionCommandResult.Failure(
                    "Kill refused by the execution lease or exact reconciliation admission gate.");
            }

            var cancellationTargets = _oms.ReadAllProjections()
                .Where(IsKillOrderOutstanding)
                .Select(projection => projection.ClientOrderId)
                .ToArray();
            var alreadyPendingCancellation = _oms.ReadAllProjections()
                .Where(projection => projection.State == OrderLifecycleState.PendingCancel)
                .Select(projection => projection.ClientOrderId)
                .ToHashSet();
            foreach (var cancelOrderId in cancellationTargets.Where(id => !alreadyPendingCancellation.Contains(id)))
            {
                var cancel = _engine.Handle(new ExecutionServiceRequest(
                    ExecutionServiceProtocol.CurrentVersion,
                    NextRequestId("kill-cancel"),
                    ExecutionServiceRequestKind.Cancel,
                    _adapter.Account,
                    _lease.Grant.LeaseId,
                    _lease.Grant.FencingToken,
                    Cancel: new ExecutionCancelRequest(cancelOrderId)));
                if (!cancel.Response.IsSuccess)
                {
                    return ExecutionCommandResult.Failure(
                        $"Kill stopped safely while cancelling {cancelOrderId}: " +
                        $"{cancel.Response.Reason ?? cancel.Response.Fault.ToString()}.");
                }
            }

            if (cancellationTargets.Length > 0)
            {
                var cancellationsSettled = await WaitForKillCancellationsAsync(
                        cancellationTargets,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!cancellationsSettled.IsSuccess)
                    return cancellationsSettled;
            }

            var snapshot = _adapter.CaptureReconciliationSnapshot();
            _clock.SetTo(snapshot.CapturedAtUtc);
            var position = snapshot.Positions
                .FirstOrDefault(item => item.Instrument == _adapter.Instrument)?.Quantity ?? ScaledQuantity.Zero;
            if (position.Coefficient == 0)
            {
                return ExecutionCommandResult.Success(
                    $"Kill verified {cancellationTargets.Length} working-order cancellation(s); {RouteLabel} reports the configured position flat and intake remains stopped.");
            }
            if (!position.TryGetWholeUnits(out var currentUnits))
                return ExecutionCommandResult.Failure("Kill refused because the current broker position is not an exact whole quantity.");

            await _adapter.RefreshReferencePriceAsync(cancellationToken).ConfigureAwait(false);
            if (_adapter.LatestReferencePrice is not { IsValid: true, Coefficient: > 0 } referencePrice ||
                _adapter.LatestReferencePriceObservedAtUtc is not { Kind: DateTimeKind.Utc } observed ||
                _adapter.LatestReferencePriceFetchedAtUtc is not { Kind: DateTimeKind.Utc } fetched ||
                observed > fetched ||
                fetched - observed > MaximumReferencePriceAge)
            {
                return ExecutionCommandResult.Failure(
                    "Kill stopped safely after cancellations because no exact reference trade newer than 15 seconds is available for flattening.");
            }
            if (!TryCalculateGrossExposure(currentUnits, referencePrice, out var grossExposure))
                return ExecutionCommandResult.Failure("Kill refused because exact broker exposure cannot be represented safely.");
            if (!TrySelectTestTimeInForce(out var timeInForce))
                return ExecutionCommandResult.Failure("Kill refused because neither DAY nor GTC time in force is supported.");
            if (!TryReserveOrderSlot())
                return ExecutionCommandResult.Failure("Kill refused because the bounded order capacity is exhausted.");

            var sequence = Interlocked.Increment(ref _requestSequence);
            var clientOrderId = new ClientOrderId($"daxk-{_clientOrderNamespace}-{sequence}");
            var side = currentUnits > 0 ? OrderSide.Sell : OrderSide.Buy;
            var flattenQuantity = ScaledQuantity.FromWhole(
                currentUnits == long.MinValue ? long.MaxValue : Math.Abs(currentUnits));
            if (currentUnits == long.MinValue)
                return ExecutionCommandResult.Failure("Kill refused because the broker position magnitude is not representable.");
            var intent = new TradeIntent(
                _adapter.Instrument,
                TradeIntentQuantityMode.Delta,
                ScaledQuantity.FromWhole(-currentUnits),
                null,
                null,
                ScaledMoney.Zero,
                $"execution-console.{_bookId}.alpaca-{EnvironmentLabel.ToLowerInvariant()}-kill",
                sequence,
                _risk.CurrentPolicy.PolicyVersion);
            var instruction = new CanonicalOrderInstruction(
                new OrderIdentity(
                    new IntentId($"intent-{clientOrderId.Value}"),
                    null,
                    new LegId($"leg-{clientOrderId.Value}"),
                    clientOrderId,
                    null,
                    null,
                    new CorrelationId($"correlation-{clientOrderId.Value}"),
                    new CausationId($"cause-{clientOrderId.Value}"),
                    _lease.Grant.LeaseId,
                    _lease.Grant.FencingToken),
                intent,
                new CanonicalOrderTerms(
                    side,
                    CanonicalOrderType.Market,
                    timeInForce,
                    flattenQuantity,
                    null,
                    null));
            var riskInput = new RiskInputSnapshot(
                intent,
                position,
                referencePrice,
                new ScaledRatio(1, 0),
                grossExposure,
                ScaledMoney.Zero,
                ScaledMoney.Zero,
                DateOnly.FromDateTime(_clock.UtcNow));
            var exchange = _engine.Handle(new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                NextRequestId("kill-flatten"),
                ExecutionServiceRequestKind.Submit,
                _adapter.Account,
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken,
                Submit: new ExecutionSubmitRequest(instruction, riskInput)));
            return exchange.Response.IsSuccess
                ? ExecutionCommandResult.Success(
                    $"Kill verified {cancellationTargets.Length} cancellation(s) and dispatched flatten order {clientOrderId} through guarded OMS to {RouteLabel}; intake remains stopped.")
                : ExecutionCommandResult.Failure(
                    $"Kill flatten failed closed after {cancellationTargets.Length} cancellation(s): {exchange.Response.Reason ?? exchange.Response.Fault.ToString()}.");
        }

        private async ValueTask<ExecutionCommandResult> WaitForKillCancellationsAsync(
            IReadOnlyCollection<ClientOrderId> cancellationTargets,
            CancellationToken cancellationToken)
        {
            var targetSet = cancellationTargets.ToHashSet();
            var deadlineUtc = DateTime.UtcNow + KillCancellationTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _adapter.RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
                var reconciled = RunReconciliation(ReconciliationTrigger.OperatorRequest);
                var cancellationPending = _oms.ReadAllProjections().Any(projection =>
                    targetSet.Contains(projection.ClientOrderId) && !OrderLifecycle.IsTerminal(projection.State));
                if (!cancellationPending &&
                    reconciled.IsSuccess &&
                    _lease.CanAdmitNewOrders &&
                    _reconciliation.CanAdmitNewOrders(_adapter.Account))
                {
                    return ExecutionCommandResult.Success("Working-order cancellations were verified by exact reconciliation.");
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return ExecutionCommandResult.Failure(
                        "Kill stopped safely: working-order cancellation was not verified by exact reconciliation within 10 seconds; no flatten order was sent.");
                }
                await Task.Delay(KillCancellationPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(
            BookConfiguration configuration,
            ExecutionManualOrderRequest request,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Instrument != _adapter.Instrument ||
                !string.Equals(request.Symbol, _adapter.Symbol, StringComparison.Ordinal))
            {
                return ExecutionCommandResult.Failure(
                    $"{OrderLabel} refused because the instrument is not certified by this Alpaca adapter.");
            }
            if (!request.Quantity.TryGetWholeUnits(out var requestedUnits) || requestedUnits <= 0)
                return ExecutionCommandResult.Failure($"{OrderLabel} quantity must be a positive whole number.");
            if (!request.HasWellFormedPriceTerms ||
                request.LimitPrice is { IsValid: false } or { Coefficient: <= 0 } ||
                request.StopPrice is { IsValid: false } or { Coefficient: <= 0 })
            {
                return ExecutionCommandResult.Failure(
                    $"{OrderLabel} price terms are invalid or do not match the selected order type.");
            }
            if (Volatile.Read(ref _submittedOrderCount) >= MaximumManualOrders)
            {
                return ExecutionCommandResult.Failure(
                    $"The bounded order runtime reached its {MaximumManualOrders}-order limit.");
            }
            if (!_adapter.Session.CanExecute)
                return ExecutionCommandResult.Failure($"{RouteLabel} is disconnected or cannot execute.");

            await _adapter.RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
            var reconciled = RunReconciliation(ReconciliationTrigger.OperatorRequest);
            if (!reconciled.IsSuccess || !_reconciliation.CanAdmitNewOrders(_adapter.Account))
            {
                return ExecutionCommandResult.Failure(
                    $"{OrderLabel} refused by exact reconciliation; resolve the displayed material cases first.");
            }

            ScaledPrice referencePrice;
            if (request.OrderType == ExecutionManualOrderType.Limit)
            {
                referencePrice = request.LimitPrice!.Value;
            }
            else
            {
                await _adapter.RefreshReferencePriceAsync(cancellationToken).ConfigureAwait(false);
                if (_adapter.LatestReferencePrice is not { IsValid: true, Coefficient: > 0 } latest ||
                    _adapter.LatestReferencePriceObservedAtUtc is not { Kind: DateTimeKind.Utc } observed ||
                    _adapter.LatestReferencePriceFetchedAtUtc is not { Kind: DateTimeKind.Utc } fetched ||
                    observed > fetched ||
                    fetched - observed > MaximumReferencePriceAge)
                {
                    return ExecutionCommandResult.Failure(
                        $"Market {OrderLabel.ToLowerInvariant()} refused because no exact reference trade newer than 15 seconds is available.");
                }
                referencePrice = latest;
            }

            var snapshot = _adapter.CaptureReconciliationSnapshot();
            _clock.SetTo(snapshot.CapturedAtUtc);
            var position = snapshot.Positions.FirstOrDefault(item => item.Instrument == _adapter.Instrument)?.Quantity ??
                           ScaledQuantity.Zero;
            if (!position.TryGetWholeUnits(out var currentUnits))
                return ExecutionCommandResult.Failure("The current Alpaca position is not an exact whole quantity.");
            if (!TryCalculateGrossExposure(currentUnits, referencePrice, out var grossExposure))
                return ExecutionCommandResult.Failure("The exact Alpaca gross exposure cannot be represented safely.");
            if (!TrySelectTestTimeInForce(out var timeInForce))
            {
                return ExecutionCommandResult.Failure(
                    $"{OrderLabel} refused because the adapter supports neither DAY nor GTC time in force.");
            }
            if (!TryReserveOrderSlot())
            {
                return ExecutionCommandResult.Failure(
                    $"The bounded order runtime reached its {MaximumManualOrders}-order limit.");
            }

            var sequence = Interlocked.Increment(ref _requestSequence);
            var clientOrderId = new ClientOrderId($"daxt-{_clientOrderNamespace}-{sequence}");
            var signedUnits = request.Side == ExecutionManualOrderSide.Buy ? requestedUnits : -requestedUnits;
            var intent = new TradeIntent(
                request.Instrument,
                TradeIntentQuantityMode.Delta,
                ScaledQuantity.FromWhole(signedUnits),
                null,
                null,
                ScaledMoney.Zero,
                $"execution-console.{_bookId}.alpaca-{EnvironmentLabel.ToLowerInvariant()}-ticket",
                sequence,
                _risk.CurrentPolicy.PolicyVersion);
            var instruction = new CanonicalOrderInstruction(
                new OrderIdentity(
                    new IntentId($"intent-{clientOrderId.Value}"),
                    null,
                    new LegId($"leg-{clientOrderId.Value}"),
                    clientOrderId,
                    null,
                    null,
                    new CorrelationId($"correlation-{clientOrderId.Value}"),
                    new CausationId($"cause-{clientOrderId.Value}"),
                    _lease.Grant.LeaseId,
                    _lease.Grant.FencingToken),
                intent,
                new CanonicalOrderTerms(
                    request.Side == ExecutionManualOrderSide.Buy ? OrderSide.Buy : OrderSide.Sell,
                    request.CanonicalOrderType,
                    timeInForce,
                    request.Quantity,
                    request.LimitPrice,
                    request.StopPrice));
            var riskInput = new RiskInputSnapshot(
                intent,
                position,
                referencePrice,
                new ScaledRatio(1, 0),
                grossExposure,
                ScaledMoney.Zero,
                ScaledMoney.Zero,
                DateOnly.FromDateTime(_clock.UtcNow));

            var exchange = _engine.Handle(new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                NextRequestId("submit"),
                ExecutionServiceRequestKind.Submit,
                _adapter.Account,
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken,
                Submit: new ExecutionSubmitRequest(instruction, riskInput)));
            return exchange.Response.IsSuccess
                ? ExecutionCommandResult.Success(
                    $"{OrderLabel} {clientOrderId} was dispatched through OMS to {RouteLabel} ({exchange.Response.State}).")
                : ExecutionCommandResult.Failure(
                    $"{OrderLabel} failed closed: {exchange.Response.Reason ?? exchange.Response.Fault.ToString()}.");
        }

        public async ValueTask<ExecutionCommandResult> SubmitTargetAsync(
            BookConfiguration configuration,
            TradeIntent intent,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.QuantityMode != TradeIntentQuantityMode.TargetPosition)
                return ExecutionCommandResult.Failure("Sandbox replication accepts only TargetPosition intents.");
            if (intent.Instrument != _adapter.Instrument)
                return ExecutionCommandResult.Failure("Sandbox target instrument is not certified by this Alpaca adapter.");
            if (!intent.SignedUnits.TryGetWholeUnits(out var targetUnits))
                return ExecutionCommandResult.Failure("Sandbox target must contain an exact whole-unit position.");
            if (Volatile.Read(ref _submittedOrderCount) >= MaximumManualOrders)
            {
                return ExecutionCommandResult.Failure(
                    $"The bounded order runtime reached its {MaximumManualOrders}-order limit.");
            }
            if (!_adapter.Session.CanExecute)
                return ExecutionCommandResult.Failure($"{RouteLabel} is disconnected or cannot execute.");

            await _adapter.RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
            var reconciled = RunReconciliation(ReconciliationTrigger.OperatorRequest);
            if (!reconciled.IsSuccess || !_reconciliation.CanAdmitNewOrders(_adapter.Account))
            {
                return ExecutionCommandResult.Failure(
                    "Sandbox target refused by exact reconciliation; resolve material cases first.");
            }

            await _adapter.RefreshReferencePriceAsync(cancellationToken).ConfigureAwait(false);
            if (_adapter.LatestReferencePrice is not { IsValid: true, Coefficient: > 0 } referencePrice ||
                _adapter.LatestReferencePriceObservedAtUtc is not { Kind: DateTimeKind.Utc } observed ||
                _adapter.LatestReferencePriceFetchedAtUtc is not { Kind: DateTimeKind.Utc } fetched ||
                observed > fetched ||
                fetched - observed > MaximumReferencePriceAge)
            {
                return ExecutionCommandResult.Failure(
                    "Sandbox target refused because no exact reference trade newer than 15 seconds is available.");
            }

            var snapshot = _adapter.CaptureReconciliationSnapshot();
            _clock.SetTo(snapshot.CapturedAtUtc);
            var position = snapshot.Positions.FirstOrDefault(item => item.Instrument == _adapter.Instrument)?.Quantity ??
                           ScaledQuantity.Zero;
            if (!position.TryGetWholeUnits(out var currentUnits))
                return ExecutionCommandResult.Failure("The current Alpaca position is not an exact whole quantity.");
            if (currentUnits == targetUnits)
                return ExecutionCommandResult.Success("The Alpaca book already matches the sandbox target.");

            long delta;
            try
            {
                delta = checked(targetUnits - currentUnits);
            }
            catch (OverflowException)
            {
                return ExecutionCommandResult.Failure("Sandbox target delta cannot be represented safely.");
            }
            if (delta == long.MinValue)
                return ExecutionCommandResult.Failure("Sandbox target delta cannot be represented safely.");
            if (!TryCalculateGrossExposure(currentUnits, referencePrice, out var grossExposure))
                return ExecutionCommandResult.Failure("The exact Alpaca gross exposure cannot be represented safely.");
            if (!TrySelectTestTimeInForce(out var timeInForce))
                return ExecutionCommandResult.Failure("Sandbox target refused because no supported time in force is available.");
            if (!TryReserveOrderSlot())
            {
                return ExecutionCommandResult.Failure(
                    $"The bounded order runtime reached its {MaximumManualOrders}-order limit.");
            }

            var sequence = Interlocked.Increment(ref _requestSequence);
            var clientOrderId = new ClientOrderId($"daxr-{_clientOrderNamespace}-{sequence}");
            var instruction = new CanonicalOrderInstruction(
                new OrderIdentity(
                    new IntentId($"intent-{clientOrderId.Value}"),
                    null,
                    new LegId($"leg-{clientOrderId.Value}"),
                    clientOrderId,
                    null,
                    null,
                    new CorrelationId($"correlation-{clientOrderId.Value}"),
                    new CausationId($"cause-{clientOrderId.Value}"),
                    _lease.Grant.LeaseId,
                    _lease.Grant.FencingToken),
                intent,
                new CanonicalOrderTerms(
                    delta > 0 ? OrderSide.Buy : OrderSide.Sell,
                    CanonicalOrderInstruction.EntryOrderTypeOf(intent),
                    timeInForce,
                    ScaledQuantity.FromWhole(Math.Abs(delta)),
                    intent.EntryLimitPrice,
                    intent.EntryStopPrice));
            var riskInput = new RiskInputSnapshot(
                intent,
                position,
                referencePrice,
                new ScaledRatio(1, 0),
                grossExposure,
                ScaledMoney.Zero,
                ScaledMoney.Zero,
                DateOnly.FromDateTime(_clock.UtcNow));
            var exchange = _engine.Handle(new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                NextRequestId("sandbox-submit"),
                ExecutionServiceRequestKind.Submit,
                _adapter.Account,
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken,
                Submit: new ExecutionSubmitRequest(instruction, riskInput)));
            return exchange.Response.IsSuccess
                ? ExecutionCommandResult.Success(
                    $"Sandbox target {clientOrderId} was dispatched through guarded OMS to {RouteLabel} ({exchange.Response.State}).")
                : ExecutionCommandResult.Failure(
                    $"Sandbox target failed closed: {exchange.Response.Reason ?? exchange.Response.Fault.ToString()}.");
        }

        private ExecutionCommandResult RunReconciliation(ReconciliationTrigger trigger)
        {
            var snapshot = _adapter.CaptureReconciliationSnapshot();
            _clock.SetTo(snapshot.CapturedAtUtc);
            var exchange = _engine.Handle(new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                NextRequestId("reconcile"),
                ExecutionServiceRequestKind.Reconcile,
                _adapter.Account,
                _lease.Grant.LeaseId,
                _lease.Grant.FencingToken,
                ReconciliationTrigger: trigger));
            if (!exchange.Response.IsSuccess)
            {
                return ExecutionCommandResult.Failure(
                    $"{RouteLabel} reconciliation failed closed: {exchange.Response.Reason ?? exchange.Response.Fault.ToString()}.");
            }

            var open = LatestMaterialCases().Count(item => item.Status != ReconciliationCaseStatus.Resolved);
            return ExecutionCommandResult.Success(
                open == 0
                    ? $"{RouteLabel} reconciliation completed; exact admission is open."
                    : $"{RouteLabel} reconciliation found {open} material case(s); admission remains blocked.");
        }

        private IReadOnlyList<ReconciliationCase> LatestMaterialCases() =>
            _caseStore.Read(_adapter.Account)
                .GroupBy(item => item.CaseId)
                .Select(group => group.Last())
                .Where(item => item.IsMaterial)
                .ToArray();

        private IReadOnlyList<ExecutionReconciliationReadModel> BuildReconciliationCases(
            IReadOnlyList<ReconciliationCase> materialCases) =>
            Array.AsReadOnly(materialCases
                .OrderBy(item => item.Status == ReconciliationCaseStatus.Resolved)
                .ThenByDescending(item => item.OpenedAtUtc)
                .Take(4)
                .Select(item => new ExecutionReconciliationReadModel(
                    item.SubjectKind == ReconciliationSubjectKind.Order
                        ? $"order {item.ClientOrderId?.Value}"
                        : item.SubjectKind == ReconciliationSubjectKind.Position
                            ? _adapter.Symbol
                            : item.SubjectKey,
                    $"local OMS and {RouteLabel} evidence require exact operator review",
                    SplitWords(item.Kind.ToString()),
                    CaseTone(item.Kind),
                    item.Status.ToString()))
                .ToArray());

        private ExecutionRiskReadModel BuildRisk()
        {
            var decisions = _risk.Decisions;
            var limits = _risk.CurrentPolicy.Limits;
            var maxOrder = decisions.Count == 0 ? 0m : decisions.Max(item => Math.Abs(ToDecimal(item.OrderNotional)));
            var maxExposure = decisions.Count == 0 ? 0m : decisions.Max(item => Math.Abs(ToDecimal(item.ExposureAfter.GrossExposure)));
            return new ExecutionRiskReadModel(
                Array.AsReadOnly(
                [
                    RiskUsage("Per-order notional", maxOrder, ToDecimal(limits.MaximumOrderNotional)),
                    RiskUsage("Book exposure", maxExposure, ToDecimal(limits.MaximumGrossExposure)),
                    RiskUsage("Daily loss limit", 0m, ToDecimal(limits.DailyLossLimit)),
                ]),
                $"{RouteLabel} orders require whole quantities, exact prices, lease fencing, risk approval, and reconciliation admission.");
        }

        private static ExecutionRiskUsageReadModel RiskUsage(string label, decimal used, decimal limit)
        {
            var percentage = limit <= 0m ? 100d : Math.Clamp((double)(used / limit * 100m), 0d, 100d);
            return new ExecutionRiskUsageReadModel(
                label,
                $"{ExecutionFormatting.CompactMoney(used)} / {ExecutionFormatting.CompactMoney(limit)}",
                percentage,
                percentage >= 90d ? ExecutionTone.Negative : percentage >= 65d ? ExecutionTone.Warning : ExecutionTone.Positive);
        }

        private IReadOnlyList<ExecutionHistoryReadModel> BuildHistory(
            BookConfiguration configuration,
            IReadOnlyList<OrderEventOutboxEntry> outbox) =>
            Array.AsReadOnly(outbox
                .TakeLast(OperationalTableCapacity)
                .Reverse()
                .Select(item =>
                {
                    var orderEvent = item.Event;
                    return new ExecutionHistoryReadModel(
                        orderEvent.OccurredAtUtc,
                        configuration.Name,
                        _adapter.Symbol,
                        SplitWords(orderEvent.Kind.ToString()),
                        string.IsNullOrWhiteSpace(orderEvent.Reason) ? orderEvent.Source.ToString() : orderEvent.Reason,
                        orderEvent.Fill is { } fill ? FormatQuantity(ToDecimal(fill.Quantity)) : "-",
                        orderEvent.Fill is { } priced ? FormatQuantity(ToDecimal(priced.Price)) : "-",
                        "-",
                        LedgerTone(orderEvent.Kind));
                })
                .ToArray());

        private IReadOnlyList<ExecutionLedgerEventReadModel> BuildLedger(
            IReadOnlyList<OrderEventOutboxEntry> outbox) =>
            Array.AsReadOnly(outbox
                .TakeLast(LedgerViewCapacity)
                .Reverse()
                .Select(item => ToLedgerReadModel(item.Event))
                .ToArray());

        private static ExecutionQualityReadModel BuildExecutionQuality(
            IReadOnlyList<OrderProjection> projections,
            IReadOnlyList<OrderEventOutboxEntry> outbox,
            int reconciliationCaseCount)
        {
            var events = outbox.Select(item => item.Event).ToArray();
            return new ExecutionQualityReadModel(
                projections.Count,
                projections.Count(item => item.State == OrderLifecycleState.Filled),
                projections.Count(item => item.State == OrderLifecycleState.Rejected),
                events.Count(item => item.Kind == OrderEventKind.CancelRequested),
                reconciliationCaseCount,
                projections.Count(item => item.State == OrderLifecycleState.Unknown),
                0,
                0d,
                0,
                0d);
        }

        private ExecutionLedgerEventReadModel ToLedgerReadModel(TradingTerminal.Execution.Oms.OrderEvent item)
        {
            var message = item.Kind switch
            {
                OrderEventKind.SubmissionRecorded => $"DISPATCH {item.AggregateId} {RouteLabel}",
                OrderEventKind.VenueAcknowledged => $"ACK {item.AggregateId} {RouteLabel}",
                OrderEventKind.FillReceived when item.Fill is { } fill =>
                    $"FILL {item.AggregateId} {FormatQuantity(ToDecimal(fill.Quantity))} @{FormatQuantity(ToDecimal(fill.Price))}",
                OrderEventKind.VenueRejected => $"REJECT {item.AggregateId} {item.Reason}",
                _ => $"{SplitWords(item.Kind.ToString()).ToUpperInvariant()} {item.AggregateId}",
            };
            var hash = item.EventHash.Length > 7 ? $"{item.EventHash[..7]}…" : item.EventHash;
            return new ExecutionLedgerEventReadModel(
                item.OccurredAtUtc,
                item.OccurredAtUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                message.Trim(),
                hash,
                LedgerTone(item.Kind));
        }

        private static bool TryCalculateGrossExposure(
            long currentUnits,
            ScaledPrice referencePrice,
            out ScaledMoney result) =>
            TryCalculateExposure(ScaledQuantity.FromWhole(currentUnits), referencePrice, out result);

        private static bool TryCalculateExposure(
            ScaledQuantity quantity,
            ScaledPrice referencePrice,
            out ScaledMoney result)
        {
            if (!quantity.IsValid || !referencePrice.IsValid || referencePrice.Coefficient <= 0)
            {
                result = default;
                return false;
            }
            var absoluteUnits = quantity.Coefficient == long.MinValue
                ? (Int128)long.MaxValue + 1
                : Math.Abs(quantity.Coefficient);
            var coefficient = absoluteUnits * referencePrice.Coefficient;
            var scale = quantity.Scale + referencePrice.Scale;
            while (scale > 0 && coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }
            if (scale > 18 || coefficient < 0 || coefficient > long.MaxValue)
            {
                result = default;
                return false;
            }
            result = new ScaledMoney((long)coefficient, (byte)scale);
            return result.IsValid;
        }

        private bool TrySelectTestTimeInForce(out CanonicalTimeInForce timeInForce)
        {
            var supported = _adapter.Capabilities.CanonicalCapabilities.TimeInForce;
            if ((supported & SupportedTimeInForce.Day) != 0)
            {
                timeInForce = CanonicalTimeInForce.Day;
                return true;
            }
            if ((supported & SupportedTimeInForce.GoodTillCancelled) != 0)
            {
                timeInForce = CanonicalTimeInForce.GoodTillCancelled;
                return true;
            }
            timeInForce = default;
            return false;
        }

        private bool TryReserveOrderSlot()
        {
            while (true)
            {
                var current = Volatile.Read(ref _submittedOrderCount);
                if (current >= MaximumManualOrders)
                    return false;
                if (Interlocked.CompareExchange(ref _submittedOrderCount, current + 1, current) == current)
                    return true;
            }
        }

        private static RiskEngine CreateRiskEngine(string bookId)
        {
            var limits = new RiskLimits(
                ScaledQuantity.FromWhole(100_000),
                new ScaledMoney(250_000, 0),
                ScaledQuantity.FromWhole(100_000),
                new ScaledMoney(500_000, 0),
                new ScaledMoney(5_000, 0));
            var fault = RiskPolicy.TryCreate(
                $"execution-console-{bookId}",
                "1",
                limits,
                out var policy);
            if (fault != RiskPolicyFault.None || policy is null)
                throw new InvalidOperationException($"The Alpaca console risk policy is invalid: {fault}.");
            return new RiskEngine(policy);
        }

        private static bool IsCancellable(OrderProjection projection) => projection.State is
            OrderLifecycleState.Armed or
            OrderLifecycleState.Releasing or
            OrderLifecycleState.Acknowledging or
            OrderLifecycleState.Working or
            OrderLifecycleState.PartiallyFilled or
            OrderLifecycleState.PendingReplace;

        private static bool IsKillOrderOutstanding(OrderProjection projection) =>
            IsCancellable(projection) || projection.State == OrderLifecycleState.PendingCancel;

        private DateTime LastEventTime(ClientOrderId clientOrderId) =>
            _oms.ReadEvents(clientOrderId).Last().OccurredAtUtc;

        private string NextRequestId(string operation) =>
            $"console:{_bookId}:alpaca:{operation}:{Interlocked.Increment(ref _requestSequence)}";

        private static ExecutionTone StateTone(OrderLifecycleState state) => state switch
        {
            OrderLifecycleState.Filled or OrderLifecycleState.Working or OrderLifecycleState.Reconciled => ExecutionTone.Positive,
            OrderLifecycleState.Rejected or OrderLifecycleState.Expired or OrderLifecycleState.Unknown => ExecutionTone.Negative,
            OrderLifecycleState.Armed or OrderLifecycleState.Reconciling or OrderLifecycleState.PendingCancel or OrderLifecycleState.PendingReplace => ExecutionTone.Warning,
            OrderLifecycleState.Releasing or OrderLifecycleState.Acknowledging => ExecutionTone.Info,
            _ => ExecutionTone.Neutral,
        };

        private static ExecutionTone CaseTone(ReconciliationCaseKind kind) => kind switch
        {
            ReconciliationCaseKind.QuantityMismatch or ReconciliationCaseKind.PriceMismatch => ExecutionTone.Warning,
            ReconciliationCaseKind.BrokerMissing or ReconciliationCaseKind.LocallyMissing or ReconciliationCaseKind.ManualException => ExecutionTone.Negative,
            ReconciliationCaseKind.DuplicateCandidate => ExecutionTone.Accent,
            _ => ExecutionTone.Neutral,
        };

        private static ExecutionTone LedgerTone(OrderEventKind kind) => kind switch
        {
            OrderEventKind.FillReceived or OrderEventKind.VenueAcknowledged => ExecutionTone.Positive,
            OrderEventKind.RiskRejected or OrderEventKind.ValidationRejected or OrderEventKind.VenueRejected => ExecutionTone.Negative,
            OrderEventKind.ReconciliationStarted or OrderEventKind.OutcomeUnknown => ExecutionTone.Warning,
            _ => ExecutionTone.Neutral,
        };

        private static decimal ToDecimal(ScaledQuantity value) => ToDecimal(value.Coefficient, value.Scale);

        private static decimal ToDecimal(ScaledPrice value) => ToDecimal(value.Coefficient, value.Scale);

        private static decimal ToDecimal(ScaledMoney value) => ToDecimal(value.Coefficient, value.Scale);

        private static decimal ToDecimal(long coefficient, byte scale) => coefficient / DecimalPower(scale);

        private static decimal DecimalPower(byte scale)
        {
            var value = 1m;
            for (var index = 0; index < scale; index++)
                value *= 10m;
            return value;
        }

        private static string FormatQuantity(decimal value) =>
            value.ToString(value == decimal.Truncate(value) ? "N0" : "N3", CultureInfo.InvariantCulture);

        private static string FormatSigned(decimal value, string format) =>
            value == 0m
                ? 0m.ToString(format, CultureInfo.InvariantCulture)
                : $"{(value > 0m ? "+" : "−")}{Math.Abs(value).ToString(format, CultureInfo.InvariantCulture)}";

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            if (age.TotalSeconds < 60)
                return $"{Math.Floor(age.TotalSeconds):0}s";
            if (age.TotalMinutes < 60)
                return $"{Math.Floor(age.TotalMinutes):0}m";
            return $"{Math.Floor(age.TotalHours):0}h";
        }

        private static string SplitWords(string value) =>
            string.Concat(value.Select((character, index) =>
                index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

        private void OnAdapterClockEvent(BrokerAdapterEvent adapterEvent)
        {
            if (adapterEvent.Account == _adapter.Account && adapterEvent.OccurredAtUtc.Kind == DateTimeKind.Utc)
                _clock.AdvanceTo(adapterEvent.OccurredAtUtc);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _adapter.EventReceived -= OnAdapterClockEvent;
            _coordinator.Dispose();
            _lease.Dispose();
        }
    }

    private sealed class MutableExecutionClock : IClock
    {
        private readonly object _gate = new();
        private DateTime _utcNow;

        internal MutableExecutionClock(DateTime utcNow) => _utcNow = EnsureUtc(utcNow);

        public DateTime UtcNow
        {
            get
            {
                lock (_gate)
                    return _utcNow;
            }
        }

        internal void SetTo(DateTime value) => AdvanceTo(value);

        internal void Advance(TimeSpan value)
        {
            lock (_gate)
                _utcNow = _utcNow.Add(value);
        }

        internal void AdvanceTo(DateTime value)
        {
            var utc = EnsureUtc(value);
            lock (_gate)
            {
                if (utc > _utcNow)
                    _utcNow = utc;
            }
        }

        private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
    }

    private sealed record BookConfiguration(
        string Id,
        string Name,
        string AdapterId,
        string AdapterName,
        IReadOnlyList<string> Strategies,
        decimal OpeningEquity,
        IReadOnlyList<ExecutionTradeHistoryPoint> AnalyticsHistory,
        IReadOnlyList<BookInstrumentConfiguration> Instruments,
        string EscalationLine,
        string UnavailableDetail)
    {
        internal static BookConfiguration New(
            string id,
            string name,
            string adapterId,
            string adapterName,
            IReadOnlyList<string> strategies,
            InstrumentId instrument,
            string symbol) => new(
                id,
                name,
                adapterId,
                adapterName,
                Array.AsReadOnly(strategies.ToArray()),
                25_000m,
                Array.Empty<ExecutionTradeHistoryPoint>(),
                instrument.IsNone
                    ? Array.Empty<BookInstrumentConfiguration>()
                    : Array.AsReadOnly(
                    [
                        new BookInstrumentConfiguration(
                            instrument.Value,
                            symbol,
                            adapterName,
                            0m,
                            0m,
                            "-",
                            "100.0",
                            100m,
                            "$0.00",
                            "$0.00",
                            ExecutionTone.Neutral),
                    ]),
                "Risk escalation not configured",
                "alternate client not attached");

        internal ExecutionBookReadModel BuildUnavailableReadModel(bool isPaused)
        {
            var analytics = ExecutionAnalyticsProjector.BuildBook(
                Id,
                Name,
                OpeningEquity,
                AnalyticsHistory,
                openPositions: 0,
                longExposure: 0m,
                shortExposure: 0m,
                new ExecutionQualityReadModel(0, 0, 0, 0, 0, 0, 0, 0d, 0, 0d),
                DateTime.UtcNow);
            var period = analytics.Period(ExecutionTimeRange.ThirtyDays);
            var history = AnalyticsHistory
                .OrderByDescending(item => item.ClosedAtUtc)
                .Select(item => new ExecutionHistoryReadModel(
                    item.ClosedAtUtc,
                    Name,
                    item.Instrument,
                    "Closed trade",
                    "Representative in-memory analytics history",
                    "-",
                    "-",
                    ExecutionFormatting.SignedMoney(item.RealizedProfitAndLoss),
                    item.RealizedProfitAndLoss > 0m ? ExecutionTone.Positive : ExecutionTone.Negative))
                .ToArray();
            return new ExecutionBookReadModel(
                Id,
                Name,
                AdapterId,
                AdapterName,
                Strategies,
                period.Metrics.NetProfitAndLossDisplay,
                period.Metrics.ProfitAndLossTone,
                new ExecutionLeaseReadModel(ExecutionLeaseStatus.Stale, null, UnavailableDetail),
                isPaused,
                AdmissionOpen: false,
                OpenRealPositionCount: 0,
                Array.Empty<ExecutionPositionReadModel>(),
                Array.Empty<ExecutionOrderReadModel>(),
                Array.AsReadOnly(history),
                Array.Empty<ExecutionReconciliationReadModel>(),
                new ExecutionRiskReadModel(
                    Array.Empty<ExecutionRiskUsageReadModel>(),
                    EscalationLine),
                Array.Empty<ExecutionLedgerEventReadModel>(),
                analytics);
        }

    }

    private sealed record BookInstrumentConfiguration(
        int InstrumentId,
        string Symbol,
        string ConfiguredRoute,
        decimal ModelUnits,
        decimal TargetQuantity,
        string AveragePrice,
        string LastPrice,
        decimal ReferencePrice,
        string UnrealizedProfitAndLoss,
        string RealizedProfitAndLoss,
        ExecutionTone ProfitAndLossTone);
}
