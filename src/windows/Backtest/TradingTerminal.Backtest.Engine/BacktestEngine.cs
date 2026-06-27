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
        var sw = Stopwatch.StartNew();

        var tickSizeOf = spec.Universe.Instruments.ToDictionary(i => i.Id, i => i.TickSize);
        var multipliers = spec.Universe.Instruments.ToDictionary(i => i.Id, i => i.ContractMultiplier);

        var clock = new SimClock();
        var fees = FeeModels.From(spec.CostOrDefault);
        var fillModel = new L1TouchFillModel(spec.ExecutionOrDefault.SlippageTicks);
        var book = new SimulatedOrderBook(clock, fillModel, id => tickSizeOf.GetValueOrDefault(id, 0.01));
        var portfolio = new Portfolio(spec.StartingCash, multipliers, fees);
        var router = new EngineOrderRouter(book, spec.Universe);
        var ctx = new StrategyContext(clock, router, new PortfolioView(portfolio), spec.Universe, spec.ParametersOrEmpty);

        var equity = new List<EquitySample>();
        var visual = spec.Visual == VisualRecording.On
            ? new VisualRecorder(spec.Universe.Primary.Id, TimeSpan.FromMinutes(1))
            : null;
        DateTime? firstUtc = null, lastUtc = null, lastSample = null;
        double peak = spec.StartingCash;
        long eventsProcessed = 0;

        // Order events: settle fills into the portfolio synchronously, then queue the kernel's
        // order callback on a serial chain so callbacks fire in order and before the next event.
        var pending = Task.CompletedTask;
        book.Event += (instrument, evt) =>
        {
            if (evt.LastFillQuantity > 0 && evt.LastFillPrice is { } px)
                portfolio.OnFill(instrument, evt.TimestampUtc, evt.Side, evt.LastFillQuantity, px, evt.Liquidity);

            pending = pending.ContinueWith(
                _ => kernel.OnOrderEventAsync(evt, ctx, ct),
                ct, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        };

        await kernel.OnStartAsync(ctx, ct).ConfigureAwait(false);

        await foreach (var ev in _feed.StreamAsync(spec, ct).ConfigureAwait(false))
        {
            clock.SetTo(ev.TimestampUtc);
            await pending.ConfigureAwait(false);
            firstUtc ??= ev.TimestampUtc;
            lastUtc = ev.TimestampUtc;

            switch (ev.Kind)
            {
                case MarketEventKind.Quote:
                {
                    var tick = ev.Quote!;
                    book.OnQuote(ev.Instrument, tick);                 // may fill → portfolio + queued callback
                    var mid = (tick.Bid + tick.Ask) * 0.5;
                    portfolio.OnMark(ev.Instrument, mid);
                    visual?.OnMid(ev.Instrument, ev.TimestampUtc, mid);
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

            eventsProcessed++;
        }

        await pending.ConfigureAwait(false);
        await kernel.OnEndAsync(ctx, ct).ConfigureAwait(false);

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
}
