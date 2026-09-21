using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// Downloads the installer a verified <see cref="UpdateManifest"/> names, and refuses to hand back
/// anything it cannot prove is the release the signer published.
///
/// <para>Three gates, none of them optional. <b>https</b>, checked here and not only at the feed,
/// because a signed manifest pointing at <c>http</c> still hands the user to whoever is on the path.
/// <b>SHA-256 over the received bytes</b>, computed while streaming and compared with the signed
/// manifest — this is the check that binds the file to the release, since the feed signature covers
/// the manifest and never the megabytes behind the URL. <b>Authenticode</b> plus an optional signer
/// pin, via <see cref="InstallerTrust"/>.</para>
///
/// <para>Everything stages under <c>%LocalAppData%/DaxAlgoTerminal/updates/staging/</c>. A partial or
/// rejected download is deleted rather than left for a later run to find, and installers left by
/// other releases are swept on the way in, so declining three releases does not accumulate three
/// few-hundred-megabyte files.</para>
/// </summary>
public sealed class HttpUpdateDownloader : IUpdateDownloader
{
    /// <summary>Read granularity: large enough not to churn, small enough to keep the progress bar
    /// moving and cancellation responsive on a slow link.</summary>
    private const int BufferBytes = 128 * 1024;

    private const long DefaultMaxBytes = 512L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly InstallerTrust _trust;
    private readonly string _stagingRoot;
    private readonly bool _allowed;
    private readonly long _maxBytes;
    private readonly ILogger<HttpUpdateDownloader> _logger;

    /// <param name="http">Client for the installer download; it must have no overall timeout, since a
    /// large release on a slow line would otherwise be killed part-way through.</param>
    /// <param name="signatures">The Authenticode inspector, injected so the trust rule is testable
    /// without a real signed installer.</param>
    /// <param name="stagingRoot">Directory the verified installer is written to.</param>
    /// <param name="allowed">False when automatic installing is switched off, or when the running
    /// version could not be determined — see <see cref="UpdateServiceCollectionExtensions"/>.</param>
    /// <param name="pinnedThumbprint">Optional exact signer thumbprint; empty accepts any Authenticode
    /// signature Windows trusts.</param>
    /// <param name="maxBytes">Ceiling on the download, enforced against bytes actually received.</param>
    /// <param name="logger">Where refusals are recorded; the user only ever sees a vague message.</param>
    public HttpUpdateDownloader(
        HttpClient http,
        IPluginSignatureInspector signatures,
        string stagingRoot,
        bool allowed,
        string? pinnedThumbprint,
        long maxBytes,
        ILogger<HttpUpdateDownloader> logger)
    {
        _http = http;
        _trust = new InstallerTrust(signatures, pinnedThumbprint);
        _stagingRoot = stagingRoot;
        _allowed = allowed;
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _logger = logger;
    }

    public bool CanInstall => _allowed;

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_allowed)
            return new UpdateDownloadResult(UpdateDownloadOutcome.NotAllowed,
                Detail: "Automatic installing is switched off for this build.");

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateDownloadResult(UpdateDownloadOutcome.NotOffered,
                Detail: $"The release offers no https installer URL: '{manifest.DownloadUrl}'.");

        var expectedHash = InstallerTrust.Normalize(manifest.Sha256);
        if (!InstallerTrust.IsSha256Hex(expectedHash))
            return new UpdateDownloadResult(UpdateDownloadOutcome.NotOffered,
                Detail: "The release manifest carries no usable sha256 for the installer.");

        string target;
        try
        {
            target = PrepareStaging(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateDownloadResult(UpdateDownloadOutcome.Failed,
                Detail: $"Could not prepare the staging directory: {ex.Message}");
        }

        // A previous attempt may already hold this exact release — the app was closed between the
        // download and the restart. Re-verifying costs seconds; a second download costs the whole file.
        if (File.Exists(target) && _trust.Verify(target, expectedHash) is null)
        {
            _logger.LogInformation("Reusing the already-verified installer for {Version}.", manifest.Version);
            return new UpdateDownloadResult(UpdateDownloadOutcome.Ok, target);
        }

        try
        {
            var failure = await FetchAsync(uri, target, expectedHash, manifest, progress, cancellationToken)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                Delete(target);
                return failure;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(target);
            return new UpdateDownloadResult(UpdateDownloadOutcome.Cancelled, Detail: "The download was cancelled.");
        }
        catch (Exception ex)
        {
            // Deliberately broad, matching HttpUpdateChecker: unreachable host, TLS rejection, a full
            // disk, a client disposed on shutdown. None of them may surface as an exception — the only
            // sane response is that the banner keeps offering the release-notes link.
            Delete(target);
            _logger.LogWarning(ex, "The update installer could not be downloaded.");
            return new UpdateDownloadResult(UpdateDownloadOutcome.Failed, Detail: ex.Message);
        }

        // The streamed hash already matched; this pass adds the Authenticode verdict.
        var untrusted = _trust.Verify(target);
        if (untrusted is not null)
        {
            _logger.LogWarning("Rejected the downloaded installer for {Version}: {Detail}",
                manifest.Version, untrusted.Detail);
            Delete(target);
            return new UpdateDownloadResult(untrusted.Outcome, Detail: untrusted.Detail);
        }

        _logger.LogInformation("Verified installer for {Version} staged at {Path}.", manifest.Version, target);
        return new UpdateDownloadResult(UpdateDownloadOutcome.Ok, target);
    }

    /// <summary>Streams the body to disk, hashing as it goes. Null on success, otherwise the failure
    /// to report.</summary>
    private async Task<UpdateDownloadResult?> FetchAsync(
        Uri uri,
        string target,
        string expectedHash,
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new UpdateDownloadResult(UpdateDownloadOutcome.Failed,
                Detail: $"The release host answered {(int)response.StatusCode} for the installer.");

        // The declared length only feeds the progress bar. The ceiling below is enforced against what
        // actually arrives, so a server that lies about its length or never stops is still cut off.
        var declared = response.Content.Headers.ContentLength;
        if (declared > _maxBytes)
            return new UpdateDownloadResult(UpdateDownloadOutcome.Failed,
                Detail: $"The installer declares {declared} bytes, over the {_maxBytes}-byte ceiling.");

        var total = declared ?? (manifest.SizeBytes > 0 ? manifest.SizeBytes : null);

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[BufferBytes];
        long received = 0;
        progress?.Report(new UpdateDownloadProgress(0, total));

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            received += read;
            if (received > _maxBytes)
                return new UpdateDownloadResult(UpdateDownloadOutcome.Failed,
                    Detail: $"The installer exceeded the {_maxBytes}-byte ceiling while downloading.");

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(received, total));
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
        {
            // The manifest is signed, so a mismatch means the file behind the URL is not the file the
            // signer released. That is hostile, not a flaky download, and is logged as such.
            _logger.LogWarning(
                "Installer hash mismatch for {Version}: expected {Expected}, got {Actual}.",
                manifest.Version, expectedHash, actual);
            return new UpdateDownloadResult(UpdateDownloadOutcome.HashMismatch,
                Detail: "The downloaded installer does not match the hash in the signed manifest.");
        }

        return null;
    }

    /// <summary>Creates the staging directory, sweeps installers left by other releases, and returns
    /// where this release's installer belongs.</summary>
    private string PrepareStaging(UpdateManifest manifest)
    {
        Directory.CreateDirectory(_stagingRoot);

        var name = $"DaxAlgo-Terminal-Setup-v{SanitizeVersion(manifest.Version)}.exe";
        var target = Path.Combine(_stagingRoot, name);

        foreach (var stale in Directory.EnumerateFiles(_stagingRoot))
        {
            if (string.Equals(Path.GetFileName(stale), name, StringComparison.OrdinalIgnoreCase)) continue;
            Delete(stale);
        }

        return target;
    }

    /// <summary>Keeps a hostile manifest from steering the staged file out of the staging directory.
    /// The version has already parsed as a <see cref="Version"/> before a download is ever offered, so
    /// this only has to survive whatever could slip past that.</summary>
    private static string SanitizeVersion(string version)
    {
        Span<char> buffer = stackalloc char[32];
        var length = 0;
        foreach (var c in version)
        {
            if (length == buffer.Length) break;
            if (char.IsAsciiDigit(c) || c == '.') buffer[length++] = c;
        }
        return length == 0 ? "unknown" : new string(buffer[..length]);
    }

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or unwritable staging file is not worth failing an update over: the next
            // download overwrites it, and nothing is ever run that has not just been verified.
            _logger.LogDebug(ex, "Could not delete the staged installer at {Path}.", path);
        }
    }
}
