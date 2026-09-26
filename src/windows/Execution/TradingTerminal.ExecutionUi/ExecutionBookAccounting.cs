using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Oms;
using OrderEvent = TradingTerminal.Execution.Oms.OrderEvent;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// One fill a book received, in the book's own units, beside the price the order was sent against.
/// <paramref name="Multiplier"/> is the account-currency value of one unit moving one price point (1 for a
/// share, the volume step for a currency pair, the contract multiplier for a future).
/// </summary>
internal readonly record struct BookFill(
    DateTime TimeUtc,
    string ClientOrderId,
    string BrokerOrderId,
    int Instrument,
    OrderSide Side,
    decimal Units,
    decimal Price,
    decimal Fee,
    decimal? ArrivalPrice,
    decimal Multiplier,
    string Source)
{
    public decimal SignedUnits => Side == OrderSide.Buy ? Units : -Units;
}

/// <summary>What a fill did: the P&amp;L it realized (zero when it opened or added) and what it cost against
/// the price at send, in basis points — positive is a cost, negative an improvement.</summary>
internal readonly record struct BookFillOutcome(BookFill Fill, decimal RealizedProfitAndLoss, double? SlippageBasisPoints);

/// <summary>One instrument's position as the fills built it: average cost, P&amp;L realized so far after fees.</summary>
internal sealed record BookInstrumentAccount(
    int Instrument,
    decimal Units,
    decimal AveragePrice,
    decimal RealizedProfitAndLoss,
    decimal Fees,
    decimal Multiplier)
{
    public decimal UnrealizedProfitAndLoss(decimal? mark) =>
        mark is > 0m && Units != 0m ? (mark.Value - AveragePrice) * Units * Multiplier : 0m;

    public decimal MarketValue(decimal? mark) => mark is > 0m ? Units * mark.Value * Multiplier : 0m;
}

/// <summary>A book's fills replayed: positions, the closed trades analytics are built from, and the cost of
/// execution.</summary>
internal sealed record BookAccounting(
    IReadOnlyDictionary<int, BookInstrumentAccount> Instruments,
    IReadOnlyList<ExecutionTradeHistoryPoint> ClosedTrades,
    IReadOnlyList<BookFillOutcome> Fills,
    int SlippageObservations,
    double TotalSlippageBasisPoints,
    decimal ImplementationShortfall,
    decimal Fees,
    decimal Turnover)
{
    public static readonly BookAccounting Empty = new(
        new Dictionary<int, BookInstrumentAccount>(),
        Array.Empty<ExecutionTradeHistoryPoint>(),
        Array.Empty<BookFillOutcome>(),
        0,
        0d,
        0m,
        0m,
        0m);

    public decimal RealizedProfitAndLoss => Instruments.Values.Sum(item => item.RealizedProfitAndLoss);

    public BookInstrumentAccount? For(int instrument) =>
        Instruments.TryGetValue(instrument, out var account) ? account : null;
}

/// <summary>
/// Average-cost accounting over a book's fills, and the fills themselves read out of the OMS ledger.
///
/// <para>The OMS records every fill exactly (quantity, price, fee) and, on the order, the price its risk check
/// used — the price at send. That is everything a desk reads off a blotter: position and average cost, the P&amp;L
/// each closing fill realized, and what execution cost against the decision. Nothing here is estimated: a fill with
/// no recorded price at send simply has no slippage.</para>
///
/// <para>Fees are charged where they belong: a closing fill carries its own fee plus its share of the fees paid to
/// open what it closes, so a round trip's realized P&amp;L is net of both legs and a profit factor or win rate
/// computed from closed trades is after costs.</para>
/// </summary>
internal static class ExecutionBookAccounting
{
    /// <summary>The fills in <paramref name="events"/> (ledger order), each joined to its order's side, price at
    /// send and multiplier. Events whose order is not in <paramref name="projections"/> are skipped.</summary>
    internal static IReadOnlyList<BookFill> FillsFrom(
        IEnumerable<OrderEvent> events,
        IEnumerable<OrderProjection> projections,
        decimal defaultMultiplier = 1m)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(projections);
        var orders = new Dictionary<string, OrderProjection>(StringComparer.Ordinal);
        foreach (var projection in projections)
            orders[projection.ClientOrderId.Value] = projection;

        var fills = new List<BookFill>();
        foreach (var item in events)
        {
            if (item.Kind != OrderEventKind.FillReceived ||
                item.Fill is not { } fill ||
                !orders.TryGetValue(item.AggregateId.Value, out var order))
            {
                continue;
            }

            var units = ToDecimal(fill.Quantity.Coefficient, fill.Quantity.Scale);
            var price = ToDecimal(fill.Price.Coefficient, fill.Price.Scale);
            if (units <= 0m || price <= 0m)
                continue;

            decimal? arrival = null;
            var multiplier = defaultMultiplier;
            if (order.RiskDecision is { } decision)
            {
                if (decision.Input.ReferencePrice is { IsValid: true, Coefficient: > 0 } reference)
                    arrival = ToDecimal(reference.Coefficient, reference.Scale);
                if (decision.Input.ContractMultiplier is { Coefficient: > 0 } ratio)
                    multiplier = ToDecimal(ratio.Coefficient, ratio.Scale);
            }

            fills.Add(new BookFill(
                item.OccurredAtUtc,
                order.ClientOrderId.Value,
                (item.BrokerOrderId ?? order.BrokerOrderId)?.Value ?? string.Empty,
                order.Instruction.TradeIntent.Instrument.Value,
                order.Terms.Side,
                units,
                price,
                fill.Fee.IsValid ? ToDecimal(fill.Fee.Coefficient, fill.Fee.Scale) : 0m,
                arrival,
                multiplier,
                SourceOf(order.Instruction.TradeIntent.StrategyId)));
        }

        return fills;
    }

    /// <summary>Replays <paramref name="fills"/> in order. <paramref name="symbol"/> names an instrument for the
    /// closed-trade list.</summary>
    internal static BookAccounting Replay(IReadOnlyList<BookFill> fills, Func<int, string> symbol)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(symbol);
        if (fills.Count == 0)
            return BookAccounting.Empty;

        var states = new Dictionary<int, State>();
        var closed = new List<ExecutionTradeHistoryPoint>();
        var outcomes = new List<BookFillOutcome>(fills.Count);
        var slippageCount = 0;
        var slippageTotal = 0d;
        var shortfall = 0m;
        var fees = 0m;
        var turnover = 0m;

        foreach (var fill in fills)
        {
            if (!states.TryGetValue(fill.Instrument, out var state))
                states[fill.Instrument] = state = new State();

            var realized = state.Apply(fill);
            if (realized is { } closedPnl)
                closed.Add(new ExecutionTradeHistoryPoint(fill.TimeUtc, symbol(fill.Instrument), closedPnl));

            double? slippage = null;
            if (fill.ArrivalPrice is { } arrival && arrival > 0m)
            {
                var direction = fill.Side == OrderSide.Buy ? 1m : -1m;
                slippage = (double)(direction * (fill.Price - arrival) / arrival * 10_000m);
                slippageCount++;
                slippageTotal += slippage.Value;
                shortfall += direction * (fill.Price - arrival) * fill.Units * fill.Multiplier;
            }

            shortfall += fill.Fee;
            fees += fill.Fee;
            turnover += fill.Units * fill.Price * fill.Multiplier;
            outcomes.Add(new BookFillOutcome(fill, realized ?? 0m, slippage));
        }

        var accounts = states.ToDictionary(
            pair => pair.Key,
            pair => new BookInstrumentAccount(
                pair.Key,
                pair.Value.Units,
                pair.Value.Units == 0m ? 0m : pair.Value.AveragePrice,
                pair.Value.Realized,
                pair.Value.Fees,
                pair.Value.Multiplier));
        return new BookAccounting(
            accounts,
            Array.AsReadOnly(closed.ToArray()),
            Array.AsReadOnly(outcomes.ToArray()),
            slippageCount,
            slippageTotal,
            shortfall,
            fees,
            turnover);
    }

    /// <summary>Who sent an order, from its intent's strategy id: the console's own ids read as Manual, Kill or
    /// Flatten; anything else is the strategy that sent it.</summary>
    internal static string SourceOf(string? strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
            return "Unknown";
        if (!strategyId.StartsWith("execution-console.", StringComparison.Ordinal))
            return strategyId;
        if (strategyId.EndsWith(".manual", StringComparison.Ordinal) || strategyId.EndsWith("-ticket", StringComparison.Ordinal))
            return "Manual";
        return strategyId.EndsWith("-kill", StringComparison.Ordinal) ? "Kill" : "Flatten";
    }

    // ── Read-model projection, shared by the paper and broker book runtimes ─────────────────────────

    /// <summary>The blotter, newest first, at most <paramref name="capacity"/> rows.</summary>
    internal static IReadOnlyList<ExecutionFillReadModel> FillRows(
        BookAccounting accounting,
        string bookName,
        Func<int, string> symbol,
        ExecutionBookUnitReadModel unit,
        int capacity)
    {
        var rows = new List<ExecutionFillReadModel>(Math.Min(capacity, accounting.Fills.Count));
        for (var index = accounting.Fills.Count - 1; index >= 0 && rows.Count < capacity; index--)
        {
            var outcome = accounting.Fills[index];
            var fill = outcome.Fill;
            var buy = fill.Side == OrderSide.Buy;
            var closed = outcome.RealizedProfitAndLoss != 0m;
            rows.Add(new ExecutionFillReadModel(
                fill.TimeUtc,
                bookName,
                fill.ClientOrderId,
                fill.BrokerOrderId.Length == 0 ? "-" : fill.BrokerOrderId,
                symbol(fill.Instrument),
                buy ? "BUY" : "SELL",
                buy ? ExecutionTone.Positive : ExecutionTone.Negative,
                ExecutionFormatting.Units(fill.Units),
                unit.Native(fill.Units),
                ExecutionFormatting.Price(fill.Price),
                fill.ArrivalPrice is { } arrival ? ExecutionFormatting.Price(arrival) : "-",
                outcome.SlippageBasisPoints,
                ExecutionFormatting.Money(fill.Fee),
                closed ? ExecutionFormatting.SignedMoney(outcome.RealizedProfitAndLoss) : "-",
                ExecutionFormatting.ToneOf(outcome.RealizedProfitAndLoss),
                fill.Source));
        }

        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The engine's order counts with what the fills say about cost.</summary>
    internal static ExecutionQualityReadModel WithFills(ExecutionQualityReadModel quality, BookAccounting accounting) =>
        quality with
        {
            Fills = accounting.Fills.Count,
            SlippageBasisPointObservations = accounting.SlippageObservations,
            TotalSlippageBasisPoints = accounting.TotalSlippageBasisPoints,
            Fees = accounting.Fees,
            ImplementationShortfall = accounting.ImplementationShortfall,
            Turnover = accounting.Turnover,
        };

    /// <summary>A position row with its average cost, mark, P&amp;L and weight filled in from the fills.</summary>
    internal static ExecutionPositionReadModel Priced(
        ExecutionPositionReadModel row,
        BookInstrumentAccount? account,
        decimal units,
        decimal? mark,
        ExecutionBookUnitReadModel unit,
        decimal netAssetValue)
    {
        var unrealized = account?.UnrealizedProfitAndLoss(mark) ?? 0m;
        var realized = account?.RealizedProfitAndLoss ?? 0m;
        var multiplier = account?.Multiplier ?? unit.ValuePerPoint;
        decimal? marketValue = mark is > 0m ? units * mark.Value * multiplier : null;
        return row with
        {
            AveragePrice = units != 0m && account is { AveragePrice: > 0m } held ? ExecutionFormatting.Price(held.AveragePrice) : "-",
            LastPrice = mark is > 0m ? ExecutionFormatting.Price(mark.Value) : "-",
            UnrealizedProfitAndLoss = units == 0m ? "$0.00" : ExecutionFormatting.SignedMoney(unrealized),
            RealizedProfitAndLoss = ExecutionFormatting.SignedMoney(realized),
            ProfitAndLossTone = ExecutionFormatting.ToneOf(unrealized + realized),
            NativeQuantity = unit.Native(units),
            MarketValue = marketValue is { } value ? ExecutionFormatting.Money(value) : "-",
            PercentOfNav = marketValue is { } weighed && netAssetValue > 0m
                ? $"{weighed / netAssetValue * 100m:0.0}%"
                : "-",
            UnrealizedTone = ExecutionFormatting.ToneOf(unrealized),
            RealizedTone = ExecutionFormatting.ToneOf(realized),
        };
    }

    /// <summary>An order row with its terms, fill progress, sender and placement time.</summary>
    internal static ExecutionOrderReadModel Described(
        ExecutionOrderReadModel row,
        OrderProjection projection,
        DateTime placedAtUtc)
    {
        var terms = projection.ReplacementTerms ?? projection.Terms;
        return row with
        {
            TimeInForce = terms.TimeInForce.ToString().ToUpperInvariant() switch
            {
                "GOODTILLCANCELLED" or "GOODTILLCANCELED" => "GTC",
                "IMMEDIATEORCANCEL" => "IOC",
                "FILLORKILL" => "FOK",
                var other => other,
            },
            LimitPrice = terms.LimitPrice is { } limit ? ExecutionFormatting.Price(ToDecimal(limit.Coefficient, limit.Scale)) : "-",
            StopPrice = terms.StopPrice is { } stop ? ExecutionFormatting.Price(ToDecimal(stop.Coefficient, stop.Scale)) : "-",
            Filled = ExecutionFormatting.Units(ToDecimal(projection.FilledQuantity.Coefficient, projection.FilledQuantity.Scale)),
            Source = SourceOf(projection.Instruction.TradeIntent.StrategyId),
            PlacedAtUtc = placedAtUtc,
        };
    }

    internal static decimal ToDecimal(long coefficient, byte scale)
    {
        var value = (decimal)coefficient;
        for (var i = 0; i < scale; i++)
            value /= 10m;
        return value;
    }

    private sealed class State
    {
        public decimal Units;
        public decimal AveragePrice;
        public decimal Realized;
        public decimal Fees;
        public decimal Multiplier = 1m;

        /// <summary>Fees paid to open the units still held, charged to whatever later closes them.</summary>
        private decimal _openFees;

        /// <summary>Applies one fill; returns the P&amp;L it realized when it closed anything.</summary>
        public decimal? Apply(BookFill fill)
        {
            Multiplier = fill.Multiplier;
            Fees += fill.Fee;
            var signed = fill.SignedUnits;
            if (Units == 0m || Math.Sign(Units) == Math.Sign(signed))
            {
                var held = Math.Abs(Units);
                AveragePrice = (AveragePrice * held + fill.Price * fill.Units) / (held + fill.Units);
                Units += signed;
                _openFees += fill.Fee;
                return null;
            }

            var closing = Math.Min(fill.Units, Math.Abs(Units));
            var direction = Math.Sign(Units);
            var share = closing / Math.Abs(Units);
            var openFees = _openFees * share;
            var closeFee = fill.Fee * (closing / fill.Units);
            var pnl = (fill.Price - AveragePrice) * closing * fill.Multiplier * direction - openFees - closeFee;
            _openFees -= openFees;
            Realized += pnl;
            Units += Math.Sign(signed) * closing;

            var remaining = fill.Units - closing;
            if (remaining > 0m)
            {
                // Flipped through flat: what is left opens the other way at this fill's price.
                Units = Math.Sign(signed) * remaining;
                AveragePrice = fill.Price;
                _openFees = fill.Fee - closeFee;
            }
            else if (Units == 0m)
            {
                AveragePrice = 0m;
                _openFees = 0m;
            }

            return pnl;
        }
    }
}
