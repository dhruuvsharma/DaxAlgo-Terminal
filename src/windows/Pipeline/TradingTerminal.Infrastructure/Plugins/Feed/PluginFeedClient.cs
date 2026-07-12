using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>The outcome of a feed refresh.</summary>
/// <param name="Index">The verified index in effect (may be a cached last-good), or null when there is
/// none (no feed configured / never fetched / cache empty).</param>
/// <param name="Updated">True when this refresh produced a NEWER verified index than before.</param>
/// <param name="FromCache">True when the result came from the offline cache, not a fresh fetch.</param>
public sealed record FeedRefreshResult(PluginIndex? Index, bool Updated, bool FromCache, string? Detail);

/// <summary>
/// Fetches the signed marketplace index in the background, verifies it against the pinned key
/// (<see cref="FeedSignatureVerifier"/>), and caches the last-good copy so the catalog works offline. It
/// NEVER blocks startup and NEVER throws: a network error, a tampered feed, or an unsigned feed all
/// degrade to the cached index (or "no feed"), logged, so the rest of the app is unaffected. The feed
/// index and its detached signature (<c>&lt;feed&gt;.sig</c>) are stored under
/// <c>%LocalAppData%/DaxAlgoTerminal/plugin-feed/</c> with the server ETag for conditional GETs.
/// </summary>
public sealed class PluginFeedClient
{
    private const string IndexFile = "plugins-index.json";
    private const string SigFile = "plugins-index.json.sig";
    private const string ETagFile = "plugins-index.etag";

    private readonly HttpClient _http;
    private readonly FeedSignatureVerifier _verifier;
    private readonly string _feedUrl;
    private readonly string _cacheDir;
    private readonly ILogger? _logger;

    public PluginFeedClient(HttpClient http, FeedSignatureVerifier verifier, string feedUrl, string cacheDirectory, ILogger? logger = null)
    {
        _http = http;
        _verifier = verifier;
        _feedUrl = feedUrl?.Trim() ?? string.Empty;
        _cacheDir = cacheDirectory;
        _logger = logger;
    }

    /// <summary>The verified index currently in effect (last successful fetch or cache), or null.</summary>
    public PluginIndex? Current { get; private set; }

    /// <summary>True when a feed is actually usable (URL + pinned key both set).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_feedUrl) && _verifier.IsConfigured;

    /// <summary>Fetches + verifies the index (conditional on ETag), caching the last-good copy. On any
    /// failure it falls back to the cached copy (re-verified). Returns what index is now in effect.</summary>
    public async Task<FeedRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new FeedRefreshResult(null, false, false, "No feed URL / pinned key configured.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _feedUrl);
            var etag = ReadCache(ETagFile) is { Length: > 0 } t ? t : null;
            if (etag is not null) request.Headers.IfNoneMatch.TryParseAdd(etag);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return UseCache("Feed unchanged (304).");

            if (!response.IsSuccessStatusCode)
                return UseCache($"Feed fetch returned {(int)response.StatusCode}.");

            var indexBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var signature = await FetchSignatureAsync(ct).ConfigureAwait(false);
            if (signature is null) return UseCache("Feed signature could not be fetched.");

            var verified = _verifier.Verify(indexBytes, signature);
            if (!verified.Success)
            {
                _logger?.LogWarning("Ignoring feed: {Reason}", verified.Detail);
                return UseCache($"Feed rejected: {verified.Detail}");
            }

            var newEtag = response.Headers.ETag?.ToString();
            WriteCache(indexBytes, signature, newEtag);
            Current = verified.Index;
            _logger?.LogInformation("Loaded plugin feed: {Count} plugins", verified.Index!.Plugins.Count);
            return new FeedRefreshResult(verified.Index, Updated: true, FromCache: false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger?.LogInformation("Feed offline ({Message}); using cache.", ex.Message);
            return UseCache($"Feed unreachable: {ex.Message}");
        }
    }

    /// <summary>Loads + verifies the cached last-good index (offline / 304 / rejected-fetch path).</summary>
    private FeedRefreshResult UseCache(string detail)
    {
        var indexBytes = ReadCacheBytes(IndexFile);
        var signature = ReadCache(SigFile);
        if (indexBytes is null || string.IsNullOrEmpty(signature))
            return new FeedRefreshResult(Current, false, true, detail + " No cached feed.");

        var verified = _verifier.Verify(indexBytes, signature);
        if (!verified.Success)
        {
            _logger?.LogWarning("Cached feed failed verification: {Reason}", verified.Detail);
            return new FeedRefreshResult(Current, false, true, detail + " Cached feed invalid.");
        }

        var updated = Current is null;
        Current = verified.Index;
        return new FeedRefreshResult(verified.Index, updated, FromCache: true, detail);
    }

    private async Task<string?> FetchSignatureAsync(CancellationToken ct)
    {
        try
        {
            var sig = await _http.GetStringAsync(_feedUrl + ".sig", ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(sig) ? null : sig.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // ── cache I/O (best-effort; a cache failure never breaks a refresh) ────────────────────────────
    private string CachePath(string file) => Path.Combine(_cacheDir, file);

    private void WriteCache(byte[] indexBytes, string signature, string? etag)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllBytes(CachePath(IndexFile), indexBytes);
            File.WriteAllText(CachePath(SigFile), signature);
            File.WriteAllText(CachePath(ETagFile), etag ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug("Could not cache feed: {Message}", ex.Message);
        }
    }

    private byte[]? ReadCacheBytes(string file)
    {
        try { return File.Exists(CachePath(file)) ? File.ReadAllBytes(CachePath(file)) : null; }
        catch { return null; }
    }

    private string? ReadCache(string file)
    {
        try { return File.Exists(CachePath(file)) ? File.ReadAllText(CachePath(file)) : null; }
        catch { return null; }
    }
}
