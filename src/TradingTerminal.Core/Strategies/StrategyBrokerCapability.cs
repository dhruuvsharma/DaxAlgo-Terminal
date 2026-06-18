using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Core.Strategies;

/// <summary>
/// The broker capability matrix that backs <see cref="ITradingStrategy.SupportedBrokers"/>'s default:
/// which connected backends can actually serve the informative-extra data feeds. Bars + L1 are the
/// universal baseline (every broker), so a strategy that needs only those is broker-agnostic and
/// declares no specific brokers. Mirrors the per-broker capability documented on
/// <see cref="IBrokerClient"/> / CLAUDE.md rule 10 — keep this in sync if a backend gains a feed.
/// </summary>
public static class StrategyBrokerCapability
{
    /// <summary>
    /// Backends that expose a real trade tape (<c>SubscribeTradesAsync</c> returns a stream rather
    /// than throwing <see cref="NotSupportedException"/>): Interactive Brokers, Binance, Ironbeam.
    /// NinjaTrader / cTrader / Alpaca have no tape in this build.
    /// </summary>
    public static readonly IReadOnlyList<BrokerKind> TapeBrokers = new[]
    {
        BrokerKind.InteractiveBrokers,
        BrokerKind.Binance,
        BrokerKind.IronBeam,
    };

    /// <summary>
    /// Backends that serve Level-2 market depth: Interactive Brokers, cTrader, Ironbeam, Upstox,
    /// and the public crypto exchanges (Binance / Coinbase / Bybit / Kraken / OKX).
    /// </summary>
    public static readonly IReadOnlyList<BrokerKind> DepthBrokers = new[]
    {
        BrokerKind.InteractiveBrokers,
        BrokerKind.CTrader,
        BrokerKind.IronBeam,
        BrokerKind.Upstox,
        BrokerKind.Binance,
        BrokerKind.Coinbase,
        BrokerKind.Bybit,
        BrokerKind.Kraken,
        BrokerKind.Okx,
    };

    /// <summary>
    /// The brokers that can fully drive a strategy with the given data appetite. Tape-requiring
    /// strategies map to <see cref="TapeBrokers"/>, depth-requiring to <see cref="DepthBrokers"/>,
    /// and L1/Bars-only strategies to the empty list (broker-agnostic — runs on any backend).
    /// </summary>
    public static IReadOnlyList<BrokerKind> ForRequirement(StrategyDataRequirement requirement)
    {
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape)) return TapeBrokers;
        if (requirement.HasFlag(StrategyDataRequirement.Depth)) return DepthBrokers;
        return Array.Empty<BrokerKind>();
    }
}
