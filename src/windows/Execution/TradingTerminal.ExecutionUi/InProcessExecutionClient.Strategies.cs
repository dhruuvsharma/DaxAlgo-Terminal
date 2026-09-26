using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// How a running strategy reaches its execution books.
///
/// <para>A strategy window runs the strategy against its own model book; that is the only thing a strategy
/// ever trades. A book that names the strategy copies that model book to its broker, scaled by the book's
/// size, through the same guarded intake the engine uses for everything else — pause, reconciliation, risk,
/// the lease and the Paper/Real gates all apply. Closing the window stops the copying and leaves the book's
/// position where it is.</para>
/// </summary>
public interface IStrategyExecutionBridge
{
    /// <summary>
    /// Offers a running strategy's model portfolio to every book bound to <paramref name="strategyName"/> (the
    /// name the book picker shows). Books created later pick it up too. Dispose to stop.
    /// </summary>
    IDisposable Attach(string strategyName, IModelPortfolioSource portfolio);
}

public sealed partial class InProcessExecutionClient : IStrategyExecutionBridge
{
    /// <summary>How often a target the book refused is offered again — a card that was not connected yet, a
    /// reconciliation that has since settled.</summary>
    private static readonly TimeSpan StrategyRetryInterval = TimeSpan.FromSeconds(15);

    private readonly object _strategyGate = new();
    private readonly List<AttachedStrategy> _attachedStrategies = [];
    private readonly List<PersistedExecutionBook> _unrestoredBooks = [];
    private readonly HubReferencePrices? _hubPrices;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _strategyTargetTimes =
        new(StringComparer.Ordinal);
    private Timer? _strategyRetry;

    /// <summary>The terminal's own latest quote for an instrument, for a broker book whose broker gives none.</summary>
    private TradingTerminal.Core.Execution.RoutePrice? HubPrice(InstrumentId instrument) => _hubPrices?.Latest(instrument);

    private sealed class AttachedStrategy(string name, IModelPortfolioSource portfolio)
    {
        public string Name { get; } = name;

        public IModelPortfolioSource Portfolio { get; } = portfolio;

        /// <summary>One replicator per bound book, by book id.</summary>
        public Dictionary<string, SandboxExecutionReplicator> Replicators { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Detachment(InProcessExecutionClient client, AttachedStrategy attached) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                client.Detach(attached);
        }
    }

    public IDisposable Attach(string strategyName, IModelPortfolioSource portfolio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        ArgumentNullException.ThrowIfNull(portfolio);
        ThrowIfDisposed();
        var attached = new AttachedStrategy(strategyName.Trim(), portfolio);
        lock (_strategyGate)
        {
            _attachedStrategies.Add(attached);
            _strategyRetry ??= new Timer(_ => RetryRefusedTargets(), null, StrategyRetryInterval, StrategyRetryInterval);
        }

        SyncStrategyBindings();
        return new Detachment(this, attached);
    }

    private void Detach(AttachedStrategy attached)
    {
        SandboxExecutionReplicator[] stopping;
        string[] books;
        lock (_strategyGate)
        {
            _attachedStrategies.Remove(attached);
            stopping = [.. attached.Replicators.Values];
            books = [.. attached.Replicators.Keys];
            attached.Replicators.Clear();
        }

        StopReplicators(stopping);
        // A warning describes what the running strategy asked for; with its window closed there is nothing to warn about.
        lock (_gate)
        {
            foreach (var id in books)
            {
                if (FindBook(id) is { } entry)
                    entry.StrategyWarning = null;
            }
        }

        Invalidate();
    }

    private static bool Names(string bookStrategy, string strategyName) =>
        string.Equals(bookStrategy.Trim(), strategyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings the replicators in line with the books and the running strategies: one per (book, strategy) pair the
    /// book names. A book copies ONE running strategy — the first window attached for it — so two windows of the
    /// same strategy cannot send the book two different targets.
    /// </summary>
    private void SyncStrategyBindings()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        (string Id, string Name, IReadOnlyList<string> Strategies, long Size)[] books;
        lock (_gate)
        {
            // Every book, bound or not: a book with no runtime yet binds to the strategy's instrument when its
            // first target arrives (InProcessExecutionClient.Binding).
            books = [.. _books
                .Select(book => (book.Configuration.Id, book.Configuration.Name, book.Configuration.Strategies,
                    book.Configuration.UnitsPerStrategyUnit))];
        }

        var stopping = new List<SandboxExecutionReplicator>();
        var started = 0;
        lock (_strategyGate)
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attached in _attachedStrategies)
            {
                foreach (var bookId in attached.Replicators.Keys.ToArray())
                {
                    var book = books.FirstOrDefault(item => item.Id == bookId);
                    if (book.Id is null || !book.Strategies.Any(name => Names(name, attached.Name)) || !claimed.Add(bookId))
                    {
                        stopping.Add(attached.Replicators[bookId]);
                        attached.Replicators.Remove(bookId);
                    }
                }
            }

            foreach (var attached in _attachedStrategies)
            {
                foreach (var book in books)
                {
                    var bound = book.Strategies.FirstOrDefault(name => Names(name, attached.Name));
                    if (bound is null || claimed.Contains(book.Id))
                        continue;
                    // The book's own spelling of the name is what the intake checks the target against.
                    var replicator = new SandboxExecutionReplicator(
                        attached.Portfolio,
                        this,
                        new SandboxExecutionReplicationOptions(book.Id, bound.Trim(), UnitsPerStrategyUnit: book.Size));
                    var strategy = attached.Name;
                    var bookId = book.Id;
                    var bookName = book.Name;
                    replicator.SubmissionCompleted += outcome => OnStrategyTarget(strategy, bookId, bookName, outcome);
                    attached.Replicators[book.Id] = replicator;
                    claimed.Add(book.Id);
                    started++;
                }
            }
        }

        StopReplicators(stopping);
        if (started > 0)
            Invalidate();
    }

    /// <summary>Surfaces what happened to a strategy's target where the console shows command results.</summary>
    private void OnStrategyTarget(string strategy, string bookId, string book, SandboxExecutionReplicationOutcome outcome)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _strategyTargetTimes[bookId] = DateTime.UtcNow;
        lock (_gate)
            _lastOperationMessage = $"{strategy} → {book}: {outcome.Result.Message}";
        Invalidate();
    }

    /// <summary>Each bound book's running strategy and its last target, by book id. Taken before <c>_gate</c>,
    /// never inside it.</summary>
    private Dictionary<string, ExecutionStrategyLinkReadModel> StrategyLinks()
    {
        var links = new Dictionary<string, ExecutionStrategyLinkReadModel>(StringComparer.Ordinal);
        lock (_strategyGate)
        {
            foreach (var attached in _attachedStrategies)
            {
                foreach (var (bookId, replicator) in attached.Replicators)
                {
                    var outcome = replicator.LastOutcome;
                    long? target = outcome?.Intent is { } intent && intent.SignedUnits.TryGetWholeUnits(out var units)
                        ? units
                        : null;
                    links[bookId] = new ExecutionStrategyLinkReadModel(
                        attached.Name,
                        IsRunning: true,
                        target,
                        outcome?.Result.Message ?? "Running; the strategy has not changed its position yet.",
                        outcome?.Result.IsSuccess ?? true,
                        _strategyTargetTimes.TryGetValue(bookId, out var at) ? at : null)
                    {
                        // No intent means the position never became a target: nothing reached the book to refuse.
                        Problem = outcome is { Intent: null, Result.IsSuccess: false } refused ? refused.Result.Message : string.Empty,
                    };
                }
            }
        }

        return links;
    }

    /// <summary>The book with its strategy link, and — when the strategy has sent a target — the position's target
    /// and drift, which is what the next order closes.</summary>
    private static ExecutionBookReadModel WithStrategyLink(
        ExecutionBookReadModel book,
        IReadOnlyDictionary<string, ExecutionStrategyLinkReadModel> links)
    {
        if (!links.TryGetValue(book.Id, out var link))
        {
            return book.Strategies.Count == 0
                ? book
                : book with
                {
                    StrategyLink = new ExecutionStrategyLinkReadModel(
                        book.Strategies[0],
                        IsRunning: false,
                        null,
                        book.IsAwaitingInstrument
                            ? "Open this strategy's window: the book trades the instrument it runs on."
                            : "Open this strategy's window to start copying its position.",
                        true,
                        null),
                };
        }

        var warning = book.HasStrategyWarning ? book.StrategyWarning : link.Problem;
        if (link.LastTargetUnits is not { } target || book.Positions.Count != 1)
            return book with { StrategyLink = link, StrategyWarning = warning };

        var drift = book.PositionUnits - target;
        return book with
        {
            StrategyLink = link,
            StrategyWarning = warning,
            Positions = Array.AsReadOnly(
            [
                book.Positions[0] with
                {
                    TargetQuantity = ExecutionFormatting.SignedUnits(target),
                    Delta = ExecutionFormatting.SignedUnits(drift),
                    HasDivergence = drift != 0m,
                    ModelUnits = book.UnitsPerStrategyUnit > 0
                        ? ExecutionFormatting.SignedUnits((decimal)target / book.UnitsPerStrategyUnit)
                        : "-",
                },
            ]),
        };
    }

    /// <summary>Offers again every target a book refused, so a card that connects later or a reconciliation that
    /// settles is picked up without waiting for the strategy's next change.</summary>
    private void RetryRefusedTargets()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        SandboxExecutionReplicator[] refused;
        lock (_strategyGate)
        {
            refused = [.. _attachedStrategies
                .SelectMany(attached => attached.Replicators.Values)
                .Where(replicator => replicator.LastOutcome is { Result.IsSuccess: false })];
        }

        foreach (var replicator in refused)
            replicator.ReplicateCurrent();
    }

    private static void StopReplicators(IEnumerable<SandboxExecutionReplicator> replicators)
    {
        // Asynchronously: a replicator finishing a submission must not hold the caller (often the UI thread).
        foreach (var replicator in replicators)
            _ = replicator.DisposeAsync().AsTask();
    }

    private void DisposeStrategyBindings()
    {
        SandboxExecutionReplicator[] stopping;
        lock (_strategyGate)
        {
            _strategyRetry?.Dispose();
            _strategyRetry = null;
            stopping = [.. _attachedStrategies.SelectMany(attached => attached.Replicators.Values)];
            foreach (var attached in _attachedStrategies)
                attached.Replicators.Clear();
            _attachedStrategies.Clear();
        }

        StopReplicators(stopping);
    }

    // ── One order ────────────────────────────────────────────────────────────────────────────────

    public async ValueTask<ExecutionCommandResult> CancelOrderAsync(
        string bookId,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BookEntry? entry;
        lock (_gate)
            entry = FindBook(bookId?.Trim() ?? string.Empty);

        ExecutionCommandResult result;
        if (entry?.Runtime is null)
        {
            result = ExecutionCommandResult.Failure("The book has no attached execution runtime.");
        }
        else if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            result = ExecutionCommandResult.Failure("Choose an order to cancel.");
        }
        else if (!entry.TryBeginOperation())
        {
            result = ExecutionCommandResult.Failure("Another command is active for this book; try again in a moment.");
        }
        else
        {
            try
            {
                result = await entry.Runtime.CancelOrderAsync(clientOrderId.Trim(), cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<ExecutionCommandResult> ReplaceOrderAsync(
        string bookId,
        string clientOrderId,
        long units,
        ScaledPrice? limitPrice,
        ScaledPrice? stopPrice,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BookEntry? entry;
        lock (_gate)
            entry = FindBook(bookId?.Trim() ?? string.Empty);

        ExecutionCommandResult result;
        if (entry?.Runtime is null)
        {
            result = ExecutionCommandResult.Failure("The book has no attached execution runtime.");
        }
        else if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            result = ExecutionCommandResult.Failure("Choose an order to change.");
        }
        else if (!entry.TryBeginOperation())
        {
            result = ExecutionCommandResult.Failure("Another command is active for this book; try again in a moment.");
        }
        else
        {
            try
            {
                result = await entry.Runtime
                    .ReplaceOrderAsync(clientOrderId.Trim(), units, limitPrice, stopPrice, cancellationToken)
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

    /// <summary>
    /// The new terms for a working order and the intent its fresh risk check is bound to. The engine requires the
    /// risk intent to describe what is still to fill — the new quantity less what has filled — in the order's own
    /// direction; a target order states it as the position that results.
    /// </summary>
    private static bool TryReplacement(
        OrderProjection projection,
        long units,
        ScaledPrice? limitPrice,
        ScaledPrice? stopPrice,
        long currentUnits,
        out CanonicalOrderTerms terms,
        out TradeIntent riskIntent,
        out string failure)
    {
        var current = projection.ReplacementTerms ?? projection.Terms;
        terms = current;
        riskIntent = projection.Instruction.TradeIntent;
        if (!projection.FilledQuantity.TryGetWholeUnits(out var filled) || units <= filled)
        {
            failure = $"The new quantity must be more than the {filled} unit(s) already filled.";
            return false;
        }

        terms = current with
        {
            Quantity = ScaledQuantity.FromWhole(units),
            LimitPrice = current.LimitPrice is null ? null : limitPrice ?? current.LimitPrice,
            StopPrice = current.StopPrice is null ? null : stopPrice ?? current.StopPrice,
        };
        if (terms.Validate() != OrderDomainFault.None)
        {
            failure = "The new quantity or prices are not valid for this order.";
            return false;
        }

        var remaining = checked(units - filled) * (current.Side == OrderSide.Buy ? 1 : -1);
        riskIntent = riskIntent with
        {
            SignedUnits = ScaledQuantity.FromWhole(riskIntent.QuantityMode == TradeIntentQuantityMode.TargetPosition
                ? checked(currentUnits + remaining)
                : remaining),
        };
        failure = string.Empty;
        return true;
    }

    // ── Books waiting for their card ─────────────────────────────────────────────────────────────

    private static ExecutionBookCreateRequest RequestFor(PersistedExecutionBook book) =>
        new(book.Name, book.AdapterId, book.Strategies,
            book.Instrument > 0 ? new InstrumentId(book.Instrument) : default,
            book.Instrument > 0 ? book.Symbol : string.Empty,
            book.UnitsPerStrategyUnit > 0 ? book.UnitsPerStrategyUnit : 1);

    private void KeepUnrestored(PersistedExecutionBook book)
    {
        lock (_gate)
        {
            _unrestoredBooks.RemoveAll(item => string.Equals(item.Name, book.Name, StringComparison.OrdinalIgnoreCase));
            _unrestoredBooks.Add(book);
        }
    }

    private void ForgetUnrestored(string? name)
    {
        lock (_gate)
            _unrestoredBooks.RemoveAll(item => string.Equals(item.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Remembered books not currently live, so a restart without their broker does not forget them.
    /// Called under <c>_gate</c>.</summary>
    private IEnumerable<PersistedExecutionBook> UnrestoredBooks(IReadOnlyList<PersistedExecutionBook> live) =>
        _unrestoredBooks
            .Where(waiting => !live.Any(book => string.Equals(book.Name, waiting.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    /// <summary>After a card connects: brings back the remembered books that were waiting for it.</summary>
    private async Task RestoreWaitingBooksAsync(string adapterId, CancellationToken cancellationToken)
    {
        PersistedExecutionBook[] waiting;
        lock (_gate)
        {
            waiting = [.. _unrestoredBooks.Where(book =>
                string.Equals(book.AdapterId, adapterId, StringComparison.Ordinal))];
        }

        foreach (var book in waiting)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                return;
            var result = await CreateBookAsync(RequestFor(book), cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess && book.IsPaused && FindBookByName(book.Name) is { } entry)
                entry.IsPaused = true;
        }
    }
}
