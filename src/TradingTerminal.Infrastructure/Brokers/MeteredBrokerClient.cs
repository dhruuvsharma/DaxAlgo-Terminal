using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// Decorator over an <see cref="IBrokerClient"/> that records one call against the shared
/// <see cref="IBrokerApiMeter"/> for each method invocation, then delegates. Transparent to
/// every caller — the inner broker keeps its identity (<see cref="Kind"/>, observables,
/// streaming semantics, exceptions).
///
/// <para>For <c>IAsyncEnumerable</c> methods we record on the outer call (i.e. when the consumer
/// invokes <c>Subscribe*Async</c>), not per emitted message. That matches what brokers actually
/// count for rate-limiting: one request RPC at subscription setup, then streaming response messages
/// that are free.</para>
/// </summary>
public sealed class MeteredBrokerClient : IBrokerClient
{
    private readonly IBrokerClient _inner;
    private readonly IBrokerApiMeter _meter;

    public MeteredBrokerClient(IBrokerClient inner, IBrokerApiMeter meter)
    {
        _inner = inner;
        _meter = meter;
    }

    public BrokerKind Kind => _inner.Kind;
    public IObservable<ConnectionState> ConnectionState => _inner.ConnectionState;
    public IObservable<OrderEvent> OrderEvents => _inner.OrderEvents;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(ConnectAsync));
        return _inner.ConnectAsync(ct);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(DisconnectAsync));
        return _inner.DisconnectAsync(ct);
    }

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(ListInstrumentsAsync));
        return _inner.ListInstrumentsAsync(ct);
    }

    public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(RequestHistoricalBarsAsync));
        return _inner.RequestHistoricalBarsAsync(contract, barSize, duration, ct);
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(SubscribeBarsAsync));
        return _inner.SubscribeBarsAsync(contract, barSize, ct);
    }

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(SubscribeTicksAsync));
        return _inner.SubscribeTicksAsync(contract, ct);
    }

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(SubscribeDepthAsync));
        return _inner.SubscribeDepthAsync(contract, levels, ct);
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(PlaceOrderAsync));
        return _inner.PlaceOrderAsync(request, ct);
    }

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        _meter.RecordCall(_inner.Kind, nameof(CancelOrderAsync));
        return _inner.CancelOrderAsync(clientOrderId, ct);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
