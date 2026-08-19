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

    /// <summary>Stops this kernel instance.</summary>
    Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;
}
