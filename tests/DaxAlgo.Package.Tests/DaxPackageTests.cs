using System.IO.Compression;
using System.Text;
using DaxAlgo.Package;
using FluentAssertions;
using Xunit;

namespace DaxAlgo.PackageTests;

/// <summary>
/// The package is accepted from strangers, so most of these are adversarial: the interesting cases
/// are not "does a good package round-trip" but "what does a hostile one do".
/// </summary>
public sealed class DaxPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxpackage-tests", Guid.NewGuid().ToString("N"));

    public DaxPackageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static DaxPackageRequest Request(
        DaxPackageKind kind = DaxPackageKind.Strategy,
        IReadOnlyList<DaxPayloadSource>? payloads = null) => new()
    {
        Kind = kind,
        Id = "acme.meanreversion",
        Version = "1.2.0",
        DisplayName = "Mean Reversion",
        EntryTypeName = "Acme.MeanReversionStrategy",
        Description = "Fades stretched moves.",
        Publisher = "ACME Research",
        Payloads = payloads ?? [
            DaxPayloadSource.FromBytes("src/Strategy.cs", DaxPayloadRole.Source, Bytes("public class S {}")),
            DaxPayloadSource.FromBytes("ui/Panel.xaml", DaxPayloadRole.Ui, Bytes("<UserControl/>")),
            DaxPayloadSource.FromBytes("bin/Acme.dll", DaxPayloadRole.Assembly, [0x4D, 0x5A, 0x90, 0x00]),
        ],
    };

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ── Round-trip ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Round_trips_source_ui_and_assembly_in_one_file()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        var written = DaxPackage.Write(path, Request());

        written.Manifest.Payloads.Should().HaveCount(3);
        var read = DaxPackage.Read(path);

        read.Manifest.Kind.Should().Be(DaxPackageKind.Strategy);
        read.Manifest.Id.Should().Be("acme.meanreversion");
        read.Manifest.EntryTypeName.Should().Be("Acme.MeanReversionStrategy");
        Encoding.UTF8.GetString(read.Payloads["payload/src/Strategy.cs"]).Should().Be("public class S {}");
        Encoding.UTF8.GetString(read.Payloads["payload/ui/Panel.xaml"]).Should().Be("<UserControl/>");
        read.Payloads["payload/bin/Acme.dll"].Should().Equal([0x4D, 0x5A, 0x90, 0x00]);
    }

    [Fact]
    public void Visualizer_uses_its_own_extension_and_round_trips()
    {
        var path = Path_("v" + DaxPackage.VisualizerExtension);
        DaxPackage.Write(path, Request(DaxPackageKind.Visualizer));

        DaxPackage.Read(path).Manifest.Kind.Should().Be(DaxPackageKind.Visualizer);
        DaxPackage.KindFor(path).Should().Be(DaxPackageKind.Visualizer);
    }

    [Fact]
    public void Manifest_bytes_are_reproducible_regardless_of_payload_order()
    {
        var a = DaxPackage.Write(Path_("a" + DaxPackage.StrategyExtension), Request());
        var reversed = Request().Payloads.Reverse().ToList();
        var b = DaxPackage.Write(Path_("b" + DaxPackage.StrategyExtension), Request(payloads: reversed));

        b.ManifestSha256.Should().Be(a.ManifestSha256,
            "the same inputs must digest identically however the caller ordered them");
    }

    [Fact]
    public void Inspect_reads_the_manifest_without_expanding_payloads()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request());

        DaxPackage.Inspect(path).DisplayName.Should().Be("Mean Reversion");
    }

    [Fact]
    public void Extract_writes_every_payload_under_the_destination()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request());
        var into = Path_("out");

        DaxPackage.Extract(path, into).Id.Should().Be("acme.meanreversion");
        File.Exists(Path.Combine(into, "payload", "src", "Strategy.cs")).Should().BeTrue();
        File.Exists(Path.Combine(into, "payload", "bin", "Acme.dll")).Should().BeTrue();
    }

    // ── Kind integrity ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Refuses_to_write_a_kind_under_the_wrong_extension()
    {
        var act = () => DaxPackage.Write(Path_("wrong" + DaxPackage.VisualizerExtension), Request(DaxPackageKind.Strategy));

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.KindMismatch);
    }

    [Fact]
    public void Renaming_a_package_cannot_change_what_it_claims_to_be()
    {
        // The whole reason the kind lives inside the manifest as well as in the file name.
        var real = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(real, Request(DaxPackageKind.Strategy));
        var renamed = Path_("s" + DaxPackage.VisualizerExtension);
        File.Move(real, renamed);

        var act = () => DaxPackage.Read(renamed);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.KindMismatch);
    }

    // ── Tamper detection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_a_payload_edited_after_packing()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request());
        Repack(path, entries =>
        {
            entries["payload/src/Strategy.cs"] = Bytes("public class S { /* injected */ }");
        });

        var act = () => DaxPackage.Read(path);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.PayloadDigestMismatch);
    }

    [Fact]
    public void Rejects_an_undeclared_entry_smuggled_into_the_archive()
    {
        // Not covered by any digest, and a naive extractor would happily write it to disk.
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request());
        Repack(path, entries => entries["payload/bin/evil.dll"] = [0x4D, 0x5A]);

        var act = () => DaxPackage.Read(path);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.PayloadUndeclared);
    }

    [Fact]
    public void Rejects_a_declared_payload_that_is_not_in_the_archive()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request());
        Repack(path, entries => entries.Remove("payload/ui/Panel.xaml"));

        var act = () => DaxPackage.Read(path);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.PayloadMissing);
    }

    // ── Path safety ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("..\\escape.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("C:/windows/system32/evil.dll")]
    [InlineData("nested/../../escape.cs")]
    public void Refuses_a_payload_path_that_escapes_the_package(string path)
    {
        var act = () => DaxPackage.Write(Path_("s" + DaxPackage.StrategyExtension),
            Request(payloads: [DaxPayloadSource.FromBytes(path, DaxPayloadRole.Source, Bytes("x"))]));

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.UnsafePath);
    }

    [Fact]
    public void A_payload_can_never_collide_with_the_manifest()
    {
        // Structural, not a check that could be forgotten: the manifest sits at the archive root and
        // every payload is namespaced under payload/, so the two cannot occupy the same entry.
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request(payloads: [DaxPayloadSource.FromBytes(
            DaxPackage.ManifestEntryPath, DaxPayloadRole.Resource, Bytes("{}"))]));

        var read = DaxPackage.Read(path);

        read.Manifest.Payloads.Should().ContainSingle()
            .Which.Path.Should().Be("payload/" + DaxPackage.ManifestEntryPath);
        read.Manifest.DisplayName.Should().Be("Mean Reversion", "the real manifest is untouched");
    }

    // ── Accepted set ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("strategy.daxalgostrategy")]
    [InlineData("viz.daxalgovisualizer")]
    [InlineData("MIXED.DaxAlgoStrategy")]
    public void Accepts_only_the_two_artifact_extensions(string name)
    {
        DaxPackage.IsAccepted(name, out var reason).Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("legacy.dll", "Raw assemblies")]
    [InlineData("legacy.daxplugin", "retired")]
    [InlineData("archive.zip", "not a DaxAlgo artifact")]
    [InlineData("sealed.daxq", "not a DaxAlgo artifact")]
    public void Refuses_everything_else_and_says_why(string name, string expected)
    {
        DaxPackage.IsAccepted(name, out var reason).Should().BeFalse();
        reason.Should().Contain(expected);
    }

    [Fact]
    public void The_accepted_set_is_exactly_two_extensions()
    {
        // Pinned so a third file type cannot be admitted without this test being changed on purpose.
        DaxPackage.AcceptedExtensions.Should().BeEquivalentTo([".daxalgostrategy", ".daxalgovisualizer"]);
    }

    // ── Malformed input ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_something_that_is_not_an_archive()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        File.WriteAllText(path, "definitely not a zip");

        var act = () => DaxPackage.Read(path);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.NotAnArchive);
    }

    [Fact]
    public void Rejects_an_archive_with_no_manifest()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("src/Strategy.cs").Open())
            entry.Write(Bytes("x"));

        var act = () => DaxPackage.Read(path);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.ManifestMissing);
    }

    [Fact]
    public void Rejects_a_manifest_from_a_newer_format_version()
    {
        var path = Path_("s" + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, Request());
        Repack(path, entries =>
        {
            var json = Encoding.UTF8.GetString(entries[DaxPackage.ManifestEntryPath]);
            entries[DaxPackage.ManifestEntryPath] = Bytes(json.Replace("\"formatVersion\":1", "\"formatVersion\":99"));
        });

        var act = () => DaxPackage.Read(path);

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.UnsupportedFormat);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Requires_the_identifying_fields(string blank)
    {
        var act = () => DaxPackage.Write(Path_("s" + DaxPackage.StrategyExtension),
            Request() with { Id = blank });

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.ManifestMalformed);
    }

    [Fact]
    public void Enforces_the_total_expansion_limit()
    {
        var big = new byte[4096];
        var act = () => DaxPackage.Write(Path_("s" + DaxPackage.StrategyExtension),
            Request(payloads: [DaxPayloadSource.FromBytes("bin/big.dll", DaxPayloadRole.Assembly, big)]),
            new DaxPackageLimits { MaximumTotalBytes = 1024 });

        act.Should().Throw<DaxPackageException>().Which.Error.Should().Be(DaxPackageError.LimitExceeded);
    }

    /// <summary>Rewrites a package's raw entries, standing in for an attacker with the file.</summary>
    private static void Repack(string path, Action<Dictionary<string, byte[]>> mutate)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var read = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
            foreach (var e in read.Entries)
            {
                using var s = e.Open();
                using var buffer = new MemoryStream();
                s.CopyTo(buffer);
                entries[e.FullName] = buffer.ToArray();
            }

        mutate(entries);

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var s = archive.CreateEntry(name).Open();
            s.Write(data);
        }
    }
}
