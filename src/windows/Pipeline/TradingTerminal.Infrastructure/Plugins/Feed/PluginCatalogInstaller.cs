using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>
/// Installs a plugin chosen from the marketplace catalog. It downloads the <c>.daxplugin</c> from the
/// feed's URL, checks the downloaded bytes against the sha256 the SIGNED index declared — binding the
/// trusted index to the bytes actually served, so a swapped or corrupted download is refused — and then
/// hands the verified package to <see cref="PluginInstaller.InstallFromPackage"/>. From there it runs the
/// identical package-integrity / manifest / SDK / trust / IL-scan gate chain as a hand-picked file, and
/// activates on the next restart like any other install. Never throws: every failure comes back as a
/// <see cref="PluginInstallResult"/> with <c>Success = false</c>.
/// </summary>
public static class PluginCatalogInstaller
{
    /// <summary>Ceiling on a downloaded package. A strategy plugin is a DLL plus a few private deps, not a
    /// bundle — anything past this is refused before it is committed to disk (guards a lying/absent
    /// Content-Length too).</summary>
    public const long MaxPackageBytes = 64L * 1024 * 1024;

    private const int CopyBufferBytes = 81920;

    /// <summary>Downloads, checksum-verifies, and installs <paramref name="version"/> into
    /// <paramref name="pluginsRoot"/> through the standard install gates.</summary>
    public static async Task<PluginInstallResult> InstallAsync(
        HttpClient http,
        PluginFeedVersion version,
        string pluginsRoot,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector,
        PluginStateStore? state = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(version.Url))
            return new PluginInstallResult(false, "This plugin has no download URL in the feed.");
        if (string.IsNullOrWhiteSpace(version.Sha256))
            return new PluginInstallResult(false,
                "This plugin has no checksum in the feed — refusing to install unverified bytes.");

        var tempPath = Path.Combine(
            Path.GetTempPath(), "daxalgo-dl-" + Guid.NewGuid().ToString("N") + DaxPluginPackage.Extension);
        try
        {
            var downloaded = await DownloadAsync(http, version.Url, tempPath, ct).ConfigureAwait(false);
            if (!downloaded.Success) return downloaded;

            var actual = PluginIntegrity.Sha256(tempPath);
            if (!string.Equals(actual, version.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "Feed download checksum mismatch for {Url}: index says {Expected}, downloaded {Actual}.",
                    version.Url, version.Sha256, actual);
                return new PluginInstallResult(false,
                    "The downloaded package does not match the checksum in the signed feed — install refused.");
            }

            return PluginInstaller.InstallFromPackage(tempPath, pluginsRoot, policy, inspector, state);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new PluginInstallResult(false, $"Download failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private static async Task<PluginInstallResult> DownloadAsync(
        HttpClient http, string url, string destPath, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new PluginInstallResult(false, $"Download returned HTTP {(int)response.StatusCode} for {url}.");
        if (response.Content.Headers.ContentLength is > MaxPackageBytes)
            return new PluginInstallResult(false, $"Package is larger than the {MaxPackageBytes / (1024 * 1024)} MB limit.");

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = File.Create(destPath);

        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxPackageBytes)
                return new PluginInstallResult(false, $"Package exceeds the {MaxPackageBytes / (1024 * 1024)} MB limit.");
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return new PluginInstallResult(true, "Downloaded.");
    }
}
