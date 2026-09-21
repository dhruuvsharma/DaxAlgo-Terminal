using System.Text;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The wire contract between the release workflow and this app.
///
/// <para>`release.yml` builds the manifest as a PowerShell hashtable and writes it with
/// <c>ConvertTo-Json</c>. Nothing in either repository's build fails if a key there is spelled
/// differently from the <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> here —
/// the property simply binds to its default. A mistyped <c>version</c> would deserialize to the empty
/// string, fail to parse, and report the feed as malformed; a mistyped <c>sha256</c> would silently
/// produce a release that can never be installed, because a manifest with no hash is not offered.</para>
///
/// <para>So the literal below is the shape the workflow actually emits, kept here as the thing that
/// breaks first when the two drift.</para>
/// </summary>
public sealed class ReleaseFeedContractTests
{
    /// <summary>Exactly what `Generate and sign the update feed` writes, field for field.</summary>
    private const string PublishedFeed = """
        {
          "schemaVersion": 1,
          "version": "1.4.0",
          "publishedUtc": "2026-09-21T04:31:07.4812345Z",
          "releaseNotesUrl": "https://github.com/dhruuvsharma/DaxAlgo-Terminal-Pro/releases/tag/v1.4.0",
          "downloadUrl": "https://daxalgo.ai/download/v1.4.0/DaxAlgo-Terminal-Setup-v1.4.0.exe",
          "sha256": "9f2c8f4a1b3d5e6f70819a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f",
          "sizeBytes": 214748364
        }
        """;

    private static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(Encoding.UTF8.GetBytes(json))!;

    [Fact]
    public void Every_field_the_release_workflow_publishes_binds()
    {
        var manifest = Parse(PublishedFeed);

        manifest.SchemaVersion.Should().Be(1);
        manifest.Version.Should().Be("1.4.0");
        manifest.PublishedUtc.Should().NotBeNull();
        manifest.ReleaseNotesUrl.Should().StartWith("https://");
        manifest.DownloadUrl.Should().Be(
            "https://daxalgo.ai/download/v1.4.0/DaxAlgo-Terminal-Setup-v1.4.0.exe");
        manifest.Sha256.Should().HaveLength(64);
        manifest.SizeBytes.Should().Be(214748364);
    }

    [Fact]
    public void The_published_download_url_matches_the_route_the_site_serves()
    {
        // daxalgo-web's Worker matches /download/v<x.y.z>/<file> with a strict regex and 404s
        // anything else, so a manifest whose URL does not fit that shape describes a download that
        // cannot be fetched — and the app would only discover it mid-update.
        var manifest = Parse(PublishedFeed);

        var uri = new Uri(manifest.DownloadUrl);
        uri.Scheme.Should().Be(Uri.UriSchemeHttps);
        uri.AbsolutePath.Should().MatchRegex(@"^/download/v\d+(\.\d+){1,3}/[A-Za-z0-9._-]+$");
    }

    [Fact]
    public void The_feed_url_and_its_signature_sit_where_the_site_serves_them()
    {
        // HttpUpdateChecker fetches the signature by appending ".sig" to the feed URL. The Worker
        // serves /updates/<edition>/release.json and release.json.sig and nothing else under
        // /updates/, so both spellings have to fall inside that route.
        const string feed = "https://daxalgo.ai/updates/pro/release.json";
        var route = new System.Text.RegularExpressions.Regex(
            @"^/updates/(pro|basic)/release\.json(\.sig)?$");

        route.IsMatch(new Uri(feed).AbsolutePath).Should().BeTrue();
        route.IsMatch(new Uri(feed + ".sig").AbsolutePath).Should().BeTrue();
    }

    [Fact]
    public void An_unknown_field_from_a_newer_publisher_is_tolerated()
    {
        // The manifest is a wire contract: a future workflow may add a field, and an older build must
        // read what it understands rather than reject the release outright.
        var manifest = Parse("""
            {"schemaVersion":1,"version":"1.4.0","sha256":"","channel":"beta","minimumOs":"10.0.19041"}
            """);

        manifest.Version.Should().Be("1.4.0");
    }
}
