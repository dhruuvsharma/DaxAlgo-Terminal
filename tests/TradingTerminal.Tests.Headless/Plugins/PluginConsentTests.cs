using System;
using System.Collections.Generic;
using System.IO;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the unsigned-plugin consent path: under Curated, a plugin that is neither pinned by our build
/// nor signed by a pinned publisher is neither silently loaded nor silently dropped — the user is asked,
/// and the answer is remembered against the assembly's sha256 so an update has to ask again.
/// </summary>
public sealed class PluginConsentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "consent-" + Guid.NewGuid().ToString("N"));

    public PluginConsentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Prompt(bool answer) : IPluginConsentPrompt
    {
        public List<PluginConsentRequest> Asked { get; } = [];

        public bool RequestConsent(PluginConsentRequest request)
        {
            Asked.Add(request);
            return answer;
        }
    }

    private sealed class SignatureInspector(PluginSignature signature) : IPluginSignatureInspector
    {
        public PluginSignature Inspect(string assemblyPath) => signature;
    }

    private static readonly PluginTrustPolicy Curated = PluginTrustPolicy.Curated(["A-THUMBPRINT"]);

    private string WritePlugin(string name, string content = "unsigned-third-party")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, name + ".dll"), content);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id":"{{name}}", "name":"{{name}}", "version":"1.0.0", "targetSdkVersion":"{{SdkInfo.Version}}" }""");
        return dir;
    }

    private PluginLoadReport Load(IPluginConsentPrompt? prompt, PluginStateStore state) =>
        PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkInfo.Version, Curated,
            new NullSignatureInspector(), state, PluginScanMode.Enforce, prompt);

    [Fact]
    public void Refusing_consent_rejects_the_plugin()
    {
        WritePlugin("ThirdParty");
        var prompt = new Prompt(answer: false);

        var report = Load(prompt, new PluginStateStore(_root));

        prompt.Asked.Should().ContainSingle();
        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "ThirdParty" && p.Outcome == PluginLoadOutcome.RejectedByTrust);
    }

    [Fact]
    public void With_no_prompt_at_all_the_answer_is_no()
    {
        // Headless hosts (the CLI, tests, CI) have nobody to ask. Nothing is trusted by default just
        // because the question couldn't be put.
        WritePlugin("ThirdParty");

        var report = Load(prompt: null, new PluginStateStore(_root));

        report.Problems.Should().ContainSingle(p => p.Outcome == PluginLoadOutcome.RejectedByTrust);
    }

    [Fact]
    public void Granting_consent_is_remembered_so_the_user_is_asked_once()
    {
        WritePlugin("ThirdParty");
        var state = new PluginStateStore(_root);
        var prompt = new Prompt(answer: true);

        // It's garbage bytes, so it faults at LOAD — but getting past the trust gate to a load fault is
        // exactly what consent is meant to do.
        var first = Load(prompt, state);
        first.Problems.Should().ContainSingle(p => p.Outcome == PluginLoadOutcome.Faulted);
        prompt.Asked.Should().ContainSingle();

        // Second start: the consent is persisted, so the prompt is never shown again.
        var reloadedState = new PluginStateStore(_root);
        reloadedState.ClearQuarantine("ThirdParty"); // the load fault quarantined it; that's a separate gate
        var again = new Prompt(answer: false);
        Load(again, reloadedState);

        again.Asked.Should().BeEmpty("consent survives a restart");
    }

    [Fact]
    public void Consent_is_bound_to_the_exact_build_so_an_updated_plugin_asks_again()
    {
        var dir = WritePlugin("ThirdParty");
        var state = new PluginStateStore(_root);
        state.GrantConsent("ThirdParty", PluginIntegrity.Sha256(Path.Combine(dir, "ThirdParty.dll")));

        // The plugin updates itself (or is swapped). Same name, different bytes.
        File.WriteAllText(Path.Combine(dir, "ThirdParty.dll"), "version-2-or-something-else-entirely");
        var prompt = new Prompt(answer: false);

        var report = Load(prompt, state);

        prompt.Asked.Should().ContainSingle("a new build inherits nothing from the trust its predecessor was given");
        report.Problems.Should().ContainSingle(p => p.Outcome == PluginLoadOutcome.RejectedByTrust);
    }

    [Fact]
    public void A_first_party_pinned_plugin_never_prompts()
    {
        var dir = WritePlugin("Ours", "shipped-by-us");
        File.WriteAllText(Path.Combine(_root, PluginTrustedHashes.FileName),
            $$"""
            { "plugins": [ { "plugin": "Ours", "assemblies": {
                "Ours.dll": "{{PluginIntegrity.Sha256(Path.Combine(dir, "Ours.dll"))}}" } } ] }
            """);
        var prompt = new Prompt(answer: false);

        Load(prompt, new PluginStateStore(_root));

        prompt.Asked.Should().BeEmpty("the shipped catalogue must not interrogate the user on first run");
    }

    [Fact]
    public void Consent_for_a_valid_but_unknown_signer_remains_marked_as_untrusted()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "ThirdParty")).FullName;
        var fixture = Path.Combine(
            AppContext.BaseDirectory, "TestPlugins", "DaxAlgo.SamplePlugin", "DaxAlgo.SamplePlugin.dll");
        File.Copy(fixture, Path.Combine(dir, "ThirdParty.dll"));
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id":"third.party", "name":"Third Party", "version":"1.0.0", "targetSdkVersion":"{{SdkInfo.Version}}" }""");

        var prompt = new Prompt(answer: true);
        var inspector = new SignatureInspector(new PluginSignature(
            IsSigned: true, IsValid: true, Thumbprint: "NOT-TRUSTED", Subject: "CN=Unknown Publisher"));

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, Curated, inspector,
            new PluginStateStore(_root), PluginScanMode.Off, prompt);

        prompt.Asked.Should().ContainSingle("an unrecognized signing key still requires explicit consent");
        report.Loaded.Should().ContainSingle();
        report.Loaded[0].Unsigned.Should().BeTrue(
            "consent is not evidence that the signer is a recognized publisher");
    }

    [Fact]
    public void A_plugin_the_scan_BLOCKS_is_never_offered_for_consent()
    {
        // You cannot click through a Block. The scan runs before the consent gate precisely so the user
        // is never given the chance to wave P/Invoke or Process.Start past the policy.
        var dir = WritePlugin("Nasty");
        // A real assembly is needed for the scanner to find anything, so borrow this test assembly's:
        // it references Process, File, etc. — plenty to trip the Block rules.
        File.Copy(typeof(PluginConsentTests).Assembly.Location, Path.Combine(dir, "Nasty.dll"), overwrite: true);
        var prompt = new Prompt(answer: true);

        var report = Load(prompt, new PluginStateStore(_root));

        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Nasty" && p.Outcome == PluginLoadOutcome.BlockedByScan);
        prompt.Asked.Should().BeEmpty("a Block-level plugin is refused outright, not offered to the user");
    }
}
