using System.Diagnostics;
using TradingTerminal.Backtest.Engine.Accounting;
using TradingTerminal.Backtest.Engine.Cost;
using TradingTerminal.Backtest.Engine.Execution;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Stats;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine;

/// <summary>
/// Drives one backtest end-to-end. Single-threaded by design: there is one logical timeline and all
/// state transitions stay on the caller's task — concurrency belongs at the sweep layer (run N
/// engines in parallel), never inside one run.
///
/// Per event it advances the clock, lets the order book fill resting orders (routing fills to the
/// portfolio and queuing the kernel's order callback), marks open positions, dispatches the typed
/// kernel callback, and samples equity at most once per minute of simulated time. After the last
/// event it flattens accounting and hands the equity timeline + ledger to <see cref="ReportBuilder"/>.
/// </summary>
public sealed class BacktestEngine
{
    private readonly IMarketDataFeed _feed;

    public BacktestEngine(IMarketDataFeed feed) => _feed = feed;

    public async Task<BacktestReport> RunAsync(RunSpec spec, IStrategyKernel kernel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        Exception? runFailure = null;
        try
        {
            ArgumentNullException.ThrowIfNull(spec);
            ValidateSupportedOptions(spec);
            return await RunCoreAsync(spec, kernel, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                await DisposeKernelAsync(kernel).ConfigureAwait(false);
            }
            catch when (runFailure is not null)
            {
                // Preserve the run/cancellation failure. Cleanup is best-effort on an already-failed run.
            }
        }
    }

    private async Task<BacktestReport> RunCoreAsync(RunSpec spec, IStrategyKernel kernel, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var tickSizeOf = spec.Universe.Instruments.ToDictionary(i => i.Id, i => i.TickSize);
        var multipliers = spec.Universe.Instruments.ToDictionary(i => i.Id, i => i.ContractMultiplier);

        var clock = new SimClock();
        var fees = FeeModels.From(spec.CostOrDefault);
        var fillModel = new L1TouchFillModel(spec.ExecutionOrDefault.SlippageTicks);
        var book = new SimulatedOrderBook(clock, fillModel, id => tickSizeOf.GetValueOrDefault(id, 0.01));
        var portfolio = new Portfolio(spec.StartingCash, multipliers, fees);

        // Book transitions are synchronous. Settle and enqueue them before the router republishes
        // them to IObservable subscribers, preserving causality even if a subscriber submits again.
        var orderEvents = new Queue<OrderEvent>();
        long orderTransitionCount = 0;
        book.Event += (instrument, evt) =>
        {
            if (evt.LastFillQuantity > 0 && evt.LastFillPrice is { } px)
                portfolio.OnFill(instrument, evt.TimestampUtc, evt.Side, evt.LastFillQuantity, px, evt.Liquidity);

            orderEvents.Enqueue(evt);
            orderTransitionCount++;
        };

        var router = new EngineOrderRouter(book, spec.Universe);
        var ctx = new StrategyContext(clock, router, new PortfolioView(portfolio), spec.Universe, spec.ParametersOrEmpty);

        var equity = new List<EquitySample>();
        var lastQuotes = new Dictionary<InstrumentId, Tick>();
        var visual = spec.Visual == VisualRecording.On
            ? new VisualRecorder(spec.Universe.Primary.Id, TimeSpan.FromMinutes(1))
            : null;
        DateTime? firstUtc = null, lastUtc = null, lastSample = null;
        double peak = spec.StartingCash;
        long eventsProcessed = 0;

        async Task DrainOrderEventsAsync()
        {
            while (orderEvents.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var evt = orderEvents.Dequeue();
                await kernel.OnOrderEventAsync(evt, ctx, ct).ConfigureAwait(false);
            }
        }

        await kernel.OnStartAsync(ctx, ct).ConfigureAwait(false);
        await DrainOrderEventsAsync().ConfigureAwait(false);

        await foreach (var ev in _feed.StreamAsync(spec, ct).ConfigureAwait(false))
        {
            clock.SetTo(ev.TimestampUtc);
            firstUtc ??= ev.TimestampUtc;
            lastUtc = ev.TimestampUtc;

            switch (ev.Kind)
            {
                case MarketEventKind.Quote:
                {
                    var tick = ev.Quote!;
                    lastQuotes[ev.Instrument] = tick;
                    book.OnQuote(ev.Instrument, tick);                 // may fill → portfolio + queued callback
                    var mid = (tick.Bid + tick.Ask) * 0.5;
                    portfolio.OnMark(ev.Instrument, mid);
                    visual?.OnMid(ev.Instrument, ev.TimestampUtc, mid);

                    // Fills caused by this quote are visible before the strategy sees the quote.
                    await DrainOrderEventsAsync().ConfigureAwait(false);
                    await kernel.OnQuoteAsync(ev.Instrument, tick, ctx, ct).ConfigureAwait(false);

                    var eq = portfolio.Equity();
                    if (eq > peak) peak = eq;
                    if (lastSample is null || (ev.TimestampUtc - lastSample.Value).TotalSeconds >= 60)
                    {
                        equity.Add(new EquitySample(ev.TimestampUtc, eq, portfolio.Cash, peak > 0 ? (peak - eq) / peak : 0));
                        lastSample = ev.TimestampUtc;
                    }
                    break;
                }
                case MarketEventKind.Trade:
                    await kernel.OnTradeAsync(ev.Instrument, ev.Trade!, ctx, ct).ConfigureAwait(false);
                    break;
                case MarketEventKind.Depth:
                    await kernel.OnDepthAsync(ev.Instrument, ev.Depth!, ctx, ct).ConfigureAwait(false);
                    break;
                case MarketEventKind.Bar:
                    await kernel.OnBarAsync(ev.Instrument, ev.Bar!, ctx, ct).ConfigureAwait(false);
                    break;
            }

            // Fully drain transitions produced by the market callback. The FIFO loop also consumes
            // transitions produced re-entrantly from OnOrderEventAsync itself.
            await DrainOrderEventsAsync().ConfigureAwait(false);
            eventsProcessed++;
        }

        await kernel.OnEndAsync(ctx, ct).ConfigureAwait(false);
        await DrainOrderEventsAsync().ConfigureAwait(false);

        // OnEnd commonly emits market orders to flatten exposure. There is no next quote, so replay
        // each instrument's last known touch at the end timestamp until no further transition is
        // produced. This also drains orders emitted from final fill callbacks in strict FIFO order.
        while (lastQuotes.Count > 0)
        {
            var transitionsBefore = orderTransitionCount;
            foreach (var instrument in spec.Universe.Instruments)
            {
                if (lastQuotes.TryGetValue(instrument.Id, out var quote))
                    book.OnQuote(instrument.Id, quote with { TimestampUtc = clock.UtcNow });
            }

            if (orderTransitionCount == transitionsBefore)
                break;

            await DrainOrderEventsAsync().ConfigureAwait(false);
        }

        var finalEquity = portfolio.Equity();
        if (lastUtc is { } endUtc)
        {
            if (finalEquity > peak) peak = finalEquity;
            equity.Add(new EquitySample(endUtc, finalEquity, portfolio.Cash, peak > 0 ? (peak - finalEquity) / peak : 0));
        }

        sw.Stop();
        var summary = new RunSummary(
            StartUtc: firstUtc ?? DateTime.UnixEpoch,
            EndUtc: lastUtc ?? DateTime.UnixEpoch,
            StartingCash: spec.StartingCash,
            EndingEquity: finalEquity,
            EventsProcessed: eventsProcessed,
            EngineMilliseconds: sw.Elapsed.TotalMilliseconds);

        var report = ReportBuilder.Build(summary, equity, portfolio.Trades, spec.Universe);
        return visual is null ? report : report with { Visual = visual.Build(report.Trades) };
    }

    private static void ValidateSupportedOptions(RunSpec spec)
    {
        if (spec.Data.Modeling != ModelingMode.RealTicks)
        {
            throw new NotSupportedException(
                $"Modeling mode '{spec.Data.Modeling}' is not supported. BacktestEngine currently supports only '{ModelingMode.RealTicks}'.");
        }

        var execution = spec.ExecutionOrDefault;
        if (execution.FillModel != FillModelKind.L1Touch)
        {
            throw new NotSupportedException(
                $"Fill model '{execution.FillModel}' is not supported. BacktestEngine currently supports only '{FillModelKind.L1Touch}'.");
        }

        if (execution.LatencyMs != 0)
        {
            throw new NotSupportedException(
                $"Execution latency '{execution.LatencyMs}' ms is not supported. Set LatencyMs to 0.");
        }
    }

    private static async ValueTask DisposeKernelAsync(IStrategyKernel kernel)
    {
        if (kernel is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (kernel is IDisposable disposable)
            disposable.Dispose();
    }
}
