using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TradingTerminal.Charts;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Workspace;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The canvas seam: what the shell hands a canvas, and what a canvas is allowed to refuse.
///
/// <para>The workspace exists to end an inconsistency, not to add a window. Every visual surface in
/// the terminal used to load its own instrument list and draw its own picker — the price chart does,
/// an authored unit does it as a parameter buried in an expander, the order book does it again — so
/// the same choice was made in three places, with three controls, none of which remembered each
/// other. The shell owns the choice; a canvas is told.</para>
/// </summary>
public sealed class WorkspaceCanvasTests
{
    [Fact]
    public void A_following_canvas_sees_the_shells_selection()
    {
        var subject = new WorkspaceSubject();
        var seen = new List<string?>();
        subject.PropertyChanged += (_, _) => seen.Add(subject.Instrument?.Contract.Symbol);

        subject.Instrument = Instrument("ES");
        subject.Instrument = Instrument("NQ");

        Assert.Equal(["ES", "NQ"], seen);
    }

    [Fact]
    public void A_canvas_declares_how_it_answers_to_the_subject()
    {
        // Follows is the default and the point of the exercise: a descriptor written without thinking
        // about it gets the consistent behaviour rather than the exceptional one.
        var ordinary = Canvas("ordinary");

        Assert.Equal(CanvasSubjectMode.Follows, ordinary.Instrument);
        Assert.Equal(CanvasSubjectMode.Follows, ordinary.Timeframe);
    }

    [Fact]
    public void Pinning_disables_the_header_control_and_says_why()
    {
        // A disabled control's only question is "why", and the answer travels with the canvas that
        // caused it rather than living in the shell as a special case per canvas.
        var pinned = Canvas("depth") with
        {
            Instrument = CanvasSubjectMode.Pins,
            PinnedReason = "The depth ladder follows its own book.",
        };

        var model = Model(pinned);

        Assert.False(model.CanPickInstrument);
        Assert.True(model.CanPickTimeframe);
        Assert.Equal("The depth ladder follows its own book.", model.PinnedReason);
    }

    [Fact]
    public void Ignoring_hides_the_control_rather_than_greying_it()
    {
        // Greying out a timeframe switcher above something with no notion of time is an invitation to
        // wonder what would happen if it were enabled. There is no answer, so there is no control.
        var model = Model(Canvas("static") with { Timeframe = CanvasSubjectMode.Ignores });

        Assert.False(model.ShowsTimeframe);
        Assert.True(model.ShowsInstrument);
    }

    [Fact]
    public void The_first_registered_canvas_leads()
    {
        // Registration order is the picker's order, so a composition root chooses the default simply by
        // registering it first — no priority field to drift out of step with a list nobody re-reads.
        var model = Model(Canvas("first"), Canvas("second"));

        Assert.Equal("first", model.SelectedCanvas?.Id);
    }

    [Fact]
    public void The_picker_is_grouped_so_a_growing_list_stays_scannable()
    {
        var model = Model(
            Canvas("a") with { Group = "Charts" },
            Canvas("b") with { Group = "Order flow" });

        Assert.NotEmpty(model.CanvasesView.Groups);
    }

    [WpfFact]
    public void Every_registered_canvas_materialises()
    {
        // THE LESSON FROM THE HYP.ShimmerLabel CRASH, applied one layer up. A canvas that parses is not
        // a canvas that renders: WPF expands templates during Measure, and a resource it cannot resolve
        // throws there — on the dispatcher, on every layout pass. A factory that is only ever called in
        // production is a factory whose first run is on a user's machine.
        var built = 0;
        var canvas = new WorkspaceCanvas(
            "probe", "Probe", "Charts",
            _ =>
            {
                built++;
                return new WorkspaceCanvasView(new TextBlock { Text = "canvas" });
            });

        var view = canvas.Create(new WorkspaceCanvasContext(
            new EmptyServices(), new WorkspaceSubject()));

        view.View.Measure(new Size(800, 600));
        view.View.Arrange(new Rect(0, 0, 800, 600));

        Assert.Equal(1, built);
        Assert.True(view.View.ActualWidth > 0, "the canvas produced no visual — it did not expand");
    }

    [WpfFact]
    public void Leaving_a_canvas_tears_it_down()
    {
        // Not tidiness. The price chart owns a WebView2, whose out-of-process composition paints ABOVE
        // any WPF content sharing the cell — so a canvas left realised behind another paints straight
        // over whatever replaced it. Disposal on the way out is what keeps a swap a swap.
        var torn = false;
        var view = new WorkspaceCanvasView(new TextBlock())
        {
            Lifetime = new Teardown(() => torn = true),
        };

        view.Lifetime!.Dispose();

        Assert.True(torn);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static WorkspaceCanvas Canvas(string id) => new(
        id, id, "Charts", _ => new WorkspaceCanvasView(new TextBlock()));

    private static WorkspaceViewModel Model(params WorkspaceCanvas[] canvases) => new(
        canvases,
        new EmptyRepository(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceViewModel>.Instance);

    private static TradableInstrument Instrument(string symbol) =>
        new(symbol, "Equity", Contract.UsStock(symbol), BrokerKind.Simulated);

    private sealed class Teardown(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>No instruments, so the shell reports "connect a broker first" rather than throwing.
    /// The tests here are about the canvas seam, not about loading a universe.</summary>
    private sealed class EmptyRepository : IMarketDataRepository
    {
        public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TradableInstrument>>([]);

        public Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
            Contract contract, BrokerKind broker, BarSize barSize,
            TimeSpan duration, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
            Contract contract, BrokerKind broker, BarSize barSize,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
            Contract contract, BrokerKind broker,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
            Contract contract, BrokerKind broker, int levels = 10,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
