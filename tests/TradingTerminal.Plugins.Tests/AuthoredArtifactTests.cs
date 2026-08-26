using System.IO;
using System.Text;
using DaxAlgo.Package;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Packaging an authored unit (#44 phase 6) — the step that turns something Hyperion made into a file.
///
/// <para>Before this, everything the builder produced lived only in the running process: registered,
/// drawn, verified, and gone at the next launch, with nothing on disk to back up, send to anyone, or
/// install on a second machine. These tests use the real Roslyn compiler and the real package reader, so
/// what they prove is that a unit written today can be installed tomorrow.</para>
/// </summary>
public sealed class AuthoredArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daxalgo-artifact-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Ambient = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using DaxAlgo.Sdk;
        using DaxAlgo.Sdk.Drawing;
        using TradingTerminal.Core.Domain;
        using TradingTerminal.Core.Strategies;
        using TradingTerminal.Core.Strategies.Parameters;
        """;

    private const string Kernel = """
        public sealed class PackagedKernel : IStrategyKernel
        {
            private readonly System.Collections.Generic.List<double> _closes = new(64);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 200));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct)
            {
                _ = c.Parameters.GetInt("lookback");
                _closes.Clear();
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext c, CancellationToken ct)
            {
                if (_closes.Count == 64) _closes.RemoveAt(0);
                _closes.Add(bar.Close);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Packaged", RenderPanelKind.Chart);
                if (_closes.Count == 0) { Plot.Waiting(surface); return; }

                Series.Draw(surface, "Close", _closes);
            }
        }
        """;

    private const string Visualizer = """
        public sealed class PackagedViz : IVisualizer
        {
            public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
            public Task OnStartAsync(IVisualizerContext c, CancellationToken ct) => Task.CompletedTask;
            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Viz", RenderPanelKind.Chart);
                Plot.Waiting(surface, "no data");
            }
        }
        """;

    private static StrategyScript Script(string id, string body, string displayName = "Packaged unit") =>
        new(id, displayName, [new StrategyFile("Unit.cs", Ambient + "\n" + body)]);

    private static StrategyCompileResult Compile(StrategyScript script) =>
        new RoslynStrategyCompiler().Compile(script);

    private AuthoredArtifactResult Package(string id, string body, string displayName = "Packaged unit")
    {
        var script = Script(id, body, displayName);
        var compiled = Compile(script);
        compiled.Success.Should().BeTrue(
            string.Join("; ", compiled.Errors.Select(e => e.Message)));

        return AuthoredArtifact.Write(script, compiled, _root);
    }

    // ── the file ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKernelIsWrittenAsAStrategyArtifact()
    {
        var result = Package("packaged.kernel", Kernel);

        result.Success.Should().BeTrue(result.Message);
        result.Path.Should().EndWith(".daxalgostrategy");
        File.Exists(result.Path!).Should().BeTrue();
    }

    [Fact]
    public void AVisualizerIsWrittenAsAVisualizerArtifact()
    {
        // The extension is the only outward difference between the two, and it is taken from what the
        // author actually wrote rather than from anything they typed.
        var result = Package("packaged.viz", Visualizer);

        result.Success.Should().BeTrue(result.Message);
        result.Path.Should().EndWith(".daxalgovisualizer");
        result.Manifest!.Kind.Should().Be(DaxPackageKind.Visualizer);
    }

    [Fact]
    public void TheEntryTypeIsTheExactResolvedTypeNotAGuess()
    {
        // The host resolves this name exactly and never scans for a substitute, so a near-miss here is a
        // package that installs and then cannot start.
        var result = Package("packaged.kernel", Kernel);

        result.Manifest!.EntryTypeName.Should().EndWith("PackagedKernel");
    }

    [Fact]
    public void TheArtifactCarriesBothTheAssemblyAndTheSource()
    {
        // The assembly is what makes it installable at all. The source is what makes it reviewable a year
        // later by somebody who was not there when the model wrote it.
        var result = Package("packaged.kernel", Kernel);
        var roles = result.Manifest!.Payloads.Select(p => p.Role).ToArray();

        roles.Should().Contain(DaxPayloadRole.Assembly);
        roles.Should().Contain(DaxPayloadRole.Source);
    }

    [Fact]
    public void TheSourceInTheArtifactIsTheSourceThatWasCompiled()
    {
        var result = Package("packaged.kernel", Kernel);
        var contents = DaxPackage.Read(result.Path!);

        var source = contents.Payloads.Single(p => p.Key.StartsWith("payload/src/", StringComparison.Ordinal));
        Encoding.UTF8.GetString(source.Value).Should().Contain("class PackagedKernel");
    }

    // ── it reads back ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheArtifactReadsBackThroughTheRealReader()
    {
        // Written by us, read by the code that reads a stranger's package. Anything the reader would
        // reject in the field is rejected here instead.
        var result = Package("packaged.kernel", Kernel);
        var contents = DaxPackage.Read(result.Path!);

        contents.Manifest.Id.Should().Be("packaged.kernel");
        contents.Manifest.DisplayName.Should().Be("Packaged unit");
        contents.Payloads.Should().NotBeEmpty();
    }

    [Fact]
    public void ATamperedArtifactIsRejected()
    {
        // The digests are the point of the format. If a byte can be changed after signing-off and still
        // install, the review the user did was of something else.
        var result = Package("packaged.kernel", Kernel);
        var bytes = File.ReadAllBytes(result.Path!);

        // Flip a byte in the middle of the archive, keeping the length identical so the change has to be
        // caught by a digest rather than by a size check.
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(result.Path!, bytes);

        var read = () => DaxPackage.Read(result.Path!);
        read.Should().Throw<DaxPackageException>();
    }

    // ── it installs ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheArtifactInstallsThroughTheOrdinaryPluginInstaller()
    {
        // The capstone. Real source, the real compiler, the real package writer, the real reader, and the
        // installer a user drives from the Plugin Manager — the whole chain #44 phase 6 asks for, in one
        // pass, with nothing stubbed.
        var result = Package("packaged.kernel", Kernel);
        var plugins = Path.Combine(_root, "plugins");

        var install = PluginInstaller.InstallFromArtifact(
            result.Path!,
            plugins,
            PluginTrustPolicy.Permissive,
            NoSignature.Instance,
            scanMode: PluginScanMode.Enforce);

        install.Success.Should().BeTrue(install.Message);
        Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories)
            .Should().NotBeEmpty("the installed plugin folder must contain the assembly the host loads");
    }

    [Fact]
    public void ACuratedHostRefusesAnUnsignedAuthoredArtifact()
    {
        // The reason writing an artifact must never install it. An authored unit is, by design, whatever
        // a model could be talked into writing; if packaging it were also a way to load it, that would be
        // the most attractive route into a curated host that exists. The refusal is the feature.
        var result = Package("packaged.kernel", Kernel);

        var install = PluginInstaller.InstallFromArtifact(
            result.Path!,
            Path.Combine(_root, "curated"),
            PluginTrustPolicy.Curated(["0000000000000000000000000000000000000000"]),
            NoSignature.Instance);

        install.Success.Should().BeFalse("an unsigned local build is not a trusted publisher");
    }

    // ── it declines cleanly ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CodeThatDidNotCompileIsNotPackaged()
    {
        var script = Script("packaged.broken", "public sealed class Broken : IStrategyKernel { }");
        var result = AuthoredArtifact.Write(script, Compile(script), _root);

        result.Success.Should().BeFalse();
        result.Path.Should().BeNull();
        Directory.Exists(_root).Should().BeFalse("nothing should have been created for a failed compile");
    }

    [Fact]
    public void AUnitWithNoIdIsRefusedRatherThanWrittenToAStrangePath()
    {
        var script = new StrategyScript(" ", "No id", [new StrategyFile("Unit.cs", Ambient + "\n" + Kernel)]);

        // The compiler needs an id too, so this is asserted on the writer directly with a compile that did
        // succeed for a differently-named script — the writer must not depend on the caller having checked.
        var compiled = Compile(Script("packaged.kernel", Kernel));
        var result = AuthoredArtifact.Write(script, compiled, _root);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("id");
    }

    [Fact]
    public void AnIdThatIsNotAFileNameStillProducesOneFile()
    {
        // Ids are typed by users and generated by models. Neither is a file name.
        var result = Package("my strategy/../v2:final", Kernel);

        result.Success.Should().BeTrue(result.Message);
        Path.GetFileName(result.Path!).Should().NotContain("..");
        Path.GetDirectoryName(Path.GetFullPath(result.Path!))
            .Should().Be(Path.GetFullPath(_root), "a crafted id must not steer where the file lands");
    }

    [Fact]
    public void RepackagingOverwritesRatherThanAccumulating()
    {
        // A user regenerates until it is right. One artifact per unit, not one per attempt.
        Package("packaged.kernel", Kernel);
        Package("packaged.kernel", Kernel);

        Directory.EnumerateFiles(_root, "*.daxalgostrategy").Should().HaveCount(1);
    }

    /// <summary>A plugin whose signature is never inspected — what Permissive means in practice.</summary>
    private sealed class NoSignature : IPluginSignatureInspector
    {
        public static NoSignature Instance { get; } = new();

        public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
    }
}
