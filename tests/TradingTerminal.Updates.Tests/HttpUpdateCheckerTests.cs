using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Security;
using TradingTerminal.Infrastructure.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// End-to-end behaviour of the update checker against a stubbed feed. The cases that matter are the
/// hostile ones: a badly-signed manifest must not be believed AND must not reach the cache, and an
/// unreachable feed must degrade rather than fail.
/// </summary>
public sealed class HttpUpdateCheckerTests : IDisposable
{
    private const string FeedUrl = "https://releases.example.com/release.json";

    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-update-tests", Guid.NewGuid().ToString("N"));

    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _signingKey.Dispose();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* temp dir */ }
    }

    private PinnedEcdsaVerifier Pinned =>
        new(Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo()));

    private static string Manifest(string version, int schema = 1) =>
        $$"""
          {"schemaVersion":{{schema}},"version":"{{version}}","releaseNotesUrl":"https://example.com/notes"}
          """;

    private string Sign(string manifest) =>
        Convert.ToBase64String(_signingKey.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256));

    private HttpUpdateChecker Checker(HttpMessageHandler handler, string current, PinnedEcdsaVerifier? verifier = null) =>
        new(new HttpClient(handler), verifier ?? Pinned, FeedUrl, Version.Parse(current), _cacheDir,
            NullLogger<HttpUpdateChecker>.Instance);

    [Fact]
    public async Task Reports_an_update_when_the_signed_feed_publishes_a_newer_version()
    {
        var manifest = Manifest("1.4.0");
        var checker = Checker(new StubHandler(manifest, Sign(manifest)), current: "1.3.2");

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        result.Available!.Version.Should().Be("1.4.0");
        result.FromCache.Should().BeFalse();
        result.HasUpdate.Should().BeTrue();
    }

    [Theory]
    [InlineData("1.3.2")]  // same
    [InlineData("2.0.0")]  // a dev build ahead of the feed
    public async Task Reports_up_to_date_when_the_feed_is_not_ahead(string current)
    {
        var manifest = Manifest("1.3.2");
        var checker = Checker(new StubHandler(manifest, Sign(manifest)), current);

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.UpToDate);
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_a_manifest_whose_signature_does_not_verify_and_never_caches_it()
    {
        // The attacker's scenario: a real-looking manifest advertising a version we'd prompt for.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest("9.9.9");
        var forged = Convert.ToBase64String(
            attacker.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256));

        var result = await Checker(new StubHandler(manifest, forged), current: "1.3.2").CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
        result.Available.Should().BeNull();

        // The poisoning check: nothing unverified may be written, or the next offline start-up would
        // read the attacker's manifest back out of our own cache and trust it.
        Directory.Exists(_cacheDir).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_a_manifest_signed_over_different_bytes_than_it_serves()
    {
        // Signature valid for an older manifest, replayed against a rewritten body.
        var signature = Sign(Manifest("1.3.2"));
        var swapped = Manifest("9.9.9");

        var result = await Checker(new StubHandler(swapped, signature), current: "1.3.2").CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
        Directory.Exists(_cacheDir).Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_a_plaintext_feed_url_without_making_a_request()
    {
        var handler = new StubHandler(Manifest("1.4.0"), "unused");
        var checker = new HttpUpdateChecker(
            new HttpClient(handler), Pinned, "http://releases.example.com/release.json",
            new Version(1, 3, 2), _cacheDir, NullLogger<HttpUpdateChecker>.Instance);

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
        result.Detail.Should().Contain("https");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_not_configured_when_no_key_is_pinned()
    {
        var handler = new StubHandler(Manifest("1.4.0"), "unused");
        var checker = Checker(handler, current: "1.3.2", verifier: new PinnedEcdsaVerifier(null));

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.NotConfigured);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_a_manifest_from_a_schema_newer_than_this_build_understands()
    {
        var manifest = Manifest("1.4.0", schema: UpdateManifest.SupportedSchemaVersion + 1);
        var result = await Checker(new StubHandler(manifest, Sign(manifest)), current: "1.3.2").CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
        result.Detail.Should().Contain("schema");
    }

    [Fact]
    public async Task Ignores_a_version_it_cannot_parse_rather_than_guessing()
    {
        // Semver pre-release tags are explicitly unsupported; guessing at ordering would be worse.
        var manifest = Manifest("1.4.0-beta.2");
        var result = await Checker(new StubHandler(manifest, Sign(manifest)), current: "1.3.2").CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
    }

    [Fact]
    public async Task Falls_back_to_the_cached_manifest_when_the_feed_is_unreachable()
    {
        var manifest = Manifest("1.4.0");

        // First check populates the cache from a good response.
        (await Checker(new StubHandler(manifest, Sign(manifest)), "1.3.2").CheckAsync())
            .Outcome.Should().Be(UpdateOutcome.UpdateAvailable);

        // Second check: the network is gone.
        var offline = await Checker(new ThrowingHandler(), "1.3.2").CheckAsync();

        offline.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        offline.FromCache.Should().BeTrue();
        offline.Available!.Version.Should().Be("1.4.0");
    }

    [Fact]
    public async Task Fails_cleanly_when_the_feed_is_unreachable_and_nothing_is_cached()
    {
        var result = await Checker(new ThrowingHandler(), "1.3.2").CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
        result.Available.Should().BeNull();
    }

    [Fact]
    public async Task Sends_the_cached_etag_and_serves_the_cache_on_304()
    {
        var manifest = Manifest("1.4.0");
        var first = new StubHandler(manifest, Sign(manifest)) { ETag = "\"v14\"" };
        await Checker(first, "1.3.2").CheckAsync();

        var second = new StubHandler(manifest, Sign(manifest)) { NotModified = true };
        var result = await Checker(second, "1.3.2").CheckAsync();

        second.IfNoneMatch.Should().Be("\"v14\"");
        result.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        result.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task Does_not_throw_when_the_feed_returns_an_error_status()
    {
        var result = await Checker(new StatusHandler(HttpStatusCode.InternalServerError), "1.3.2").CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.Failed);
    }

    // ── Handlers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Serves the manifest on the feed URL and the signature on <c>&lt;url&gt;.sig</c>.</summary>
    private sealed class StubHandler(string manifest, string signature) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string? ETag { get; init; }
        public bool NotModified { get; init; }
        public string? IfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var isSignature = request.RequestUri!.AbsoluteUri.EndsWith(".sig", StringComparison.Ordinal);

            if (!isSignature)
            {
                IfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
                if (NotModified) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isSignature ? signature : manifest),
            };
            if (!isSignature && ETag is not null)
                response.Headers.TryAddWithoutValidation("ETag", ETag);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known.");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
