using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData("Pack.dll")]
    [InlineData("plugin.json")]
    [InlineData("PrivateDep.dll")]
    public void Writer_rejects_output_aliases_before_modifying_any_source(string outputFileName)
    {
        var sourceDir = Path.Combine(_root, "writer-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "alias"));
        File.WriteAllBytes(Path.Combine(sourceDir, "Pack.dll"), [0x01, 0x02, 0x03]);
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"), "manifest-before");
        File.WriteAllBytes(Path.Combine(sourceDir, "PrivateDep.dll"), [0x04, 0x05, 0x06]);
        var snapshots = Directory.EnumerateFiles(sourceDir)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var aliasedOutput = Path.Combine(sourceDir, "alias", "..", outputFileName);

        var act = () => DaxPluginPackage.Write(sourceDir, "Pack.dll", aliasedOutput);

        act.Should().Throw<InvalidOperationException>().WithMessage("*aliases*");
        foreach (var (fileName, expectedBytes) in snapshots)
            File.ReadAllBytes(Path.Combine(sourceDir, fileName)).Should().Equal(expectedBytes);
    }

    [Fact]
    public void Utf8_bom_index_from_windows_powershell_51_is_accepted()
    {
        var dll = "dll"u8.ToArray();
        var manifest = "manifest"u8.ToArray();
        var json = ValidIndex(
            "Pack.dll",
            ("Pack.dll", dll),
            (PluginManifest.FileName, manifest));
        byte[] bomIndex = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)];
        var package = WriteRawPackage(
            bomIndex,
            ("Pack.dll", dll),
            (PluginManifest.FileName, manifest));

        var (extractedDir, mainAssemblyName) = DaxPluginPackage.ExtractAndVerify(package);
        try
        {
            mainAssemblyName.Should().Be("Pack");
            File.ReadAllBytes(Path.Combine(extractedDir, "Pack.dll")).Should().Equal(dll);
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
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

    [Theory]
    [InlineData("{ \"mainAssembly\":\"Pack.dll\", \"files\":{} }", "formatVersion is missing")]
    [InlineData("{ \"formatVersion\":2, \"mainAssembly\":\"Pack.dll\", \"files\":{} }", "Unsupported")]
    public void Missing_or_unsupported_format_version_is_refused(string indexJson, string expectedMessage)
    {
        var package = WriteRawPackage(indexJson, ("Pack.dll", "dll"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Duplicate_json_properties_are_refused()
    {
        var hash = Hash("dll"u8.ToArray());
        var index = $$"""
            { "formatVersion":1, "FormatVersion":1, "mainAssembly":"Pack.dll",
              "files": { "Pack.dll":"{{hash}}" } }
            """;
        var package = WriteRawPackage(index, ("Pack.dll", "dll"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*duplicate property alias*");
    }

    [Theory]
    [InlineData("dup.txt", "dup.txt")]
    [InlineData("A.txt", "a.txt")]
    [InlineData("caf\u00e9.txt", "cafe\u0301.txt")]
    public void Duplicate_payload_path_aliases_are_refused(string firstName, string secondName)
    {
        var dll = "dll"u8.ToArray();
        var package = WriteRawPackage(
            ValidIndex("Pack.dll", ("Pack.dll", dll)),
            ("Pack.dll", dll),
            (firstName, "one"u8.ToArray()),
            (secondName, "two"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*duplicate*alias*");
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("PACKAGE.JSON")]
    public void Duplicate_or_case_aliased_integrity_index_is_refused(string secondIndexName)
    {
        var dll = "dll"u8.ToArray();
        var index = ValidIndex("Pack.dll", ("Pack.dll", dll));
        var package = WriteRawPackage(index, ("Pack.dll", dll));
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            using var stream = zip.CreateEntry(secondIndexName).Open();
            stream.Write(Encoding.UTF8.GetBytes(index));
        }

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Case_aliased_index_path_is_not_treated_as_an_exact_match()
    {
        var dll = "dll"u8.ToArray();
        var hash = Hash(dll);
        var index = $$"""
            { "formatVersion":1, "mainAssembly":"PACK.dll",
              "files": { "PACK.dll":"{{hash}}" } }
            """;
        var package = WriteRawPackage(index, ("Pack.dll", dll));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*aliases*match exactly*");
    }

    [Fact]
    public void Duplicate_case_aliased_index_paths_are_refused()
    {
        var hash = Hash("dll"u8.ToArray());
        var index = $$"""
            { "formatVersion":1, "mainAssembly":"Pack.dll",
              "files": { "Pack.dll":"{{hash}}", "PACK.dll":"{{hash}}" } }
            """;
        var package = WriteRawPackage(index, ("Pack.dll", "dll"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*duplicate*alias*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void Invalid_sha256_strings_are_refused(string invalidHash)
    {
        var index = $$"""
            { "formatVersion":1, "mainAssembly":"Pack.dll",
              "files": { "Pack.dll":"{{invalidHash}}" } }
            """;
        var package = WriteRawPackage(index, ("Pack.dll", "dll"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*expected exactly 64 hexadecimal characters*");
    }

    [Theory]
    [InlineData("/absolute.dll")]
    [InlineData("//server/share.dll")]
    [InlineData("C:/drive.dll")]
    [InlineData("dir\\backslash.dll")]
    [InlineData("dir//empty.dll")]
    [InlineData("dir/./dot.dll")]
    [InlineData("dir/../traversal.dll")]
    [InlineData("payload.dll:stream")]
    [InlineData("NUL.txt")]
    [InlineData("dir/COM1.dll")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Unsafe_windows_path_forms_are_refused(string unsafePath)
    {
        var dll = "dll"u8.ToArray();
        var package = WriteRawPackage(
            ValidIndex("Pack.dll", ("Pack.dll", dll)),
            ("Pack.dll", dll),
            (unsafePath, "bad"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Empty_index_path_is_refused()
    {
        var hash = Hash("dll"u8.ToArray());
        var index = $$"""
            { "formatVersion":1, "mainAssembly":"Pack.dll",
              "files": { "":"{{hash}}", "Pack.dll":"{{hash}}" } }
            """;
        var package = WriteRawPackage(index, ("Pack.dll", "dll"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*empty path*");
    }

    [Fact]
    public void File_used_as_a_directory_parent_is_refused()
    {
        var dll = "dll"u8.ToArray();
        var package = WriteRawPackage(
            ValidIndex("Pack.dll", ("Pack.dll", dll)),
            ("Pack.dll", dll),
            ("data", "file"u8.ToArray()),
            ("data/child.bin", "child"u8.ToArray()));

        var act = () => DaxPluginPackage.ExtractAndVerify(package);

        act.Should().Throw<InvalidDataException>().WithMessage("*directory parent*");
    }

    [Fact]
    public void Compressed_package_size_limit_is_enforced_for_local_packages()
    {
        var package = WritePackage("Pack", "1.0.0");
        var limits = DaxPluginPackage.DefaultExtractionLimits with
        {
            MaxPackageBytes = new FileInfo(package).Length - 1,
        };

        var act = () => DaxPluginPackage.ExtractAndVerifyWithLimits(package, limits);

        act.Should().Throw<InvalidDataException>().WithMessage("*compressed-size limit*");
    }

    [Theory]
    [InlineData("entry-count")]
    [InlineData("entry-size")]
    [InlineData("total-size")]
    [InlineData("index-size")]
    [InlineData("path-length")]
    public void Configured_archive_limits_are_enforced(string limitName)
    {
        var package = WritePackage("Pack", "1.0.0");
        var limits = limitName switch
        {
            "entry-count" => DaxPluginPackage.DefaultExtractionLimits with { MaxEntryCount = 2 },
            "entry-size" => DaxPluginPackage.DefaultExtractionLimits with { MaxEntryUncompressedBytes = 3 },
            "total-size" => DaxPluginPackage.DefaultExtractionLimits with { MaxTotalUncompressedBytes = 4 },
            "index-size" => DaxPluginPackage.DefaultExtractionLimits with { MaxIndexBytes = 16 },
            "path-length" => DaxPluginPackage.DefaultExtractionLimits with { MaxPathLength = 7 },
            _ => throw new ArgumentOutOfRangeException(nameof(limitName)),
        };

        var act = () => DaxPluginPackage.ExtractAndVerifyWithLimits(package, limits);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Path_depth_limit_is_enforced()
    {
        var dll = "dll"u8.ToArray();
        var package = WriteRawPackage(
            ValidIndex("Pack.dll", ("Pack.dll", dll)),
            ("Pack.dll", dll),
            ("one/two/three.bin", "data"u8.ToArray()));
        var limits = DaxPluginPackage.DefaultExtractionLimits with { MaxPathDepth = 2 };

        var act = () => DaxPluginPackage.ExtractAndVerifyWithLimits(package, limits);

        act.Should().Throw<InvalidDataException>().WithMessage("*depth limit*");
    }

    [Fact]
    public void Highly_compressible_payload_over_ratio_limit_is_refused()
    {
        var dll = new byte[2 * 1024 * 1024];
        var package = WriteRawPackage(
            ValidIndex("Pack.dll", ("Pack.dll", dll)),
            ("Pack.dll", dll));
        var limits = DaxPluginPackage.DefaultExtractionLimits with
        {
            MaxCompressionRatio = 2,
            CompressionRatioSlackBytes = 0,
        };

        var act = () => DaxPluginPackage.ExtractAndVerifyWithLimits(package, limits);

        act.Should().Throw<InvalidDataException>().WithMessage("*compression-ratio limit*");
    }

    [Fact]
    public void Failed_streaming_verification_removes_its_temporary_directory()
    {
        var dll = "tampered"u8.ToArray();
        var index = """
            { "formatVersion":1, "mainAssembly":"Pack.dll",
              "files": { "Pack.dll":"0000000000000000000000000000000000000000000000000000000000000000" } }
            """;
        var package = WriteRawPackage(index, ("Pack.dll", dll));
        var extractionRoot = Path.Combine(_root, "bounded-extraction");
        Directory.CreateDirectory(extractionRoot);

        var act = () => DaxPluginPackage.ExtractAndVerifyWithLimits(
            package,
            DaxPluginPackage.DefaultExtractionLimits,
            extractionRoot);

        act.Should().Throw<InvalidDataException>().WithMessage("*integrity check failed*");
        Directory.EnumerateFileSystemEntries(extractionRoot).Should().BeEmpty();
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

    private string WriteRawPackage(string indexJson, params (string Name, byte[] Content)[] entries)
        => WriteRawPackage(Encoding.UTF8.GetBytes(indexJson), entries);

    private string WriteRawPackage(byte[] indexBytes, params (string Name, byte[] Content)[] entries)
    {
        var output = Path.Combine(_root, $"raw-{Guid.NewGuid():N}{DaxPluginPackage.Extension}");
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(content);
        }

        using var index = zip.CreateEntry(DaxPluginPackage.IndexEntryName, CompressionLevel.Optimal).Open();
        index.Write(indexBytes);
        return output;
    }

    private static string ValidIndex(string mainAssembly, params (string Name, byte[] Content)[] entries)
    {
        var files = entries.ToDictionary(
            entry => entry.Name,
            entry => Hash(entry.Content),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            mainAssembly,
            files,
        });
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}
