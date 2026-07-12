using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins.Feed;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// The feed client: a correctly-signed fetch verifies + caches; a later outage or a tampered fetch falls
/// back to the cached last-good copy; and none of it throws — the app stays offline-first.
/// </summary>
public sealed class PluginFeedClientTests : IDisposable
{
    private const string FeedUrl = "https://feed.example/plugins-index.json";
    private const string Feed = """
        { "feedVersion": 1, "plugins": [ { "id": "a.plugin", "name": "A", "publisher": "P",
          "description": "d", "latest": { "version": "1.0.0", "sdkVersion": "0.1.0-alpha",
          "url": "https://x/a-1.0.0.daxplugin", "sha256": "AA" } } ] }
        """;

    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "feed-" + Guid.NewGuid().ToString("N"));
    private readonly string _publicKey;
    private readonly byte[] _feedBytes;
    private readonly string _signatureB64;

    public PluginFeedClientTests()
    {
        Directory.CreateDirectory(_cacheDir);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        _feedBytes = Encoding.UTF8.GetBytes(Feed);
        _signatureB64 = Convert.ToBase64String(ecdsa.SignData(_feedBytes, HashAlgorithmName.SHA256));
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private PluginFeedClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new FeedSignatureVerifier(_publicKey), FeedUrl, _cacheDir);

    [Fact]
    public async Task A_signed_feed_is_fetched_verified_and_cached()
    {
        var result = await Client(GoodFeedHandler()).RefreshAsync();

        result.Updated.Should().BeTrue();
        result.FromCache.Should().BeFalse();
        result.Index!.Plugins.Should().ContainSingle().Which.Id.Should().Be("a.plugin");
        File.Exists(Path.Combine(_cacheDir, "plugins-index.json")).Should().BeTrue("the last-good copy is cached");
    }

    [Fact]
    public async Task An_outage_after_a_good_fetch_falls_back_to_the_cache()
    {
        // Prime the cache with a good fetch, then a client that always fails the network.
        await Client(GoodFeedHandler()).RefreshAsync();

        var offline = Client(new StubHandler(_ => throw new HttpRequestException("no network")));
        var result = await offline.RefreshAsync();

        result.FromCache.Should().BeTrue();
        result.Index!.Plugins.Should().ContainSingle().Which.Id.Should().Be("a.plugin");
    }

    [Fact]
    public async Task A_tampered_feed_is_ignored_and_the_cache_is_used()
    {
        await Client(GoodFeedHandler()).RefreshAsync(); // prime cache

        var tampered = new StubHandler(req =>
        {
            var body = req.RequestUri!.AbsoluteUri.EndsWith(".sig") ? _signatureB64 : Feed.Replace("a.plugin", "evil.plugin");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var result = await Client(tampered).RefreshAsync();

        result.FromCache.Should().BeTrue("a signature mismatch falls back to the last-good cache");
        result.Index!.Plugins.Should().ContainSingle().Which.Id.Should().Be("a.plugin", "not the tampered 'evil.plugin'");
    }

    [Fact]
    public async Task A_304_not_modified_uses_the_cache()
    {
        await Client(GoodFeedHandler()).RefreshAsync(); // primes cache (+ etag if the server sends one)

        var notModified = new StubHandler(req =>
            req.RequestUri!.AbsoluteUri.EndsWith(".sig")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_signatureB64) }
                : new HttpResponseMessage(HttpStatusCode.NotModified));
        var result = await Client(notModified).RefreshAsync();

        result.FromCache.Should().BeTrue();
        result.Index!.Plugins.Should().ContainSingle().Which.Id.Should().Be("a.plugin");
    }

    [Fact]
    public async Task No_url_means_no_feed_not_an_error()
    {
        var client = new PluginFeedClient(new HttpClient(GoodFeedHandler()), new FeedSignatureVerifier(_publicKey), string.Empty, _cacheDir);

        client.IsConfigured.Should().BeFalse();
        (await client.RefreshAsync()).Index.Should().BeNull();
    }

    // ── handlers ────────────────────────────────────────────────────────────────────────────────
    private StubHandler GoodFeedHandler() => new(req =>
    {
        var body = req.RequestUri!.AbsoluteUri.EndsWith(".sig") ? _signatureB64 : Feed;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
