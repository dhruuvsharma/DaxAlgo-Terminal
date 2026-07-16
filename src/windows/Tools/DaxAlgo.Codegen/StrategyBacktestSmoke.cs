using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The build pipeline's runtime smoke: instantiate the freshly compiled strategy and drive its whole
/// lifecycle (<c>OnStartAsync</c> → a few dozen synthetic ticks → <c>OnEndAsync</c>) against a stub
/// clock and router, purely to catch the throws a compiler can't — a divide-by-zero on the first tick,
/// a null deref in warm-up, an off-by-one on an empty buffer.
/// <para>
/// <b>Advisory by design.</b> A failure comes back as a message (the caller turns it into a warning
/// diagnostic), never an exception, and never blocks registration — synthetic quotes exercise the code
/// path, not the strategy's judgment. Everything runs on the thread pool with a wall clock, so a
/// strategy that livelocks can't hang the builder's turn.
/// </para>
/// </summary>
public static class StrategyBacktestSmoke
{
    /// <summary>Ticks fed through <c>OnTickAsync</c> — enough to trip a small look-back window's edge
    /// cases without turning the smoke into a backtest.</summary>
    private const int TickCount = 48;

    /// <summary>Wall clock for the whole run. A compiled strategy that can't chew 48 synthetic ticks in
    /// this long is wedged, and the smoke reports that instead of waiting.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs the smoke. Null ⇒ the lifecycle completed without throwing; otherwise a short human/model
    /// readable failure ("OnTickAsync threw DivideByZeroException: …"). Only the caller's own
    /// cancellation propagates as an exception.
    /// </summary>
    public static async Task<string?> RunAsync(BacktestStrategyOption option, CancellationToken ct = default)
    {
        // The strategy is untrusted compiled code: run it off-thread and give up after the budget rather
        // than let a busy-loop hold the turn hostage. An abandoned runaway task is the accepted cost —
        // the same code would have been registered and run anyway.
        var run = Task.Run(() => DriveLifecycleAsync(ct), ct);
        var finished = await Task.WhenAny(run, Task.Delay(Budget, ct)).ConfigureAwait(false);

        if (finished != run)
            return $"the strategy did not finish {TickCount} synthetic ticks within {Budget.TotalSeconds:0}s — check for a loop that never exits.";

        return await run.ConfigureAwait(false);

        async Task<string?> DriveLifecycleAsync(CancellationToken token)
        {
            var stage = "constructing";
            try
            {
                var strategy = option.Create(Contract.UsStock("SMOKE"));
                var clock = new SmokeClock(new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc));
                var router = new SmokeRouter();

                stage = "OnStartAsync";
                await strategy.OnStartAsync(clock, router, token).ConfigureAwait(false);

                stage = "OnTickAsync";
                var rng = new Random(42);   // deterministic: the same code always smokes the same way
                var mid = 100.0;
                for (var i = 0; i < TickCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    mid = Math.Max(0.01, mid + (rng.NextDouble() - 0.5) * 0.25);
                    clock.Advance(TimeSpan.FromSeconds(1));
                    await strategy.OnTickAsync(
                        new Tick(clock.UtcNow, mid - 0.01, mid + 0.01, 100, 100), clock, router, token)
                        .ConfigureAwait(false);
                }

                stage = "OnEndAsync";
                await strategy.OnEndAsync(clock, router, token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;   // the user pressed Stop — not a strategy defect
            }
            catch (Exception ex)
            {
                return $"{stage} threw {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    /// <summary>A clock the smoke advances one second per tick — deterministic, and never the wall clock.</summary>
    private sealed class SmokeClock(DateTime start) : IClock
    {
        public DateTime UtcNow { get; private set; } = start;
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>Accepts every order and reports it Working; emits no events. The smoke checks that the
    /// strategy survives its own code, not what its orders would have done.</summary>
    private sealed class SmokeRouter : IOrderRouter
    {
        public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
            Task.FromResult(new OrderResult(request.ClientOrderId, null, OrderState.Working));

        public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) => Task.CompletedTask;

        public IObservable<OrderEvent> OrderEvents { get; } = new NeverObservable();

        private sealed class NeverObservable : IObservable<OrderEvent>
        {
            public IDisposable Subscribe(IObserver<OrderEvent> observer) => new Nothing();
            private sealed class Nothing : IDisposable { public void Dispose() { } }
        }
    }
}
