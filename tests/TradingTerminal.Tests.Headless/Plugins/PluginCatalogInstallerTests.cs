using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Plugins.Feed;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// The catalog installer (issue #25): a download whose bytes match the sha256 in the signed feed is
/// installed through the standard package gates; a mismatch, a bad URL, or an oversized/failed download is
/// refused without touching the plugins folder. The served package is real (garbage assembly bytes wrapped
/// in a valid .daxplugin) so the checksum + integrity path is exercised end to end — nothing executes.
/// </summary>
public sealed class PluginCatalogInstallerTests : IDisposable
{
    private const string Url = "https://feed.example/a-1.0.0.daxplugin";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "cataloginst-" + Guid.NewGuid().ToString("N"));
    private readonly string _pluginsRoot;

    public PluginCatalogInstallerTests()
    {
        _pluginsRoot = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_pluginsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_download_matching_the_feed_checksum_is_installed()
    {
        var (bytes, sha) = BuildPackage("Pack", "1.0.0");
        var http = new HttpClient(new StubHandler(bytes));

        var result = await PluginCatalogInstaller.InstallAsync(
            http, Version("1.0.0", sha), _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeTrue(result.Message);
        File.Exists(Path.Combine(_pluginsRoot, "Pack", "Pack.dll")).Should().BeTrue();
        File.Exists(Path.Combine(_pluginsRoot, "Pack", "plugin.json")).Should().BeTrue();
    }

    [Fact]
    public async Task A_checksum_mismatch_is_refused_and_nothing_is_installed()
    {
        var (bytes, _) = BuildPackage("Pack", "1.0.0");
        var http = new HttpClient(new StubHandler(bytes));

        var result = await PluginCatalogInstaller.InstallAsync(
            http, Version("1.0.0", "DEADBEEF"), _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("does not match the checksum");
        Directory.Exists(Path.Combine(_pluginsRoot, "Pack")).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_url_is_refused_before_any_network_call()
    {
        var http = new HttpClient(new StubHandler(throwOnSend: true));

        var result = await PluginCatalogInstaller.InstallAsync(
            http, Version("1.0.0", "AA") with { Url = "" }, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("no download URL");
    }

    [Fact]
    public async Task A_missing_checksum_is_refused()
    {
        var http = new HttpClient(new StubHandler(throwOnSend: true));

        var result = await PluginCatalogInstaller.InstallAsync(
            http, Version("1.0.0", "") , _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("no checksum");
    }

    [Fact]
    public async Task A_download_error_is_reported_not_thrown()
    {
        var http = new HttpClient(new StubHandler(status: HttpStatusCode.NotFound));

        var result = await PluginCatalogInstaller.InstallAsync(
            http, Version("1.0.0", "AA"), _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("HTTP 404");
    }

    [Fact]
    public async Task The_curated_trust_gate_still_applies_to_a_verified_download()
    {
        var (bytes, sha) = BuildPackage("Pack", "1.0.0");
        var http = new HttpClient(new StubHandler(bytes));

        var result = await PluginCatalogInstaller.InstallAsync(
            http, Version("1.0.0", sha), _pluginsRoot, PluginTrustPolicy.Curated(["AA11"]), new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("trust policy");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static PluginFeedVersion Version(string v, string sha) => new(v, SdkInfo.Version, Url, sha);

    /// <summary>Builds a valid .daxplugin (garbage assembly + manifest) and returns its bytes + sha256.</summary>
    private (byte[] Bytes, string Sha256) BuildPackage(string name, string version)
    {
        var sourceDir = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, name + ".dll"), [0xDE, 0xAD, 0xBE, 0xEF]);
        File.WriteAllText(Path.Combine(sourceDir, PluginManifest.FileName),
            $$"""{ "id":"{{name}}","name":"{{name}}","version":"{{version}}","targetSdkVersion":"{{SdkInfo.Version}}" }""");

        var output = Path.Combine(_root, $"{name}-{version}-{Guid.NewGuid():N}{DaxPluginPackage.Extension}");
        DaxPluginPackage.Write(sourceDir, name + ".dll", output);
        var bytes = File.ReadAllBytes(output);
        return (bytes, PluginIntegrity.Sha256(output));
    }

    private sealed class StubHandler(
        byte[]? body = null, bool throwOnSend = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (throwOnSend) throw new InvalidOperationException("network must not be called");
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new ByteArrayContent(body);
            return Task.FromResult(response);
        }
    }
}
