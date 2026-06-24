using System.Reactive.Linq;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenAPI.Net;
using TradingTerminal.Core.Brokers.CTrader;

namespace TradingTerminal.Infrastructure.CTrader;

/// <summary>
/// Implementation of <see cref="ICTraderAccountDiscovery"/>. Opens a short-lived
/// <see cref="OpenClient"/> session, app-auths with the OAuth client credentials, and asks
/// Spotware to enumerate every trading account the access token is permitted to drive. The
/// connection is torn down before returning — the form VM does not hold a live broker session
/// just to populate a picker.
/// </summary>
public sealed class CTraderAccountDiscoveryService : ICTraderAccountDiscovery
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<CTraderAccountDiscoveryService> _logger;

    public CTraderAccountDiscoveryService(ILogger<CTraderAccountDiscoveryService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<CTraderDiscoveredAccount>> DiscoverAsync(
        string host,
        int port,
        string clientId,
        string clientSecret,
        string accessToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("ClientId is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(clientSecret)) throw new ArgumentException("ClientSecret is required.", nameof(clientSecret));
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("AccessToken is required.", nameof(accessToken));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DiscoveryTimeout);
        var token = timeoutCts.Token;

        using var client = new OpenClient(host, port, TimeSpan.FromSeconds(10));
        await client.Connect().WaitAsync(token).ConfigureAwait(false);

        // App-auth first — Spotware rejects ProtoOAGetAccountListByAccessTokenReq otherwise.
        await SendAndAwaitAsync<ProtoOAApplicationAuthRes>(
            client,
            new ProtoOAApplicationAuthReq
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
            },
            token).ConfigureAwait(false);

        var accountsRes = await SendAndAwaitAsync<ProtoOAGetAccountListByAccessTokenRes>(
            client,
            new ProtoOAGetAccountListByAccessTokenReq
            {
                AccessToken = accessToken,
            },
            token).ConfigureAwait(false);

        // ProtoOACtidTraderAccount in OpenAPI.Net 1.4.4 only exposes CtidTraderAccountId + IsLive
        // (no broker-title field on this type). Leave BrokerTitle null — the picker formats it
        // gracefully when the field is missing.
        var accounts = accountsRes.CtidTraderAccount
            .Select(a => new CTraderDiscoveredAccount(
                AccountId: (long)a.CtidTraderAccountId,
                IsLive: a.IsLive,
                BrokerTitle: null))
            .ToList();

        _logger.LogInformation(
            "cTrader account discovery returned {Count} account(s) from {Host}:{Port}",
            accounts.Count, host, port);

        return accounts;
    }

    /// <summary>
    /// Sends one request and resolves when either the expected response type OR a
    /// <c>ProtoOAErrorRes</c> arrives on the OpenClient stream. The transient connection
    /// has no overlapping in-flight requests, so type-filtering is sufficient — no need
    /// for the per-msgId TCS map that <c>RealCTraderClient</c> uses.
    /// </summary>
    private static async Task<TRes> SendAndAwaitAsync<TRes>(
        OpenClient client,
        IMessage request,
        CancellationToken ct)
        where TRes : class, IMessage
    {
        var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var responseSub = client.OfType<TRes>().Subscribe(r => tcs.TrySetResult(r));
        using var errorSub = client.OfType<ProtoOAErrorRes>().Subscribe(err => tcs.TrySetResult(err));
        using var ctReg = ct.Register(() => tcs.TrySetCanceled());

        await client.SendMessage((dynamic)request).ConfigureAwait(false);

        var result = await tcs.Task.ConfigureAwait(false);
        if (result is TRes typed) return typed;
        if (result is ProtoOAErrorRes errRes)
            throw new InvalidOperationException(
                $"cTrader error {errRes.ErrorCode}: {errRes.Description}");
        throw new InvalidOperationException($"cTrader: unexpected response {result.GetType().Name}");
    }
}
