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

    /// <summary>Stops this kernel instance.</summary>
    Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;
}
