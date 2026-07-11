using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// The <c>.daxplugin</c> distribution package: a zip of one plugin folder (main assembly +
/// plugin.json + pdb/deps.json + any private dependencies like HelixToolkit) plus a
/// <c>package.json</c> integrity index — per-file sha256 and the main-assembly name. Install
/// verifies EVERY file against the index before any gate runs, so a tampered or truncated package
/// is refused outright; a raw-DLL install can't carry private deps, a package can.
/// </summary>
public static class DaxPluginPackage
{
    public const string Extension = ".daxplugin";
    public const string IndexEntryName = "package.json";

    internal sealed record IndexDto(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("mainAssembly")] string MainAssembly,
        [property: JsonPropertyName("files")] Dictionary<string, string> Files);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Packs every file under <paramref name="pluginDirectory"/> (recursively; forward-slash
    /// relative paths as entry names) into <paramref name="outputPath"/>, with a sha256 per entry in
    /// the <see cref="IndexEntryName"/> index. <paramref name="mainAssemblyFileName"/> names the
    /// assembly containing the IStrategyPlugin (e.g. <c>MyStrategy.dll</c>).</summary>
    public static void Write(string pluginDirectory, string mainAssemblyFileName, string outputPath)
    {
        if (!File.Exists(Path.Combine(pluginDirectory, mainAssemblyFileName)))
            throw new FileNotFoundException($"Main assembly '{mainAssemblyFileName}' not found in {pluginDirectory}.");

        var outputFull = Path.GetFullPath(outputPath);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        File.Delete(outputFull);
        using var zip = ZipFile.Open(outputFull, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories))
        {
            // Never pack the package itself (or a stale index) if it sits inside the source folder.
            if (string.Equals(Path.GetFullPath(file), outputFull, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(pluginDirectory, file).Replace('\\', '/');
            if (string.Equals(rel, IndexEntryName, StringComparison.OrdinalIgnoreCase)) continue;

            using (var stream = File.OpenRead(file))
                files[rel] = Convert.ToHexString(SHA256.HashData(stream));
            zip.CreateEntryFromFile(file, rel);
        }

        var index = new IndexDto(FormatVersion: 1, mainAssemblyFileName, files);
        using var indexStream = zip.CreateEntry(IndexEntryName).Open();
        JsonSerializer.Serialize(indexStream, index, JsonOptions);
    }

    /// <summary>Extracts <paramref name="packagePath"/> into a fresh temp folder, verifying the
    /// index first: every entry must be listed with a matching sha256 (tamper check), every listed
    /// file must be present (truncation check), and no entry may escape the extraction folder
    /// (zip-slip guard) — all BEFORE any trust/manifest gate sees the content. Returns the folder
    /// and the main assembly's simple name (== the plugin folder name convention). Throws
    /// <see cref="InvalidDataException"/> with the reason on any violation; the caller owns
    /// deleting the returned folder.</summary>
    public static (string ExtractedDir, string MainAssemblyName) ExtractAndVerify(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);

        var indexEntry = zip.GetEntry(IndexEntryName)
            ?? throw new InvalidDataException($"Not a {Extension} package — the {IndexEntryName} integrity index is missing.");
        IndexDto index;
        using (var indexStream = indexEntry.Open())
            index = JsonSerializer.Deserialize<IndexDto>(indexStream, JsonOptions)
                ?? throw new InvalidDataException($"Empty {IndexEntryName} integrity index.");

        if (string.IsNullOrWhiteSpace(index.MainAssembly)
            || !index.MainAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || index.MainAssembly.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException($"Invalid mainAssembly in the package index: '{index.MainAssembly}'.");

        var tempDir = Path.Combine(Path.GetTempPath(), "daxalgo-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // directory marker
                if (string.Equals(entry.FullName, IndexEntryName, StringComparison.OrdinalIgnoreCase)) continue;

                var destination = Path.GetFullPath(Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(tempDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Package entry escapes the extraction folder: '{entry.FullName}'.");

                if (!index.Files.TryGetValue(entry.FullName, out var expectedHash))
                    throw new InvalidDataException($"Package contains a file not covered by its integrity index: '{entry.FullName}'.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);

                using var stream = File.OpenRead(destination);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Package integrity check failed (tampered content): '{entry.FullName}'.");
                seen.Add(entry.FullName);
            }

            var missing = index.Files.Keys.FirstOrDefault(f => !seen.Contains(f));
            if (missing is not null)
                throw new InvalidDataException($"Package is missing a file its integrity index lists: '{missing}'.");
            if (!seen.Contains(index.MainAssembly))
                throw new InvalidDataException($"Package does not contain its declared main assembly '{index.MainAssembly}'.");

            return (tempDir, Path.GetFileNameWithoutExtension(index.MainAssembly));
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }
}
