using TradingTerminal.Core.Domain;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Routing;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// A book trades what its strategy trades.
///
/// <para>A book is a name, an account and a strategy. It has no instrument of its own: the strategy's window runs on
/// one, and the first target the book receives from it says which. The book binds to that instrument on the account
/// — a broker book reads the symbol's rules from the broker and attaches its own lease — and from then on trades it
/// by hand as well. Ten books can copy one strategy, each on its own account.</para>
///
/// <para>When the account cannot trade the strategy's instrument the book says why, in the broker's words where there
/// are any, and keeps saying it until the reason goes away: the card connects, the strategy moves to an instrument
/// the broker lists, or the other book on the same symbol is closed. A refused target is offered again every
/// fifteen seconds, so nothing has to be restarted.</para>
/// </summary>
public sealed partial class InProcessExecutionClient
{
    /// <summary>Broker symbols being bound right now, by <c>adapter|SYMBOL</c> → book id. Guarded by <c>_gate</c>.</summary>
    private readonly Dictionary<string, string> _symbolClaims = new(StringComparer.Ordinal);

    /// <summary>Whether a target for <paramref name="instrument"/> needs the book (re)bound first.</summary>
    private static bool NeedsInstrument(BookEntry entry, InstrumentId instrument) =>
        entry.Runtime is null ||
        entry.Configuration.Instruments.Count == 0 ||
        entry.Configuration.Instruments[0].InstrumentId != instrument.Value;

    /// <summary>
    /// Binds <paramref name="entry"/> to the strategy's <paramref name="instrument"/> on its account. Failure carries
    /// the reason as a sentence the book shows. Called inside the book's operation slot.
    /// </summary>
    private async Task<ExecutionCommandResult> BindToStrategyInstrumentAsync(
        BookEntry entry,
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        var configuration = entry.Configuration;
        if (instrument.IsNone)
            return ExecutionCommandResult.Failure("The strategy's window has no instrument.");

        var canonical = CanonicalSymbol(instrument);
        var current = configuration.Instruments.Count > 0 ? configuration.Instruments[0] : null;
        if (current is not null && current.InstrumentId != instrument.Value && entry.Runtime is { } held)
        {
            // A book follows its strategy onto another instrument only from flat: what it holds on the old one would
            // otherwise be left unmanaged, and the engine books one instrument per book.
            var read = held.BuildReadModel(configuration, entry.IsPaused);
            if (read.PositionUnits != 0m || read.WorkingOrderCount > 0)
            {
                var holding = read.PositionUnits != 0m
                    ? $"{ExecutionFormatting.SignedUnits(read.PositionUnits)} units of {current.Symbol}"
                    : $"working orders on {current.Symbol}";
                return ExecutionCommandResult.Failure(
                    $"The strategy now trades {canonical}, but this book still holds {holding}. " +
                    "Kill the book to flatten it, and it will follow the strategy.");
            }
        }

        if (string.Equals(configuration.AdapterId, "paper", StringComparison.Ordinal))
        {
            var runtime = entry.Runtime ?? InProcessBookRuntime.CreateEmpty(
                configuration.Id, _paperAccount, _executionLeaseStore, _tradingMode,
                id => HubPrice(new InstrumentId(id)));
            Rebind(entry, configuration with { Instruments = [BookInstrument(instrument, canonical, "Paper")] }, runtime);
            return ExecutionCommandResult.Success($"'{configuration.Name}' trades {canonical}, the instrument its strategy runs on.");
        }

        switch (FindRegisteredAdapter(configuration.AdapterId))
        {
            case RoutedExecutionAdapter card:
                return await BindRoutedAsync(entry, card, instrument, canonical, cancellationToken).ConfigureAwait(false);

            case IBookableExecutionAdapter bookable when bookable.Instrument == instrument:
                Rebind(entry, configuration with
                {
                    Instruments = [BookInstrument(instrument, bookable.Symbol, bookable.DisplayName)],
                }, entry.Runtime);
                return ExecutionCommandResult.Success($"'{configuration.Name}' trades {bookable.Symbol}.");

            case IBookableExecutionAdapter bookable:
                return ExecutionCommandResult.Failure(
                    $"This {bookable.DisplayName} account is set up for {bookable.Symbol} only, and the strategy trades {canonical}.");

            default:
                return ExecutionCommandResult.Failure(
                    $"{configuration.AdapterName} is not available in this session. Connect it under Connections.");
        }
    }

    private async Task<ExecutionCommandResult> BindRoutedAsync(
        BookEntry entry,
        RoutedExecutionAdapter card,
        InstrumentId instrument,
        string canonical,
        CancellationToken cancellationToken)
    {
        var configuration = entry.Configuration;
        var name = AdapterDisplayName(card);
        if (!card.Session.CanExecute)
            return ExecutionCommandResult.Failure($"{name} is not connected. Connect it under Connections and the book picks the strategy up.");

        var brokerSymbol = BrokerSymbolFor(instrument, card.Route.Broker, canonical);
        if (brokerSymbol is null)
        {
            return ExecutionCommandResult.Failure(
                $"The terminal knows no {card.Route.DisplayName} symbol for {canonical}. {card.Route.DisplayName} has never " +
                "served it here, so there is nothing to send orders for.");
        }

        // Claimed under the lock for the whole bind, so two books that bind at the same moment cannot both pass the
        // check: the second is told which book holds the symbol, rather than failing further down on its lease.
        var claim = $"{configuration.AdapterId}|{brokerSymbol.ToUpperInvariant()}";
        string? sharing;
        lock (_gate)
        {
            sharing = _books
                .Where(book => !ReferenceEquals(book, entry) &&
                               string.Equals(book.Configuration.AdapterId, configuration.AdapterId, StringComparison.Ordinal) &&
                               book.Configuration.Instruments.Count > 0 &&
                               string.Equals(BrokerSymbol(book.Configuration.Instruments[0].Symbol), brokerSymbol, StringComparison.OrdinalIgnoreCase))
                .Select(book => book.Configuration.Name)
                .FirstOrDefault();
            if (sharing is null && _symbolClaims.TryGetValue(claim, out var claimant) &&
                !string.Equals(claimant, configuration.Id, StringComparison.Ordinal))
            {
                sharing = FindBook(claimant)?.Configuration.Name ?? claimant;
            }

            if (sharing is null)
                _symbolClaims[claim] = configuration.Id;
        }

        if (sharing is not null)
        {
            return ExecutionCommandResult.Failure(
                $"Book '{sharing}' already trades {brokerSymbol} on this {name} account. The broker reports one position per " +
                "symbol, so two books cannot share it: put this book on another account, or close the other book.");
        }

        try
        {
            return await AttachRoutedAsync(entry, card, instrument, brokerSymbol, name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _symbolClaims.Remove(claim);
        }
    }

    private async Task<ExecutionCommandResult> AttachRoutedAsync(
        BookEntry entry,
        RoutedExecutionAdapter card,
        InstrumentId instrument,
        string brokerSymbol,
        string name,
        CancellationToken cancellationToken)
    {
        var configuration = entry.Configuration;
        RoutedExecutionAdapter? bound = null;
        BrokerBookRuntime? runtime = null;
        try
        {
            bound = card.CreateBookAdapter(instrument, brokerSymbol);
            await bound.ConnectAsync(cancellationToken).ConfigureAwait(false);
            runtime = new BrokerBookRuntime(configuration.Id, bound, _executionLeaseStore, _tradingMode, ownedAdapter: bound);
            var initialized = await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (!initialized.IsSuccess)
            {
                runtime.Dispose();
                return initialized;
            }

            Rebind(entry, configuration with { Instruments = [BookInstrument(instrument, brokerSymbol, name)] }, runtime);
            return ExecutionCommandResult.Success($"'{configuration.Name}' trades {brokerSymbol} on {name}, the instrument its strategy runs on.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DiscardAsync(runtime, bound).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DiscardAsync(runtime, bound).ConfigureAwait(false);
            return ExecutionCommandResult.Failure($"{name} cannot trade {brokerSymbol}: {SafeReason(exception)}");
        }
    }

    /// <summary>A half-built binding: the runtime owns its adapter once it exists, so dispose one or the other.</summary>
    private static async ValueTask DiscardAsync(BrokerBookRuntime? runtime, RoutedExecutionAdapter? bound)
    {
        if (runtime is not null)
            runtime.Dispose();
        else if (bound is not null)
            await bound.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Swaps the book onto its new instrument and runtime, remembers it, and starts watching its quotes.</summary>
    private void Rebind(BookEntry entry, BookConfiguration configuration, IBookRuntime? runtime)
    {
        IBookRuntime? previous;
        lock (_gate)
        {
            previous = entry.Runtime;
            entry.Configuration = configuration;
            entry.Runtime = runtime;
        }

        if (previous is not null && !ReferenceEquals(previous, runtime))
            previous.Dispose();
        if (configuration.Instruments.Count > 0)
            _hubPrices?.Watch(new InstrumentId(configuration.Instruments[0].InstrumentId));
        PersistBooks();
    }

    private void SetStrategyWarning(BookEntry entry, string? warning)
    {
        lock (_gate)
            entry.StrategyWarning = warning;
    }

    private string CanonicalSymbol(InstrumentId instrument)
    {
        try
        {
            if (_instrumentRegistry?.Get(instrument)?.CanonicalSymbol is { Length: > 0 } symbol)
                return symbol;
        }
        catch
        {
            // An unreadable registry falls back to the id, which still names the instrument.
        }

        return $"#{instrument.Value}";
    }

    /// <summary>The broker's symbol for the instrument: its registered alias on that broker, else the canonical symbol,
    /// or null when the terminal knows neither.</summary>
    private string? BrokerSymbolFor(InstrumentId instrument, TradingTerminal.Core.Brokers.BrokerKind broker, string canonical)
    {
        try
        {
            if (_instrumentRegistry?.ToBrokerSymbol(instrument, broker) is { Length: > 0 } alias)
                return BrokerSymbol(alias);
        }
        catch
        {
            // No alias: the canonical symbol is the next best guess.
        }

        return canonical.StartsWith('#') ? null : canonical;
    }

    private static BookInstrumentConfiguration BookInstrument(InstrumentId instrument, string symbol, string route) =>
        new(instrument.Value, symbol, route, 0m, 0m, "-", "-", 0m, "$0.00", "$0.00", ExecutionTone.Neutral);
}
