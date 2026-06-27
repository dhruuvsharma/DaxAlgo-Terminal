using System;
using System.Collections.Generic;
using System.IO;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the curated-marketplace trust gate: the <see cref="PluginTrustPolicy"/> decisions, the
/// loader refusing an untrusted plugin BEFORE running its code, the real
/// <see cref="AuthenticodeSignatureInspector"/> correctly reporting the unsigned sample as unsigned,
/// and manifest parsing. The trusted-signed positive path is exercised with a fake inspector (no
/// signing infrastructure needed); the real WinVerifyTrust valid-signed path is left for a CI test
/// against a genuinely signed binary.
/// </summary>
public sealed class PluginSecurityTests
{
    private static readonly PluginSignature TrustedSig = new(IsSigned: true, IsValid: true, Thumbprint: "ABC123", Subject: "CN=Trusted Publisher");
    private static readonly PluginSignature UntrustedSig = new(IsSigned: true, IsValid: true, Thumbprint: "DEAD99", Subject: "CN=Some Rando");

    private sealed class FakeInspector(PluginSignature signature) : IPluginSignatureInspector
    {
        public PluginSignature Inspect(string assemblyPath) => signature;
    }

    private static string StagedPluginsRoot => Path.Combine(AppContext.BaseDirectory, "TestPlugins");

    // ── Trust-policy decisions ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Permissive_allows_unsigned() =>
        PluginTrustPolicy.Permissive.Allows(PluginSignature.Unsigned, hasManifest: false, out _).Should().BeTrue();

    [Fact]
    public void Curated_rejects_unsigned()
    {
        PluginTrustPolicy.Curated(["ABC123"]).Allows(PluginSignature.Unsigned, hasManifest: true, out var reason).Should().BeFalse();
        reason.Should().Contain("signature");
    }

    [Fact]
    public void Curated_rejects_signed_but_untrusted_thumbprint()
    {
        PluginTrustPolicy.Curated(["ABC123"]).Allows(UntrustedSig, hasManifest: true, out var reason).Should().BeFalse();
        reason.Should().Contain("trusted publisher");
    }

    [Fact]
    public void Curated_allows_signed_valid_trusted_publisher() =>
        PluginTrustPolicy.Curated(["abc 123"]).Allows(TrustedSig, hasManifest: true, out _).Should().BeTrue(); // case/space-insensitive

    [Fact]
    public void Curated_rejects_when_manifest_required_but_missing()
    {
        PluginTrustPolicy.Curated(["ABC123"]).Allows(TrustedSig, hasManifest: false, out var reason).Should().BeFalse();
        reason.Should().Contain("manifest");
    }

    // ── LoadInto gating against the real external sample plugin (fake inspector) ───────────────────

    [Fact]
    public void LoadInto_strict_policy_loads_a_plugin_from_a_trusted_publisher()
    {
        var services = new ServiceCollection();
        var policy = new PluginTrustPolicy(RequireSignature: true, RequireManifest: false, new HashSet<string> { "ABC123" });

        var loaded = PluginLoader.LoadInto(services, StagedPluginsRoot, SdkInfo.Version, policy, new FakeInspector(TrustedSig));

        loaded.Should().ContainSingle();
        services.Should().Contain(d => d.ServiceType == typeof(ITradingStrategy));
    }

    [Fact]
    public void LoadInto_strict_policy_rejects_an_untrusted_plugin_without_loading_its_code()
    {
        var services = new ServiceCollection();
        var policy = new PluginTrustPolicy(RequireSignature: true, RequireManifest: false, new HashSet<string> { "ABC123" });
        var errors = new List<Exception>();

        var loaded = PluginLoader.LoadInto(services, StagedPluginsRoot, SdkInfo.Version, policy,
            new FakeInspector(UntrustedSig), onError: (_, ex) => errors.Add(ex));

        loaded.Should().BeEmpty();
        services.Should().BeEmpty("a rejected plugin must never register anything");
        errors.Should().ContainSingle().Which.Should().BeOfType<PluginRejectedException>();
    }

    // ── Real Authenticode inspector on the (unsigned) sample plugin ───────────────────────────────

    [Fact]
    public void Authenticode_inspector_reports_the_unsigned_sample_as_unsigned()
    {
        var dll = Path.Combine(StagedPluginsRoot, "DaxAlgo.SamplePlugin", "DaxAlgo.SamplePlugin.dll");
        File.Exists(dll).Should().BeTrue();

        var sig = new AuthenticodeSignatureInspector().Inspect(dll);

        sig.IsSigned.Should().BeFalse();
        sig.Thumbprint.Should().BeNull();
    }

    // ── Manifest parsing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_round_trips_from_plugin_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daxalgo-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
                """
                { "id": "acme.alpha", "name": "Acme Alpha", "version": "1.2.3",
                  "targetSdkVersion": "0.1.0-alpha", "publisher": "Acme Quant" }
                """);

            var manifest = PluginManifest.TryRead(dir);

            manifest.Should().NotBeNull();
            manifest!.Id.Should().Be("acme.alpha");
            manifest.Publisher.Should().Be("Acme Quant");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Manifest_is_null_when_absent() =>
        PluginManifest.TryRead(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")))
            .Should().BeNull();
}
