using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Security;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// Reads the signed release manifest over HTTPS, verifies its detached signature against the pinned
/// key, and compares the published version with the running one.
///
/// Offline-first, mirroring the marketplace feed: the last manifest that verified is cached under
/// <c>%LocalAppData%/DaxAlgoTerminal/updates/</c> with the server ETag, so a conditional GET is cheap
/// and a missing network degrades to the cached answer instead of an error.
///
/// **It downloads nothing but the manifest.** See <see cref="IUpdateChecker"/> for why.
/// </summary>
public sealed class HttpUpdateChecker : IUpdateChecker
{
    private const string ManifestFile = "release.json";
    private const string SignatureFile = "release.json.sig";
    private const string ETagFile = "release.etag";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;
    private readonly PinnedEcdsaVerifier _signature;
    private readonly string _feedUrl;
    private readonly Version _current;
    private readonly string _cacheDir;
    private readonly ILogger<HttpUpdateChecker> _logger;

    public HttpUpdateChecker(
        HttpClient http,
        PinnedEcdsaVerifier signature,
        string feedUrl,
        Version current,
        string cacheDir,
        ILogger<HttpUpdateChecker> logger)
    {
        _http = http;
        _signature = signature;
        _feedUrl = feedUrl;
        _current = current;
        _cacheDir = cacheDir;
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_feedUrl) || !_signature.IsConfigured)
            return new UpdateCheckResult(UpdateOutcome.NotConfigured, _current,
                Detail: "No update feed URL or no pinned key.");

        // Refuse plaintext outright. The signature would still protect the content, but a plaintext
        // feed leaks which build every user is running to anyone on the path.
        if (!Uri.TryCreate(_feedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(UpdateOutcome.Failed, _current,
                Detail: $"The update feed URL must be absolute https: '{_feedUrl}'.");

        try
        {
            var fetched = await FetchAsync(uri, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
                return Evaluate(fetched.Value.Manifest, fetched.Value.Signature, fromCache: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Genuine shutdown, not a feed failure — the caller's loop owns it.
        }
        catch (Exception ex)
        {
            // Deliberately broad. Unreachable host, DNS failure, TLS rejection, request timeout, a
            // disposed client on shutdown — every one of them degrades to the cached answer. The
            // interface contract is that a dead or hostile feed can never surface as an exception.
            _logger.LogDebug(ex, "Update check could not reach the feed; falling back to the cache.");
        }

        var cached = ReadCache();
        if (cached is not null)
            return Evaluate(cached.Value.Manifest, cached.Value.Signature, fromCache: true);

        return new UpdateCheckResult(UpdateOutcome.Failed, _current,
            Detail: "The update feed was unreachable and no verified manifest is cached.");
    }

    /// <summary>Conditional GET of the manifest plus its detached signature. Null when unchanged (304).</summary>
    private async Task<(byte[] Manifest, string Signature)?> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var etag = ReadCacheFile(ETagFile);
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return null;
        response.EnsureSuccessStatusCode();

        var manifest = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var signature = await _http.GetStringAsync(uri + ".sig", ct).ConfigureAwait(false);

        // Only cache AFTER the signature verifies, so a hostile response can never poison the cache.
        if (_signature.Verify(manifest, signature, out _) == PinnedSignatureOutcome.Ok)
            WriteCache(manifest, signature, response.Headers.ETag?.ToString());

        return (manifest, signature);
    }

    private UpdateCheckResult Evaluate(byte[] manifestBytes, string signature, bool fromCache)
    {
        var outcome = _signature.Verify(manifestBytes, signature, out var detail);
        if (outcome == PinnedSignatureOutcome.NoPinnedKey)
            return new UpdateCheckResult(UpdateOutcome.NotConfigured, _current, Detail: detail);
        if (outcome != PinnedSignatureOutcome.Ok)
        {
            // A feed that serves a badly-signed manifest is treated as hostile, not as degraded.
            _logger.LogWarning("Update manifest rejected: {Detail}", detail);
            return new UpdateCheckResult(UpdateOutcome.Failed, _current, FromCache: fromCache, Detail: detail);
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(UpdateOutcome.Failed, _current, FromCache: fromCache,
                Detail: $"Malformed update manifest: {ex.Message}");
        }

        if (manifest is null)
            return new UpdateCheckResult(UpdateOutcome.Failed, _current, FromCache: fromCache,
                Detail: "The update manifest was empty.");
        if (manifest.SchemaVersion > UpdateManifest.SupportedSchemaVersion)
            return new UpdateCheckResult(UpdateOutcome.Failed, _current, FromCache: fromCache,
                Detail: $"Manifest schema v{manifest.SchemaVersion} is newer than this build understands.");
        if (!Version.TryParse(manifest.Version, out var published))
            return new UpdateCheckResult(UpdateOutcome.Failed, _current, FromCache: fromCache,
                Detail: $"Unparsable version '{manifest.Version}' (semver pre-release tags are not supported).");

        return published > _current
            ? new UpdateCheckResult(UpdateOutcome.UpdateAvailable, _current, manifest, fromCache)
            : new UpdateCheckResult(UpdateOutcome.UpToDate, _current, manifest, fromCache);
    }

    private (byte[] Manifest, string Signature)? ReadCache()
    {
        var manifest = ReadCacheBytes(ManifestFile);
        var signature = ReadCacheFile(SignatureFile);
        return manifest is null || string.IsNullOrWhiteSpace(signature) ? null : (manifest, signature);
    }

    private void WriteCache(byte[] manifest, string signature, string? etag)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllBytes(Path.Combine(_cacheDir, ManifestFile), manifest);
            File.WriteAllText(Path.Combine(_cacheDir, SignatureFile), signature);
            if (!string.IsNullOrWhiteSpace(etag))
                File.WriteAllText(Path.Combine(_cacheDir, ETagFile), etag);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not cache the update manifest; the next check just refetches.");
        }
    }

    private byte[]? ReadCacheBytes(string name)
    {
        try
        {
            var path = Path.Combine(_cacheDir, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private string? ReadCacheFile(string name)
    {
        var bytes = ReadCacheBytes(name);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes).Trim();
    }
}
