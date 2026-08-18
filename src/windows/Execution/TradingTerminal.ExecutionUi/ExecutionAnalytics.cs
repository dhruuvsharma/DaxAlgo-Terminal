namespace TradingTerminal.ExecutionUi;

/// <summary>
/// Pure metric math over an equity/realized-P&amp;L history supplied by the execution read-model
/// provider. The OMS intentionally does not define account lot matching or realized P&amp;L; the
/// default in-process client therefore supplies an explicitly labelled representative history.
/// </summary>
public static class ExecutionMetricMath
{
    private const int MaximumChartPoints = 370;

    public static ExecutionPeriodAnalyticsReadModel Calculate(
        decimal openingEquity,
        IReadOnlyList<ExecutionTradeHistoryPoint> tradeHistory,
        ExecutionTimeRange range,
        DateTime asOfUtc,
        int openPositions,
        decimal netExposure)
    {
        ArgumentNullException.ThrowIfNull(tradeHistory);
        if (openingEquity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(openingEquity), "Opening equity must be positive.");

        asOfUtc = EnsureUtc(asOfUtc);
        var start = RangeStart(range, asOfUtc);
        var history = tradeHistory
            .Where(item => EnsureUtc(item.ClosedAtUtc) <= asOfUtc)
            .OrderBy(item => item.ClosedAtUtc)
            .ToArray();
        var equityAtStart = openingEquity + history
            .Where(item => EnsureUtc(item.ClosedAtUtc).Date < start)
            .Sum(item => item.RealizedProfitAndLoss);
        var periodTrades = history
            .Where(item => EnsureUtc(item.ClosedAtUtc).Date >= start)
            .ToArray();
        var daily = periodTrades
            .GroupBy(item => EnsureUtc(item.ClosedAtUtc).Date)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.RealizedProfitAndLoss));

        var dailySeries = new List<ExecutionDailyPnlPointReadModel>();
        for (var date = start; date <= asOfUtc.Date; date = date.AddDays(1))
        {
            daily.TryGetValue(date, out var profitAndLoss);
            dailySeries.Add(new ExecutionDailyPnlPointReadModel(date, profitAndLoss));
        }

        if (dailySeries.Count > MaximumChartPoints)
            dailySeries = dailySeries[^MaximumChartPoints..];

        return CalculateFromDaily(
            range,
            equityAtStart,
            dailySeries,
            periodTrades.Length,
            periodTrades.Count(item => item.RealizedProfitAndLoss > 0m),
            openPositions,
            netExposure);
    }

    internal static ExecutionPeriodAnalyticsReadModel CalculateFromDaily(
        ExecutionTimeRange range,
        decimal equityAtStart,
        IReadOnlyList<ExecutionDailyPnlPointReadModel> dailySeries,
        int tradeCount,
        int winningTrades,
        int openPositions,
        decimal netExposure)
    {
        var rollingEquity = equityAtStart;
        var peak = equityAtStart;
        var maximumDrawdown = 0m;
        var returns = new List<double>(dailySeries.Count);
        var equitySeries = new List<ExecutionEquityPointReadModel>(dailySeries.Count);

        foreach (var point in dailySeries)
        {
            var priorEquity = rollingEquity;
            rollingEquity += point.RealizedProfitAndLoss;
            if (priorEquity != 0m)
                returns.Add((double)(point.RealizedProfitAndLoss / priorEquity));
            if (rollingEquity > peak)
                peak = rollingEquity;
            if (peak > 0m)
            {
                var drawdown = (rollingEquity - peak) / peak * 100m;
                if (drawdown < maximumDrawdown)
                    maximumDrawdown = drawdown;
            }
            equitySeries.Add(new ExecutionEquityPointReadModel(point.DateUtc, rollingEquity));
        }

        var netProfitAndLoss = dailySeries.Sum(item => item.RealizedProfitAndLoss);
        var returnPercent = equityAtStart == 0m ? 0m : netProfitAndLoss / equityAtStart * 100m;
        var winRate = tradeCount == 0 ? 0m : winningTrades * 100m / tradeCount;
        var metrics = new ExecutionMetricResult(
            rollingEquity,
            netProfitAndLoss,
            returnPercent,
            AnnualizedSharpe(returns),
            maximumDrawdown,
            winRate,
            openPositions,
            netExposure,
            tradeCount,
            winningTrades);

        return new ExecutionPeriodAnalyticsReadModel(
            range,
            RangeLabel(range),
            metrics,
            Array.AsReadOnly(equitySeries.ToArray()),
            Array.AsReadOnly(dailySeries.ToArray()));
    }

    public static double AnnualizedSharpe(IReadOnlyList<double> periodicReturns)
    {
        ArgumentNullException.ThrowIfNull(periodicReturns);
        if (periodicReturns.Count < 2)
            return 0d;

        var mean = periodicReturns.Average();
        var sumSquaredDeviation = periodicReturns.Sum(value =>
        {
            var delta = value - mean;
            return delta * delta;
        });
        var sampleDeviation = Math.Sqrt(sumSquaredDeviation / (periodicReturns.Count - 1));
        return sampleDeviation <= 1e-12 ? 0d : mean / sampleDeviation * Math.Sqrt(252d);
    }

    public static decimal MaximumDrawdownPercent(IReadOnlyList<decimal> equitySeries)
    {
        ArgumentNullException.ThrowIfNull(equitySeries);
        if (equitySeries.Count == 0)
            return 0m;

        var peak = equitySeries[0];
        var maximumDrawdown = 0m;
        foreach (var equity in equitySeries)
        {
            if (equity > peak)
                peak = equity;
            if (peak <= 0m)
                continue;
            var drawdown = (equity - peak) / peak * 100m;
            if (drawdown < maximumDrawdown)
                maximumDrawdown = drawdown;
        }
        return maximumDrawdown;
    }

    internal static DateTime RangeStart(ExecutionTimeRange range, DateTime asOfUtc) => range switch
    {
        ExecutionTimeRange.SevenDays => asOfUtc.Date.AddDays(-6),
        ExecutionTimeRange.ThirtyDays => asOfUtc.Date.AddDays(-29),
        ExecutionTimeRange.NinetyDays => asOfUtc.Date.AddDays(-89),
        ExecutionTimeRange.YearToDate => new DateTime(asOfUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    internal static string RangeLabel(ExecutionTimeRange range) => range switch
    {
        ExecutionTimeRange.SevenDays => "7D",
        ExecutionTimeRange.ThirtyDays => "30D",
        ExecutionTimeRange.NinetyDays => "90D",
        ExecutionTimeRange.YearToDate => "YTD",
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

internal static class ExecutionAnalyticsProjector
{
    private static readonly ExecutionTimeRange[] Ranges =
    [
        ExecutionTimeRange.SevenDays,
        ExecutionTimeRange.ThirtyDays,
        ExecutionTimeRange.NinetyDays,
        ExecutionTimeRange.YearToDate,
    ];

    internal static ExecutionPortfolioAnalyticsReadModel BuildBook(
        string bookId,
        string bookName,
        decimal openingEquity,
        IReadOnlyList<ExecutionTradeHistoryPoint> tradeHistory,
        int openPositions,
        decimal longExposure,
        decimal shortExposure,
        ExecutionQualityReadModel executionQuality,
        DateTime asOfUtc)
    {
        var netExposure = longExposure + shortExposure;
        var periods = Ranges
            .Select(range => ExecutionMetricMath.Calculate(
                openingEquity,
                tradeHistory,
                range,
                asOfUtc,
                openPositions,
                netExposure))
            .ToArray();
        var exposures = NormalizeExposures(
        [
            new ExecutionExposureReadModel(
                bookId,
                bookName,
                longExposure,
                shortExposure,
                netExposure,
                0d,
                0d),
        ]);
        return new ExecutionPortfolioAnalyticsReadModel(
            Array.AsReadOnly(periods),
            exposures,
            executionQuality);
    }

    /// <summary>
    /// Analytics for a portfolio with no books: one zeroed period per range, not an empty list.
    ///
    /// <para>Callers ask for a specific range by name, so a portfolio that simply has nothing to
    /// report still has to answer for every range. Returning an empty <c>Periods</c> made
    /// <c>Period(range)</c> throw "Sequence contains no matching element", which took the Execution
    /// Console's view-model constructor down and left the window silently unopened. That could not
    /// happen while demo books were seeded, because the count was never zero.</para>
    /// </summary>
    private static readonly ExecutionPortfolioAnalyticsReadModel EmptyPortfolio = new(
        Array.AsReadOnly(Ranges
            .Select(range => ExecutionMetricMath.CalculateFromDaily(
                range,
                equityAtStart: 0m,
                dailySeries: Array.Empty<ExecutionDailyPnlPointReadModel>(),
                tradeCount: 0,
                winningTrades: 0,
                openPositions: 0,
                netExposure: 0m))
            .ToArray()),
        Array.Empty<ExecutionExposureReadModel>(),
        new ExecutionQualityReadModel(0, 0, 0, 0, 0, 0, 0, 0d, 0, 0d));

    internal static ExecutionPortfolioAnalyticsReadModel Aggregate(
        IReadOnlyList<ExecutionBookReadModel> books)
    {
        if (books.Count == 0)
            return EmptyPortfolio;

        var periods = new List<ExecutionPeriodAnalyticsReadModel>(Ranges.Length);
        foreach (var range in Ranges)
        {
            var inputs = books
                .Select(book => book.Analytics.Period(range))
                .ToArray();
            var combinedDaily = inputs
                .SelectMany(item => item.DailyProfitAndLossSeries)
                .GroupBy(item => item.DateUtc)
                .OrderBy(group => group.Key)
                .Select(group => new ExecutionDailyPnlPointReadModel(
                    group.Key,
                    group.Sum(item => item.RealizedProfitAndLoss)))
                .ToArray();
            var openingEquity = inputs.Sum(item => item.Metrics.Equity - item.Metrics.NetProfitAndLoss);
            periods.Add(ExecutionMetricMath.CalculateFromDaily(
                range,
                openingEquity,
                combinedDaily,
                inputs.Sum(item => item.Metrics.TradeCount),
                inputs.Sum(item => item.Metrics.WinningTrades),
                inputs.Sum(item => item.Metrics.OpenPositions),
                inputs.Sum(item => item.Metrics.NetExposure)));
        }

        var exposures = NormalizeExposures(books
            .SelectMany(book => book.Analytics.ExposureByBook)
            .ToArray());
        var quality = AggregateQuality(books.Select(book => book.Analytics.ExecutionQuality));
        return new ExecutionPortfolioAnalyticsReadModel(
            Array.AsReadOnly(periods.ToArray()),
            exposures,
            quality);
    }

    internal static ExecutionQualityReadModel AggregateQuality(IEnumerable<ExecutionQualityReadModel> items)
    {
        var values = items.ToArray();
        return new ExecutionQualityReadModel(
            values.Sum(item => item.Orders),
            values.Sum(item => item.FilledOrders),
            values.Sum(item => item.Rejects),
            values.Sum(item => item.Cancels),
            values.Sum(item => item.ReconciliationCases),
            values.Sum(item => item.UnknownOutcomes),
            values.Sum(item => item.SlippageObservationCount),
            values.Sum(item => item.TotalSlippageTicks),
            values.Sum(item => item.AcknowledgementObservationCount),
            values.Sum(item => item.TotalAcknowledgementLatencyMilliseconds));
    }

    private static IReadOnlyList<ExecutionExposureReadModel> NormalizeExposures(
        IReadOnlyList<ExecutionExposureReadModel> items)
    {
        if (items.Count == 0)
            return Array.Empty<ExecutionExposureReadModel>();
        var maximum = items.Max(item => Math.Max(Math.Abs(item.LongExposure), Math.Abs(item.ShortExposure)));
        var normalized = items
            .Select(item => item with
            {
                LongPercentage = maximum == 0m ? 0d : (double)(Math.Abs(item.LongExposure) / maximum * 100m),
                ShortPercentage = maximum == 0m ? 0d : (double)(Math.Abs(item.ShortExposure) / maximum * 100m),
            })
            .ToArray();
        return Array.AsReadOnly(normalized);
    }
}
