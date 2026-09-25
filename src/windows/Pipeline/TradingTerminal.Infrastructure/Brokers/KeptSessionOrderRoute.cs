using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// An order route for a broker whose API is reached through a signed-in session (Schwab, TradeStation,
/// tastytrade, E*TRADE, Tradovate, Saxo, IG, Questrade) rather than a key.
///
/// <para><b>One session, shared.</b> The route asks <see cref="SessionKeeper.Shared"/> for the broker's
/// keeper, so it and the broker's market-data client renew one token between them. Two keepers would each
/// spend the same rotating refresh token, and whichever renewed second would be refused.</para>
///
/// <para><b>Which environment a session reaches.</b> Some brokers sign a session into one environment —
/// IG's demo or live gateway, Saxo's simulation or live, tastytrade's sandbox. A card asking for the other
/// environment is refused with that said, rather than sending a paper order to a live account. TradeStation's
/// one token reaches both its live and SIM accounts.</para>
///
/// <para><b>A 401 is retried once</b> on a renewed session: the broker checked the token before it read the
/// request, so nothing was placed.</para>
/// </summary>
internal abstract class KeptSessionOrderRoute : OrderRouteBase
{
    protected KeptSessionOrderRoute(
        BrokerKind broker, IBrokerCredentialSource credentials, IBrokerSessionStore store, ILogger logger,
        TimeProvider? time = null, HttpMessageHandler? handler = null)
        : base(credentials, logger, time, handler)
    {
        Keeper = SessionKeeper.Shared(broker, credentials, store, RenewAsync, logger, time);
    }

    protected SessionKeeper Keeper { get; }

    /// <summary>Renews the session exactly as the broker's market-data client does — whichever of the two
    /// asks first creates the shared keeper with its own renewal.</summary>
    protected abstract Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct);

    /// <summary>Puts the session on a request. A bearer token by default.</summary>
    protected virtual void Authorize(HttpRequestMessage request, KeptSession session) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

    /// <summary>The environment the stored session is signed into, or null when one session reaches both.</summary>
    protected virtual RouteEnvironment? SessionEnvironment => null;

    /// <summary>The stored session, or a refusal telling the user to sign in — nothing was sent.</summary>
    protected async Task<KeptSession> SessionAsync(CancellationToken ct)
    {
        try
        {
            return await Keeper.CurrentAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new BrokerOrderRouteException($"{DisplayName}: {exception.Message}", isRejection: true, exception);
        }
    }

    /// <summary>Refuses a call for an environment the stored session does not reach.</summary>
    protected void RequireEnvironment(RouteEnvironment environment)
    {
        if (SessionEnvironment is not { } signedInto || signedInto == environment)
            return;
        var have = signedInto == RouteEnvironment.Live ? "the live account" : $"the {PaperEnvironmentName} environment";
        var want = environment == RouteEnvironment.Live ? "the live account" : $"the {PaperEnvironmentName} environment";
        throw new BrokerOrderRouteException(
            $"{DisplayName}: the stored session is signed into {have}, and this card trades {want}. Sign in to {want} in the login window.",
            isRejection: true);
    }

    /// <summary>What an authorised call answered, with the <c>Location</c> header for brokers that return a new
    /// order's id only there (Schwab).</summary>
    protected sealed record Answer(int Status, JsonElement Root, string Body, Uri? Location);

    /// <summary>Sends an authorised request for <paramref name="environment"/>, renewing and retrying once on a 401.</summary>
    protected async Task<Answer> CallAsync(RouteEnvironment environment, Func<KeptSession, HttpRequestMessage> build, CancellationToken ct)
    {
        RequireEnvironment(environment);
        var session = await SessionAsync(ct).ConfigureAwait(false);
        var answer = await SendOnceAsync(build, session, ct).ConfigureAwait(false);
        if (answer.Status != 401)
            return answer;
        Keeper.Invalidate(session);
        var renewed = await SessionAsync(ct).ConfigureAwait(false);
        return await SendOnceAsync(build, renewed, ct).ConfigureAwait(false);
    }

    private async Task<Answer> SendOnceAsync(Func<KeptSession, HttpRequestMessage> build, KeptSession session, CancellationToken ct)
    {
        using var request = build(session);
        Authorize(request, session);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new Answer((int)response.StatusCode, Parse(body), body, response.Headers.Location);
    }

    /// <summary>A GET of <paramref name="url"/>.</summary>
    protected Task<Answer> GetAsync(RouteEnvironment environment, string url, CancellationToken ct) =>
        CallAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get, url), ct);

    /// <summary>A request with a JSON body.</summary>
    protected static HttpRequestMessage Json(HttpMethod method, string url, JsonNode body) =>
        new(method, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
