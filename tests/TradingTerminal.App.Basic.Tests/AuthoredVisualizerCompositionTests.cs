using DaxAlgo.Sdk;
using TradingTerminal.App;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.UI.Logging;
using Xunit;

namespace TradingTerminal.App.Basic.Tests;

/// <summary>
/// The shell's own wiring — the one place in this tree that nothing could test.
///
/// <para><b>Why this project exists.</b> Every capability an authored window has must be passed from
/// the shell to <c>AuthoredUnitHost</c>, and that wiring lived inside a lambda inside a window behind
/// a DI container, so the only way to find a missing seam was to open the terminal and look. The verb
/// capability shipped that way: declared, sanitised, mediated, bound, rendered by the presenter — and
/// never passed, so the terminal showed no buttons. Every unit test missed it, because they all built
/// the host directly rather than the way the application does.</para>
///
/// <para>These drive the real composition with a real sandbox runtime and a real unit. A seam added to
/// the host and forgotten here now fails a test instead of shipping.</para>
/// </summary>
public sealed class AuthoredVisualizerCompositionTests
{
    [WpfFact]
    public void Every_seam_the_host_offers_is_wired()
    {
        // The blunt version, and the one that would have caught the defect: nothing optional may be
        // left null. Read off the presenter rather than the constructor, because what matters is what
        // the window ends up able to do.
        var (runtime, unit) = Compose();
        using (runtime)
        using (unit)
        {
            Assert.True(unit.Presenter.CanEditParameters, "apply was not wired");
            Assert.True(unit.Presenter.CanPause, "setPaused was not wired");

            // Only after starting: the runtime builds the unit, so before that there are no verbs to
            // read. Composing and starting are one sequence for exactly this reason.
            AuthoredVisualizerComposition.StartAsync(runtime, unit).GetAwaiter().GetResult();
            Assert.True(unit.Presenter.HasActions, "actions/invokeAction were not wired");
        }
    }

    [WpfFact]
    public async Task A_verb_declared_by_the_unit_reaches_the_unit_when_pressed()
    {
        // End to end through the composition the application uses: the unit declares a verb, the
        // presenter shows it, pressing it runs the unit's own code.
        var (runtime, unit) = Compose();
        using (runtime)
        using (unit)
        {
            await AuthoredVisualizerComposition.StartAsync(runtime, unit);

            var button = Assert.Single(unit.Presenter.Actions);
            Assert.Equal("Copy book", button.Label);

            // Fire-and-forget by design: the presenter raises, the host runs it. Hence the wait.
            unit.Presenter.InvokeActionCommand.Execute(button);

            Assert.True(
                await WaitFor(() => Probe.Invoked.Count > 0),
                "pressing the button never reached the unit");
        }
    }

    [WpfFact]
    public async Task A_take_away_offered_during_that_verb_reaches_the_window()
    {
        // The other half of the same wiring, and the one with a security argument behind it: an offer
        // is honoured only inside an action, and it has to land somewhere.
        var (runtime, unit) = Compose();
        using (runtime)
        using (unit)
        {
            await AuthoredVisualizerComposition.StartAsync(runtime, unit);
            unit.Presenter.InvokeActionCommand.Execute(unit.Presenter.Actions[0]);

            Assert.True(
                await WaitFor(() => unit.Presenter.Log.Any(
                    line => line.Message.Contains("Book (CSV)", StringComparison.Ordinal))),
                "the offer never reached the window");
        }
    }

    [WpfFact]
    public void The_units_own_clock_reaches_the_window()
    {
        // Animation is `surface.Now` minus an instant the unit stamped in a data callback, so the two
        // have to be ONE clock. The stub's is a fixed instant well away from any wall clock, which is
        // what makes this fail both ways: null if the seam was not passed, and today's date if the
        // window started a clock of its own.
        var (runtime, unit) = Compose();
        using (runtime)
        using (unit)
        {
            var clock = unit.Presenter.Clock;
            Assert.NotNull(clock);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), clock!());
        }
    }

    [WpfFact]
    public void The_layout_the_unit_declares_reaches_the_window()
    {
        // A unit could declare a multi-panel window, have it validated, see it in the preview, and
        // then open as one panel. That was a real defect once; this is the guard for it.
        var (runtime, unit) = Compose();
        using (runtime)
        using (unit)
        {
            Assert.NotNull(unit.Presenter.Layout);
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private static (TradingTerminal.Sandbox.SandboxVisualizerRuntime Runtime,
        TradingTerminal.UI.Controls.Render.AuthoredUnitHost Unit) Compose() =>
        AuthoredVisualizerComposition.Create(
            "Probe",
            () => new Probe(),
            Probe.Declared,
            new StubHub(),
            new StubClock(),
            new InMemoryLogSink());

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>A unit that declares one of everything the composition has to carry.</summary>
    private sealed class Probe : IVisualizer
    {
        internal static List<string> Invoked { get; } = [];

        internal static StrategyParameterSchema Declared { get; } = new(
            StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)),
            StrategyParameter.Int("levels", "Levels", 5, min: 1, max: 25));

        public StrategyParameterSchema Schema => Declared;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public IReadOnlyList<UnitAction> Actions => [new("copy", "Copy book")];

        public DaxAlgo.Sdk.Layout.UnitLayout Layout => DaxAlgo.Sdk.Layout.UnitLayout.Rows(
            DaxAlgo.Sdk.Layout.UnitLayout.Panel("Top", _ => { }),
            DaxAlgo.Sdk.Layout.UnitLayout.Panel("Bottom", _ => { }));

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public Task OnActionAsync(string id, IVisualizerContext context, CancellationToken ct)
        {
            Invoked.Add(id);
            context.Export.Offer("Book (CSV)", "side,price,size");
            return Task.CompletedTask;
        }

        public void Draw(IRenderSurface surface) => surface.Text(2d, 10d, "probe");
    }

    private sealed class StubClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class StubHub : IMarketDataHub
    {
        public IObservable<Quote> Quotes(InstrumentId instrument) => new Empty<Quote>();

        public IObservable<TradePrint> Trades(InstrumentId instrument) => new Empty<TradePrint>();

        public IObservable<OhlcvBar> Bars(InstrumentId instrument, BarSize size) => new Empty<OhlcvBar>();

        public IObservable<DepthSnapshot> Depth(InstrumentId instrument) => new Empty<DepthSnapshot>();

        // The composition is what is under test, not the feed. Nothing here publishes.
        public void PublishQuote(Quote quote)
        {
        }

        public void PublishTrade(TradePrint trade)
        {
        }

        public void PublishBar(OhlcvBar bar)
        {
        }

        public void PublishDepth(InstrumentId instrument, DepthSnapshot depth)
        {
        }

        private sealed class Empty<T> : IObservable<T>
        {
            public IDisposable Subscribe(IObserver<T> observer) => new Nothing();

            private sealed class Nothing : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
