using System.IO;
using System.Text;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using DaxAlgo.Sdk;
using TradingTerminal.Authoring.Rasterizer;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using TradingTerminal.UI.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Puts a unit somebody wrote by hand through the same gate, registration and install the builder
/// uses.
///
/// <para><b>Off unless <c>UNIT_SOURCE</c> names a file.</b> The AI path has a harness; a hand-written
/// unit had none, so the only way to try one was to paste it into the pane. That is fine once and
/// useless as a loop — and the loop is the point when the thing being iterated on is how a picture
/// looks.</para>
///
/// <para>It deliberately shares every step with <see cref="HyperionRegisterRun"/>: the same
/// <see cref="UnitGate"/>, the same <see cref="AuthoredUnitSink"/>, the same artifact and the same
/// installer under the profile the loader will use. A hand-written unit that installs through a
/// different path than a generated one proves nothing about either.</para>
/// </summary>
public sealed class InstallAHandWrittenUnit(ITestOutputHelper output)
{
    [Fact]
    public void Compile_register_and_keep_it()
    {
        var source = Environment.GetEnvironmentVariable("UNIT_SOURCE");
        if (string.IsNullOrWhiteSpace(source)) return;

        Assert.True(File.Exists(source), $"No source at {source}");

        var id = Environment.GetEnvironmentVariable("UNIT_ID")
            ?? Path.GetFileNameWithoutExtension(source).ToLowerInvariant();
        var name = Environment.GetEnvironmentVariable("UNIT_NAME")
            ?? Path.GetFileNameWithoutExtension(source);

        var files = new[] { new StrategyFile(Path.GetFileName(source), File.ReadAllText(source)) };
        var script = new StrategyScript(id, name, files);

        var compiler = new RoslynStrategyCompiler();
        var gate = new UnitGate(compiler, id, name).Run(files);

        output.WriteLine($"{name} ({id})");
        output.WriteLine($"compiled: {gate.Compile?.Success == true}   "
            + $"rungs: {gate.Report.RungsCleared}   failed at: {gate.Report.FailedAt?.ToString() ?? "nothing"}");

        foreach (var finding in gate.Report.Findings) output.WriteLine("  " + finding);

        if (gate.Compile is not { Success: true } compiled || gate.Unit is not { } unit)
        {
            Assert.Fail("It does not compile — see the findings above.");
            return;
        }

        output.WriteLine($"unit: {unit.Kind} {unit.Type.Name}");

        // The real registries, so a descriptor that cannot be built fails here rather than in the app.
        var sink = new AuthoredUnitSink(new StrategyKernelRegistry(), new VisualizerRegistry());
        output.WriteLine(sink.Register(unit, id, name));

        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;

        // A saved session too, so the unit opens in the builder like any other and can be iterated on
        // there rather than only from a file.
        var now = DateTime.Now;
        AuthoringSessionStore.Save(new AuthoringSessionSnapshot(
            id,
            name,
            [new AuthoringChatEntry(AuthoringChatEntry.System, "Written by hand, not generated.", now)],
            [],
            files,
            Registered: true));

        var artifact = AuthoredArtifact.Write(script, compiled);
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

        // Kept beside the run artifacts so the source that produced the installed DLL is findable.
        var kept = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo Terminal", "hyperion-runs", id);

        Directory.CreateDirectory(kept);
        File.WriteAllText(
            Path.Combine(kept, Path.GetFileName(source)),
            File.ReadAllText(source),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // AND A PICTURE OF IT, because the ladder answers "does it draw" and the question here is
        // "does it look right". Every rung can pass over a panel nobody would want to look at, and the
        // whole reason this file exists is that the pictures were not good enough.
        //
        // Driven against a DENSE tape rather than the shared synthetic one. That drive emits two prints
        // per bar, both a fixed distance either side of the close — enough to prove a footprint enters
        // its callbacks, and nowhere near enough to show what one looks like. A preview of a cluster
        // with two levels in it is a preview of nothing, and judging the picture was the point.
        var preview = AuthoredUnitPreview.Create(unit);
        var draw = preview.Draw;
        var layout = preview.Layout;

        if (unit.Kind == AuthoringKind.Visualizer
            && Activator.CreateInstance(unit.Type) is IVisualizer dense)
        {
            SyntheticDrive.Run(dense, DenseTape());
            draw = dense.Draw;
            layout = dense.Layout;
        }

        if (draw is not null)
        {
            using var rasterizer = new WpfUnitRasterizer();
            var raster = rasterizer.RenderAsync(draw, layout, 1400, 900).GetAwaiter().GetResult();

            if (raster is not null)
            {
                var png = Path.Combine(kept, id + ".png");
                File.WriteAllBytes(png, raster.Png);
                output.WriteLine($"rendered {raster.Width}x{raster.Height} to {png}");
            }
        }

        output.WriteLine($"source kept at {kept}");
    }

    /// <summary>
    /// A minute-bar tape with a real cluster in it: many prints per bar, spread across the bar's range
    /// and signed by where they landed against the spread.
    ///
    /// <para>Deliberately uneven. A tape that puts the same size at every price draws a rectangle, and
    /// a footprint whose point of control and value area are wherever the arithmetic rounded is a
    /// picture that cannot be wrong and therefore cannot be checked.</para>
    /// </summary>
    private static SyntheticDrive.CapturedMarket DenseTape()
    {
        var instrument = SyntheticDrive.Instrument;
        var start = new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc);
        var bars = new List<OhlcvBar>();
        var trades = new List<TradePrint>();
        var quotes = new List<Quote>();

        var price = 148.00d;
        var random = new Random(20260911);
        var sequence = 0L;

        for (var b = 0; b < 26; b++)
        {
            var open = start.AddMinutes(b);
            var drift = ((b % 7) - 3) * 0.04d;
            var close = Math.Round(price + drift + ((random.NextDouble() - 0.5d) * 0.08d), 2);
            var high = Math.Max(price, close) + Math.Round(random.NextDouble() * 0.12d, 2);
            var low = Math.Min(price, close) - Math.Round(random.NextDouble() * 0.12d, 2);

            var volume = 0L;
            var levels = (int)Math.Max(4d, Math.Round((high - low) / 0.01d));

            for (var i = 0; i <= levels; i++)
            {
                var at = Math.Round(low + (i * 0.01d), 2);

                // Heaviest near the middle of the bar's range, thinning toward the extremes — which is
                // what gives a cluster a point of control worth marking.
                var centre = 1d - (Math.Abs(at - ((high + low) / 2d)) / Math.Max(0.01d, (high - low) / 2d));
                var size = (long)Math.Max(1d, Math.Round(6d + (centre * 40d) + (random.NextDouble() * 8d)));

                // BOTH SIDES AT MOST LEVELS. A fixture that puts every print on one side of the
                // level draws a column of "0 | 42" cells and hides the one thing a footprint is read
                // for — the imbalance between them. Skewed by where the level sits against the close,
                // which is what a real tape does.
                var lean = Math.Clamp(0.5d + ((at - close) * 6d), 0.15d, 0.85d);
                var ask = (long)Math.Round(size * lean);
                var bid = Math.Max(0L, size - ask);
                quotes.Add(new Quote(
                    instrument, open.AddSeconds(i), open.AddSeconds(i),
                    at - 0.01d, at + 0.01d, 50L, 50L, BrokerKind.Simulated, sequence, false));

                if (ask > 0L)
                {
                    trades.Add(new TradePrint(
                        instrument, open.AddSeconds(i), open.AddSeconds(i), at, ask,
                        AggressorSide.Buy, BrokerKind.Simulated, sequence++, false));
                }

                if (bid > 0L)
                {
                    trades.Add(new TradePrint(
                        instrument, open.AddSeconds(i), open.AddSeconds(i), at, bid,
                        AggressorSide.Sell, BrokerKind.Simulated, sequence++, false));
                }

                volume += size;
            }

            bars.Add(new OhlcvBar(
                instrument, BarSize.OneMinute, open, price, high, low, close, volume,
                BrokerKind.Simulated, IsFinal: true));

            price = close;
        }

        return new SyntheticDrive.CapturedMarket(bars, quotes, trades, []);
    }
}
