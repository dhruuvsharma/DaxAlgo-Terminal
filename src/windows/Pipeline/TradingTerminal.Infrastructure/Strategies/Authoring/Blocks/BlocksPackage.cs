using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DaxAlgo.Blocks;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// A Blocks unit as an installed package: how the installer and the loader recognise one, scan it, and
/// put it in the catalog after a restart.
///
/// <para><b>Recognised by what it was compiled against, never by what it claims.</b> An assembly is a
/// Blocks unit when its metadata references <c>DaxAlgo.Blocks</c>, read as data before any code loads.
/// That is the one fact that entitles it to the Blocks scan, and a manifest field saying so would be a
/// field anyone could write.</para>
///
/// <para><b>The Blocks scan is the sandbox scan minus the network</b> — the same rule the Blocks compiler
/// applies, so a unit that built clean installs clean and loads clean. Processes, files, threads,
/// reflection emit and assembly loading stay refused, whoever signed it.</para>
/// </summary>
public static class BlocksPackage
{
    /// <summary>The assembly a Blocks unit is compiled against.</summary>
    public const string SdkAssemblyName = "DaxAlgo.Blocks";

    /// <summary>Where a package's page files are installed, relative to the plugin folder.</summary>
    public const string PageFolder = "ui";

    /// <summary>Where a package's source files are installed, relative to the plugin folder.</summary>
    public const string SourceFolder = "src";

    /// <summary>The largest page or source file read back from an installed package.</summary>
    public const int MaximumFileBytes = 1_048_576;

    /// <summary>True when <paramref name="assemblyPath"/> references the Blocks SDK. Reads metadata only.</summary>
    public static bool IsBlocksAssembly(string assemblyPath) =>
        WithMetadata(assemblyPath, reader => reader.AssemblyReferences
            .Select(reader.GetAssemblyReference)
            .Any(reference => reader.StringComparer.Equals(reference.Name, SdkAssemblyName)));

    /// <summary>
    /// True when the compiled code references the orders block — the unit is a strategy. Read from the
    /// assembly's type references, the IL equivalent of the compiler's own check, so an installed unit is
    /// the same kind it was when it was built.
    /// </summary>
    public static bool UsesOrders(string assemblyPath) =>
        WithMetadata(assemblyPath, reader => reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Any(type => reader.StringComparer.Equals(type.Namespace, typeof(IOrders).Namespace!)
                         && reader.StringComparer.Equals(type.Name, nameof(IOrders))));

    /// <summary>The Blocks scan over every assembly in <paramref name="folder"/>.</summary>
    public static PluginScanReport Scan(string folder)
    {
        if (!Directory.Exists(folder)) return PluginScanReport.Clean;

        var findings = new List<PluginScanFinding>();
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            var report = BlocksUnitCompiler.Scan(File.ReadAllBytes(dll), Path.GetFileNameWithoutExtension(dll));
            findings.AddRange(report.Findings);
        }

        var verdict = findings.Count == 0 ? PluginScanSeverity.Clean : findings.Max(f => f.Severity);
        return new PluginScanReport(verdict, findings);
    }

    /// <summary>Concrete <see cref="IUnit"/> types the host can construct.</summary>
    public static IReadOnlyList<Type> Units(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(t => t is not null).Select(t => t!)];
        }
        catch (Exception)
        {
            return [];
        }

        return [.. types.Where(t => t is { IsClass: true, IsAbstract: false }
                                    && typeof(IUnit).IsAssignableFrom(t)
                                    && t.GetConstructor(Type.EmptyTypes) is not null)];
    }

    /// <summary>
    /// Registers every Blocks unit the startup loader loaded, with the page and source installed beside
    /// it. A unit that cannot be constructed is skipped and reported; the rest still get their cards.
    /// </summary>
    /// <returns>How many were registered.</returns>
    public static int Register(IEnumerable<LoadedPlugin> loaded, IBlocksUnitRegistry registry, Action<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(registry);

        var count = 0;
        foreach (var plugin in loaded)
        {
            if (plugin.Image is not { } image || !IsBlocksAssembly(plugin.AssemblyPath)) continue;

            var folder = Path.GetDirectoryName(plugin.AssemblyPath)!;
            var units = Units(image);
            if (units.Count != 1)
            {
                skipped?.Invoke($"{plugin.Name}: expected exactly one IUnit, found {units.Count}.");
                continue;
            }

            try
            {
                var type = units[0];
                IUnit Create() => (IUnit)Activator.CreateInstance(type)!;

                var info = Create().Info;
                var id = ManifestId(folder) ?? Path.GetFileName(folder);
                var name = string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name == id
                    ? (string.IsNullOrWhiteSpace(info.Name) ? id : info.Name)
                    : plugin.Name;

                var page = Files(folder, PageFolder, prefix: PageFolder + "/");
                var sources = Files(folder, SourceFolder, prefix: string.Empty);

                registry.Register(new BlocksUnitRegistration(
                    id, name, info.Description ?? string.Empty, UsesOrders(plugin.AssemblyPath), Create, page, [.. sources, .. page]));
                count++;
            }
            catch (Exception ex)
            {
                skipped?.Invoke($"{plugin.Name}: {ex.Message}");
            }
        }

        return count;
    }

    private static string? ManifestId(string folder)
    {
        try
        {
            return PluginManifest.TryRead(folder)?.Id is { Length: > 0 } id ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Text files under <paramref name="sub"/>, named relative to it with <paramref name="prefix"/>.
    /// Bounded: an installed page is read into memory once per registration.</summary>
    private static IReadOnlyList<StrategyFile> Files(string folder, string sub, string prefix)
    {
        var root = Path.Combine(folder, sub);
        if (!Directory.Exists(root)) return [];

        var files = new List<StrategyFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumFileBytes) continue;

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(new StrategyFile(prefix + relative, File.ReadAllText(path)));
        }

        return files;
    }

    private static bool WithMetadata(string assemblyPath, Func<MetadataReader, bool> read)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            return pe.HasMetadata && read(pe.GetMetadataReader());
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
