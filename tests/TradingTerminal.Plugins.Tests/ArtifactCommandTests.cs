using System.IO;
using System.Text;
using DaxAlgo.Package;
using DaxAlgo.StrategyTool;
using FluentAssertions;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The operator commands (#35): <c>pack</c>, <c>unpack</c>, <c>inspect</c>, <c>verify</c>.
///
/// <para>The format is opaque by design, and that cuts both ways: right for a file from a stranger,
/// unhelpful when an install fails and the only thing anybody can say is "rejected". These are the
/// commands you reach for then, so what they must never do is succeed on a file the host would refuse,
/// or fail on one it would accept.</para>
/// </summary>
public sealed class ArtifactCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-cli-" + Guid.NewGuid().ToString("N"));

    private string In(string name) => Path.Combine(_dir, name);

    public ArtifactCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A folder laid out the way an installed plugin is.</summary>
    private string Folder(bool withManifest = true, bool withAssembly = true)
    {
        var folder = In("source");
        Directory.CreateDirectory(Path.Combine(folder, "src"));

        if (withAssembly) File.WriteAllBytes(Path.Combine(folder, "demo.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        File.WriteAllText(Path.Combine(folder, "src", "Unit.cs"), "public sealed class Demo { }");

        if (withManifest)
        {
            File.WriteAllText(
                Path.Combine(folder, "plugin.json"),
                """{ "id": "demo.unit", "name": "Demo unit", "version": "1.2.3", "targetSdkVersion": "0.4.0" }""");
        }

        return folder;
    }

    private int Pack(params (string Key, string Value)[] options)
    {
        var map = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase);
        map.TryAdd("entry", "Demo.Unit");
        map.TryAdd("out", In("out.daxalgostrategy"));
        return ArtifactCommands.Pack(Folder(), map);
    }

    // ── pack ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PackProducesAFileTheRealReaderAccepts()
    {
        Pack().Should().Be(0);

        var contents = DaxPackage.Read(In("out.daxalgostrategy"));
        contents.Manifest.Id.Should().Be("demo.unit");
        contents.Manifest.Version.Should().Be("1.2.3");
    }

    [Fact]
    public void PackTakesIdentityFromPluginJsonSoItNeedNotBeRetyped()
    {
        Pack().Should().Be(0);

        DaxPackage.Read(In("out.daxalgostrategy")).Manifest.DisplayName.Should().Be("Demo unit");
    }

    [Fact]
    public void PackRefusesWithoutAnEntryType()
    {
        // The host resolves the entry type exactly and never scans for a substitute. A packer that
        // guessed would be deciding on the host's behalf, using a reflection load of untrusted code.
        ArtifactCommands.Pack(Folder(), new Dictionary<string, string> { ["out"] = In("x.daxalgostrategy") })
            .Should().Be(1);
    }

    [Fact]
    public void PackRefusesAFolderWithNoAssembly()
    {
        // It would write happily and then be refused at install as source-only. Saying so here is better
        // than a file that exists and cannot be used.
        var folder = In("no-assembly");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Unit.cs"), "class X { }");

        ArtifactCommands.Pack(folder, new Dictionary<string, string>
        {
            ["entry"] = "X", ["out"] = In("x.daxalgostrategy"),
        }).Should().Be(1);
    }

    [Fact]
    public void PackRefusesAnUnknownKind()
    {
        ArtifactCommands.Pack(Folder(), new Dictionary<string, string>
        {
            ["entry"] = "Demo.Unit", ["kind"] = "indicator", ["out"] = In("x.daxalgostrategy"),
        }).Should().Be(1);
    }

    [Fact]
    public void AVisualizerGetsTheVisualizerExtension()
    {
        ArtifactCommands.Pack(Folder(), new Dictionary<string, string>
        {
            ["entry"] = "Demo.Unit", ["kind"] = "visualizer", ["out"] = In("out.daxalgovisualizer"),
        }).Should().Be(0);

        DaxPackage.Read(In("out.daxalgovisualizer")).Manifest.Kind.Should().Be(DaxPackageKind.Visualizer);
    }

    [Fact]
    public void RolesAreDeclaredFromWhatEachFileIs()
    {
        Pack().Should().Be(0);

        var payloads = DaxPackage.Read(In("out.daxalgostrategy")).Manifest.Payloads;

        payloads.Should().Contain(p => p.Role == DaxPayloadRole.Assembly && p.Path.EndsWith(".dll"));
        payloads.Should().Contain(p => p.Role == DaxPayloadRole.Source && p.Path.EndsWith(".cs"));
    }

    // ── verify ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyPassesAGoodPackage()
    {
        Pack().Should().Be(0);

        ArtifactCommands.Verify(In("out.daxalgostrategy")).Should().Be(0);
    }

    [Fact]
    public void VerifyFailsWhenAPayloadIsChanged()
    {
        // The exit code IS the feature — this is what a CI gate reads.
        //
        // The change is made to a payload's own bytes rather than at a random offset in the file. A
        // flipped byte at the midpoint of a small archive lands in ZIP structure as often as in content,
        // which makes the test pass or fail on where the fixture happened to put things rather than on
        // whether a digest is checked.
        Pack().Should().Be(0);
        var path = In("out.daxalgostrategy");

        using (var archive = System.IO.Compression.ZipFile.Open(
                   path, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = archive.Entries.First(e => e.FullName.EndsWith(".cs", StringComparison.Ordinal));
            var name = entry.FullName;
            entry.Delete();

            var replacement = archive.CreateEntry(name);
            using var stream = replacement.Open();
            stream.Write(Encoding.UTF8.GetBytes("public sealed class Demo { /* and something else */ }"));
        }

        ArtifactCommands.Verify(path).Should().Be(1);
    }

    [Fact]
    public void VerifyFailsWhenAnUndeclaredFileIsAdded()
    {
        // The other half of the same guarantee: a package is rejected for what was smuggled in as well
        // as for what was altered.
        Pack().Should().Be(0);
        var path = In("out.daxalgostrategy");

        using (var archive = System.IO.Compression.ZipFile.Open(
                   path, System.IO.Compression.ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry("payload/extra.dll").Open();
            stream.Write([0x4D, 0x5A, 0x00, 0x00]);
        }

        ArtifactCommands.Verify(path).Should().Be(1);
    }

    [Fact]
    public void VerifyFailsAMissingFile()
    {
        ArtifactCommands.Verify(In("nothing.daxalgostrategy")).Should().Be(1);
    }

    [Fact]
    public void VerifyFailsSomethingThatIsNotAPackageAtAll()
    {
        File.WriteAllText(In("hello.daxalgostrategy"), "this is not a zip");

        ArtifactCommands.Verify(In("hello.daxalgostrategy")).Should().Be(1);
    }

    // ── inspect ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InspectReadsAGoodPackage()
    {
        Pack().Should().Be(0);

        ArtifactCommands.Inspect(In("out.daxalgostrategy")).Should().Be(0);
    }

    [Fact]
    public void InspectFailsRatherThanPrintingHalfAPackage()
    {
        ArtifactCommands.Inspect(In("nothing.daxalgostrategy")).Should().Be(1);
    }

    // ── unpack ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnpackGetsTheSourceOutWhereSomebodyCanReadIt()
    {
        // Source nobody can get at is source nobody will read, and reviewing before running is the whole
        // premise of the review gate.
        Pack().Should().Be(0);

        ArtifactCommands.Unpack(In("out.daxalgostrategy"), In("unpacked")).Should().Be(0);

        File.ReadAllText(Path.Combine(In("unpacked"), "src", "Unit.cs"))
            .Should().Contain("class Demo");
    }

    [Fact]
    public void UnpackWritesTheManifestSoTheFolderCanBeRepacked()
    {
        // The manifest is not a payload, so without this the entry type is lost and the round trip
        // cannot close.
        Pack().Should().Be(0);
        ArtifactCommands.Unpack(In("out.daxalgostrategy"), In("unpacked")).Should().Be(0);

        File.ReadAllText(Path.Combine(In("unpacked"), "manifest.json")).Should().Contain("Demo.Unit");
    }

    [Fact]
    public void UnpackThenPackRoundTrips()
    {
        Pack().Should().Be(0);
        ArtifactCommands.Unpack(In("out.daxalgostrategy"), In("unpacked")).Should().Be(0);

        ArtifactCommands.Pack(In("unpacked"), new Dictionary<string, string>
        {
            ["entry"] = "Demo.Unit", ["out"] = In("again.daxalgostrategy"),
        }).Should().Be(0);

        DaxPackage.Read(In("again.daxalgostrategy")).Manifest.Id.Should().Be("demo.unit");
    }

    [Fact]
    public void UnpackFailsAMissingFile()
    {
        ArtifactCommands.Unpack(In("nothing.daxalgostrategy"), In("unpacked")).Should().Be(1);
    }
}
