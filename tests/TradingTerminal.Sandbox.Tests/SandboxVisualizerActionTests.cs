using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// Running a unit's declared verb, through the runtime that owns it.
///
/// <para>An action is an id and a callback rather than a delegate the host holds, and this is why:
/// the runtime invokes it under <c>_drawGate</c>, the same gate the pump holds across every data
/// callback. An action almost always touches the same fields a data callback does, so anything that
/// ran it off the render thread without that gate would race the pump in exactly the way the gate
/// exists to prevent — and the author would need a second threading rule for buttons.</para>
/// </summary>
public sealed class SandboxVisualizerActionTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_declared_verb_reaches_the_unit()
    {
        var instrument = new InstrumentId(4242);
        ActionVisualizer? unit = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => unit = new ActionVisualizer(Schema(instrument)),
            currentValues: null,
            new FakeMarketDataHub(),
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();

        Assert.Equal(["reset"], runtime.Actions.Select(a => a.Id));
        Assert.True(await runtime.InvokeActionAsync("reset"));
        Assert.Equal(["reset"], unit!.Invoked);
    }

    [Fact]
    public async Task An_id_nobody_declared_is_refused_rather_than_passed_on()
    {
        // A stale press after a restart is an ordinary event. Passing an unknown id through would put
        // the unit in charge of validating what the host offered it.
        var instrument = new InstrumentId(4243);
        ActionVisualizer? unit = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => unit = new ActionVisualizer(Schema(instrument)),
            currentValues: null,
            new FakeMarketDataHub(),
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();

        Assert.False(await runtime.InvokeActionAsync("never-declared"));
        Assert.Empty(unit!.Invoked);
    }

    [Fact]
    public async Task Nothing_running_means_nothing_to_run()
    {
        await using var runtime = new SandboxVisualizerRuntime(
            () => new ActionVisualizer(Schema(new InstrumentId(4244))),
            currentValues: null,
            new FakeMarketDataHub(),
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        Assert.Empty(runtime.Actions);
        Assert.False(await runtime.InvokeActionAsync("reset"));
    }

    [Fact]
    public async Task A_verb_that_throws_is_reported_and_the_unit_keeps_running()
    {
        var instrument = new InstrumentId(4245);
        var faults = new List<string>();
        await using var runtime = new SandboxVisualizerRuntime(
            () => new ThrowingActionVisualizer(Schema(instrument)),
            currentValues: null,
            new FakeMarketDataHub(),
            new MutableClock(Epoch),
            (_, _, message) => faults.Add(message),
            _ => { });

        await runtime.StartAsync();

        Assert.False(await runtime.InvokeActionAsync("boom"));
        Assert.Contains(faults, f => f.Contains("action 'boom'", StringComparison.OrdinalIgnoreCase));
        Assert.True(runtime.IsRunning, "one bad button must not stop the unit");

        // And the runtime is still usable afterwards — the gate was released rather than held by the
        // exception, which is the failure that would freeze every later frame.
        Assert.False(await runtime.InvokeActionAsync("boom"));
    }

    [Fact]
    public async Task A_malformed_set_is_refused_whole_by_the_runtime_too()
    {
        // The sanitising is in the SDK type, but the runtime is a second reader of it, and a second
        // reader that forgot to sanitise would be the usual defect one layer down.
        await using var runtime = new SandboxVisualizerRuntime(
            () => new DuplicateActionVisualizer(Schema(new InstrumentId(4246))),
            currentValues: null,
            new FakeMarketDataHub(),
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();

        Assert.Empty(runtime.Actions);
        Assert.False(await runtime.InvokeActionAsync("same"));
    }

    private static StrategyParameterSchema Schema(InstrumentId instrument) =>
        new(StrategyParameter.Instrument("instrument", "Instrument", instrument));

    private class ActionVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        public List<string> Invoked { get; } = [];

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public virtual IReadOnlyList<UnitAction> Actions => [new("reset", "Reset")];

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public virtual Task OnActionAsync(string id, IVisualizerContext context, CancellationToken ct)
        {
            Invoked.Add(id);
            return Task.CompletedTask;
        }

        public void Draw(IRenderSurface surface) => surface.Text(2d, 10d, "x");
    }

    private sealed class ThrowingActionVisualizer(StrategyParameterSchema schema)
        : ActionVisualizer(schema)
    {
        public override IReadOnlyList<UnitAction> Actions => [new("boom", "Boom")];

        public override Task OnActionAsync(string id, IVisualizerContext context, CancellationToken ct) =>
            throw new InvalidOperationException("the button is wrong");
    }

    private sealed class DuplicateActionVisualizer(StrategyParameterSchema schema)
        : ActionVisualizer(schema)
    {
        public override IReadOnlyList<UnitAction> Actions =>
            [new("same", "One"), new("same", "Two")];
    }
}
