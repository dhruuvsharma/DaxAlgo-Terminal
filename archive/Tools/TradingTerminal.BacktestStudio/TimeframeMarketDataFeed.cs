using System.Runtime.CompilerServices;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// Pro Studio feed decorator that adds completed bars at the selected interval while preserving the
/// raw quote/trade/depth events required by other strategies. Bar events are timestamped at bucket
/// close (their payload retains bucket-open time), so replay remains causal and the engine clock never
/// moves backwards.
/// </summary>
public sealed class TimeframeMarketDataFeed(IMarketDataFeed inner, BarSize timeframe) : IMarketDataFeed
{
    private readonly TimeSpan _interval = timeframe.ToTimeSpan();

    public async IAsyncEnumerable<MarketEvent> StreamAsync(
        RunSpec spec,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<InstrumentId, BarBucket>();
        var nativeBars = new HashSet<InstrumentId>();
        var lastTimestamp = DateTime.MinValue;

        await foreach (var marketEvent in inner.StreamAsync(spec, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spec.Data.FromUtc is { } fromUtc && marketEvent.TimestampUtc < fromUtc)
                continue;
            if (spec.Data.ToUtc is { } toUtc && marketEvent.TimestampUtc >= toUtc)
                break;
            lastTimestamp = marketEvent.TimestampUtc;

            foreach (var closed in CloseBefore(buckets, marketEvent.TimestampUtc))
                yield return closed;

            if (marketEvent.Kind == MarketEventKind.Bar)
            {
                if (marketEvent.Bar?.Size == timeframe)
                {
                    nativeBars.Add(marketEvent.Instrument);
                    buckets.Remove(marketEvent.Instrument);
                    yield return marketEvent;
                }
                continue;
            }

            if (!nativeBars.Contains(marketEvent.Instrument) &&
                TrySample(marketEvent, spec, out var price, out var volume, out var source))
            {
                if (!buckets.TryGetValue(marketEvent.Instrument, out var bucket))
                {
                    bucket = new BarBucket(
                        marketEvent.Instrument,
                        timeframe,
                        BucketOpen(marketEvent.TimestampUtc, _interval),
                        _interval,
                        price,
                        volume,
                        source);
                    buckets.Add(marketEvent.Instrument, bucket);
                }
                else
                {
                    bucket.Add(price, volume, source);
                }
            }

            yield return marketEvent;
        }

        if (lastTimestamp != DateTime.MinValue)
        {
            foreach (var bucket in buckets.Values.OrderBy(value => value.OpenTimeUtc))
                yield return bucket.Close(lastTimestamp);
        }
    }

    private static IEnumerable<MarketEvent> CloseBefore(
        Dictionary<InstrumentId, BarBucket> buckets,
        DateTime timestampUtc)
    {
        var closed = buckets.Values
            .Where(bucket => bucket.CloseTimeUtc <= timestampUtc)
            .OrderBy(bucket => bucket.CloseTimeUtc)
            .ThenBy(bucket => bucket.Instrument.Value)
            .ToArray();
        foreach (var bucket in closed)
        {
            buckets.Remove(bucket.Instrument);
            yield return bucket.Close(bucket.CloseTimeUtc);
        }
    }

    private static DateTime BucketOpen(DateTime timestampUtc, TimeSpan interval)
    {
        var utc = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        var ticks = utc.Ticks - utc.Ticks % interval.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static bool TrySample(
        MarketEvent marketEvent,
        RunSpec spec,
        out double price,
        out long volume,
        out BrokerKind source)
    {
        source = spec.Universe.Find(marketEvent.Instrument)?.Source ?? BrokerKind.Simulated;
        volume = 0;
        price = 0;
        switch (marketEvent.Kind)
        {
            case MarketEventKind.Quote when marketEvent.Quote is { } quote &&
                                                double.IsFinite(quote.Bid) &&
                                                double.IsFinite(quote.Ask) &&
                                                quote.Bid > 0 && quote.Ask > 0:
                price = (quote.Bid + quote.Ask) * 0.5;
                return true;
            case MarketEventKind.Trade when marketEvent.Trade is { } trade &&
                                                double.IsFinite(trade.Price) && trade.Price > 0:
                price = trade.Price;
                volume = Math.Max(0, trade.Size);
                source = trade.Source;
                return true;
            case MarketEventKind.Depth when marketEvent.Depth is { } depth &&
                                                double.IsFinite(depth.BestBid) &&
                                                double.IsFinite(depth.BestAsk) &&
                                                depth.BestBid > 0 && depth.BestAsk > 0:
                price = (depth.BestBid + depth.BestAsk) * 0.5;
                return true;
            default:
                return false;
        }
    }

    private sealed class BarBucket
    {
        private double _high;
        private double _low;
        private double _close;
        private long _volume;
        private BrokerKind _source;

        internal BarBucket(
            InstrumentId instrument,
            BarSize size,
            DateTime openTimeUtc,
            TimeSpan interval,
            double price,
            long volume,
            BrokerKind source)
        {
            Instrument = instrument;
            Size = size;
            OpenTimeUtc = openTimeUtc;
            CloseTimeUtc = openTimeUtc + interval;
            Open = price;
            _high = price;
            _low = price;
            _close = price;
            _volume = volume;
            _source = source;
        }

        internal InstrumentId Instrument { get; }
        internal BarSize Size { get; }
        internal DateTime OpenTimeUtc { get; }
        internal DateTime CloseTimeUtc { get; }
        internal double Open { get; }

        internal void Add(double price, long volume, BrokerKind source)
        {
            _high = Math.Max(_high, price);
            _low = Math.Min(_low, price);
            _close = price;
            _volume = checked(_volume + volume);
            _source = source;
        }

        internal MarketEvent Close(DateTime emittedAtUtc)
        {
            var bar = new OhlcvBar(
                Instrument,
                Size,
                OpenTimeUtc,
                Open,
                _high,
                _low,
                _close,
                _volume,
                _source,
                IsFinal: true);
            return new MarketEvent(emittedAtUtc, Instrument, MarketEventKind.Bar, Bar: bar);
        }
    }
}
