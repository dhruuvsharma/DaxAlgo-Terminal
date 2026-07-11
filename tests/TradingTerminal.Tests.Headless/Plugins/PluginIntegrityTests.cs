using System;
using System.IO;
using System.Text.Json;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers hash-pinned trust + integrity + revocation — the layer that lets the shipped app trust its
/// OWN unsigned plugins (Curated would otherwise reject all nine first-party plugins and open with an
/// empty strategy catalog, since there is no code-signing certificate) while still refusing anything
/// that was rewritten on disk or withdrawn.
/// </summary>
public sealed class PluginIntegrityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "pin-" + Guid.NewGuid().ToString("N"));

    public PluginIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Pinning ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pinned_folder_that_matches_is_a_Match()
    {
        var dir = WritePlugin("Alpha", "payload");
        PinAsShipped("Alpha");

        var result = PluginTrustedHashes.Load(_root).Verify("Alpha", dir, out var detail);

        result.Should().Be(PluginPinResult.Match);
        detail.Should().BeNull();
    }

    [Fact]
    public void An_unlisted_folder_is_NotPinned_not_an_error()
    {
        var dir = WritePlugin("ThirdParty", "payload");
        PinAsShipped(); // pins nothing

        PluginTrustedHashes.Load(_root).Verify("ThirdParty", dir, out _)
            .Should().Be(PluginPinResult.NotPinned, "a third-party plugin just isn't ours — it goes to the signature/consent path");
    }

    [Fact]
    public void A_modified_assembly_is_Tampered()
    {
        var dir = WritePlugin("Alpha", "payload");
        PinAsShipped("Alpha");
        File.WriteAllText(Path.Combine(dir, "Alpha.dll"), "payload-with-a-backdoor");

        var result = PluginTrustedHashes.Load(_root).Verify("Alpha", dir, out var detail);

        result.Should().Be(PluginPinResult.Tampered);
        detail.Should().Contain("does not match");
    }

    [Fact]
    public void An_ADDED_assembly_is_Tampered()
    {
        // Dropping an extra DLL into a shipped plugin's folder is how you'd smuggle a payload the
        // plugin's own (unmodified, still-matching) assembly then loads.
        var dir = WritePlugin("Alpha", "payload");
        PinAsShipped("Alpha");
        File.WriteAllText(Path.Combine(dir, "Evil.dll"), "surprise");

        PluginTrustedHashes.Load(_root).Verify("Alpha", dir, out var detail)
            .Should().Be(PluginPinResult.Tampered);
        detail.Should().Contain("added");
    }

    [Fact]
    public void A_REMOVED_assembly_is_Tampered()
    {
        var dir = WritePlugin("Alpha", "payload");
        File.WriteAllText(Path.Combine(dir, "Helper.dll"), "helper");
        PinAsShipped("Alpha");
        File.Delete(Path.Combine(dir, "Helper.dll"));

        PluginTrustedHashes.Load(_root).Verify("Alpha", dir, out var detail)
            .Should().Be(PluginPinResult.Tampered);
        detail.Should().Contain("missing");
    }

    [Fact]
    public void A_missing_or_corrupt_pin_file_pins_nothing_rather_than_failing()
    {
        PluginTrustedHashes.Load(_root).IsEmpty.Should().BeTrue();

        File.WriteAllText(Path.Combine(_root, PluginTrustedHashes.FileName), "{ not json");
        PluginTrustedHashes.Load(_root).IsEmpty.Should().BeTrue(
            "a broken pin file must never take startup down; nothing is pinned, so everything falls through to signature/consent");
    }

    // ── Revocation ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Revocation_matches_by_hash_and_by_plugin_id()
    {
        File.WriteAllText(Path.Combine(_root, PluginRevocationList.FileName), """
            { "revoked": [
                { "sha256": "ABC123", "reason": "exfiltrates credentials" },
                { "id": "bad.plugin", "reason": "publisher compromised" }
            ] }
            """);

        var list = PluginRevocationList.Load(_root);

        list.IsRevoked("abc123", pluginId: null, out var byHash).Should().BeTrue("hashes compare case-insensitively");
        byHash.Should().Be("exfiltrates credentials");

        list.IsRevoked("SOMEOTHERHASH", "bad.plugin", out var byId).Should().BeTrue();
        byId.Should().Be("publisher compromised");

        list.IsRevoked("SOMEOTHERHASH", "good.plugin", out _).Should().BeFalse();
    }

    [Fact]
    public void A_missing_revocation_list_revokes_nothing()
    {
        PluginRevocationList.Load(_root).IsEmpty.Should().BeTrue();
        PluginRevocationList.Load(_root).IsRevoked("ANY", "any", out _).Should().BeFalse();
    }

    // ── Through the loader ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_loader_quarantines_a_tampered_first_party_plugin_and_never_loads_it()
    {
        // The plugin is garbage bytes: if the integrity gate did NOT fire first, the loader would try to
        // load it and classify it Faulted (BadImageFormat). Getting Tampered proves the gate ran BEFORE
        // any load attempt.
        var dir = WritePlugin("Alpha", "not-a-real-assembly");
        PinAsShipped("Alpha");
        File.WriteAllText(Path.Combine(dir, "Alpha.dll"), "rewritten-after-shipping");
        var state = new PluginStateStore(_root);

        var report = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkInfo.Version, state);

        report.Loaded.Should().BeEmpty();
        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Alpha" && p.Outcome == PluginLoadOutcome.Tampered);
        state.QuarantineFor("Alpha").Should().NotBeNull();
    }

    [Fact]
    public void The_loader_quarantines_a_revoked_plugin()
    {
        var dir = WritePlugin("Bad", "payload");
        var hash = PluginIntegrity.Sha256(Path.Combine(dir, "Bad.dll"));
        File.WriteAllText(Path.Combine(_root, PluginRevocationList.FileName),
            $$"""{ "revoked": [ { "sha256": "{{hash}}", "reason": "known-malicious build" } ] }""");
        var state = new PluginStateStore(_root);

        var report = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkInfo.Version, state);

        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Bad" && p.Outcome == PluginLoadOutcome.Revoked);
        report.Problems[0].Reason.Should().Be("known-malicious build");
        state.QuarantineFor("Bad").Should().NotBeNull();
    }

    [Fact]
    public void The_loader_quarantines_a_third_party_plugin_that_changed_since_it_was_installed()
    {
        var dir = WritePlugin("ThirdParty", "as-installed");
        var state = new PluginStateStore(_root);
        state.SetInstalledHash("ThirdParty", PluginIntegrity.Sha256(Path.Combine(dir, "ThirdParty.dll")));

        File.WriteAllText(Path.Combine(dir, "ThirdParty.dll"), "swapped-behind-the-users-back");

        var report = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkInfo.Version, state);

        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "ThirdParty" && p.Outcome == PluginLoadOutcome.Tampered);
        report.Problems[0].Reason.Should().Contain("since it was installed");
    }

    [Fact]
    public void Curated_mode_accepts_an_UNSIGNED_plugin_that_the_build_pinned()
    {
        // The reason hash-pinning exists: the nine first-party plugins are unsigned, so a Curated build
        // would otherwise reject every one of them and ship an empty strategy catalog. A pinned folder
        // IS the trust anchor. (Garbage bytes, so it faults at LOAD — but reaching a load fault proves
        // it got past the trust gate, which is what this test is about.)
        var dir = WritePlugin("Alpha", "unsigned-but-ours");
        PinAsShipped("Alpha");
        var curated = PluginTrustPolicy.Curated(["SOME-THUMBPRINT-WE-DONT-HAVE"]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, curated, new NullSignatureInspector(),
            new PluginStateStore(_root));

        report.Problems.Should().ContainSingle();
        report.Problems[0].Outcome.Should().NotBe(PluginLoadOutcome.RejectedByTrust,
            "a build-pinned plugin is trusted by its hash, with no certificate involved");
        report.Problems[0].Outcome.Should().Be(PluginLoadOutcome.Faulted, "it is not a real assembly");
    }

    [Fact]
    public void Curated_mode_still_rejects_an_unsigned_plugin_that_is_NOT_pinned()
    {
        // Ours is pinned; the third-party drop-in beside it is not. Hash-pinning must not become a
        // blanket "unsigned is fine" — only OUR assemblies are trusted by hash.
        WritePlugin("Ours", "shipped-by-us");
        WritePlugin("ThirdParty", "unsigned-and-unknown");
        PinAsShipped("Ours");
        var curated = PluginTrustPolicy.Curated(["AA11"]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, curated, new NullSignatureInspector(),
            new PluginStateStore(_root));

        report.Problems.Should().Contain(p =>
            p.PluginFolderName == "ThirdParty" && p.Outcome == PluginLoadOutcome.RejectedByTrust);
        report.Problems.Should().NotContain(p =>
            p.PluginFolderName == "Ours" && p.Outcome == PluginLoadOutcome.RejectedByTrust,
            "our own pinned plugin is trusted by hash even under Curated");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates <c>{_root}/{name}/{name}.dll</c> with the given content. Not a real assembly —
    /// these tests are about the gates that run BEFORE any assembly is loaded.</summary>
    private string WritePlugin(string name, string content)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, name + ".dll"), content);
        return dir;
    }

    /// <summary>Writes plugins-trusted.json exactly as the build's gen-trusted-plugins.ps1 would: every
    /// assembly of every named folder, with its current hash.</summary>
    private void PinAsShipped(params string[] plugins)
    {
        var entries = plugins.Select(p => new TrustedPlugin(
            p,
            Directory.EnumerateFiles(Path.Combine(_root, p), "*.dll")
                .ToDictionary(Path.GetFileName!, PluginIntegrity.Sha256)));

        File.WriteAllText(
            Path.Combine(_root, PluginTrustedHashes.FileName),
            JsonSerializer.Serialize(new { plugins = entries }));
    }
}
