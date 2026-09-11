using System.IO;
using System.Text;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using TradingTerminal.UI.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Registers the units a live run produced, through the path the Register button uses.
///
/// <para><b>Off unless <c>HYPERION_REGISTER=1</c>.</b> Installing code where the host loads it at every
/// future start is the one genuinely consequential thing in this area, and the product gates it behind a
/// review overlay for that reason. This is the same gate answered once, deliberately, for a named set of
/// units — not a way around it.</para>
///
/// <para><b>It does not relax the trust policy, and that is the point.</b> The policy is read from the
/// same <c>Plugins</c> section the shell reads and handed to the same installer. A host configured
/// Curated refuses an unsigned local build, and this says so per unit rather than quietly succeeding
/// against a policy nobody asked for.</para>
/// </summary>
public sealed class HyperionRegisterRun(ITestOutputHelper output)
{
    /// <summary>What a single unit's registration came to.</summary>
    private sealed record Outcome(string Id, string Verdict, bool Registered, bool Kept);

    [Fact]
    public void Register_what_the_runs_built()
    {
        if (Environment.GetEnvironmentVariable("HYPERION_REGISTER") != "1") return;

        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;

        var policy = HostPolicy(out var described);
        output.WriteLine($"trust policy: {described}");
        output.WriteLine($"units root:   {AuthoredUnitsRoot.Path}");
        output.WriteLine(string.Empty);

        var compiler = new RoslynStrategyCompiler();
        var kernels = new StrategyKernelRegistry();
        var visualizers = new VisualizerRegistry();
        var sink = new AuthoredUnitSink(kernels, visualizers);

        var store = new AuthoredUnitStore(new PluginHostContext(
            AuthoredUnitsRoot.Path,
            policy,
            LoadedPlugins: [],
            State: new PluginStateStore(AuthoredUnitsRoot.Path)));

        var outcomes = new List<Outcome>();

        foreach (var brief in HyperionBriefs.All)
        {
            if (AuthoringSessionStore.Load(brief.Id) is not { Files.Count: > 0 } session)
            {
                outcomes.Add(new Outcome(brief.Id, "no saved session — the run produced nothing", false, false));
                continue;
            }

            var script = new StrategyScript(session.StrategyId, session.DisplayName, session.Files);

            // THE SAME BAR THE PANE SETS, which is a clean compile and one hostable class — not a
            // clean ladder.
            //
            // Being stricter here would have been easy to argue for and wrong: the ladder's later rungs
            // are about quality, and one of them is "two labels overlap by a few pixels". The pane opens
            // its review overlay on a compile and lets the author decide, so refusing to register a unit
            // the app would happily register is this harness inventing a policy. The rungs are reported
            // instead, which is the useful half of being strict.
            var gate = new UnitGate(compiler, script.Id, script.DisplayName).Run(session.Files);

            if (gate.Compile is not { Success: true } compiled || gate.Unit is not { } unit)
            {
                outcomes.Add(new Outcome(
                    brief.Id,
                    "did not compile: " + First(gate.Report.Findings),
                    false,
                    false));
                continue;
            }

            var ladder = gate.Passed
                ? $"{gate.Report.RungsCleared} rung(s), clean"
                : $"{gate.Report.RungsCleared} rung(s), open at {gate.Report.FailedAt}: "
                  + First(gate.Report.Findings);

            var registered = sink.Register(unit, script.Id, script.DisplayName);

            // Kept, so it is still there next start. The artifact is written first — it is what the
            // installer installs, and it is also the thing the user can hand to somebody else.
            var artifact = AuthoredArtifact.Write(script, compiled);
            var kept = false;
            var keptWhy = artifact.Success
                ? "packaged, not installed"
                : $"could not package: {artifact.Message}";

            if (artifact.Success && AuthoredUnitsRoot.Ensure() is { } root)
            {
                var install = store.Install(artifact.Path!, root);
                kept = install.Success;
                keptWhy = install.Message;
            }

            outcomes.Add(new Outcome(
                brief.Id,
                $"{unit.Kind} · {unit.Type.Name} · {ladder} — {registered} {keptWhy}",
                Registered: registered.StartsWith("Registered", StringComparison.Ordinal),
                Kept: kept));
        }

        Report(outcomes, described);
    }

    /// <summary>
    /// The policy the SHELL would use, read from the same configuration section.
    ///
    /// <para>Not a constant and not Permissive-by-default: which one is in force decides whether these
    /// units load at all, and getting it from anywhere but the config would make this report a
    /// description of a machine nobody runs.</para>
    /// </summary>
    private static PluginTrustPolicy HostPolicy(out string described)
    {
        var explicitMode = Environment.GetEnvironmentVariable("HYPERION_TRUST");
        if (!string.IsNullOrWhiteSpace(explicitMode)
            && Enum.TryParse<PluginTrustMode>(explicitMode, ignoreCase: true, out var forced))
        {
            described = $"{forced} (HYPERION_TRUST)";
            return PluginTrustPolicy.From(new PluginsOptions { TrustPolicy = forced });
        }

        // Read by hand rather than through a configuration builder: this needs one string out of one
        // section, and the alternative is a JSON-provider package reference on a test project purely to
        // read two fields.
        var (mode, from) = ConfiguredMode();

        described = $"{mode} (from {from})"
            + (mode == PluginTrustMode.Curated
                ? " — an unsigned local build is refused, which is exactly what these are"
                : string.Empty);

        return PluginTrustPolicy.From(new PluginsOptions { TrustPolicy = mode });
    }

    /// <summary>The <c>Plugins:TrustPolicy</c> the nearest appsettings.json asks for, and where it came
    /// from. Curated when none is found, because that is what the shipped file says and assuming the
    /// laxer answer would describe a machine nobody runs.</summary>
    private static (PluginTrustMode Mode, string From) ConfiguredMode()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "appsettings.json");
            if (!File.Exists(candidate)) continue;

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(candidate));
                if (document.RootElement.TryGetProperty("Plugins", out var plugins)
                    && plugins.TryGetProperty("TrustPolicy", out var value)
                    && Enum.TryParse<PluginTrustMode>(value.GetString(), ignoreCase: true, out var mode))
                {
                    return (mode, candidate);
                }
            }
            catch (Exception)
            {
                // A malformed settings file is not this harness's problem to report; fall through to the
                // shipped default, which is the stricter answer.
            }
        }

        return (PluginTrustMode.Curated, "the shipped default — no appsettings.json found");
    }

    private static string First(IReadOnlyList<VerificationFinding> findings) =>
        findings.Count == 0 ? "no finding recorded" : findings[0].ToString();

    private void Report(IReadOnlyList<Outcome> outcomes, string policy)
    {
        var text = new StringBuilder()
            .AppendLine("# Registration")
            .AppendLine()
            .AppendLine($"- trust policy: {policy}")
            .AppendLine($"- units root: `{AuthoredUnitsRoot.Path}`")
            .AppendLine($"- artifacts: `{AuthoredArtifact.DefaultRoot}`")
            .AppendLine()
            .AppendLine("| unit | registered | kept | detail |")
            .AppendLine("|---|---|---|---|");

        foreach (var outcome in outcomes)
        {
            text.Append("| `").Append(outcome.Id).Append("` | ")
                .Append(outcome.Registered ? "yes" : "no").Append(" | ")
                .Append(outcome.Kept ? "yes" : "no").Append(" | ")
                .Append(outcome.Verdict.Replace("|", "/", StringComparison.Ordinal)).AppendLine(" |");

            output.WriteLine($"{(outcome.Registered ? "OK  " : "FAIL")} {outcome.Id}: {outcome.Verdict}");
        }

        var path = Path.Combine(
            Environment.GetEnvironmentVariable("HYPERION_OUT")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgo Terminal", "hyperion-runs"),
            "registration.md");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        output.WriteLine($"\nwritten to {path}");
    }
}
