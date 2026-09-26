using System.Globalization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;
using TradingTerminal.ExecutionUi;
using TradingTerminal.UI;

namespace TradingTerminal.ExecutionUi.Tests;

/// <summary>
/// The Execution Desk's figures: average-cost accounting over a book's fills (fees charged to the trades that
/// close them), slippage against the price at send, the added performance measures, and a paper book that trades
/// at the terminal's own quote and reports what it did.
/// </summary>
[Collection("Execution client")]
public sealed class ExecutionDeskTests
{
    private static readonly InstrumentId Eur = new(9101);

    private static BookFill Fill(
        OrderSide side,
        decimal units,
        decimal price,
        decimal fee = 0m,
        decimal? arrival = null,
        decimal multiplier = 1m,
        int minute = 0) =>
        new(new DateTime(2026, 9, 1, 10, minute, 0, DateTimeKind.Utc), $"order-{minute}", string.Empty, Eur.Value,
            side, units, price, fee, arrival, multiplier, "Manual");

    private static BookAccounting Replay(params BookFill[] fills) =>
        ExecutionBookAccounting.Replay(fills, _ => "EURUSD");

    [Fact]
    public void A_closing_fill_is_charged_its_own_fee_and_its_share_of_the_fees_that_opened_it()
    {
        var accounting = Replay(
            Fill(OrderSide.Buy, 10m, 100m, fee: 1m, minute: 0),
            Fill(OrderSide.Buy, 10m, 110m, fee: 1m, minute: 1),
            Fill(OrderSide.Sell, 15m, 120m, fee: 1.5m, minute: 2));

        // (120 - 105) × 15 = 225, less 3/4 of the 2.00 paid to open, less the 1.50 to close.
        var trade = Assert.Single(accounting.ClosedTrades);
        Assert.Equal(222m, trade.RealizedProfitAndLoss);
        var position = accounting.For(Eur.Value)!;
        Assert.Equal((5m, 105m, 222m, 3.5m), (position.Units, position.AveragePrice, position.RealizedProfitAndLoss, position.Fees));
    }

    [Fact]
    public void A_fill_through_flat_closes_what_is_held_and_opens_the_rest_the_other_way_at_its_price()
    {
        var accounting = Replay(
            Fill(OrderSide.Buy, 5m, 105m, fee: 0.5m, minute: 0),
            Fill(OrderSide.Sell, 10m, 100m, fee: 1m, minute: 1));

        // Closes 5: (100 - 105) × 5 = -25, less the 0.50 opening fee and half of this fill's 1.00.
        Assert.Equal(-26m, Assert.Single(accounting.ClosedTrades).RealizedProfitAndLoss);
        var position = accounting.For(Eur.Value)!;
        Assert.Equal((-5m, 100m), (position.Units, position.AveragePrice));
        Assert.Equal(-50m, position.UnrealizedProfitAndLoss(110m));
    }

    [Fact]
    public void Slippage_is_a_cost_when_the_fill_is_worse_than_the_price_at_send_whichever_the_side()
    {
        var accounting = Replay(
            Fill(OrderSide.Buy, 1m, 100.10m, arrival: 100m, minute: 0),
            Fill(OrderSide.Sell, 1m, 99.90m, arrival: 100m, minute: 1),
            Fill(OrderSide.Buy, 2m, 99.95m, arrival: 100m, fee: 0.25m, minute: 2));

        var bps = accounting.Fills.Select(outcome => outcome.SlippageBasisPoints!.Value).ToArray();
        Assert.Equal([10d, 10d, -5d], bps.Select(value => Math.Round(value, 6)));
        Assert.Equal(3, accounting.SlippageObservations);
        // 0.10 + 0.10 - 0.10 of price difference, plus the 0.25 fee.
        Assert.Equal(0.35m, accounting.ImplementationShortfall);
        Assert.Equal(0.25m, accounting.Fees);
    }

    [Fact]
    public void The_multiplier_turns_price_points_into_money()
    {
        // One cTrader unit of EURUSD is 1,000 EUR: a 0.01 move on 2 units is 20 in the account currency.
        var accounting = Replay(
            Fill(OrderSide.Buy, 2m, 1.08m, multiplier: 1_000m, minute: 0),
            Fill(OrderSide.Sell, 2m, 1.09m, multiplier: 1_000m, minute: 1));

        Assert.Equal(20m, Assert.Single(accounting.ClosedTrades).RealizedProfitAndLoss);
        Assert.Equal(4_340m, accounting.Turnover);
    }

    [Theory]
    [InlineData("execution-console.book-1.manual", "Manual")]
    [InlineData("execution-console.book-1.ctrader-ticket", "Manual")]
    [InlineData("execution-console.book-1.ctrader-kill", "Kill")]
    [InlineData("execution-console.book-1", "Flatten")]
    [InlineData("EMA Cross", "EMA Cross")]
    public void An_order_says_who_sent_it(string strategyId, string source) =>
        Assert.Equal(source, ExecutionBookAccounting.SourceOf(strategyId));

    [Fact]
    public void Value_at_risk_counts_only_days_since_the_book_was_active_and_waits_for_twenty()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var quiet = Enumerable.Range(0, 30).Select(day => new ExecutionDailyPnlPointReadModel(start.AddDays(day), 0m));
        var active = Enumerable.Range(0, 19).Select(day =>
            new ExecutionDailyPnlPointReadModel(start.AddDays(30 + day), day % 2 == 0 ? 10m : -day));

        var (_, nineteen) = ExecutionMetricMath.HistoricalValueAtRisk95([.. quiet, .. active]);
        Assert.Equal(19, nineteen);

        var twenty = active.Append(new ExecutionDailyPnlPointReadModel(start.AddDays(49), -40m)).ToArray();
        var (valueAtRisk, observations) = ExecutionMetricMath.HistoricalValueAtRisk95([.. quiet, .. twenty]);
        // Nearest rank: the 1st of 20 sorted days is the 5th percentile, the -40 day.
        Assert.Equal((40m, 20), (valueAtRisk, observations));
    }

    [Fact]
    public void Sortino_divides_the_mean_by_downside_deviation_alone()
    {
        double[] returns = [0.02, -0.01, 0.03, -0.01];
        var downside = Math.Sqrt((0.0001 + 0.0001) / 4);

        Assert.Equal(0.0075 / downside * Math.Sqrt(252), ExecutionMetricMath.AnnualizedSortino(returns), 9);
        Assert.Equal(0d, ExecutionMetricMath.AnnualizedSortino([0.01, 0.02]));
    }

    [Fact]
    public void Profit_factor_expectancy_and_average_trades_come_from_closed_trades()
    {
        var asOf = new DateTime(2026, 8, 5, 18, 0, 0, DateTimeKind.Utc);
        var result = ExecutionMetricMath.Calculate(
            1_000m,
            [
                new ExecutionTradeHistoryPoint(asOf.AddDays(-3), "EURUSD", 100m),
                new ExecutionTradeHistoryPoint(asOf.AddDays(-2), "EURUSD", -50m),
                new ExecutionTradeHistoryPoint(asOf.AddDays(-1), "EURUSD", 50m),
            ],
            ExecutionTimeRange.SevenDays,
            asOf,
            openPositions: 0,
            netExposure: 0m).Metrics;

        Assert.Equal((150m, -50m, 1), (result.GrossProfit, result.GrossLoss, result.LosingTrades));
        Assert.Equal("3.00", result.ProfitFactorDisplay);
        Assert.Equal(100m / 3m, result.Expectancy);
        Assert.Equal("$75.00 / -$50.00", result.AverageWinLossDisplay);
        Assert.Equal("payoff 1.50", result.PayoffDisplay);
        Assert.False(result.HasValueAtRisk);
    }

    [Fact]
    public async Task A_paper_book_fills_at_the_live_quote_and_reports_its_fills_position_and_pnl()
    {
        var hub = new Hub();
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore(), marketData: hub);
        var book = await PaperBookAsync(client, hub);

        hub.Push(99.9, 100.1);
        await ExpectAsync(client.SubmitManualOrderAsync(Order(book, ExecutionManualOrderSide.Buy, 3)));
        hub.Push(109.9, 110.1);
        await ExpectAsync(client.SubmitManualOrderAsync(Order(book, ExecutionManualOrderSide.Sell, 1)));

        var read = client.GetSnapshot().Books.Single();
        Assert.Equal(["SELL", "BUY"], read.Fills.Select(fill => fill.Side));
        Assert.Equal(["110", "100"], read.Fills.Select(fill => fill.Price));
        Assert.Equal("+$10.00", read.Fills[0].RealizedProfitAndLoss);
        Assert.All(read.Fills, fill => Assert.Equal(0d, fill.SlippageBasisPoints));

        var position = Assert.Single(read.Positions);
        Assert.Equal(("100", "110", "$220.00"), (position.AveragePrice, position.LastPrice, position.MarketValue));
        Assert.Equal(("+$20.00", "+$10.00"), (position.UnrealizedProfitAndLoss, position.RealizedProfitAndLoss));
        Assert.Equal((2m, 10m, 20m), (read.PositionUnits, read.RealizedProfitAndLoss, read.UnrealizedProfitAndLoss));

        var metrics = read.Analytics.Period(ExecutionTimeRange.ThirtyDays).Metrics;
        Assert.Equal((1, 1, 10m), (metrics.TradeCount, metrics.WinningTrades, metrics.GrossProfit));
        Assert.Equal((2, 2), (read.Analytics.ExecutionQuality.Fills, read.Analytics.ExecutionQuality.SlippageBasisPointObservations));
        Assert.Equal(220m, read.GrossExposure);
    }

    [Fact]
    public async Task A_working_paper_order_can_be_changed_in_place()
    {
        var hub = new Hub();
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore(), marketData: hub);
        var book = await PaperBookAsync(client, hub);
        hub.Push(99.9, 100.1);
        TryPrice("90", out var limit);
        await ExpectAsync(client.SubmitManualOrderAsync(
            new ExecutionManualOrderRequest(book, Eur, "EURUSD", ExecutionManualOrderSide.Buy, ScaledQuantity.FromWhole(1),
                ExecutionManualOrderType.Limit, limit)));
        var working = Assert.Single(client.GetSnapshot().Books.Single().Orders, order => order.IsOpen);
        Assert.Equal(("90", "DAY"), (working.LimitPrice, working.TimeInForce));

        TryPrice("95", out var higher);
        var changed = await client.ReplaceOrderAsync(book, working.ClientOrderId, 2, higher, null);

        Assert.True(changed.IsSuccess, changed.Message);
        var after = Assert.Single(client.GetSnapshot().Books.Single().Orders, order => order.ClientOrderId == working.ClientOrderId);
        Assert.Equal(("95", "2"), (after.LimitPrice, after.Quantity));
    }

    [Fact]
    public async Task The_ticket_states_the_order_and_what_it_will_be_checked_against_before_it_is_sent()
    {
        var hub = new Hub();
        using var client = new InProcessExecutionClient(bookStore: new MemoryStore(), marketData: hub);
        var book = await PaperBookAsync(client, hub);
        hub.Push(99.9, 100.1);
        await ExpectAsync(client.SubmitManualOrderAsync(Order(book, ExecutionManualOrderSide.Buy, 3)));

        var originalTimerFactory = UiThread.CreateRenderTimer;
        UiThread.CreateRenderTimer = (_, _) => new Nothing();
        try
        {
            using var viewModel = new ExecutionConsoleViewModel(client, new AlwaysConfirm(), TestBrokerLoginFormFactory.Alpaca());
            viewModel.SelectedBookEntry = viewModel.BookEntries.Single(entry => !entry.IsAllBooks);
            viewModel.SetTicketSideCommand.Execute("Sell");
            viewModel.TicketQuantity = "2";

            Assert.Equal("Sell 2 units EURUSD · Market", viewModel.TicketSubmitLabel);
            Assert.StartsWith("= 2 · 1 unit = 1 share or contract", viewModel.TicketNativeHint, StringComparison.Ordinal);
            Assert.Equal("+3 → +1 units", viewModel.TicketPositionAfter);
            Assert.NotEqual("-", viewModel.TicketNotional);
            Assert.Contains(viewModel.TicketChecks, check => check.Text == "Intake is on" && check.Tone == ExecutionTone.Positive);
            Assert.DoesNotContain(viewModel.TicketChecks, check => check.Tone == ExecutionTone.Negative);

            Assert.Equal(1, viewModel.FillCount);
            Assert.Equal("$0.00", viewModel.Desk.OpenProfitAndLoss);
            Assert.StartsWith("1 book", viewModel.Desk.BooksLine, StringComparison.Ordinal);
        }
        finally
        {
            UiThread.CreateRenderTimer = originalTimerFactory;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<string> PaperBookAsync(InProcessExecutionClient client, Hub hub)
    {
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest("Desk EUR", "paper", [], Eur, "EURUSD"));
        Assert.True(created.IsSuccess, created.Message);
        Assert.True(hub.HasSubscriber, "A new book should start watching its instrument's quotes.");
        var book = client.GetSnapshot().Books.Single().Id;
        await ExpectAsync(client.ReconcileAsync(book));
        return book;
    }

    private static ExecutionManualOrderRequest Order(string book, ExecutionManualOrderSide side, long units) =>
        new(book, Eur, "EURUSD", side, ScaledQuantity.FromWhole(units), ExecutionManualOrderType.Market, null);

    private static async Task ExpectAsync(ValueTask<ExecutionCommandResult> command)
    {
        var result = await command;
        Assert.True(result.IsSuccess, result.Message);
    }

    private static void TryPrice(string text, out ScaledPrice? price) =>
        Assert.True(ExecutionConsoleViewModel.TryPrice(text, out price));

    private sealed class Hub : IMarketDataHub
    {
        private readonly List<IObserver<Quote>> _observers = [];

        public bool HasSubscriber => _observers.Count > 0;

        public void Push(double bid, double ask)
        {
            var quote = new Quote(Eur, DateTime.UtcNow, DateTime.UtcNow, bid, ask, 1, 1, BrokerKind.Simulated, 1, false);
            foreach (var observer in _observers.ToArray())
                observer.OnNext(quote);
        }

        public IObservable<Quote> Quotes(InstrumentId instrumentId) => new Feed(this);

        public IObservable<TradePrint> Trades(InstrumentId instrumentId) => throw new NotSupportedException();

        public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) => throw new NotSupportedException();

        public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) => throw new NotSupportedException();

        public void PublishQuote(Quote quote)
        {
        }

        public void PublishTrade(TradePrint trade)
        {
        }

        public void PublishBar(OhlcvBar bar)
        {
        }

        public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot)
        {
        }

        private sealed class Feed(Hub hub) : IObservable<Quote>
        {
            public IDisposable Subscribe(IObserver<Quote> observer)
            {
                hub._observers.Add(observer);
                return new Unsubscribe(() => hub._observers.Remove(observer));
            }
        }

        private sealed class Unsubscribe(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    private sealed class MemoryStore : IExecutionBookStore
    {
        private IReadOnlyList<PersistedExecutionBook> _saved = [];

        public IReadOnlyList<PersistedExecutionBook> Read() => _saved;

        public void Save(IReadOnlyList<PersistedExecutionBook> books) => _saved = [.. books];
    }

    private sealed class AlwaysConfirm : IExecutionConfirmationService
    {
        public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<ExecutionTypedConfirmationResult> ConfirmTypedAsync(
            string title,
            string message,
            string requiredText,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionTypedConfirmationResult.Cancelled);
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
