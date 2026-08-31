using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sdk;

/// <summary>
/// Data-only, event-driven strategy contract. The host supplies only <see cref="IStrategyRuntimeContext"/>
/// capabilities; model-portfolio targets must be submitted through its virtual book.
/// </summary>
public interface IStrategyKernel
{
    /// <summary>The declarative launch-time parameter schema.</summary>
    StrategyParameterSchema Schema { get; }

    /// <summary>The market-data streams required by this kernel.</summary>
    StrategyDataRequirement DataRequirement { get; }

    /// <summary>Initializes a fresh kernel instance.</summary>
    Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct);

    /// <summary>Processes an authorized quote.</summary>
    Task OnQuoteAsync(Quote quote, IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Processes an authorized trade-tape print.</summary>
    Task OnTradeAsync(TradePrint trade, IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Processes an authorized depth snapshot.</summary>
    Task OnDepthAsync(
        InstrumentId instrument,
        DepthSnapshot depth,
        IStrategyRuntimeContext context,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>Processes an authorized bar.</summary>
    Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Describes the current frame.
    ///
    /// <para>Called by the host when it renders — <b>not</b> when data arrives. The data callbacks
    /// run on a pump thread and may fire far faster than the display; this runs on the render thread
    /// with a live surface. So compute in the data callbacks, keep what the picture needs, and draw
    /// from it here.</para>
    ///
    /// <para>Must be <b>pure and fast</b>: the host may invoke it more than once per frame, and it
    /// blocks the UI while it runs. Default is to draw nothing, which is a perfectly good
    /// strategy - plenty of strategies are pure signal logic.</para>
    /// </summary>
    void Draw(IRenderSurface surface)
    {
    }

    /// <summary>
    /// How this unit's window body is divided into panels.
    ///
    /// <para>Default is <see cref="DaxAlgo.Sdk.Layout.UnitLayout.Single"/>: one panel filling the body,
    /// drawn by <see cref="Draw"/>. Most units want exactly that and should not override this.</para>
    ///
    /// <para>Override it when the unit genuinely needs several panels — a chart beside an order book,
    /// two books with an arbitrage strip between them — and give each panel its own draw callback.
    /// <see cref="Draw"/> is then unused, because the panels do the drawing.</para>
    ///
    /// <para>The host owns everything around the body: the parameter expander above and the activity
    /// log below are chrome, identical for every unit, and not something a layout can move or omit.</para>
    /// </summary>
    DaxAlgo.Sdk.Layout.UnitLayout Layout => DaxAlgo.Sdk.Layout.UnitLayout.Single;


    /// <summary>
    /// Verbs this unit offers, shown by the host as buttons beside the parameters. Empty by default,
    /// which is right for most units.
    ///
    /// <para>Declare one when something the viewer needs is an act rather than a value — reset the
    /// profile, clear the tape, re-centre. Bounded at <see cref="UnitAction.Maximum"/>; a malformed
    /// list is refused whole.</para>
    /// </summary>
    IReadOnlyList<UnitAction> Actions => [];

    /// <summary>
    /// Runs the action the viewer pressed.
    ///
    /// <para>Invoked by the runtime under the same gate as the data callbacks, so it may touch the same
    /// fields they do without a lock — which is the whole reason an action is an id and a callback here
    /// rather than a delegate the host holds. Keep it short for the same reason a data callback is
    /// short: it blocks the next event.</para>
    ///
    /// <para><paramref name="id"/> comes from the <see cref="UnitAction"/> that was pressed. An id you
    /// do not recognise is not an error — ignore it.</para>
    /// </summary>
    Task OnActionAsync(string id, IStrategyRuntimeContext context, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Stops this kernel instance.</summary>
    Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;
}
