using System.IO;
using TradingTerminal.App.Authoring;
using TradingTerminal.Blocks.WebHost;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Puts a Blocks unit somebody wrote by hand — its class and its <c>ui/</c> page — through the gate the
/// Blocks builder uses, and installs it where the app loads authored units from. No model is called.
///
/// <para><b>Off unless <c>BLOCKS_UNIT_DIR</c> names a folder</b> laid out the way the package carries
/// it: the unit's <c>.cs</c> files at the top and its page under <c>ui/</c>. <c>UNIT_ID</c> and
/// <c>UNIT_NAME</c> name it; the folder's name is the fallback.</para>
///
/// <para><see cref="InstallAHandWrittenUnit"/> does this for the widget SDK, but a unit without its own
/// page has been refused at install since 2026-09-15 (<c>UnitPageRule</c>), so that harness now compiles
/// a unit the app will never show. This is the hand-written path that still ends in the catalog, and it
/// shares every step with the builder: the same <see cref="BlocksGate"/> with the page open, the same
/// artifact, the same installer under the profile the loader uses.</para>
/// </summary>
public sealed class InstallAHandWrittenBlocksUnit(ITestOutputHelper output)
{
    [Fact]
    public async Task Gate_package_and_install_it()
    {
        var folder = Environment.GetEnvironmentVariable("BLOCKS_UNIT_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;

        Assert.True(Directory.Exists(folder), $"No folder at {folder}");

        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
            .Where(name => (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !name.Contains('/'))
                           || name.StartsWith("ui/", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new StrategyFile(name, File.ReadAllText(Path.Combine(folder, name))))
            .ToArray();

        Assert.Contains(files, file => file.Name == BlocksGate.PageEntry);

        var id = Env("UNIT_ID") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).ToLowerInvariant();
        var name = Env("UNIT_NAME") ?? id;

        var gate = new BlocksGate(new BlocksUnitCompiler(), id, new WebPageProbe());
        var result = await gate.RunAsync(files);

        output.WriteLine($"{name} ({id})");
        output.WriteLine($"compiled: {gate.Latest?.Success == true}   rungs: {result.Report.RungsCleared}   "
            + $"failed at: {result.Report.FailedAt?.ToString() ?? "nothing"}");

        foreach (var finding in result.Report.Findings) output.WriteLine("  " + finding);
        foreach (var finding in gate.LatestDrive?.Findings ?? []) output.WriteLine("  drive: " + finding);
        foreach (var advisory in result.Advisories) output.WriteLine("  measured: " + advisory);
        if (result.Advisories.Count == 0) output.WriteLine("  measured: the page fits its window, nothing covers its main view, no broken values");

        // GATE_ONLY: measure and photograph, install nothing — for looking at a unit somebody else built.
        if (Env("GATE_ONLY") is not null)
        {
            if (gate.LatestPicture is { } shot && Env("GATE_OUT") is { } outFolder)
            {
                Directory.CreateDirectory(outFolder);
                File.WriteAllBytes(Path.Combine(outFolder, id + ".png"), shot.Png);
                output.WriteLine($"page photographed to {Path.Combine(outFolder, id + ".png")}");
            }

            return;
        }

        // Kept beside the builder's run folders, so the source behind the installed unit and a picture
        // of its page are findable after the test has gone.
        var kept = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo Terminal", "hyperion-runs", id);
        Directory.CreateDirectory(kept);

        if (gate.LatestPicture is { } picture)
        {
            var png = Path.Combine(kept, id + ".png");
            File.WriteAllBytes(png, picture.Png);
            output.WriteLine($"page photographed {picture.Width}x{picture.Height} to {png}");
        }

        Assert.True(result.Report.Passed, "The gate refused it — see the findings above.");

        var artifact = AuthoredArtifact.Write(new StrategyScript(id, name, files), gate.Latest!, kept);
        Assert.True(artifact.Success, artifact.Message);

        var root = AuthoredUnitsRoot.Ensure();
        Assert.NotNull(root);

        var store = new AuthoredUnitStore(new PluginHostContext(
            AuthoredUnitsRoot.Path,
            PluginTrustPolicy.Permissive,
            LoadedPlugins: [],
            State: new PluginStateStore(AuthoredUnitsRoot.Path)));

        var install = store.Install(artifact.Path!, root!);
        output.WriteLine(install.Message);
        Assert.True(install.Success, install.Message);

        // A saved session too, so the unit opens in the builder like any other and can be changed there.
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;
        AuthoringSessionStore.Save(new AuthoringSessionSnapshot(
            id,
            name,
            [new AuthoringChatEntry(AuthoringChatEntry.System, "Written by hand, not generated.", DateTime.Now)],
            [],
            files,
            Registered: true));

        output.WriteLine($"package kept at {artifact.Path}");
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
