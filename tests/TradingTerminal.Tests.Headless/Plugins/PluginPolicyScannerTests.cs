using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the static IL policy scan (<see cref="PluginPolicyScanner"/>): the capabilities a strategy
/// has no business having are found by reading the assembly as DATA — no plugin code runs — and are
/// classified Block; file/network I/O is Warn, and a plugin can declare it in its manifest to have it
/// disclosed rather than flagged. Fixtures are compiled on the fly, so each one is a real assembly
/// with real metadata rather than a hand-rolled fake.
/// </summary>
public sealed class PluginPolicyScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "scan-" + Guid.NewGuid().ToString("N"));

    public PluginPolicyScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Block-level capabilities ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PInvoke_is_blocked()
    {
        Compile("Native", """
            using System.Runtime.InteropServices;
            public static class Native
            {
                [DllImport("kernel32.dll")]
                public static extern int GetCurrentProcessId();
            }
            """);

        var report = PluginPolicyScanner.Scan(_root);

        report.Verdict.Should().Be(PluginScanSeverity.Block);
        report.Findings.Should().Contain(f => f.Rule == "pInvoke" && f.Severity == PluginScanSeverity.Block);
        report.Summary.Should().Contain("kernel32");
    }

    [Fact]
    public void Starting_a_process_is_blocked()
    {
        Compile("Spawner", """
            using System.Diagnostics;
            public static class Spawner
            {
                public static void Run() => Process.Start("cmd.exe");
            }
            """);

        var report = PluginPolicyScanner.Scan(_root);

        report.Verdict.Should().Be(PluginScanSeverity.Block);
        report.Findings.Should().Contain(f => f.Rule == "process");
    }

    [Fact]
    public void Loading_another_assembly_is_blocked()
    {
        Compile("Loader", """
            using System.Reflection;
            public static class Loader
            {
                public static object Load(string path) => Assembly.LoadFrom(path);
            }
            """);

        var report = PluginPolicyScanner.Scan(_root);

        report.Verdict.Should().Be(PluginScanSeverity.Block);
        report.Findings.Should().Contain(f => f.Rule == "assemblyLoad");
    }

    [Fact]
    public void A_Block_capability_can_NOT_be_self_granted_by_the_manifest()
    {
        // The whole point: an unreviewed plugin declaring "I P/Invoke, that's fine" must not get to
        // wave itself through. Only human review (curation) can allow that.
        Compile("Native", """
            using System.Runtime.InteropServices;
            public static class Native
            {
                [DllImport("kernel32.dll")]
                public static extern int GetCurrentProcessId();
            }
            """);

        var report = PluginPolicyScanner.Scan(_root, declaredPermissions: ["pInvoke", "process", "registry"]);

        report.Verdict.Should().Be(PluginScanSeverity.Block);
    }

    // ── Warn-level capabilities, and declaring them ───────────────────────────────────────────────

    [Fact]
    public void File_io_warns_and_a_declared_permission_downgrades_it_to_disclosed()
    {
        Compile("Exporter", """
            using System.IO;
            public static class Exporter
            {
                public static void Save(string path) => File.WriteAllText(path, "signals");
            }
            """);

        var undeclared = PluginPolicyScanner.Scan(_root);
        undeclared.Verdict.Should().Be(PluginScanSeverity.Warn);
        undeclared.Findings.Should().Contain(f => f.Rule == "fileIo" && f.Severity == PluginScanSeverity.Warn);

        var declared = PluginPolicyScanner.Scan(_root, declaredPermissions: ["fileIo"]);
        declared.Verdict.Should().Be(PluginScanSeverity.Clean, "a declared capability is disclosed, not flagged");
        declared.Findings.Should().Contain(f => f.Rule == "fileIo" && f.Severity == PluginScanSeverity.Clean,
            "it is still reported, so the Plugin Manager can show what the plugin uses");
    }

    [Fact]
    public void Http_warns()
    {
        Compile("Fetcher", """
            using System.Net.Http;
            public static class Fetcher
            {
                public static HttpClient Client() => new HttpClient();
            }
            """);

        PluginPolicyScanner.Scan(_root).Verdict.Should().Be(PluginScanSeverity.Warn);
    }

    // ── Clean code stays clean (no false positives on the shapes real strategies use) ─────────────

    [Fact]
    public void A_plain_strategy_scans_clean()
    {
        // Deliberately uses the things a strategy DOES do — reflection over its own types, LINQ, math,
        // Environment.NewLine, Path — none of which may trip the scanner.
        Compile("Strategy", """
            using System;
            using System.IO;
            using System.Linq;
            public sealed class Signal
            {
                public double Score(double[] xs) => xs.Where(x => x > 0).Sum() / Math.Max(1, xs.Length);
                public string Describe() => $"{GetType().Assembly.GetName().Name}{Environment.NewLine}";
                public string Where(string dir) => Path.Combine(dir, "notes.txt");
            }
            """);

        var report = PluginPolicyScanner.Scan(_root);

        report.Verdict.Should().Be(PluginScanSeverity.Clean);
        report.Findings.Should().BeEmpty();
        report.Summary.Should().Be("no flagged capabilities");
    }

    [Fact]
    public void An_empty_or_missing_folder_is_clean_not_an_error()
    {
        PluginPolicyScanner.Scan(_root).Verdict.Should().Be(PluginScanSeverity.Clean);
        PluginPolicyScanner.Scan(Path.Combine(_root, "nope")).Should().BeSameAs(PluginScanReport.Clean);
    }

    [Fact]
    public void A_corrupt_assembly_is_reported_rather_than_silently_passing()
    {
        File.WriteAllText(Path.Combine(_root, "Garbage.dll"), "not a PE file");

        var report = PluginPolicyScanner.Scan(_root);

        report.Verdict.Should().Be(PluginScanSeverity.Warn);
        report.Findings.Should().ContainSingle(f => f.Rule == "unreadable");
    }

    /// <summary>The plugin's private dependencies ship in its folder, so they are scanned too — a
    /// payload hidden in a bundled DLL still trips the scan.</summary>
    [Fact]
    public void Private_dependencies_in_the_folder_are_scanned_too()
    {
        Compile("CleanMain", """
            public sealed class Clean { public int Value => 42; }
            """);
        Compile("SneakyDep", """
            using System.Diagnostics;
            public static class Sneaky { public static void Go() => Process.Start("cmd.exe"); }
            """);

        var report = PluginPolicyScanner.Scan(_root);

        report.Verdict.Should().Be(PluginScanSeverity.Block);
        report.Findings.Should().Contain(f => f.Assembly == "SneakyDep.dll" && f.Rule == "process");
    }

    /// <summary>Emits <c>{_root}/{name}.dll</c> from source — a genuine assembly with genuine metadata.</summary>
    private void Compile(string name, string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: name,
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var result = compilation.Emit(Path.Combine(_root, name + ".dll"));
        result.Success.Should().BeTrue(
            string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
}
