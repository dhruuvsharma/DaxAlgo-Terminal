using System.IO;
using System.IO.Compression;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.UI.Diagnostics;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the <c>.daxplugin</c> package format (issue #22): write → verify → install round-trip
/// including private-dependency files, and the refusal paths — tampered content, entries missing
/// from / not covered by the integrity index, zip-slip escapes, and the trust gate. Plus the
/// watchdog's <see cref="PluginFaultTracker"/> strike policy. Package "assemblies" are garbage
/// bytes — nothing here executes plugin code.
/// </summary>
public sealed class DaxPluginPackageTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "pkg-" + Guid.NewGuid().ToString("N"));
    private readonly string _pluginsRoot;

    public DaxPluginPackageTests()
    {
        _pluginsRoot = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_pluginsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Package_round_trips_including_private_dependencies()
    {
        var package = WritePackage("Pack", "1.0.0", withPrivateDep: true);

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeTrue(result.Message);
        result.Message.Should().StartWith("Installed");
        var installed = Path.Combine(_pluginsRoot, "Pack");
        File.Exists(Path.Combine(installed, "Pack.dll")).Should().BeTrue();
        File.Exists(Path.Combine(installed, "plugin.json")).Should().BeTrue();
        File.Exists(Path.Combine(installed, "PrivateDep.dll")).Should().BeTrue(
            "packages must carry plugin-private dependencies, which the raw-DLL path can't");
    }

    [Fact]
    public void Tampered_entry_is_refused()
    {
        var package = WritePackage("Pack", "1.0.0");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            using var stream = zip.GetEntry("Pack.dll")!.Open();
            stream.Position = 0;
            stream.WriteByte(0xFF); // flip a byte after the index was computed
        }

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("integrity check failed");
        Directory.Exists(Path.Combine(_pluginsRoot, "Pack")).Should().BeFalse("nothing may be installed from a tampered package");
    }

    [Fact]
    public void Zip_without_the_integrity_index_is_refused()
    {
        var package = Path.Combine(_root, "noindex.daxplugin");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("Pack.dll").Open();
            stream.Write("x"u8);
        }

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("integrity index is missing");
    }

    [Fact]
    public void Entry_escaping_the_extraction_folder_is_refused()
    {
        var package = Path.Combine(_root, "slip.daxplugin");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("../evil.dll").Open()) stream.Write("x"u8);
            using var index = zip.CreateEntry(DaxPluginPackage.IndexEntryName).Open();
            index.Write("""{ "formatVersion":1, "mainAssembly":"evil.dll", "files": { "../evil.dll":"00" } }"""u8);
        }

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("escapes");
    }

    [Fact]
    public void File_not_covered_by_the_index_is_refused()
    {
        var package = WritePackage("Pack", "1.0.0");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            using var stream = zip.CreateEntry("sneaky.txt").Open();
            stream.Write("x"u8);
        }

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not covered by its integrity index");
    }

    [Fact]
    public void File_listed_but_missing_is_refused()
    {
        var package = WritePackage("Pack", "1.0.0", withPrivateDep: true);
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
            zip.GetEntry("PrivateDep.dll")!.Delete();

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Permissive, new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("missing a file its integrity index lists");
    }

    [Fact]
    public void Curated_policy_still_gates_a_verified_package()
    {
        var package = WritePackage("Pack", "1.0.0");

        var result = PluginInstaller.InstallFromPackage(
            package, _pluginsRoot, PluginTrustPolicy.Curated(["AA11"]), new NullSignatureInspector());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("trust policy");
    }

    // ── watchdog strike policy ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FaultTracker_strikes_out_exactly_once_at_the_limit()
    {
        var tracker = new PluginFaultTracker(strikeLimit: 3);

        tracker.RecordFault("Pack").Should().Be((1, false));
        tracker.RecordFault("PACK").Should().Be((2, false), "keys are case-insensitive");
        tracker.RecordFault("Pack").Should().Be((3, true), "the crossing fault triggers the strike-out");
        tracker.RecordFault("Pack").Should().Be((4, false), "the strike-out action must fire only once");
        tracker.RecordFault("Other").Should().Be((1, false), "plugins are tracked independently");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds a source plugin folder (garbage main dll + manifest at the given version,
    /// optionally a private dep) and packs it into a .daxplugin under the test root.</summary>
    private string WritePackage(string name, string version, bool withPrivateDep = false)
    {
        var sourceDir = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, name + ".dll"), [0xDE, 0xAD, 0xBE, 0xEF]);
        File.WriteAllText(Path.Combine(sourceDir, PluginManifest.FileName),
            $$"""{ "id":"{{name}}","name":"{{name}}","version":"{{version}}","targetSdkVersion":"{{SdkInfo.Version}}" }""");
        if (withPrivateDep)
            File.WriteAllBytes(Path.Combine(sourceDir, "PrivateDep.dll"), [0x01, 0x02]);

        var output = Path.Combine(_root, $"{name}-{version}-{Guid.NewGuid():N}{DaxPluginPackage.Extension}");
        DaxPluginPackage.Write(sourceDir, name + ".dll", output);
        return output;
    }
}
