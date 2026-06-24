namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Records and reports broker API-call activity. Recorded from any thread (broker callback
/// threads, HTTP continuations, etc.); read from the UI thread by the header chip widget.
/// Implementations must be thread-safe for record + read.
///
/// <para>Granularity: one "call" per <c>IBrokerClient</c> method invocation. Streaming
/// subscriptions (e.g. <c>SubscribeTicksAsync</c>) count as one call at setup, not per emitted
/// message — that matches how brokers actually count requests for rate-limiting.</para>
/// </summary>
public interface IBrokerApiMeter
{
    /// <summary>Record one API call against the named broker. Cheap; runs on the calling thread.</summary>
    void RecordCall(BrokerKind broker, string method);

    /// <summary>Per-broker snapshot of recent activity. Returned in a stable iteration order.</summary>
    IReadOnlyList<BrokerApiUsage> Snapshot();
}
