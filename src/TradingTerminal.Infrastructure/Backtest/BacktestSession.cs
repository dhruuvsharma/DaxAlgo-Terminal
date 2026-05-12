using System.Reactive.Linq;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Risk;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest.Persistence;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Drives a single backtest end-to-end:
///   1. Set up the simulated clock, fill model, order book, and router.
///   2. Forward order events to the strategy and track fills for the trade ledger.
///   3. Iterate the parquet tick stream: advance the clock, run the order book, sample
///      equity at most once per minute, and dispatch <c>OnTickAsync</c>.
///   4. After the last tick, flush the final equity point and close out the strategy.
///
/// Single-threaded by design — the engine has one logical timeline and we keep all state
/// transitions on the caller's task. Concurrency belongs at the parameter-sweep layer
/// (run N sessions in parallel), not inside a single session.
/// </summary>
public sealed class BacktestSession : IBacktestSession
{
    public Task<BacktestResult> RunAsync(
        BacktestConfig config,
        IBacktestStrategy strategy,
        CancellationToken ct = default) => RunAsync(config, strategy, risk: null, ct);

    public async Task<BacktestResult> RunAsync(
        BacktestConfig config,
        IBacktestStrategy strategy,
        IRiskManager? risk,
        CancellationToken ct = default)
    {
        var clock = new SimulatedClock();
        var fillModel = new L1FillModel(config.TickSize, config.SlippageTicks);
        var orderBook = new SimulatedOrderBook(clock, fillModel);
        var router = new BacktestOrderRouter(orderBook, risk);

        var ledger = new TradeLedger(config.ContractMultiplier, config.StartingCash, config.FeeModel);
        var equity = new List<EquityPoint>();
        DateTime? lastSample = null;

        var orderEventTask = Task.CompletedTask;
        using var sub = router.OrderEvents.Subscribe(evt =>
        {
            if (evt.LastFillQuantity > 0 && evt.LastFillPrice is { } px)
                ledger.OnFill(evt.TimestampUtc, evt.Side, evt.LastFillQuantity, px, evt.Liquidity);

            orderEventTask = orderEventTask.ContinueWith(
                _ => strategy.OnOrderEventAsync(evt, ct),
                ct,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        });

        await strategy.OnStartAsync(clock, router, ct).ConfigureAwait(false);

        Tick? lastTick = null;
        await foreach (var tick in ParquetTickReader.ReadAsync(
                           config.TickDataPath, config.FromUtc, config.ToUtc, ct))
        {
            clock.SetTo(tick.TimestampUtc);
            orderBook.OnTick(tick);

            await orderEventTask.ConfigureAwait(false);
            await strategy.OnTickAsync(tick, clock, router, ct).ConfigureAwait(false);

            var mid = (tick.Bid + tick.Ask) * 0.5;
            if (lastSample is null || (tick.TimestampUtc - lastSample.Value).TotalSeconds >= 60)
            {
                equity.Add(new EquityPoint(tick.TimestampUtc, ledger.Equity(mid)));
                lastSample = tick.TimestampUtc;
            }
            lastTick = tick;
        }

        await orderEventTask.ConfigureAwait(false);
        await strategy.OnEndAsync(clock, router, ct).ConfigureAwait(false);

        if (lastTick is { } finalTick)
        {
            var mid = (finalTick.Bid + finalTick.Ask) * 0.5;
            equity.Add(new EquityPoint(finalTick.TimestampUtc, ledger.Equity(mid)));
        }

        var endingCash = lastTick is { } t
            ? ledger.Equity((t.Bid + t.Ask) * 0.5)
            : config.StartingCash;

        var bare = new BacktestResult(ledger.Trades, equity, config.StartingCash, endingCash, TotalFees: ledger.TotalFees);
        return bare with { Stats = StatisticsCalculator.Calculate(bare) };
    }
}
