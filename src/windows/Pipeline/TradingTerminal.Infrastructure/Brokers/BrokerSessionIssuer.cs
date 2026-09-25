using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>One broker's sign-in: how it starts, and how its proof becomes a session.</summary>
internal interface IBrokerSignIn
{
    BrokerKind Broker { get; }

    SignInStyle Style { get; }

    /// <summary>The page to open, for a browser sign-in; null otherwise.</summary>
    string? SignInUrl(BrokerCredential app, string redirectUri) => null;

    /// <summary>The page to open, when building it takes a call first (an OAuth 1.0a request token).
    /// Defaults to <see cref="SignInUrl"/>.</summary>
    Task<string?> SignInUrlAsync(HttpClient http, BrokerCredential app, string redirectUri, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(SignInUrl(app, redirectUri));

    /// <summary>Exchanges the proof. May throw; the issuer turns any exception into a refusal.</summary>
    Task<SessionIssue> SignInAsync(HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// The one <see cref="IBrokerSessionIssuer"/>: dispatches each broker to its <see cref="IBrokerSignIn"/>.
/// Every failure — a refusal, a timeout, an unreachable host, a response in a shape nobody expected —
/// comes back as a <see cref="SessionIssue.Refused"/> carrying the reason, never as an exception into the
/// login window.
/// </summary>
internal sealed class BrokerSessionIssuer : IBrokerSessionIssuer, IDisposable
{
    private readonly Dictionary<BrokerKind, IBrokerSignIn> _signIns;
    private readonly HttpClient _http;
    private readonly ILogger<BrokerSessionIssuer> _logger;
    private readonly TimeProvider _time;

    public BrokerSessionIssuer(IEnumerable<IBrokerSignIn> signIns, ILogger<BrokerSessionIssuer> logger, TimeProvider? time = null, HttpClient? http = null)
    {
        _signIns = signIns.ToDictionary(s => s.Broker);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _http = http ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (http is null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
    }

    public bool Issues(BrokerKind broker) => _signIns.ContainsKey(broker);

    public SignInStyle StyleOf(BrokerKind broker) => _signIns.TryGetValue(broker, out var s) ? s.Style : SignInStyle.Token;

    public async Task<string?> SignInUrlAsync(BrokerKind broker, BrokerCredential app, string redirectUri, CancellationToken ct = default)
    {
        if (!_signIns.TryGetValue(broker, out var signIn)) return null;
        try
        {
            return await signIn.SignInUrlAsync(_http, app, redirectUri, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No page is the answer the form already handles; the reason goes to the log.
            _logger.LogWarning(ex, "{Broker} sign-in page could not be prepared.", broker);
            return null;
        }
    }

    public async Task<SessionIssue> SignInAsync(
        BrokerKind broker, BrokerCredential app, string proof, string redirectUri, CancellationToken ct = default)
    {
        if (!_signIns.TryGetValue(broker, out var signIn))
            return SessionIssue.Refused($"No sign-in is wired for {broker}.");

        try
        {
            var issue = await signIn.SignInAsync(_http, app, proof ?? string.Empty, redirectUri, _time.GetUtcNow(), ct).ConfigureAwait(false);
            if (issue.Ok) _logger.LogInformation("{Broker} issued a session.", broker);
            else _logger.LogWarning("{Broker} refused the sign-in: {Detail}", broker, issue.Detail);
            return issue;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Broker} sign-in failed.", broker);
            return SessionIssue.Refused(ex is TaskCanceledException ? $"{broker} did not answer in time." : ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Helpers the sign-ins share.</summary>
internal static class SignInProof
{
    /// <summary>
    /// The value of <paramref name="name"/> in what the user pasted: the whole redirected URL, just its
    /// query, or the bare value. Users paste whichever they can find, and all three are accepted — asking
    /// them to trim a URL correctly is asking for a wrong code.
    /// </summary>
    public static string Parameter(string proof, string name)
    {
        var text = proof.Trim();
        var query = text.Contains('?') ? text[(text.IndexOf('?') + 1)..] : text.Contains('=') ? text : null;
        if (query is null) return text;

        foreach (var pair in query.Split('&', '#'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && string.Equals(pair[..eq], name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }

        return string.Empty;
    }

    /// <summary>Lower-case hex SHA-256 of UTF-8 text.</summary>
    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A property at <paramref name="path"/> (dot-separated), as text, or null.</summary>
    public static string? Text(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            _ => null,
        };
    }

    /// <summary>POSTs <paramref name="content"/> and parses the answer, whatever its status — the brokers
    /// put their refusals in the body.</summary>
    public static async Task<(int Status, JsonElement Root, string Body)> PostAsync(
        HttpClient http, string url, HttpContent content, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await SendAsync(http, request, ct).ConfigureAwait(false);
    }

    public static async Task<(int Status, JsonElement Root, string Body)> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement.Clone();
        }
        catch (JsonException)
        {
            root = JsonDocument.Parse("{}").RootElement.Clone();
        }

        return ((int)response.StatusCode, root, body);
    }

    /// <summary>A refusal quoting the broker, or its status and a snippet when it said nothing usable.</summary>
    public static SessionIssue Refusal(int status, string body, params string?[] said)
    {
        var words = said.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        return SessionIssue.Refused(words is not null
            ? $"{words} (HTTP {status})."
            : $"HTTP {status}: {Snippet(body)}");
    }

    /// <summary>
    /// A response body fit to show a user: the first 200 characters of text, or the title of an HTML error
    /// page — Saxo and E*TRADE answer a refused sign-in with a whole page, and markup in the login window
    /// tells nobody anything.
    /// </summary>
    public static string Snippet(string body)
    {
        var text = body.Trim();
        if (text.StartsWith('<'))
        {
            var open = text.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
            var close = open < 0 ? -1 : text.IndexOf("</title>", open, StringComparison.OrdinalIgnoreCase);
            return open >= 0 && close > open
                ? System.Net.WebUtility.HtmlDecode(text[(open + 7)..close].Trim())
                : "(an error page)";
        }

        return text.Length > 200 ? text[..200] : text;
    }
}
