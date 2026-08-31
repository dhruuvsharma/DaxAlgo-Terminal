namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// What a user typed into a "base URL" field, made usable -- or refused before HttpClient sees it.
///
/// <para>Shared because both wire clients take the same field from the same person. The
/// OpenAI-compatible one is where bring-your-own-provider URLs actually get typed, but Anthropic's is
/// settable too, for a proxy, and a bad value there failed in exactly the same way.</para>
///
/// <para>Public because the settings page needs the same answer BEFORE it sends anything: told only
/// afterwards, a typo is indistinguishable from a provider that returned nothing.</para>
/// </summary>
public static class CodegenBaseUrl
{
    /// <summary>
    /// The stand-in a preset puts where only the user can supply the value.
    ///
    /// <para>Azure is the one provider whose preset cannot work as shipped: the resource name is part of
    /// the host, so the URL is a template rather than an address. It parses as a perfectly good absolute
    /// https URL, which means every syntactic check passes and the first thing the user sees is a DNS
    /// failure that says nothing about the placeholder they left in.</para>
    ///
    /// <para>Matched as the whole token rather than a "YOUR-" prefix, so a real host that happens to
    /// contain those letters is not refused.</para>
    /// </summary>
    public const string Placeholder = "YOUR-RESOURCE";

    /// <summary>True when a base URL is still the template it shipped as.</summary>
    public static bool IsUnedited(string? baseUrl) =>
        baseUrl is not null && baseUrl.Contains(Placeholder, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the text names this machine, so the repaired scheme should be plain http.</summary>
    private static bool LooksLoopback(string text)
    {
        var host = text.Split('/', 2)[0];

        return host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.", StringComparison.Ordinal)
            || host.StartsWith("[::1]", StringComparison.Ordinal)
            || host.StartsWith("0.0.0.0", StringComparison.Ordinal);
    }

    /// <summary>
    /// The base URL as an absolute http(s) URI, or null when what was typed cannot become one.
    ///
    /// <para>Checked once, up front, because the alternative is finding out inside HttpClient: a
    /// relative or odd-scheme URI throws <see cref="InvalidOperationException"/> or
    /// <see cref="NotSupportedException"/> from the send, and neither is what the callers are told to
    /// expect -- <c>ListModelsAsync</c> promises a failed lookup is an empty list, and the
    /// settings page relies on that promise with no catch of its own.</para>
    /// </summary>
    public static Uri? TryAbsolute(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;

    /// <summary>
    /// Takes what a user pasted and returns the base this client can append to.
    ///
    /// <para><b>The mistake this exists for is the natural one.</b> Every provider's quickstart shows
    /// the <i>full</i> endpoint — NVIDIA calls it <c>invoke_url</c>, OpenAI shows the curl line — so a
    /// user configuring a provider pastes
    /// <c>https://integrate.api.nvidia.com/v1/chat/completions</c> into a field labelled "base URL".
    /// This client then appends its own path and requests
    /// <c>.../chat/completions/chat/completions</c>, which 404s with no body worth reading. It looks
    /// like a broken provider or a rejected key, and it has already cost one setup.</para>
    ///
    /// <para>So a trailing well-known path is stripped rather than trusted. Being forgiving here is
    /// cheap; the alternative is every user rediscovering the same 404.</para>
    /// </summary>
    public static string Normalise(string? baseUrl)
    {
        var text = (baseUrl ?? string.Empty).Trim();
        if (text.Length == 0) return text;

        // A base URL with no scheme is the commonest paste of all: every local-runtime quickstart
        // writes "localhost:1234/v1", and a field labelled "base URL" invites exactly that. Uri reads
        // it as the SCHEME "localhost", and HttpClient then throws NotSupportedException out of the
        // lookup instead of failing it -- so it is repaired here rather than rejected.
        //
        // http for a loopback host because that is what LM Studio, vLLM and Ollama serve; https for
        // anything else, because a public endpoint that only speaks http would be handing the user's
        // API key to the network in clear text.
        //
        // Before the trailing slash is trimmed, or "http://" trims down to "http:", reads as having no
        // scheme, and comes back out as "https://http:" -- a URL that parses, so it would look
        // configured right up until the request.
        if (!text.Contains("://", StringComparison.Ordinal))
            text = (LooksLoopback(text) ? "http://" : "https://") + text;

        text = text.TrimEnd('/');

        // Longest first: /v1/chat/completions must lose the whole tail, not just /completions.
        foreach (var suffix in (string[])["/chat/completions", "/completions", "/responses"])
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return text[..^suffix.Length].TrimEnd('/');
        }

        return text;
    }
}
