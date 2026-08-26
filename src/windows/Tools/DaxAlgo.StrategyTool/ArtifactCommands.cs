using System.Globalization;
using System.Text.Json;
using DaxAlgo.Package;

namespace DaxAlgo.StrategyTool;

/// <summary>
/// <c>daxalgo artifact pack|unpack|inspect|verify</c> — the operator's view of a
/// <c>.daxalgostrategy</c> / <c>.daxalgovisualizer</c> (#35).
///
/// <para>These exist because the format is opaque by design and that cuts both ways. Digest-pinned,
/// path-checked and rejected whole on the smallest mismatch is right for a file arriving from a
/// stranger; it is unhelpful when an install fails and the only thing anybody can say is "rejected".
/// <c>inspect</c> is the command you reach for then — what it claims to be, who claims to have made it,
/// which type the host would resolve, and what is actually inside.</para>
///
/// <para><c>unpack</c> matters for a different reason: a package carries its source, and source nobody
/// can get at is source nobody will read. Reviewing a strategy before running it is the entire premise
/// of the review gate, and it should not require the terminal to be open.</para>
///
/// <para><b>Nothing here loads or executes a payload.</b> Every command is a read of the archive and its
/// manifest. A tool for inspecting untrusted files must not be the thing that runs them.</para>
/// </summary>
public static class ArtifactCommands
{
    /// <summary>Prints what a package says about itself and what it holds.</summary>
    public static int Inspect(string path)
    {
        if (!File.Exists(path)) return Fail($"file not found: {path}");

        try
        {
            var contents = DaxPackage.Read(path);
            var manifest = contents.Manifest;

            Console.WriteLine($"{Path.GetFileName(path)}  ({Size(new FileInfo(path).Length)})");
            Console.WriteLine();
            Console.WriteLine($"  kind         {manifest.Kind}");
            Console.WriteLine($"  id           {manifest.Id}");
            Console.WriteLine($"  version      {manifest.Version}");
            Console.WriteLine($"  name         {manifest.DisplayName}");
            Console.WriteLine($"  publisher    {manifest.Publisher ?? "(none declared)"}");
            Console.WriteLine($"  entry type   {manifest.EntryTypeName}");
            Console.WriteLine($"  format       {manifest.Format} v{manifest.FormatVersion}");
            if (!string.IsNullOrWhiteSpace(manifest.Description))
                Console.WriteLine($"  description  {manifest.Description}");

            Console.WriteLine();
            Console.WriteLine($"  {manifest.Payloads.Count} payload(s):");
            foreach (var payload in manifest.Payloads.OrderBy(p => p.Path, StringComparer.Ordinal))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    {payload.Role,-11} {Size(payload.Length),9}  {payload.Sha256[..16]}…  {payload.Path}"));
            }

            // An assembly is what makes a package installable; saying so here saves the user discovering
            // it from a refusal at install time.
            if (manifest.Payloads.All(p => p.Role != DaxPayloadRole.Assembly))
            {
                Console.WriteLine();
                Console.WriteLine("  note: no compiled assembly — this is a source-only package and cannot be installed.");
            }

            return 0;
        }
        catch (DaxPackageException ex)
        {
            return Fail($"rejected: {ex.Message}");
        }
    }

    /// <summary>Reads and validates a package. Exit code only, for a CI gate.</summary>
    public static int Verify(string path)
    {
        if (!File.Exists(path)) return Fail($"file not found: {path}");

        try
        {
            var contents = DaxPackage.Read(path);
            Console.WriteLine(
                $"ok: {contents.Manifest.Id} {contents.Manifest.Version} — "
                + $"{contents.Payloads.Count} payload(s), every digest matched.");
            return 0;
        }
        catch (DaxPackageException ex)
        {
            // The reason, not just the verdict. "Rejected" alone is what made this command necessary.
            return Fail($"{ex.Error}: {ex.Message}");
        }
    }

    /// <summary>Extracts the payloads so they can be read.</summary>
    public static int Unpack(string path, string into)
    {
        if (!File.Exists(path)) return Fail($"file not found: {path}");

        try
        {
            var contents = DaxPackage.Read(path);
            var root = Path.GetFullPath(into);
            Directory.CreateDirectory(root);

            foreach (var (payloadPath, bytes) in contents.Payloads)
            {
                var relative = payloadPath.StartsWith("payload/", StringComparison.Ordinal)
                    ? payloadPath["payload/".Length..]
                    : payloadPath;

                var destination = Path.GetFullPath(
                    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

                // The reader normalised these already. Checking again at the moment bytes reach the disk
                // is the second lock on the same door, and this door opens outside the application.
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Fail($"payload '{payloadPath}' escapes the output folder.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }

            // The manifest is not a payload, so it is written separately — without it the folder loses
            // the entry type and the package could not be repacked from what came out.
            File.WriteAllText(
                Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(contents.Manifest, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"Unpacked {contents.Payloads.Count} payload(s) to {root}.");
            return 0;
        }
        catch (DaxPackageException ex)
        {
            return Fail($"rejected: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a package from a folder laid out the way an installed plugin is:
    /// <c>&lt;name&gt;.dll</c>, an optional <c>plugin.json</c>, and anything else beside them.
    /// </summary>
    /// <remarks>
    /// <para>The entry type must be given rather than discovered. The format's own rule is that the host
    /// resolves it exactly and never scans for a substitute, and a packer that guessed would be deciding
    /// on the host's behalf — with a reflection load of untrusted code to do it.</para>
    /// </remarks>
    public static int Pack(string from, IReadOnlyDictionary<string, string> options)
    {
        if (!Directory.Exists(from)) return Fail($"folder not found: {from}");

        var entry = options.GetValueOrDefault("entry");
        if (string.IsNullOrWhiteSpace(entry))
            return Fail("--entry is required: the full type name the host should resolve.");

        var assembly = Directory.EnumerateFiles(from, "*.dll").FirstOrDefault();
        if (assembly is null) return Fail($"no assembly in '{from}'. A package without one cannot be installed.");

        var manifest = ReadPluginManifest(from);
        var id = options.GetValueOrDefault("id") ?? manifest?.Id ?? Path.GetFileNameWithoutExtension(assembly);
        var name = options.GetValueOrDefault("name") ?? manifest?.Name ?? id;
        var version = options.GetValueOrDefault("version") ?? manifest?.Version ?? "0.1.0";

        var kind = options.GetValueOrDefault("kind", "strategy").ToLowerInvariant() switch
        {
            "visualizer" => DaxPackageKind.Visualizer,
            "strategy" => DaxPackageKind.Strategy,
            var other => (DaxPackageKind?)Report(other),
        };
        if (kind is null) return 1;

        var output = options.GetValueOrDefault("out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), id + DaxPackage.ExtensionFor(kind.Value));

        try
        {
            var payloads = new List<DaxPayloadSource>();
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file).Replace('\\', '/');
                payloads.Add(DaxPayloadSource.FromFile($"payload/{relative}", RoleOf(relative), file));
            }

            var result = DaxPackage.Write(output, new DaxPackageRequest
            {
                Kind = kind.Value,
                Id = id,
                Version = version,
                DisplayName = name,
                Publisher = options.GetValueOrDefault("publisher"),
                Description = options.GetValueOrDefault("description"),
                EntryTypeName = entry!,
                Payloads = payloads,
            });

            Console.WriteLine($"Packed {payloads.Count} payload(s) -> {result.Path} ({Size(result.Length)}).");
            return 0;
        }
        catch (DaxPackageException ex)
        {
            return Fail($"rejected: {ex.Message}");
        }
    }

    /// <summary>The role a file plays, from its extension and where it sits. Declared rather than
    /// guessed at install time, which is why the manifest carries it at all.</summary>
    private static DaxPayloadRole RoleOf(string relative) =>
        Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".dll" => DaxPayloadRole.Assembly,
            ".cs" => DaxPayloadRole.Source,
            ".xaml" => DaxPayloadRole.Ui,
            _ => DaxPayloadRole.Resource,
        };

    /// <summary>The plugin manifest in a folder, when there is one — so id, name and version need not be
    /// retyped for a plugin that already declares them.</summary>
    private static PluginManifestShape? ReadPluginManifest(string folder)
    {
        var path = Path.Combine(folder, "plugin.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<PluginManifestShape>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Just enough of the plugin manifest to fill in the defaults. Declared here rather than
    /// referenced so this CLI keeps no dependency on the host's plugin assembly.</summary>
    private sealed record PluginManifestShape(string? Id, string? Name, string? Version);

    private static int Report(string kind) =>
        Fail($"unknown --kind '{kind}'. Use 'strategy' or 'visualizer'.");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.#} MB"),
    };
}
