using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// OAuth 1.0a request signing with HMAC-SHA1 (RFC 5849) — E*TRADE's scheme, and the only one here old
/// enough to need it.
///
/// <para>Every request carries a signature over its method, its URL without the query, and every
/// parameter — query and <c>oauth_*</c> alike — percent-encoded, sorted, and joined. The key is the
/// consumer secret and the token secret, each encoded, joined by <c>&amp;</c>; before a token exists the
/// second half is empty but the <c>&amp;</c> stays.</para>
/// </summary>
internal static class OAuth1
{
    /// <summary>
    /// The <c>Authorization</c> header value for one request. <paramref name="url"/> is the full URL, query
    /// included — its parameters are signed too; <paramref name="extra"/> carries further <c>oauth_*</c>
    /// parameters: <c>oauth_callback</c> for a request token, <c>oauth_verifier</c> for an access token.
    /// Before a token exists, <paramref name="token"/> and <paramref name="tokenSecret"/> are empty.
    /// </summary>
    public static string Authorization(
        string method, string url, string consumerKey, string consumerSecret, string token, string tokenSecret,
        long timestamp, string nonce, params (string Name, string Value)[] extra)
    {
        var oauth = new List<(string Name, string Value)>
        {
            ("oauth_consumer_key", consumerKey),
            ("oauth_nonce", nonce),
            ("oauth_signature_method", "HMAC-SHA1"),
            ("oauth_timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("oauth_version", "1.0"),
        };
        if (!string.IsNullOrEmpty(token)) oauth.Add(("oauth_token", token));
        oauth.AddRange(extra);

        var signature = Signature(method, url, oauth, consumerSecret, tokenSecret);
        oauth.Add(("oauth_signature", signature));
        return "OAuth realm=\"\"," + string.Join(",", oauth.Select(p => $"{Encode(p.Name)}=\"{Encode(p.Value)}\""));
    }

    /// <summary>The base64 HMAC-SHA1 signature.</summary>
    public static string Signature(
        string method, string url, IEnumerable<(string Name, string Value)> oauth, string consumerSecret, string tokenSecret)
    {
        var key = Encoding.ASCII.GetBytes(Encode(consumerSecret) + "&" + Encode(tokenSecret));
        return Convert.ToBase64String(HMACSHA1.HashData(key, Encoding.ASCII.GetBytes(BaseString(method, url, oauth))));
    }

    /// <summary>The signature base string: <c>METHOD&amp;enc(base URL)&amp;enc(sorted params)</c>.</summary>
    public static string BaseString(string method, string url, IEnumerable<(string Name, string Value)> oauth)
    {
        var uri = new Uri(url);
        var baseUrl = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}"
            + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port)
            + uri.AbsolutePath;

        var parameters = oauth.Select(p => (Encode(p.Name), Encode(p.Value))).ToList();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            parameters.Add((Encode(name), Encode(value)));
        }

        var normalised = string.Join("&", parameters
            .OrderBy(p => p.Item1, StringComparer.Ordinal)
            .ThenBy(p => p.Item2, StringComparer.Ordinal)
            .Select(p => $"{p.Item1}={p.Item2}"));

        return $"{method.ToUpperInvariant()}&{Encode(baseUrl)}&{Encode(normalised)}";
    }

    /// <summary>RFC 3986 percent-encoding: everything but <c>A–Z a–z 0–9 - . _ ~</c>, in upper-case hex.
    /// <see cref="Uri.EscapeDataString(string)"/> already does exactly this.</summary>
    public static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>A random nonce, hex.</summary>
    public static string Nonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>A form-encoded token answer (<c>oauth_token=…&amp;oauth_token_secret=…</c>) as pairs.</summary>
    public static Dictionary<string, string> ReadForm(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Trim().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) values[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }

        return values;
    }
}

/// <summary>
/// Splits a byte stream of back-to-back JSON objects into whole objects, for HTTP streaming endpoints that
/// write one object after another on a response that never ends (TradeStation's quote and depth streams).
///
/// <para>Chunk boundaries fall anywhere — mid-object, mid-string, mid-escape — so the splitter tracks
/// depth, whether it is inside a string, and whether the last character was a backslash, and emits an
/// object only when its closing brace brings the depth back to zero. Whitespace and newlines between
/// objects are skipped, so newline-delimited JSON splits the same way.</para>
/// </summary>
internal sealed class JsonObjectSplitter
{
    /// <summary>An object larger than this is not a quote; a stream producing one is broken, and holding it
    /// would grow without limit.</summary>
    public const int MaxObjectBytes = 4 * 1024 * 1024;

    private readonly List<byte> _current = [];
    private int _depth;
    private bool _inString;
    private bool _escaped;

    /// <summary>Feeds bytes; returns every object they completed, as UTF-8 text.</summary>
    public IReadOnlyList<string> Push(ReadOnlySpan<byte> bytes)
    {
        List<string>? done = null;
        foreach (var b in bytes)
        {
            if (_depth == 0)
            {
                if (b != (byte)'{') continue;
                _current.Clear();
            }

            _current.Add(b);
            if (_current.Count > MaxObjectBytes)
                throw new InvalidDataException($"A streamed JSON object exceeded {MaxObjectBytes} bytes.");

            if (_inString)
            {
                if (_escaped) _escaped = false;
                else if (b == (byte)'\\') _escaped = true;
                else if (b == (byte)'"') _inString = false;
                continue;
            }

            switch (b)
            {
                case (byte)'"':
                    _inString = true;
                    break;
                case (byte)'{':
                case (byte)'[':
                    _depth++;
                    break;
                case (byte)'}':
                case (byte)']':
                    _depth--;
                    if (_depth == 0)
                    {
                        (done ??= []).Add(Encoding.UTF8.GetString([.. _current]));
                        _current.Clear();
                    }

                    break;
            }
        }

        return done ?? (IReadOnlyList<string>)[];
    }
}
