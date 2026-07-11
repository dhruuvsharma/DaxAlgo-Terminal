using System.Reactive.Linq;
using DaxNewStrategy.Engine;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using Xunit;

namespace DaxNewStrategy.Tests;

/// <summary>
/// Offline harness: drives the kernel with synthetic ticks through a recording router — no broker,
/// no host, no data files. Green out of the box; grow these with your strategy's real invariants
/// (they are what the marketplace's build-from-source review runs).
/// </summary>
public sealed class KernelHarnessTests
{
    [Fact]
    public async Task Trending_series_goes_long_and_flattens_at_end()
    {
        var (router, clock) = Harness();
        var kernel = new DaxNewStrategyKernel(TestContract());

        await kernel.OnStartAsync(clock, router, CancellationToken.None);
        for (var i = 0; i < 300; i++)
            await kernel.OnTickAsync(TickAt(clock, mid: 100 + i * 0.05), clock, router, CancellationToken.None);
        await kernel.OnEndAsync(clock, router, CancellationToken.None);

        Assert.True(router.Orders.Count >= 2, "a trending series must open a position and flatten it");
        Assert.Equal(OrderSide.Buy, router.Orders[0].Side);
        Assert.Equal(0, NetSignedQuantity(router));
    }

    [Fact]
    public async Task Flat_series_places_no_orders()
    {
        var (router, clock) = Harness();
        var kernel = new DaxNewStrategyKernel(TestContract());

        await kernel.OnStartAsync(clock, router, CancellationToken.None);
        for (var i = 0; i < 500; i++)
            await kernel.OnTickAsync(TickAt(clock, mid: 100.0), clock, router, CancellationToken.None);
        await kernel.OnEndAsync(clock, router, CancellationToken.None);

        Assert.Empty(router.Orders);
    }

    [Fact]
    public async Task Client_order_ids_are_unique()
    {
        var (router, clock) = Harness();
        var kernel = new DaxNewStrategyKernel(TestContract());

        await kernel.OnStartAsync(clock, router, CancellationToken.None);
        // Oscillate hard enough to force several reversals.
        for (var i = 0; i < 600; i++)
            await kernel.OnTickAsync(TickAt(clock, mid: 100 + 10 * Math.Sin(i / 40.0)), clock, router, CancellationToken.None);
        await kernel.OnEndAsync(clock, router, CancellationToken.None);

        Assert.Equal(router.Orders.Count, router.Orders.Select(o => o.ClientOrderId).Distinct().Count());
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private static (RecordingRouter Router, SteppingClock Clock) Harness() => (new RecordingRouter(), new SteppingClock());

    private static Contract TestContract() => Contract.UsStock("TEST");

    private static Tick TickAt(SteppingClock clock, double mid)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        return new Tick(clock.UtcNow, Bid: mid - 0.01, Ask: mid + 0.01, BidSize: 100, AskSize: 100);
    }

    private static long NetSignedQuantity(RecordingRouter router) =>
        router.Orders.Sum(o => o.Side == OrderSide.Buy ? o.Quantity : -o.Quantity);

    private sealed class SteppingClock : IClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 1, 1, 14, 30, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class RecordingRouter : IOrderRouter
    {
        public List<OrderRequest> Orders { get; } = [];

        public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
        {
            Orders.Add(request);
            return Task.FromResult(new OrderResult(request.ClientOrderId, $"sim-{Orders.Count}", OrderState.Working));
        }

        public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) => Task.CompletedTask;

        public IObservable<OrderEvent> OrderEvents { get; } = Observable.Never<OrderEvent>();
    }
}
