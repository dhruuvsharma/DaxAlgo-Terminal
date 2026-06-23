namespace TradingTerminal.Core.Brokers.CTrader;

/// <summary>
/// A single cTrader trading account associated with an OAuth access token, as returned by
/// <c>ProtoOAGetAccountListByAccessTokenReq</c>. Exposed in <see cref="ICTraderAccountDiscovery"/>
/// so the login form can present a picker without referencing Spotware's protobuf types.
/// </summary>
public sealed record CTraderDiscoveredAccount(
    long AccountId,
    bool IsLive,
    string? BrokerTitle);
