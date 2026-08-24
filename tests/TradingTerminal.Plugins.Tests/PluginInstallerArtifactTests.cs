using System.IO;
using System.Text;
using DaxAlgo.Package;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Installing a <c>.daxalgostrategy</c> / <c>.daxalgovisualizer</c> — the only two artifact formats the
/// terminal accepts since the <c>.daxplugin</c> path was removed on 2026-08-24.
///
/// <para>Every case here is about the boundary where untrusted bytes become files on disk, which is why
/// the refusals matter more than the happy path: an installer that writes something before deciding it
/// should have refused has already lost.</para>
/// </summary>
public sealed class PluginInstallerArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daxalgo-installer-tests-" + Guid.NewGuid().ToString("N"));

    private string PluginsRoot => Path.Combine(_root, "plugins");

    public PluginInstallerArtifactTests() => Directory.CreateDirectory(PluginsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(".daxplugin")]
    [InlineData(".dll")]
    [InlineData(".zip")]
    public void RetiredAndForeignFormatsAreRefusedByName(string extension)
    {
        // Refused BY NAME, before the file is opened. A user holding a .daxplugin gets told the format
        // is retired rather than watching a generic "not an archive" error.
        var path = Path.Combine(_root, "artifact" + extension);
        File.WriteAllBytes(path, [1, 2, 3]);

        var result = Install(path);

        result.Success.Should().BeFalse();
        Directory.GetFileSystemEntries(PluginsRoot).Should().BeEmpty();
    }

    [Fact]
    public void TheRefusalForTheRetiredFormatSaysWhatToDoInstead()
    {
        var path = Path.Combine(_root, "legacy.daxplugin");
        File.WriteAllBytes(path, [1, 2, 3]);

        Install(path).Message.Should().Contain("retired").And.Contain(DaxPackage.StrategyExtension);
    }

    [Fact]
    public void ASourceOnlyPackageIsRefusedWithAReasonRatherThanAMissingDllError()
    {
        // Hyperion emits source before it emits an assembly, so this is a real state a package can be
        // in — not a corrupt file. The message has to say "build it first", because "does not contain
        // Foo.dll" would send the author looking for a packaging bug that isn't there.
        var path = WritePackage("SourceOnly", includeAssembly: false);

        var result = Install(path);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("source-only");
        Directory.GetFileSystemEntries(PluginsRoot).Should().BeEmpty();
    }

    [Fact]
    public void AVerifiedPackageLandsOnTheLoaderSFolderConvention()
    {
        var path = WritePackage("Contoso.Strategy", includeAssembly: true);

        var result = Install(path);

        result.Success.Should().BeTrue(result.Message);
        File.Exists(Path.Combine(PluginsRoot, "Contoso.Strategy", "Contoso.Strategy.dll"))
            .Should().BeTrue("the loader looks for plugins/<Name>/<Name>.dll");
    }

    [Fact]
    public void NonAssemblyPayloadsTravelWithTheArtifact()
    {
        // Copied WHOLE, so an artifact can carry its own private dependencies and its UI declaration.
        var path = WritePackage("WithExtras", includeAssembly: true, extras: true);

        Install(path).Success.Should().BeTrue();

        File.Exists(Path.Combine(PluginsRoot, "WithExtras", "ui.json")).Should().BeTrue();
        File.Exists(Path.Combine(PluginsRoot, "WithExtras", "WithExtras.cs")).Should().BeTrue();
    }

    [Fact]
    public void ATamperedPayloadIsRefusedAndNothingIsWritten()
    {
        // The digest check lives in DaxPackage.Read, which runs before staging. This pins the ORDER:
        // a package that fails verification must not leave a partially-written plugin folder behind.
        // The substitution keeps the payload length identical, so only the sha256 can catch it.
        var path = WritePackage("Tampered", includeAssembly: true);
        Corrupt(path);

        var result = Install(path);

        result.Success.Should().BeFalse();
        // Named explicitly so this cannot start passing for an unrelated reason — in particular the
        // length check, which is cheaper and fires first when a substitution changes the size.
        result.Message.Should().Contain("digest");
        Directory.GetFileSystemEntries(PluginsRoot).Should().BeEmpty();
    }

    [Fact]
    public void AMissingFileIsAFailureRatherThanAnException()
    {
        // Never throws is part of the contract — the Plugin Manager binds the message straight to the UI.
        Install(Path.Combine(_root, "nope" + DaxPackage.StrategyExtension)).Success.Should().BeFalse();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private PluginInstallResult Install(string path) =>
        PluginInstaller.InstallFromArtifact(
            path,
            PluginsRoot,
            PluginTrustPolicy.Permissive,
            new NullSignatureInspector(),
            state: null,
            scanMode: PluginScanMode.Off);

    private string WritePackage(string name, bool includeAssembly, bool extras = false)
    {
        var payloads = new List<DaxPayloadSource>();

        if (includeAssembly)
        {
            // A real managed assembly: the installer hashes it and the policy scan reads it, so random
            // bytes would fail for the wrong reason.
            var self = File.ReadAllBytes(GetType().Assembly.Location);
            payloads.Add(DaxPayloadSource.FromBytes($"payload/{name}.dll", DaxPayloadRole.Assembly, self));
        }

        payloads.Add(DaxPayloadSource.FromBytes(
            $"payload/{name}.cs", DaxPayloadRole.Source, Encoding.UTF8.GetBytes("// source")));

        if (extras)
        {
            payloads.Add(DaxPayloadSource.FromBytes(
                "payload/ui.json", DaxPayloadRole.Ui, Encoding.UTF8.GetBytes("{}")));
        }

        var path = Path.Combine(_root, name + DaxPackage.StrategyExtension);
        DaxPackage.Write(path, new DaxPackageRequest
        {
            Kind = DaxPackageKind.Strategy,
            Id = "test." + name,
            Version = "1.0.0",
            DisplayName = name,
            EntryTypeName = $"{name}.Entry",
            Payloads = payloads,
        });
        return path;
    }

    /// <summary>Substitutes one payload's bytes inside the zip, keeping the LENGTH identical so the
    /// manifest's declared size still matches. That leaves the sha256 as the only thing standing
    /// between a swapped payload and the plugins folder, which is the point of the check.</summary>
    private static void Corrupt(string packagePath)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            packagePath, System.IO.Compression.ZipArchiveMode.Update);
        var entry = archive.Entries.First(e => e.FullName.EndsWith(".cs", StringComparison.Ordinal));
        var name = entry.FullName;
        entry.Delete();
        var replacement = archive.CreateEntry(name);
        using var stream = replacement.Open();
        stream.Write("// hacked"u8); // exactly as long as "// source"
    }
}
