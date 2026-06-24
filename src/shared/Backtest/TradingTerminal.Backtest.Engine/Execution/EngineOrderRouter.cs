using System.Reactive.Subjects;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// The kernel-facing order seam for the backtester. Resolves each <see cref="OrderRequest"/>'s
/// <see cref="Contract"/> to a canonical <see cref="InstrumentId"/> against the run's
/// <see cref="Universe"/> (so an order targets the right book/position in a portfolio run), then
/// pushes it into the <see cref="SimulatedOrderBook"/>. Re-publishes order events on
/// <see cref="OrderEvents"/> for any external subscriber; the engine itself listens to the book
/// directly so it also gets the instrument tag.
/// </summary>
internal sealed class EngineOrderRouter : IOrderRouter
{
    private readonly SimulatedOrderBook _book;
    private readonly Universe _universe;
    private readonly Subject<OrderEvent> _events = new();

    public EngineOrderRouter(SimulatedOrderBook book, Universe universe)
    {
        _book = book;
        _universe = universe;
        _book.Event += (_, evt) => _events.OnNext(evt);
    }

    public IObservable<OrderEvent> OrderEvents => _events;

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(_book.Submit(request, Resolve(request.Contract)));

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        _book.Cancel(clientOrderId);
        return Task.CompletedTask;
    }

    private InstrumentId Resolve(Contract contract)
    {
        foreach (var spec in _universe.Instruments)
            if (string.Equals(spec.Contract.Symbol, contract.Symbol, StringComparison.OrdinalIgnoreCase))
                return spec.Id;
        return _universe.Primary.Id; // single-instrument runs (and unmatched symbols) target the primary
    }
}
