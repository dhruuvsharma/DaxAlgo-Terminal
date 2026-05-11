using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// <see cref="IOrderRouter"/> backed by a <see cref="SimulatedOrderBook"/>. Synchronous —
/// the returned tasks complete immediately. The book's <c>Events</c> stream is exposed
/// directly as <see cref="OrderEvents"/>.
/// </summary>
public sealed class BacktestOrderRouter : IOrderRouter
{
    private readonly SimulatedOrderBook _book;

    public BacktestOrderRouter(SimulatedOrderBook book)
    {
        _book = book;
    }

    public IObservable<OrderEvent> OrderEvents => _book.Events;

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(_book.Submit(request));

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        _book.Cancel(clientOrderId);
        return Task.CompletedTask;
    }
}
