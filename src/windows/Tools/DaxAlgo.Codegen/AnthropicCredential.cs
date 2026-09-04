using System.Net.Http;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// How a request to the Anthropic API proves who it is.
///
/// <para>Two mechanisms, and they are NOT interchangeable header values. An API key goes on
/// <c>x-api-key</c>; an OAuth access token goes on <c>Authorization: Bearer</c> <b>and</b> requires
/// <c>anthropic-beta: oauth-2025-04-20</c> — <c>/v1/messages</c> rejects the token without it.
/// Converting one to the other is a change of headers, not a swap of secret.</para>
///
/// <para><b>The OAuth token is fetched per request rather than captured.</b> It is short-lived and is
/// not refreshed for you, so a client that read it once at construction would work for a while and then
/// start failing auth on a session the user had not touched. The delegate re-asks whoever owns the
/// login — for the CLI that is <c>ant auth print-credentials</c>, which refreshes if needed.</para>
/// </summary>
public sealed class AnthropicCredential
{
    /// <summary>The beta the OAuth path requires. Endpoint-dependent in principle; sent always, so a
    /// request does not start failing because it moved to a different endpoint.</summary>
    public const string OAuthBeta = "oauth-2025-04-20";

    private readonly string? _apiKey;
    private readonly Func<CancellationToken, Task<string?>>? _token;
    private readonly Func<bool>? _available;

    private AnthropicCredential(
        string? apiKey, Func<CancellationToken, Task<string?>>? token, Func<bool>? available)
    {
        _apiKey = apiKey;
        _token = token;
        _available = available;
    }

    /// <summary>A long-lived API key, billed to the key's own organisation.</summary>
    public static AnthropicCredential Key(string? apiKey) => new(apiKey, null, null);

    /// <summary>
    /// A short-lived OAuth access token, re-read on every request.
    /// </summary>
    /// <param name="token">Returns a current access token, or null when nobody is signed in.</param>
    /// <param name="available">Cheap test for whether signing in is even possible — the CLI being
    /// installed. Separate from <paramref name="token"/> because the picker asks on every keystroke and
    /// must not pay a process launch to draw a row.</param>
    public static AnthropicCredential OAuth(
        Func<CancellationToken, Task<string?>> token, Func<bool>? available = null) =>
        new(null, token ?? throw new ArgumentNullException(nameof(token)), available);

    /// <summary>True when this credential could plausibly authenticate. For OAuth that is only whether a
    /// provider exists — whether anyone is actually signed in costs a process launch and is answered by
    /// <see cref="ApplyAsync"/> at the moment it matters.</summary>
    public bool IsConfigured => _token is not null
        ? _available?.Invoke() ?? true
        : !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>True when this is the OAuth path, so a caller can explain the sign-in rather than
    /// telling somebody to paste a key they do not have.</summary>
    public bool IsOAuth => _token is not null;

    /// <summary>
    /// Puts the right headers on a request. False means there was no usable credential and the caller
    /// should fail with an explanation rather than send an unauthenticated request.
    /// </summary>
    public async Task<bool> ApplyAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_token is not null)
        {
            var token = await _token(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return false;

            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
            return true;
        }

        if (string.IsNullOrWhiteSpace(_apiKey)) return false;

        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        return true;
    }
}
