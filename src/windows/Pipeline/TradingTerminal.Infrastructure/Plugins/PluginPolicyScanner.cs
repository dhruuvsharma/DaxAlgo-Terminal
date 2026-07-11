using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>How serious a scan finding is.</summary>
public enum PluginScanSeverity
{
    /// <summary>Nothing of interest, or the plugin declared this capability in its manifest.</summary>
    Clean,

    /// <summary>Capability worth showing the user (file or network I/O) but not refusing over — plenty
    /// of legitimate strategies write CSVs or call a data API.</summary>
    Warn,

    /// <summary>Capability a strategy has no business having (start processes, touch the registry,
    /// P/Invoke, emit or load assemblies). The plugin does not load.</summary>
    Block,
}

/// <summary>One capability found in a plugin's IL. <paramref name="Rule"/> is the stable id
/// (<c>process</c>, <c>fileIo</c>, …) that a manifest <c>permissions</c> entry can declare.</summary>
public sealed record PluginScanFinding(
    string Assembly,
    string Rule,
    PluginScanSeverity Severity,
    string Detail);

/// <summary>The verdict for a plugin folder: the worst severity found, plus every finding.</summary>
public sealed record PluginScanReport(PluginScanSeverity Verdict, IReadOnlyList<PluginScanFinding> Findings)
{
    public static PluginScanReport Clean { get; } = new(PluginScanSeverity.Clean, []);

    /// <summary>One-line reason for the Plugin Manager / quarantine record.</summary>
    public string Summary =>
        Findings.Count == 0
            ? "no flagged capabilities"
            : string.Join("; ", Findings
                .Where(f => f.Severity == Verdict)
                .Select(f => f.Detail)
                .Distinct(StringComparer.Ordinal)
                .Take(4));
}

/// <summary>
/// Static IL policy scan of a plugin folder, using the in-box <see cref="System.Reflection.Metadata"/>
/// reader — no new dependency, and crucially <b>no code from the plugin runs</b>: the assembly is read
/// as data, so the verdict is known before it is ever loaded into a context.
/// <para>
/// It looks for capabilities a trading strategy has no business having — P/Invoke, starting processes,
/// the registry, <c>Reflection.Emit</c>, loading assemblies (<see cref="PluginScanSeverity.Block"/>) —
/// and for capabilities worth disclosing but not refusing: file I/O, network I/O, environment writes
/// (<see cref="PluginScanSeverity.Warn"/>). A plugin can declare Warn-level capabilities in its
/// <c>plugin.json</c> <c>permissions</c> array, which downgrades them to
/// <see cref="PluginScanSeverity.Clean"/>. Block-level capabilities can never be self-granted — that
/// takes human review (curation).
/// </para>
/// <para>
/// <b>What this is not.</b> A determined attacker hides behind reflection over strings, or calls into a
/// bundled dependency this scanner Warns about rather than Blocks. It is a tripwire against lazy or
/// accidental abuse and a disclosure surface for the user — <b>curation and code signing remain the
/// control</b>. Do not oversell it in the UI or the docs.
/// </para>
/// </summary>
public static class PluginPolicyScanner
{
    /// <summary>Types whose mere presence in the reference table is the signal. Keyed by full name;
    /// a trailing <c>.*</c> matches the whole namespace.</summary>
    private static readonly (string Type, string Rule, PluginScanSeverity Severity, string What)[] TypeRules =
    [
        ("System.Diagnostics.Process",                  "process",        PluginScanSeverity.Block, "starts or inspects OS processes"),
        ("System.Diagnostics.ProcessStartInfo",         "process",        PluginScanSeverity.Block, "starts OS processes"),
        ("Microsoft.Win32.Registry",                    "registry",       PluginScanSeverity.Block, "reads or writes the Windows registry"),
        ("Microsoft.Win32.RegistryKey",                 "registry",       PluginScanSeverity.Block, "reads or writes the Windows registry"),
        ("System.Reflection.Emit.*",                    "reflectionEmit", PluginScanSeverity.Block, "generates code at runtime (Reflection.Emit)"),
        ("System.Runtime.Loader.AssemblyLoadContext",   "assemblyLoad",   PluginScanSeverity.Block, "loads further assemblies at runtime"),

        ("System.Net.Sockets.*",                        "network",        PluginScanSeverity.Warn,  "opens raw network sockets"),
        ("System.Net.Http.HttpClient",                  "network",        PluginScanSeverity.Warn,  "makes HTTP requests"),
        ("System.Net.WebClient",                        "network",        PluginScanSeverity.Warn,  "makes HTTP requests"),
        ("System.Net.WebRequest",                       "network",        PluginScanSeverity.Warn,  "makes HTTP requests"),
        ("System.IO.File",                              "fileIo",         PluginScanSeverity.Warn,  "reads or writes files"),
        ("System.IO.FileInfo",                          "fileIo",         PluginScanSeverity.Warn,  "reads or writes files"),
        ("System.IO.FileStream",                        "fileIo",         PluginScanSeverity.Warn,  "reads or writes files"),
        ("System.IO.Directory",                         "fileIo",         PluginScanSeverity.Warn,  "enumerates or creates directories"),
        ("System.IO.DirectoryInfo",                     "fileIo",         PluginScanSeverity.Warn,  "enumerates or creates directories"),
    ];

    /// <summary>Members whose OWNING type is far too common to flag on its own — <c>Assembly</c> is
    /// referenced by any <c>typeof(x).Assembly</c>, <c>Environment</c> by any
    /// <c>Environment.NewLine</c>. Only these specific calls matter.</summary>
    private static readonly (string Type, string Member, string Rule, PluginScanSeverity Severity, string What)[] MemberRules =
    [
        ("System.Reflection.Assembly", "Load",                    "assemblyLoad", PluginScanSeverity.Block, "loads further assemblies at runtime"),
        ("System.Reflection.Assembly", "LoadFrom",                "assemblyLoad", PluginScanSeverity.Block, "loads further assemblies at runtime"),
        ("System.Reflection.Assembly", "LoadFile",                "assemblyLoad", PluginScanSeverity.Block, "loads further assemblies at runtime"),
        ("System.Reflection.Assembly", "UnsafeLoadFrom",          "assemblyLoad", PluginScanSeverity.Block, "loads further assemblies at runtime"),
        ("System.Environment",         "SetEnvironmentVariable",  "environment",  PluginScanSeverity.Warn,  "writes environment variables"),
    ];

    /// <summary>Scans every managed assembly in <paramref name="pluginDirectory"/> (the plugin's own
    /// DLL and the private dependencies it ships). <paramref name="declaredPermissions"/> comes from
    /// the manifest: a declared Warn-level rule is downgraded to <see cref="PluginScanSeverity.Clean"/>
    /// (disclosed, not nagged about). Unreadable or native files are skipped, never fatal — a scan
    /// failure must not take the host down.</summary>
    public static PluginScanReport Scan(string pluginDirectory, IEnumerable<string>? declaredPermissions = null)
    {
        if (!Directory.Exists(pluginDirectory)) return PluginScanReport.Clean;

        var declared = new HashSet<string>(declaredPermissions ?? [], StringComparer.OrdinalIgnoreCase);
        var findings = new List<PluginScanFinding>();

        foreach (var dll in Directory.EnumerateFiles(pluginDirectory, "*.dll"))
            ScanAssembly(dll, declared, findings);

        var verdict = findings.Count == 0
            ? PluginScanSeverity.Clean
            : findings.Max(f => f.Severity);
        return new PluginScanReport(verdict, findings);
    }

    /// <summary>Scans a single assembly straight from its bytes — for the runtime authoring pane, whose
    /// user/AI-written source is compiled to an in-memory image that never touches disk. Same rules as
    /// the folder scan, so an authored strategy that P/Invokes or starts a process is caught before it
    /// is <c>Assembly.Load</c>ed, exactly like a dropped-in plugin.</summary>
    public static PluginScanReport ScanImage(byte[] assemblyImage, string name, IEnumerable<string>? declaredPermissions = null)
    {
        var declared = new HashSet<string>(declaredPermissions ?? [], StringComparer.OrdinalIgnoreCase);
        var findings = new List<PluginScanFinding>();
        try
        {
            using var pe = new PEReader(new MemoryStream(assemblyImage, writable: false));
            ScanPeReader(pe, name, declared, findings);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            findings.Add(new PluginScanFinding(name, "unreadable", PluginScanSeverity.Warn,
                $"{name} could not be scanned ({ex.GetType().Name})"));
        }

        var verdict = findings.Count == 0 ? PluginScanSeverity.Clean : findings.Max(f => f.Severity);
        return new PluginScanReport(verdict, findings);
    }

    private static void ScanAssembly(string path, HashSet<string> declared, List<PluginScanFinding> findings)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            ScanPeReader(pe, name, declared, findings);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // Unreadable → report it rather than silently passing the plugin. The loader will fail on it
            // anyway if it's the main assembly.
            findings.Add(new PluginScanFinding(name, "unreadable", PluginScanSeverity.Warn,
                $"{name} could not be scanned ({ex.GetType().Name})"));
        }
    }

    private static void ScanPeReader(PEReader pe, string name, HashSet<string> declared, List<PluginScanFinding> findings)
    {
        if (!pe.HasMetadata) return; // native DLL — nothing to read (and nothing we could vet anyway)

        var md = pe.GetMetadataReader();
        ScanPInvokes(md, name, declared, findings);
        ScanTypeReferences(md, name, declared, findings);
        ScanMemberReferences(md, name, declared, findings);
    }

    /// <summary>P/Invoke is a metadata flag, not a type reference: any method with
    /// <see cref="MethodAttributes.PinvokeImpl"/> calls straight into native code, past everything the
    /// managed policy can see.</summary>
    private static void ScanPInvokes(MetadataReader md, string assembly, HashSet<string> declared, List<PluginScanFinding> findings)
    {
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;

            var import = method.GetImport();
            var module = import.Module.IsNil
                ? "?"
                : md.GetString(md.GetModuleReference(import.Module).Name);
            Add(findings, declared, assembly, "pInvoke", PluginScanSeverity.Block,
                $"{assembly} P/Invokes native code ({md.GetString(method.Name)} → {module})");
        }
    }

    private static void ScanTypeReferences(MetadataReader md, string assembly, HashSet<string> declared, List<PluginScanFinding> findings)
    {
        foreach (var handle in md.TypeReferences)
        {
            var typeRef = md.GetTypeReference(handle);
            var full = FullName(md, typeRef);
            if (full.Length == 0) continue;

            foreach (var (type, rule, severity, what) in TypeRules)
            {
                var match = type.EndsWith(".*", StringComparison.Ordinal)
                    ? full.StartsWith(type[..^1], StringComparison.Ordinal)
                    : string.Equals(full, type, StringComparison.Ordinal);
                if (match)
                    Add(findings, declared, assembly, rule, severity, $"{assembly} {what} ({full})");
            }
        }
    }

    private static void ScanMemberReferences(MetadataReader md, string assembly, HashSet<string> declared, List<PluginScanFinding> findings)
    {
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;

            var parent = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            var type = FullName(md, parent);
            var name = md.GetString(member.Name);

            foreach (var (ruleType, ruleMember, rule, severity, what) in MemberRules)
            {
                if (string.Equals(type, ruleType, StringComparison.Ordinal) &&
                    string.Equals(name, ruleMember, StringComparison.Ordinal))
                    Add(findings, declared, assembly, rule, severity, $"{assembly} {what} ({type}.{name})");
            }
        }
    }

    /// <summary>Adds a finding, deduplicated, and downgrades a WARN-level rule the plugin declared in
    /// its manifest. A Block-level rule is never downgradable — a plugin cannot grant itself the right
    /// to P/Invoke or start processes; only human review (curation) can.</summary>
    private static void Add(
        List<PluginScanFinding> findings,
        HashSet<string> declared,
        string assembly,
        string rule,
        PluginScanSeverity severity,
        string detail)
    {
        if (severity == PluginScanSeverity.Warn && declared.Contains(rule))
            severity = PluginScanSeverity.Clean;

        if (findings.Any(f => f.Assembly == assembly && f.Rule == rule && f.Severity == severity))
            return;
        findings.Add(new PluginScanFinding(assembly, rule, severity, detail));
    }

    private static string FullName(MetadataReader md, TypeReference typeRef)
    {
        var ns = typeRef.Namespace.IsNil ? string.Empty : md.GetString(typeRef.Namespace);
        var name = md.GetString(typeRef.Name);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }
}
