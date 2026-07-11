using System.IO;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the plugin lifecycle layer added for the distribution work (issue #22): the persisted
/// <see cref="PluginStateStore"/> (disable / quarantine / pending-uninstall), the classified
/// <see cref="PluginLoadReport"/> from <see cref="PluginLoader.LoadWithReport"/>, fault
/// auto-quarantine, pending-uninstall sweep, and <see cref="PluginInstaller.Uninstall"/> +
/// version-aware install messages. All against throwaway on-disk plugin folders — a "faulting"
/// plugin is garbage bytes (BadImageFormatException at load; no code ever runs).
/// </summary>
public sealed class PluginLifecycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "plugins-" + Guid.NewGuid().ToString("N"));

    public PluginLifecycleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── State store ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StateStore_round_trips_disable_quarantine_and_pending_uninstall()
    {
        var store = new PluginStateStore(_root);
        store.SetDisabled("Alpha", true);
        store.Quarantine("Beta", "boom");
        store.MarkPendingUninstall("Gamma");

        var reloaded = new PluginStateStore(_root);
        reloaded.LoadError.Should().BeNull();
        reloaded.IsDisabled("alpha").Should().BeTrue("folder-name keys compare case-insensitively");
        reloaded.QuarantineFor("BETA")!.Reason.Should().Be("boom");
        reloaded.PendingUninstalls.Should().ContainSingle(p => p == "Gamma");

        reloaded.SetDisabled("Alpha", false);
        reloaded.ClearQuarantine("Beta").Should().BeTrue();
        reloaded.ClearPendingUninstall("Gamma").Should().BeTrue();
        new PluginStateStore(_root).Disabled.Should().BeEmpty();
    }

    [Fact]
    public void StateStore_survives_a_corrupt_file_by_resetting()
    {
        File.WriteAllText(Path.Combine(_root, PluginStateStore.FileName), "{ not json ");

        var store = new PluginStateStore(_root);

        store.LoadError.Should().NotBeNull();
        store.Disabled.Should().BeEmpty();
        // And it can persist again afterwards.
        store.SetDisabled("Alpha", true);
        new PluginStateStore(_root).IsDisabled("Alpha").Should().BeTrue();
    }

    // ── LoadWithReport classification ──────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_plugin_is_skipped_before_any_load_and_is_not_attention_worthy()
    {
        WriteGarbagePlugin("Alpha");
        var state = new PluginStateStore(_root);
        state.SetDisabled("Alpha", true);

        var report = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkInfo.Version, state);

        report.Loaded.Should().BeEmpty();
        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Alpha" && p.Outcome == PluginLoadOutcome.Disabled);
        report.AttentionCount.Should().Be(0, "a deliberate user disable is not a problem to nag about");
        state.QuarantineFor("Alpha").Should().BeNull("a skipped plugin must not be quarantined");
    }

    [Fact]
    public void Faulting_plugin_is_reported_and_auto_quarantined_then_skipped_next_run()
    {
        WriteGarbagePlugin("Broken");
        var state = new PluginStateStore(_root);

        var first = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkInfo.Version, state);

        first.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Broken" && p.Outcome == PluginLoadOutcome.Faulted);
        first.AttentionCount.Should().Be(1);
        state.QuarantineFor("Broken").Should().NotBeNull("a load fault must persist as quarantine");

        // Second run: the quarantine gate stops it BEFORE the (faulting) load is even attempted.
        var second = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkVersion(), state);
        second.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Broken" && p.Outcome == PluginLoadOutcome.Quarantined);
    }

    [Fact]
    public void Trust_rejection_is_classified_but_never_quarantined()
    {
        WriteGarbagePlugin("Unsigned", manifestJson: """{ "id":"u","name":"U","version":"1.0.0","targetSdkVersion":"0.1.0" }""");
        var state = new PluginStateStore(_root);
        var curated = PluginTrustPolicy.Curated(["AA11"]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkVersion(), curated, new NullSignatureInspector(), state);

        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Unsigned" && p.Outcome == PluginLoadOutcome.RejectedByTrust);
        state.QuarantineFor("Unsigned").Should().BeNull(
            "trust rejections are cheap to re-check and fix themselves when a signed build is installed");
    }

    [Fact]
    public void Malformed_manifest_is_its_own_outcome()
    {
        WriteGarbagePlugin("BadManifest", manifestJson: "{ nope");

        var report = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkVersion(),
            new PluginStateStore(_root));

        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "BadManifest" && p.Outcome == PluginLoadOutcome.ManifestInvalid);
    }

    [Fact]
    public void Pending_uninstall_is_swept_before_loading()
    {
        WriteGarbagePlugin("Doomed");
        var state = new PluginStateStore(_root);
        state.MarkPendingUninstall("Doomed");

        var report = PluginLoader.LoadWithReport(new ServiceCollection(), _root, SdkVersion(), state);

        Directory.Exists(Path.Combine(_root, "Doomed")).Should().BeFalse("the sweep deletes the folder pre-load");
        state.PendingUninstalls.Should().BeEmpty();
        report.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Legacy_LoadInto_still_reports_via_onError_with_original_exceptions()
    {
        WriteGarbagePlugin("Broken");
        var errors = new List<(string Path, Exception Ex)>();

        var loaded = PluginLoader.LoadInto(new ServiceCollection(), _root, SdkVersion(),
            (path, ex) => errors.Add((path, ex)));

        loaded.Should().BeEmpty();
        errors.Should().ContainSingle();
        errors[0].Ex.Should().BeOfType<BadImageFormatException>("the callback keeps the raw exception");
    }

    // ── Installer: uninstall + version-aware replace ──────────────────────────────────────────

    [Fact]
    public void Uninstall_deletes_an_unloaded_plugin_and_tidies_state()
    {
        WriteGarbagePlugin("Gone");
        var state = new PluginStateStore(_root);
        state.SetDisabled("Gone", true);
        state.Quarantine("Gone", "old fault");

        var result = PluginInstaller.Uninstall(_root, "Gone", state);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "Gone")).Should().BeFalse();
        state.IsDisabled("Gone").Should().BeFalse();
        state.QuarantineFor("Gone").Should().BeNull();
    }

    [Fact]
    public void Uninstall_refuses_path_fragments()
    {
        PluginInstaller.Uninstall(_root, "..", new PluginStateStore(_root)).Success.Should().BeFalse();
        PluginInstaller.Uninstall(_root, "a/b", new PluginStateStore(_root)).Success.Should().BeFalse();
    }

    [Fact]
    public void Install_over_an_existing_version_reports_update_and_clears_quarantine()
    {
        var state = new PluginStateStore(_root);

        var v1 = MakePackage("Pack", "1.0.0");
        var first = PluginInstaller.InstallFromDll(v1, _root, PluginTrustPolicy.Permissive, new NullSignatureInspector(), state);
        first.Success.Should().BeTrue();
        first.Message.Should().StartWith("Installed");

        state.Quarantine("Pack", "faulted on v1");
        var v2 = MakePackage("Pack", "1.1.0");
        var second = PluginInstaller.InstallFromDll(v2, _root, PluginTrustPolicy.Permissive, new NullSignatureInspector(), state);

        second.Success.Should().BeTrue();
        second.Message.Should().Contain("Updated (1.0.0 → 1.1.0)");
        state.QuarantineFor("Pack").Should().BeNull("a fresh package deserves a fresh chance");

        var downgrade = PluginInstaller.InstallFromDll(v1, _root, PluginTrustPolicy.Permissive, new NullSignatureInspector(), state);
        downgrade.Message.Should().Contain("DOWNGRADED (1.1.0 → 1.0.0)");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static string SdkVersion() => SdkInfo.Version;

    /// <summary>A plugin folder whose main "assembly" is garbage bytes — enumerable by the loader,
    /// guaranteed to fault at LoadFromAssemblyPath, never executes anything.</summary>
    private void WriteGarbagePlugin(string name, string? manifestJson = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".dll"), [0xDE, 0xAD, 0xBE, 0xEF]);
        if (manifestJson is not null)
            File.WriteAllText(Path.Combine(dir, PluginManifest.FileName), manifestJson);
    }

    /// <summary>A source "package" folder (outside the plugins root) with a manifest at the given
    /// version — enough for the installer, which never loads the assembly.</summary>
    private string MakePackage(string name, string version)
    {
        var dir = Path.Combine(_root, "..", "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, name + ".dll");
        File.WriteAllBytes(dll, [0xDE, 0xAD]);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id":"{{name}}","name":"{{name}}","version":"{{version}}","targetSdkVersion":"{{SdkInfo.Version}}" }""");
        return dll;
    }
}
