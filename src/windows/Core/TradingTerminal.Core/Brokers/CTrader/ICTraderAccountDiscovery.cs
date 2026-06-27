namespace TradingTerminal.Core.Brokers.CTrader;

/// <summary>
/// One-shot helper used by the login form to enumerate the trading accounts attached to an
/// OAuth access token, so the user doesn't need to hunt down their <c>ctidTraderAccountId</c>
/// manually. Implementations open a transient Open API 2.0 session, app-auth, call
/// <c>ProtoOAGetAccountListByAccessTokenReq</c>, then close the connection.
/// </summary>
public interface ICTraderAccountDiscovery
{
    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/>, app-auths with the supplied
    /// OAuth client credentials, and asks Spotware which trading accounts the access token can drive.
    /// </summary>
    /// <param name="host">Spotware endpoint host (<c>demo.ctraderapi.com</c> or <c>live.ctraderapi.com</c>).</param>
    /// <param name="port">Spotware endpoint port (typically 5035).</param>
    /// <param name="clientId">OAuth application client id.</param>
    /// <param name="clientSecret">OAuth application client secret.</param>
    /// <param name="accessToken">OAuth access token.</param>
    /// <param name="ct">Cancellation token. The implementation should also apply an internal timeout.</param>
    Task<IReadOnlyList<CTraderDiscoveredAccount>> DiscoverAsync(
        string host,
        int port,
        string clientId,
        string clientSecret,
        string accessToken,
        CancellationToken ct = default);
}
