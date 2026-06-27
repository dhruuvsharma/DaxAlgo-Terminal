using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// A broker-neutral, user-facing tradable instrument: a display label, a grouping
/// <see cref="Category"/> (used by the instrument-picker's grouped view), the
/// <see cref="Contract"/> it resolves to for subscriptions and orders, and the source
/// <see cref="Broker"/> that supplied it (so multi-broker UIs can show "ES — IB" vs
/// "ES — cTrader" and route subscribe/historical calls back to the right backend).
///
/// Brokers that can enumerate their tradable universe (Alpaca via <c>ListAssets</c>,
/// cTrader via <c>ProtoOASymbolsListReq</c>) return live lists; brokers whose APIs don't
/// cleanly enumerate (IB, NinjaTrader) return a curated catalog
/// (<see cref="CuratedInstrumentCatalog"/>).
/// </summary>
public sealed record TradableInstrument(
    string DisplayName,
    string Category,
    Contract Contract,
    BrokerKind Broker);
