using System.Collections.Concurrent;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// The latest quote the terminal's own market data has for an instrument, as a reference price for a broker book
/// whose broker offers none over its order API (Tradovate, tastytrade, IB).
///
/// <para>The engine needs a reference price younger than fifteen seconds before it will size a market order or a
/// strategy target, and refuses without one. A strategy window streams its instrument into the hub anyway, so
/// that is where the price comes from; a book with no live feed for its instrument still gets no price, and
/// its market orders are still refused rather than sized on a guess.</para>
///
/// <para>Subscribed on first use per instrument and kept for the life of the engine; bounded.</para>
/// </summary>
internal sealed class HubReferencePrices : IDisposable
{
    private const int MaximumInstruments = 64;

    private readonly IMarketDataHub _hub;
    private readonly ConcurrentDictionary<int, (decimal Price, DateTime AtUtc)> _latest = new();
    private readonly Dictionary<int, IDisposable> _subscriptions = [];
    private int _disposed;

    public HubReferencePrices(IMarketDataHub hub) => _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    /// <summary>The latest mid for <paramref name="instrument"/>, or null when none has arrived yet.</summary>
    public RoutePrice? Latest(InstrumentId instrument)
    {
        Watch(instrument);
        return _latest.TryGetValue(instrument.Value, out var latest) ? new RoutePrice(latest.Price, latest.AtUtc) : null;
    }

    /// <summary>Starts keeping <paramref name="instrument"/>'s latest quote.</summary>
    public void Watch(InstrumentId instrument)
    {
        if (instrument.IsNone || Volatile.Read(ref _disposed) != 0)
            return;
        lock (_subscriptions)
        {
            if (_subscriptions.ContainsKey(instrument.Value) || _subscriptions.Count >= MaximumInstruments)
                return;
            try
            {
                _subscriptions[instrument.Value] = _hub.Quotes(instrument).Subscribe(new Observer(this));
            }
            catch
            {
                // A hub that cannot stream this instrument leaves the book without a fallback price.
            }
        }
    }

    private void OnQuote(Quote quote)
    {
        var mid = quote.Mid;
        if (quote.Bid <= 0 || quote.Ask <= 0 || !double.IsFinite(mid) || mid <= 0)
            return;
        _latest[quote.InstrumentId.Value] = (decimal.Round((decimal)mid, 10), DateTime.SpecifyKind(quote.EventTimeUtc, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions.Values)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private sealed class Observer(HubReferencePrices owner) : IObserver<Quote>
    {
        public void OnNext(Quote value) => owner.OnQuote(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
