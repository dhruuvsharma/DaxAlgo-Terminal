using System.Collections.Concurrent;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// Default <see cref="IBrokerApiMeter"/>. Per-broker bucket holds a thread-safe queue of recent
/// call timestamps; reads prune anything older than the 60-second window then count remaining
/// entries. Total calls is an <see cref="Interlocked"/>-incremented counter.
///
/// <para>Soft-limit heuristics live here (per broker). They're conservative best-guesses; the
/// real limits depend on subscription tier and broker-side throttles that we can't introspect.
/// Treat them as "if you cross this, start worrying" lines.</para>
/// </summary>
public sealed class BrokerApiMeter : IBrokerApiMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Heuristic per-minute soft cap by broker. 0 = no known cap.</summary>
    private static readonly IReadOnlyDictionary<BrokerKind, int> SoftLimits =
        new Dictionary<BrokerKind, int>
        {
            // IB: historical-data is the tightest bucket (~60 requests / 10-min on most TWS
            // configurations → ~6/min sustained). Market-data lines are bounded separately.
            // 60/min picks a number that's slack for normal usage but flags hot loops.
            [BrokerKind.InteractiveBrokers] = 60,
            // NinjaTrader runs locally over NTDirect P/Invoke — no real network rate cap.
            [BrokerKind.NinjaTrader] = 0,
            // Spotware caps app traffic at ~5 req/sec. 300/min is the steady-state ceiling.
            [BrokerKind.CTrader] = 300,
            // Alpaca free tier: 200 REST req/min. WebSocket has its own subscription cap not
            // counted here.
            [BrokerKind.Alpaca] = 200,
        };

    private sealed class Bucket
    {
        public long Total;
        public DateTime? LastCallUtc;
        public readonly ConcurrentQueue<DateTime> Window = new();
    }

    private readonly ConcurrentDictionary<BrokerKind, Bucket> _buckets = new();

    public void RecordCall(BrokerKind broker, string method)
    {
        var bucket = _buckets.GetOrAdd(broker, _ => new Bucket());
        var now = DateTime.UtcNow;
        Interlocked.Increment(ref bucket.Total);
        bucket.LastCallUtc = now;
        bucket.Window.Enqueue(now);
    }

    public IReadOnlyList<BrokerApiUsage> Snapshot()
    {
        var now = DateTime.UtcNow;
        var cutoff = now - Window;
        var rows = new List<BrokerApiUsage>(_buckets.Count);

        // Stable order: by BrokerKind enum value so chips don't jump around between refreshes.
        foreach (var broker in _buckets.Keys.OrderBy(k => (int)k))
        {
            if (!_buckets.TryGetValue(broker, out var bucket)) continue;

            // Prune the window in place — cheap because timestamps are monotonic per producer
            // (one Enqueue per call) and we only peek/dequeue the oldest end.
            while (bucket.Window.TryPeek(out var oldest) && oldest < cutoff)
                bucket.Window.TryDequeue(out _);

            var soft = SoftLimits.TryGetValue(broker, out var s) ? s : 0;
            rows.Add(new BrokerApiUsage(
                Broker: broker,
                TotalCalls: Interlocked.Read(ref bucket.Total),
                CallsLastMinute: bucket.Window.Count,
                SoftLimitPerMinute: soft,
                LastCallUtc: bucket.LastCallUtc));
        }
        return rows;
    }
}
