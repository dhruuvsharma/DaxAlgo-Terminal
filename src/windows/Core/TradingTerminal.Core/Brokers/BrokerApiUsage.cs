namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Snapshot of one broker's API-call activity, as reported by <see cref="IBrokerApiMeter.Snapshot"/>.
/// "API call" here means one invocation of a method on the broker client — the unit a broker
/// counts for rate-limiting purposes (request RPC, not response messages on a long-lived
/// subscription).
/// </summary>
/// <param name="Broker">Which broker this usage row is for.</param>
/// <param name="TotalCalls">Cumulative count since the meter started (typically app launch).</param>
/// <param name="CallsLastMinute">Calls recorded in the sliding 60-second window ending now.</param>
/// <param name="SoftLimitPerMinute">Heuristic soft cap for the broker — coloured chips light up
/// red as this is approached. Hard-coded per broker today (e.g. Alpaca 200/min, cTrader ~300/min);
/// will become configurable later. 0 = no known limit (NinjaTrader is local, no real cap).</param>
/// <param name="LastCallUtc">When the most recent call landed. Null when nothing has been called
/// since the meter started.</param>
public sealed record BrokerApiUsage(
    BrokerKind Broker,
    long TotalCalls,
    int CallsLastMinute,
    int SoftLimitPerMinute,
    DateTime? LastCallUtc);
