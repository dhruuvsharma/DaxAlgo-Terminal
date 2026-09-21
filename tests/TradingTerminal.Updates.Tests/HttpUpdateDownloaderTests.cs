using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The gates that stand between a signed manifest and running an executable as the user. Every one of
/// these is a security property rather than a nicety: the feed signature proves where the URL came
/// from and says nothing whatever about the bytes behind it, so if these checks are wrong the app is
/// a remote-code-execution path.
/// </summary>
public sealed class HttpUpdateDownloaderTests : IDisposable
{
    private readonly string _staging = Path.Combine(
        Path.GetTempPath(), "daxalgo-download-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_staging, recursive: true); } catch { /* temp */ }
    }

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("pretend this is an Inno Setup installer");

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static UpdateManifest Manifest(
        string? url = "https://releases.example.com/DaxAlgo-Terminal-Setup-v1.4.0.exe",
        string? sha256 = null) =>
        new()
        {
            Version = "1.4.0",
            DownloadUrl = url ?? string.Empty,
            Sha256 = sha256 ?? HashOf(Payload),
        };

    private HttpUpdateDownloader Downloader(
        HttpMessageHandler handler,
        IPluginSignatureInspector? signatures = null,
        bool allowed = true,
        string? pinnedThumbprint = null,
        long maxBytes = 512L * 1024 * 1024) =>
        new(new HttpClient(handler), signatures ?? FakeInspector.Trusted, _staging, allowed,
            pinnedThumbprint, maxBytes, NullLogger<HttpUpdateDownloader>.Instance);

    [Fact]
    public async Task Stages_the_installer_when_the_hash_and_the_signature_both_check_out()
    {
        var downloader = Downloader(new StubHandler(Payload));

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.Ok);
        result.IsReadyToInstall.Should().BeTrue();
        File.ReadAllBytes(result.InstallerPath!).Should().Equal(Payload);
    }

    [Theory]
    [InlineData("http://releases.example.com/setup.exe")]  // plaintext — hijackable despite the signed manifest
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://releases.example.com/setup.exe")]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task Refuses_any_download_url_that_is_not_absolute_https(string url)
    {
        var handler = new StubHandler(Payload);
        var downloader = Downloader(handler);

        var result = await downloader.DownloadAsync(Manifest(url: url));

        result.Outcome.Should().Be(UpdateDownloadOutcome.NotOffered);
        handler.Requests.Should().BeEmpty("nothing should be fetched from a URL we already rejected");
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]                                                        // too short
    [InlineData("zz2c8f4a1b3d5e6f70819a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f")]  // not hex
    public async Task Refuses_a_release_whose_manifest_carries_no_usable_hash(string sha256)
    {
        // Without a hash there is nothing to check the download against, and the manifest signature
        // covers the manifest only. An unverifiable installer is not offered at all.
        var handler = new StubHandler(Payload);
        var downloader = Downloader(handler);

        var result = await downloader.DownloadAsync(Manifest(sha256: sha256));

        result.Outcome.Should().Be(UpdateDownloadOutcome.NotOffered);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Discards_a_download_that_does_not_match_the_hash_in_the_signed_manifest()
    {
        // The release host served something other than what the signer published.
        var downloader = Downloader(new StubHandler(Encoding.UTF8.GetBytes("a different installer")));

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.HashMismatch);
        result.InstallerPath.Should().BeNull();
        Directory.GetFiles(_staging).Should().BeEmpty("a rejected download must not be left on disk");
    }

    [Fact]
    public async Task Discards_an_unsigned_installer_even_when_the_hash_matches()
    {
        // A matching hash only proves the feed is internally consistent. If the manifest-signing key
        // ever leaks, Authenticode is the check still standing between that and running code.
        var downloader = Downloader(new StubHandler(Payload), FakeInspector.Unsigned);

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.UntrustedInstaller);
        Directory.GetFiles(_staging).Should().BeEmpty();
    }

    [Fact]
    public async Task Discards_an_installer_whose_Authenticode_signature_does_not_verify()
    {
        var downloader = Downloader(new StubHandler(Payload), FakeInspector.Tampered);

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.UntrustedInstaller);
    }

    [Fact]
    public async Task Discards_an_installer_signed_by_someone_other_than_the_pinned_certificate()
    {
        var downloader = Downloader(new StubHandler(Payload), FakeInspector.Trusted,
            pinnedThumbprint: "AA BB CC DD");

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.UntrustedInstaller);
    }

    [Fact]
    public async Task Accepts_the_pinned_certificate_however_the_thumbprint_is_formatted()
    {
        // certutil, signtool and the Windows UI each space and case thumbprints differently.
        var downloader = Downloader(new StubHandler(Payload),
            new FakeInspector(new PluginSignature(true, true, "aabbccdd", "CN=DaxAlgo")),
            pinnedThumbprint: "AA BB CC DD");

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.Ok);
    }

    [Fact]
    public async Task Stops_a_download_that_runs_past_the_ceiling()
    {
        // The cap is enforced against bytes actually received, so a server that understates or omits
        // its content length cannot use that to fill the user's disk.
        var oversized = new byte[8 * 1024];
        var downloader = Downloader(new StubHandler(oversized) { HideContentLength = true }, maxBytes: 1024);

        var result = await downloader.DownloadAsync(Manifest(sha256: HashOf(oversized)));

        result.Outcome.Should().Be(UpdateDownloadOutcome.Failed);
        Directory.GetFiles(_staging).Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_before_touching_the_network_when_installing_is_switched_off()
    {
        var handler = new StubHandler(Payload);
        var downloader = Downloader(handler, allowed: false);

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.NotAllowed);
        downloader.CanInstall.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_a_release_host_that_answers_with_an_error()
    {
        var downloader = Downloader(new StubHandler(Payload) { Status = HttpStatusCode.NotFound });

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.Failed);
    }

    [Fact]
    public async Task Never_throws_when_the_release_host_is_unreachable()
    {
        var downloader = Downloader(new ThrowingHandler());

        var result = await downloader.DownloadAsync(Manifest());

        result.Outcome.Should().Be(UpdateDownloadOutcome.Failed);
    }

    [Fact]
    public async Task Reuses_an_already_verified_installer_instead_of_downloading_it_twice()
    {
        var handler = new StubHandler(Payload);
        var downloader = Downloader(handler);

        var first = await downloader.DownloadAsync(Manifest());
        var second = await downloader.DownloadAsync(Manifest());

        first.InstallerPath.Should().Be(second.InstallerPath);
        handler.Requests.Should().ContainSingle("the second call should verify what is on disk, not refetch it");
    }

    [Fact]
    public async Task Re_downloads_when_the_staged_file_no_longer_matches_the_manifest()
    {
        var handler = new StubHandler(Payload);
        var downloader = Downloader(handler);
        var first = await downloader.DownloadAsync(Manifest());

        // Something swapped the staged file between runs. The hash check must catch it and refetch.
        await File.WriteAllTextAsync(first.InstallerPath!, "swapped");

        var second = await downloader.DownloadAsync(Manifest());

        second.Outcome.Should().Be(UpdateDownloadOutcome.Ok);
        handler.Requests.Should().HaveCount(2);
        (await File.ReadAllBytesAsync(second.InstallerPath!)).Should().Equal(Payload);
    }

    [Fact]
    public async Task Sweeps_installers_left_behind_by_other_releases()
    {
        Directory.CreateDirectory(_staging);
        var stale = Path.Combine(_staging, "DaxAlgo-Terminal-Setup-v1.2.0.exe");
        await File.WriteAllTextAsync(stale, "an older release nobody installed");

        await Downloader(new StubHandler(Payload)).DownloadAsync(Manifest());

        File.Exists(stale).Should().BeFalse("declining releases must not accumulate installers");
    }

    [Fact]
    public async Task Reports_progress_that_ends_at_the_full_payload()
    {
        var seen = new List<UpdateDownloadProgress>();
        var downloader = Downloader(new StubHandler(Payload));

        await downloader.DownloadAsync(Manifest(), new SynchronousProgress(seen.Add));

        seen.Should().NotBeEmpty();
        seen[^1].BytesReceived.Should().Be(Payload.Length);
        seen[^1].Fraction.Should().Be(1);
    }

    [Fact]
    public async Task Leaves_nothing_behind_when_the_download_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var downloader = Downloader(new StubHandler(Payload) { OnRead = cts.Cancel });

        var result = await downloader.DownloadAsync(Manifest(), cancellationToken: cts.Token);

        result.Outcome.Should().Be(UpdateDownloadOutcome.Cancelled);
        Directory.GetFiles(_staging).Should().BeEmpty();
    }

    // ── doubles ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeInspector(PluginSignature signature) : IPluginSignatureInspector
    {
        public static FakeInspector Trusted { get; } = new(new PluginSignature(true, true, "ABCDEF", "CN=DaxAlgo"));
        public static FakeInspector Unsigned { get; } = new(PluginSignature.Unsigned);
        public static FakeInspector Tampered { get; } = new(new PluginSignature(true, false, "ABCDEF", "CN=DaxAlgo"));

        public PluginSignature Inspect(string assemblyPath) => signature;
    }

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool HideContentLength { get; init; }
        public Action? OnRead { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            if (Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Status));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HookedStream(payload, OnRead)),
            };
            if (!HideContentLength) response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }

    /// <summary>A body that can fire a callback on the first read, so cancellation can be raised
    /// mid-download rather than before it starts.</summary>
    private sealed class HookedStream(byte[] payload, Action? onRead) : MemoryStream(payload, writable: false)
    {
        private bool _fired;

        public override int Read(Span<byte> buffer)
        {
            Fire();
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Fire();
            return base.ReadAsync(buffer, cancellationToken);
        }

        private void Fire()
        {
            if (_fired) return;
            _fired = true;
            onRead?.Invoke();
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("the release host is unreachable");
    }

    /// <summary>Runs the callback inline. <see cref="Progress{T}"/> would post to a captured context,
    /// which on the test thread makes the reports arrive after the assertions.</summary>
    private sealed class SynchronousProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
