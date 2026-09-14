using System.Text.Json;
using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>
/// A unit built from blocks, run by the real runtime against a fake hub. Each test is one thing a
/// Hyperion unit is allowed to do that the sandbox runtimes could not.
/// </summary>
public sealed class BlocksUnitRuntimeTests
{
    private static readonly InstrumentId LegA = new(1);
    private static readonly InstrumentId LegB = new(2);

    [Fact]
    public async Task A_two_instrument_arbitrage_holds_both_legs_and_reads_its_fills()
    {
        // The case the single-instrument runtime refuses outright: "basket model portfolios are deferred".
        var hub = new FakeHub();
        IUnitContext? seen = null;
        var fills = new List<FillView>();

        var unit = new LambdaUnit(
            new UnitInfo("Spread arb", Settings:
            [
                StrategyParameter.Instrument("legA", "Leg A", LegA),
                StrategyParameter.Instrument("legB", "Leg B", LegB),
                StrategyParameter.Number("entry", "Entry spread", 1d),
            ]),
            context =>
            {
                seen = context;
                double? a = null, b = null;
                var legA = context.Settings.Instrument("legA");
                var legB = context.Settings.Instrument("legB");

                void Decide()
                {
                    if (a is null || b is null) return;
                    if (a - b > context.Settings.Number("entry") && context.Portfolio.Position(legA).Units == 0d)
                    {
                        context.Orders.SetTarget(legA, -1d);
                        context.Orders.SetTarget(legB, 1d);
                    }
                }

                context.Market.OnQuote(legA, q => { a = q.Mid; Decide(); });
                context.Market.OnQuote(legB, q => { b = q.Mid; Decide(); });
                context.Portfolio.OnFill(fills.Add);
                return Task.CompletedTask;
            });

        await using var runtime = new BlocksUnitRuntime(() => unit, "arb", Host.For(hub));
        await runtime.StartAsync();

        hub.PublishQuote(FakeHub.Quote(1, 102.0, 102.2));
        hub.PublishQuote(FakeHub.Quote(2, 100.0, 100.2));

        // Leg B's order was placed on B's quote and worked there; leg A's waits for A's next price.
        hub.PublishQuote(FakeHub.Quote(1, 102.0, 102.2));

        await Host.WaitUntil(() => fills.Count == 2, "both legs should fill");

        runtime.UsesOrders.Should().BeTrue("using the orders block is what makes a unit a strategy");
        await Host.WaitUntil(() => seen!.Portfolio.Positions.Count == 2, "both positions should be open");
        seen!.Portfolio.Position(LegA).Units.Should().Be(-1d);
        seen.Portfolio.Position(LegB).Units.Should().Be(1d);
        fills.Select(f => f.Instrument).Should().BeEquivalentTo([LegA, LegB]);
        fills.Should().OnlyContain(f => f.Price > 0d);
        double.IsFinite(seen.Portfolio.Account.Equity).Should().BeTrue();
    }

    [Fact]
    public async Task Timers_posted_work_page_messages_and_market_data_never_overlap()
    {
        var hub = new FakeHub();
        var endpoint = new FakeEndpoint();
        var inside = 0;
        var overlaps = 0;
        var calls = 0;

        void Guarded()
        {
            if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
            Thread.SpinWait(2_000);
            calls++;
            Interlocked.Decrement(ref inside);
        }

        var unit = new LambdaUnit(new UnitInfo("Overlap"), context =>
        {
            context.Market.OnQuote(LegA, _ => Guarded());
            context.Schedule.Every(TimeSpan.FromMilliseconds(100), Guarded);
            context.Ui.On("poke", _ => Guarded());
            return Task.CompletedTask;
        });

        await using var runtime = new BlocksUnitRuntime(() => unit, "overlap", Host.For(hub), ui: endpoint);
        await runtime.StartAsync();

        var publishers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                hub.PublishQuote(FakeHub.Quote(1, 100, 100.1));
                endpoint.Send("poke", "{}");
            }
        }));
        await Task.WhenAll(publishers);
        await Task.Delay(350);

        overlaps.Should().Be(0, "every handler runs on the unit thread, one at a time");
        calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_slow_unit_drops_old_market_data_but_never_posted_work()
    {
        var hub = new FakeHub();
        var posted = 0;
        IUnitContext? seen = null;

        var unit = new LambdaUnit(new UnitInfo("Slow"), context =>
        {
            seen = context;
            context.Market.OnQuote(LegA, _ => Thread.Sleep(2));
            return Task.CompletedTask;
        });

        await using var runtime = new BlocksUnitRuntime(
            () => unit, "slow", Host.For(hub), options: new BlocksRuntimeOptions(MarketQueueCapacity: 16));
        await runtime.StartAsync();

        for (var i = 0; i < 2_000; i++)
        {
            hub.PublishQuote(FakeHub.Quote(1, 100, 100.1));
            if (i % 100 == 0) seen!.Schedule.Post(() => posted++);
        }

        await Host.WaitUntil(() => posted == 20, "every posted item should run");
        runtime.DroppedMarketEvents.Should().BeGreaterThan(0, "a bounded queue drops the oldest quotes instead of growing");
    }

    [Fact]
    public async Task A_flood_of_posted_work_hits_a_ceiling_and_is_reported_instead_of_growing()
    {
        // The leak gate's rule, proven rather than asserted: posted work has a ceiling too. A page that
        // floods messages while the unit is busy must not grow the queue without bound.
        var hub = new FakeHub();
        var log = new List<string>();
        var endpoint = new FakeEndpoint();
        var gate = new ManualResetEventSlim();
        var handled = 0;

        var unit = new LambdaUnit(new UnitInfo("Flooded"), context =>
        {
            context.Ui.On("flood", _ => { gate.Wait(TimeSpan.FromSeconds(5)); handled++; });
            return Task.CompletedTask;
        });

        await using var runtime = new BlocksUnitRuntime(
            () => unit, "flood", Host.For(hub, log), ui: endpoint, options: new BlocksRuntimeOptions(ControlQueueCapacity: 64));
        await runtime.StartAsync();

        for (var i = 0; i < 5_000; i++) endpoint.Send("flood", "{}");
        gate.Set();

        await Host.WaitUntil(() => runtime.RefusedWork > 0, "a full queue refuses further work");
        runtime.RefusedWork.Should().BeGreaterThan(4_000, "only about the ceiling's worth can be waiting at once");
        await Host.WaitUntil(() => handled > 0, "what was accepted still runs");
        lock (log) log.Should().Contain(line => line.StartsWith("WARN") && line.Contains("not keeping up"));
    }

    [Fact]
    public async Task Page_sends_are_coalesced_to_the_latest_payload_and_page_messages_reach_the_unit()
    {
        var hub = new FakeHub();
        var endpoint = new FakeEndpoint();
        var received = new List<double>();
        var opened = 0;

        var unit = new LambdaUnit(new UnitInfo("Page"), context =>
        {
            context.Ui.OnOpened(() => opened++);
            context.Ui.On("threshold", payload => received.Add(payload.GetProperty("value").GetDouble()));
            context.Schedule.Post(() =>
            {
                for (var i = 1; i <= 500; i++) context.Ui.Send("state", new { Count = i });
            });
            return Task.CompletedTask;
        });

        await using var runtime = new BlocksUnitRuntime(() => unit, "page", Host.For(hub), ui: endpoint);
        endpoint.Open();
        await runtime.StartAsync();
        await Host.WaitUntil(() => endpoint.Snapshot().Any(p => p.Json.Contains("500")), "the latest state should arrive");

        var states = endpoint.Snapshot().Where(p => p.Topic == "state").ToArray();
        states.Length.Should().BeLessThan(50, "500 sends in one burst are coalesced, not delivered one by one");
        JsonDocument.Parse(states[^1].Json).RootElement.GetProperty("count").GetInt32().Should().Be(500, "camelCase, and the latest wins");

        endpoint.Send("threshold", """{"value": 2.5}""");
        endpoint.Send("threshold", "not json");
        await Host.WaitUntil(() => received.Count == 1, "the page's message should reach the handler");
        received.Should().Equal(2.5);
    }

    [Fact]
    public async Task State_survives_a_restart()
    {
        var hub = new FakeHub();
        var store = new MemoryStateStore();
        int? restored = null;

        IUnit First() => new LambdaUnit(new UnitInfo("Memory"), context =>
        {
            context.State.Set("trades", 7);
            return Task.CompletedTask;
        });

        IUnit Second() => new LambdaUnit(new UnitInfo("Memory"), context =>
        {
            restored = context.State.Get<int>("trades");
            return Task.CompletedTask;
        });

        await using (var first = new BlocksUnitRuntime(First, "memory", Host.For(hub, state: store)))
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        await using var second = new BlocksUnitRuntime(Second, "memory", Host.For(hub, state: store));
        await second.StartAsync();

        restored.Should().Be(7);
    }

    [Fact]
    public async Task A_setting_change_is_heard_and_an_out_of_range_one_is_clamped_like_the_panel_does()
    {
        var hub = new FakeHub();
        var changes = new List<string>();
        IUnitContext? seen = null;

        var unit = new LambdaUnit(
            new UnitInfo("Settings", Settings: [StrategyParameter.Int("period", "Period", 20, min: 2, max: 200)]),
            context =>
            {
                seen = context;
                context.Settings.OnChanged(changes.Add);
                return Task.CompletedTask;
            });

        await using var runtime = new BlocksUnitRuntime(() => unit, "settings", Host.For(hub));
        await runtime.StartAsync();

        await runtime.ApplySettingsAsync(new Dictionary<string, object?> { ["period"] = 50 });
        await Host.WaitUntil(() => changes.Count == 1, "the unit should hear the change");
        seen!.Settings.Int("period").Should().Be(50);

        // The same coercion the parameter panel applies: clamped into range, never thrown at the page.
        await RunOnUnit(seen, () => seen.Settings.Set("period", 1));
        await Host.WaitUntil(() => changes.Count == 2, "the clamped change is still a change");
        seen.Settings.Int("period").Should().Be(2, "1 is below the declared minimum of 2");
    }

    [Fact]
    public async Task A_handler_that_throws_is_skipped_and_the_unit_keeps_running()
    {
        var hub = new FakeHub();
        var log = new List<string>();
        var delivered = 0;

        var unit = new LambdaUnit(new UnitInfo("Fragile"), context =>
        {
            context.Market.OnQuote(LegA, _ =>
            {
                delivered++;
                if (delivered == 1) throw new InvalidOperationException("boom");
            });
            return Task.CompletedTask;
        });

        await using var runtime = new BlocksUnitRuntime(() => unit, "fragile", Host.For(hub, log));
        await runtime.StartAsync();

        hub.PublishQuote(FakeHub.Quote(1, 100, 100.1));
        hub.PublishQuote(FakeHub.Quote(1, 100, 100.1));

        await Host.WaitUntil(() => delivered == 2, "the second quote should still be delivered");
        runtime.Faults.Should().Be(1);
        lock (log) log.Should().Contain(line => line.StartsWith("ERROR") && line.Contains("boom"));
    }

    [Fact]
    public async Task An_order_placed_off_a_price_is_worked_at_that_instruments_next_price()
    {
        var hub = new FakeHub();
        IUnitContext? seen = null;

        var unit = new LambdaUnit(new UnitInfo("Timer order"), context =>
        {
            seen = context;
            context.Market.OnQuote(LegA, _ => { });
            context.Schedule.After(TimeSpan.FromMilliseconds(100), () => context.Orders.SetTarget(LegA, 2d));
            return Task.CompletedTask;
        });

        await using var runtime = new BlocksUnitRuntime(() => unit, "timer", Host.For(hub));
        await runtime.StartAsync();

        hub.PublishQuote(FakeHub.Quote(1, 100, 100.1));
        await Task.Delay(300);

        var before = 0d;
        await Host.WaitUntil(() => runtime.UsesOrders, "the timer should have placed the order");
        await RunOnUnit(seen!, () => before = seen!.Portfolio.Position(LegA).Units);
        before.Should().Be(0d, "no price has arrived since the order was placed");

        hub.PublishQuote(FakeHub.Quote(1, 100, 100.1));
        await Host.WaitUntil(() => Read(seen!, () => seen!.Portfolio.Position(LegA).Units) == 2d, "the next quote works it");
    }

    [Fact]
    public async Task Stopping_releases_every_stream()
    {
        var hub = new FakeHub();
        var unit = new LambdaUnit(new UnitInfo("Streams"), context =>
        {
            context.Market.OnQuote(LegA, _ => { });
            context.Market.OnTrade(LegB, _ => { });
            context.Market.OnBar(LegA, BarSize.OneMinute, _ => { });
            return Task.CompletedTask;
        });

        var runtime = new BlocksUnitRuntime(() => unit, "streams", Host.For(hub));
        await runtime.StartAsync();
        hub.Subscribers.Should().Be(3);

        await runtime.StopAsync();
        hub.Subscribers.Should().Be(0, "a stopped unit must not keep the hub feeding it");
        runtime.State.Should().Be(UnitRunState.Stopped);
    }

    private static Task RunOnUnit(IUnitContext context, Action work)
    {
        var done = new TaskCompletionSource();
        context.Schedule.Post(() => { work(); done.SetResult(); });
        return done.Task;
    }

    private static double Read(IUnitContext context, Func<double> read)
    {
        var value = double.NaN;
        RunOnUnit(context, () => value = read()).Wait(1000);
        return value;
    }
}
