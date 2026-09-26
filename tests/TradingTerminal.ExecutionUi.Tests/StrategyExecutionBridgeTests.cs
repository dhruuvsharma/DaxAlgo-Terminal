using TradingTerminal.Core.Domain;
using TradingTerminal.Execution;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Sandbox.Runtime;
using Xunit;

namespace TradingTerminal.ExecutionUi.Tests;

/// <summary>
/// A running strategy reaches the books that name it — and only those — scaled by each book's size; books keep
/// their instrument and size across a restart, and one whose card is not connected yet is kept rather than lost.
/// </summary>
public sealed class StrategyExecutionBridgeTests
{
    private static readonly InstrumentId EurUsd = new(9001);

    private sealed class Portfolio(IModelPortfolio snapshot) : IModelPortfolioSource
    {
        public IModelPortfolio? CurrentSnapshot { get; } = snapshot;

        public event Action<IModelPortfolio>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    private static Portfolio Holding(double units) => new(new SandboxPortfolioSnapshot(
        EurUsd, units, units, 1.08d, 1, 100_000d, 0d, 0d, 0d, 100_000d, 0d, 0, 0, 0, 0, 0, true));

    private sealed class MemoryStore(params PersistedExecutionBook[] books) : IExecutionBookStore
    {
        public IReadOnlyList<PersistedExecutionBook> Saved { get; private set; } = books;

        public IReadOnlyList<PersistedExecutionBook> Read() => Saved;

        public void Save(IReadOnlyList<PersistedExecutionBook> books) => Saved = [.. books];
    }

    private static async Task<string> NextMessageAsync(InProcessExecutionClient client, Func<string, bool> matches)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            if (client.GetSnapshot().LastOperationMessage is { } message && matches(message))
                seen.TrySetResult(message);
        }

        client.SnapshotInvalidated += OnChanged;
        try
        {
            OnChanged(null, EventArgs.Empty);
            return await seen.Task.WaitAsync(TestTimeouts.Deadlock);
        }
        finally
        {
            client.SnapshotInvalidated -= OnChanged;
        }
    }

    [Fact]
    public async Task A_running_strategy_reaches_the_book_that_names_it_scaled_by_the_books_size()
    {
        var store = new MemoryStore();
        using var client = new InProcessExecutionClient(bookStore: store);
        var created = await client.CreateBookAsync(
            new ExecutionBookCreateRequest("EMA book", "paper", ["EMA Cross"], EurUsd, "EURUSD", UnitsPerStrategyUnit: 5));
        Assert.True(created.IsSuccess, created.Message);

        using var attached = client.Attach("ema cross", Holding(2));
        var message = await NextMessageAsync(client, text => text.StartsWith("ema cross → EMA book", StringComparison.Ordinal));

        Assert.Contains("converged the Simulated book to 10 unit(s)", message, StringComparison.Ordinal);
        var saved = Assert.Single(store.Saved);
        Assert.Equal((EurUsd.Value, 5L), (saved.Instrument, saved.UnitsPerStrategyUnit));
    }

    [Fact]
    public async Task A_strategy_no_book_names_sends_nothing()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore());
        var created = await client.CreateBookAsync(
            new ExecutionBookCreateRequest("EMA book", "paper", ["EMA Cross"], EurUsd, "EURUSD"));
        Assert.True(created.IsSuccess, created.Message);
        var before = client.GetSnapshot().LastOperationMessage;

        using (client.Attach("RSI Fade", Holding(3)))
            await Task.Delay(300);

        Assert.Equal(before, client.GetSnapshot().LastOperationMessage);
    }

    [Fact]
    public async Task A_book_whose_card_is_not_connected_is_kept_for_when_it_is()
    {
        var waiting = new PersistedExecutionBook("cTrader EURUSD", "ctrader-paper", "EURUSD", ["EMA Cross"], false, EurUsd.Value, 3);
        var store = new MemoryStore(waiting);
        using var client = new InProcessExecutionClient(bookStore: store);

        var restored = await client.RestoreBooksAsync();

        Assert.False(restored.IsSuccess);
        Assert.Contains(store.Saved, book => book == waiting);
    }

    [Fact]
    public async Task A_book_size_outside_the_bounds_is_refused_at_creation()
    {
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore());

        var refused = await client.CreateBookAsync(
            new ExecutionBookCreateRequest("Zero book", "paper", [], EurUsd, "EURUSD", UnitsPerStrategyUnit: 0));

        Assert.False(refused.IsSuccess);
        Assert.Contains("Units per strategy unit", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.08450", 10_845L, 4)]
    [InlineData("1.0845", 10_845L, 4)]
    [InlineData("190", 190L, 0)]
    [InlineData("0.00001", 1L, 5)]
    public void A_ticket_price_is_its_exact_value_without_trailing_zeros(string text, long coefficient, byte scale)
    {
        Assert.True(ExecutionConsoleViewModel.TryPrice(text, out var price));
        Assert.Equal(new ScaledPrice(coefficient, scale), price);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("abc")]
    public void A_ticket_price_that_is_not_positive_is_refused(string text) =>
        Assert.False(ExecutionConsoleViewModel.TryPrice(text, out _));
}
