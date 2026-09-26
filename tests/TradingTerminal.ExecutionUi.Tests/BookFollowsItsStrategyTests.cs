using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.MarketData;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.ExecutionUi.Tests;

/// <summary>
/// A book is a name, an account and the strategy it copies: it trades whatever instrument the strategy's window
/// runs on, any number of books can copy one strategy, and a book whose account cannot trade that instrument says
/// why instead of silently doing nothing.
/// </summary>
[Collection("Execution client")]
public sealed class BookFollowsItsStrategyTests
{
    private static readonly InstrumentId EurUsd = new(9201);
    private static readonly InstrumentId Gold = new(9202);

    private sealed class Portfolio(IModelPortfolio snapshot) : IModelPortfolioSource
    {
        public IModelPortfolio? CurrentSnapshot { get; } = snapshot;

        public event Action<IModelPortfolio>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    private static Portfolio Holding(InstrumentId instrument, double units) => new(new SandboxPortfolioSnapshot(
        instrument, units, units, 1.08d, 1, 100_000d, 0d, 0d, 0d, 100_000d, 0d, 0, 0, 0, 0, 0, true));

    [Fact]
    public async Task A_book_made_of_a_name_and_a_strategy_trades_the_instrument_the_strategy_runs_on()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore(), instruments: new Registry());
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest("Copy", "paper", ["EMA Cross"], UnitsPerStrategyUnit: 5));
        Assert.True(created.IsSuccess, created.Message);
        Assert.True(Book(client, "Copy").IsAwaitingInstrument);

        using var running = client.Attach("EMA Cross", Holding(EurUsd, 2));
        var book = await UntilAsync(client, "Copy", read => read.PositionUnits == 10m);

        Assert.Equal((EurUsd, "EURUSD"), (book.TradableInstruments.Single().Instrument, book.TradableInstruments.Single().Symbol));
        Assert.False(book.IsAwaitingInstrument);
        Assert.False(book.HasStrategyWarning);
    }

    [Fact]
    public async Task Several_books_copy_one_strategy_each_at_its_own_size()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore(), instruments: new Registry());
        foreach (var (name, size) in new[] { ("One", 1L), ("Two", 2L), ("Three", 3L) })
        {
            var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(name, "paper", ["EMA Cross"], UnitsPerStrategyUnit: size));
            Assert.True(created.IsSuccess, created.Message);
        }

        using var running = client.Attach("EMA Cross", Holding(EurUsd, 4));

        await UntilAsync(client, "One", read => read.PositionUnits == 4m);
        await UntilAsync(client, "Two", read => read.PositionUnits == 8m);
        await UntilAsync(client, "Three", read => read.PositionUnits == 12m);
    }

    [Fact]
    public async Task A_broker_book_whose_broker_does_not_list_the_strategys_instrument_says_why()
    {
        using var client = BrokerClient();
        await ConnectAsync(client);
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest("Gold copy", "fake-paper", ["Breakout"]));
        Assert.True(created.IsSuccess, created.Message);

        using var running = client.Attach("Breakout", Holding(Gold, 1));
        var book = await UntilAsync(client, "Gold copy", read => read.HasStrategyWarning);

        Assert.Contains("Fakebroker TESTNET cannot trade XAUUSD", book.StrategyWarning, StringComparison.Ordinal);
        Assert.Contains("XAUUSD is not listed", book.StrategyWarning, StringComparison.Ordinal);
        Assert.Empty(book.TradableInstruments);
    }

    [Fact]
    public async Task Two_books_on_one_broker_account_cannot_share_a_symbol_and_the_second_says_so()
    {
        using var client = BrokerClient();
        await ConnectAsync(client);
        foreach (var name in new[] { "First", "Second" })
        {
            var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(name, "fake-paper", ["EMA Cross"]));
            Assert.True(created.IsSuccess, created.Message);
        }

        using var running = client.Attach("EMA Cross", Holding(EurUsd, 0));
        var books = await UntilAllAsync(client, all =>
            all.Count(book => book.TradableInstruments.Count == 1) == 1 && all.Count(book => book.HasStrategyWarning) == 1);

        var refused = books.Single(book => book.HasStrategyWarning);
        var bound = books.Single(book => !book.HasStrategyWarning);
        Assert.Contains($"Book '{bound.Name}' already trades EURUSD on this Fakebroker TESTNET account", refused.StrategyWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_book_whose_card_is_not_connected_says_to_connect_it()
    {
        using var client = BrokerClient();
        await ConnectAsync(client);
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest("Later", "fake-paper", ["EMA Cross"]));
        Assert.True(created.IsSuccess, created.Message);
        var disconnected = await client.DisconnectAdapterAsync("fake-paper");
        Assert.True(disconnected.IsSuccess, disconnected.Message);

        using var running = client.Attach("EMA Cross", Holding(EurUsd, 1));
        var book = await UntilAsync(client, "Later", read => read.HasStrategyWarning);

        Assert.Contains("is not connected. Connect it under Connections", book.StrategyWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_new_book_form_asks_for_the_strategy_and_not_for_an_instrument()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore());
        var originalTimerFactory = TradingTerminal.UI.UiThread.CreateRenderTimer;
        TradingTerminal.UI.UiThread.CreateRenderTimer = (_, _) => new Nothing();
        try
        {
            using var viewModel = new ExecutionConsoleViewModel(client, new AlwaysConfirm(), TestBrokerLoginFormFactory.Alpaca());
            viewModel.NewBookCommand.Execute(null);
            viewModel.NewBookName = "No strategy";

            await viewModel.CreateBookCommand.ExecuteAsync(null);

            Assert.Contains("A book copies a strategy", viewModel.NewBookError, StringComparison.Ordinal);
            Assert.Empty(client.GetSnapshot().Books);
        }
        finally
        {
            TradingTerminal.UI.UiThread.CreateRenderTimer = originalTimerFactory;
        }
    }

    [Fact]
    public void The_strategy_list_offers_blocks_strategies_but_not_blocks_visualizers()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore());
        var blocks = new TradingTerminal.Blocks.Runtime.BlocksUnitRegistry();
        blocks.Register(new TradingTerminal.Blocks.Runtime.BlocksUnitRegistration(
            "hand-written-ema", "Hand-written EMA", string.Empty, IsStrategy: true, () => throw new NotSupportedException(), [], []));
        blocks.Register(new TradingTerminal.Blocks.Runtime.BlocksUnitRegistration(
            "depth-heatmap", "Depth heatmap", string.Empty, IsStrategy: false, () => throw new NotSupportedException(), [], []));
        var originalTimerFactory = TradingTerminal.UI.UiThread.CreateRenderTimer;
        TradingTerminal.UI.UiThread.CreateRenderTimer = (_, _) => new Nothing();
        try
        {
            using var viewModel = new ExecutionConsoleViewModel(
                client, new AlwaysConfirm(), TestBrokerLoginFormFactory.Alpaca(), blocks: blocks);
            viewModel.NewBookCommand.Execute(null);

            Assert.Equal(["Hand-written EMA"], viewModel.AvailableStrategies);
            Assert.Equal("Hand-written EMA", viewModel.SelectedNewBookStrategy);
        }
        finally
        {
            TradingTerminal.UI.UiThread.CreateRenderTimer = originalTimerFactory;
        }
    }

    [Fact]
    public async Task A_strategy_that_names_no_single_instrument_leaves_its_reason_on_the_book()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore(), instruments: new Registry());
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest("Pair copy", "paper", ["Pair"]));
        Assert.True(created.IsSuccess, created.Message);

        using var running = client.Attach("Pair", Holding(default, 0));
        var book = await UntilAsync(client, "Pair copy", read => read.HasStrategyWarning);

        Assert.Contains("not on one single instrument", book.StrategyWarning, StringComparison.Ordinal);
        Assert.True(book.IsAwaitingInstrument);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class AlwaysConfirm : IExecutionConfirmationService
    {
        public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<ExecutionTypedConfirmationResult> ConfirmTypedAsync(
            string title, string message, string requiredText, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionTypedConfirmationResult.Cancelled);
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static InProcessExecutionClient BrokerClient() => new(
        orderRoutes: [new Route()],
        brokerCredentials: new Keys(),
        bookStore: new MemoryStore(),
        instruments: new Registry());

    private static async Task ConnectAsync(InProcessExecutionClient client)
    {
        var connected = await client.ConnectAdapterAsync("fake-paper");
        Assert.True(connected.IsSuccess, connected.Message);
    }

    private static ExecutionBookReadModel Book(InProcessExecutionClient client, string name) =>
        client.GetSnapshot().Books.Single(book => book.Name == name);

    private static async Task<ExecutionBookReadModel> UntilAsync(
        InProcessExecutionClient client,
        string name,
        Func<ExecutionBookReadModel, bool> condition)
    {
        var books = await UntilAllAsync(client, all => all.Any(book => book.Name == name && condition(book)));
        return books.Single(book => book.Name == name);
    }

    private static async Task<IReadOnlyList<ExecutionBookReadModel>> UntilAllAsync(
        InProcessExecutionClient client,
        Func<IReadOnlyList<ExecutionBookReadModel>, bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Deadlock;
        while (true)
        {
            var books = client.GetSnapshot().Books;
            if (condition(books))
                return books;
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The books never reached the expected state: " + string.Join("; ", books.Select(book =>
                    $"{book.Name} units={book.PositionUnits} warning='{book.StrategyWarning}' message='{client.GetSnapshot().LastOperationMessage}'")));
            }

            await Task.Delay(25);
        }
    }

    /// <summary>A broker that lists EURUSD and nothing else.</summary>
    private sealed class Route : IBrokerOrderRoute
    {
        public BrokerKind Broker => BrokerKind.Binance;

        public string DisplayName => "Fakebroker";

        public string RouteId => "fake";

        public string? PaperEnvironmentName => "TESTNET";

        public Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct) =>
            Task.FromResult(new RouteAccount("ACC-1", "USD", 10_000m, 10_000m));

        public Task<RouteInstrument> InstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            symbol == "EURUSD"
                ? Task.FromResult(new RouteInstrument("EURUSD", 1m, 1m, 0.00001m, 1, 1_000_000,
                    RouteOrderTypes.Market | RouteOrderTypes.Limit, RouteTimesInForce.GoodTillCancelled, false, "USD"))
                : Task.FromException<RouteInstrument>(new BrokerOrderRouteException($"{symbol} is not listed.", isRejection: true));

        public Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct) =>
            throw new BrokerOrderRouteException("No orders in this test.", isRejection: true);

        public Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) => Task.CompletedTask;

        public Task<RouteOrder> ReplaceAsync(RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct) =>
            throw new BrokerOrderRouteException("No orders in this test.", isRejection: true);

        public Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteOrder>>([]);

        public Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
            Task.FromResult<RouteOrder?>(null);

        public Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            Task.FromResult(new RoutePosition(symbol, 0m));

        public Task<RouteAccount> AccountAsync(RouteEnvironment environment, CancellationToken ct) =>
            Task.FromResult(new RouteAccount("ACC-1", "USD", 10_000m, 10_000m));

        public Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
            Task.FromResult<RoutePrice?>(new RoutePrice(1.08m, DateTime.UtcNow));
    }

    private sealed class Keys : IBrokerCredentialSource
    {
        public BrokerCredential For(BrokerKind broker) => broker == BrokerKind.Binance ? new BrokerCredential("key", "secret") : BrokerCredential.None;
    }

    /// <summary>Two instruments the terminal knows by name; neither has a broker alias, so the canonical symbol is sent.</summary>
    private sealed class Registry : IInstrumentRegistry
    {
        private static readonly Instrument[] Known =
        [
            new(EurUsd, "EURUSD", default, "FX", "USD", 0.00001d, 1d),
            new(Gold, "XAUUSD", default, "FX", "USD", 0.01d, 1d),
        ];

        public Instrument? Get(InstrumentId id) => Known.FirstOrDefault(item => item.Id == id);

        public InstrumentId? Resolve(BrokerKind broker, string brokerSymbol) => null;

        public InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker) => throw new NotSupportedException();

        public string? ToBrokerSymbol(InstrumentId id, BrokerKind broker) => null;

        public void RegisterAlias(InstrumentAlias alias)
        {
        }

        public IReadOnlyList<Instrument> All() => Known;
    }

    private sealed class MemoryStore : IExecutionBookStore
    {
        private IReadOnlyList<PersistedExecutionBook> _saved = [];

        public IReadOnlyList<PersistedExecutionBook> Read() => _saved;

        public void Save(IReadOnlyList<PersistedExecutionBook> books) => _saved = [.. books];
    }
}
