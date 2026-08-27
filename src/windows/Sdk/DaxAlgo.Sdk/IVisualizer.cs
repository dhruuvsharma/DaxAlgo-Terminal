using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sdk;

/// <summary>
/// Data-only visualizer computation contract. Visualizers auto-run when hosted and receive no book;
/// consequently this interface deliberately has no explicit Run method or trading output.
/// </summary>
public interface IVisualizer
{
    /// <summary>The declarative launch-time parameter schema.</summary>
    StrategyParameterSchema Schema { get; }

    /// <summary>The market-data streams required by this visualizer.</summary>
    StrategyDataRequirement DataRequirement { get; }

    /// <summary>Initializes a fresh, automatically hosted visualizer instance.</summary>
    Task OnStartAsync(IVisualizerContext context, CancellationToken ct);

    /// <summary>Processes an authorized quote.</summary>
    Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Processes an authorized trade-tape print.</summary>
    Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Processes an authorized depth snapshot.</summary>
    Task OnDepthAsync(
        InstrumentId instrument,
        DepthSnapshot depth,
        IVisualizerContext context,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>Processes an authorized bar.</summary>
    Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

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
    /// headless visualizer.</para>
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

    /// <summary>Stops this visualizer instance.</summary>
    Task OnStopAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
}
