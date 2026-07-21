using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// The legacy v1 <c>.daxplugin</c> distribution package: a zip of one plugin folder (main
/// assembly, manifest, symbols and private dependencies) plus a <c>package.json</c> integrity
/// index. Every payload file is hashed and must have an exact, one-to-one index entry.
/// </summary>
public static class DaxPluginPackage
{
    public const string Extension = ".daxplugin";
    public const string IndexEntryName = "package.json";

    internal const int CurrentFormatVersion = 1;
    internal const long DefaultMaxPackageBytes = 64L * 1024 * 1024;
    internal const int DefaultMaxEntryCount = 256;
    internal const long DefaultMaxIndexBytes = 1L * 1024 * 1024;
    internal const long DefaultMaxEntryUncompressedBytes = 64L * 1024 * 1024;
    internal const long DefaultMaxTotalUncompressedBytes = 256L * 1024 * 1024;
    internal const int DefaultMaxCompressionRatio = 100;
    internal const long DefaultCompressionRatioSlackBytes = 1L * 1024 * 1024;
    internal const int DefaultMaxPathLength = 240;
    internal const int DefaultMaxPathDepth = 16;

    internal sealed record ExtractionLimits(
        long MaxPackageBytes,
        int MaxEntryCount,
        long MaxIndexBytes,
        long MaxEntryUncompressedBytes,
        long MaxTotalUncompressedBytes,
        int MaxCompressionRatio,
        long CompressionRatioSlackBytes,
        int MaxPathLength,
        int MaxPathDepth);

    internal static ExtractionLimits DefaultExtractionLimits { get; } = new(
        MaxPackageBytes: DefaultMaxPackageBytes,
        MaxEntryCount: DefaultMaxEntryCount,
        MaxIndexBytes: DefaultMaxIndexBytes,
        MaxEntryUncompressedBytes: DefaultMaxEntryUncompressedBytes,
        MaxTotalUncompressedBytes: DefaultMaxTotalUncompressedBytes,
        MaxCompressionRatio: DefaultMaxCompressionRatio,
        CompressionRatioSlackBytes: DefaultCompressionRatioSlackBytes,
        MaxPathLength: DefaultMaxPathLength,
        MaxPathDepth: DefaultMaxPathDepth);

    internal sealed record IndexDto(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("mainAssembly")] string MainAssembly,
        [property: JsonPropertyName("files")] Dictionary<string, string> Files);

    private sealed record ParsedIndex(
        string MainAssembly,
        Dictionary<string, byte[]> Files,
        Dictionary<string, string> CanonicalFiles);

    private sealed record ArchiveLayout(
        ZipArchiveEntry IndexEntry,
        Dictionary<string, ZipArchiveEntry> Files,
        Dictionary<string, string> CanonicalFiles);

    private readonly record struct ValidatedPath(string Original, string Canonical, bool IsDirectory);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly char[] WindowsInvalidFileNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>Packs every file under <paramref name="pluginDirectory"/> (recursively; forward-slash
    /// relative paths as entry names) into <paramref name="outputPath"/>, with a sha256 per entry in
    /// the <see cref="IndexEntryName"/> index. <paramref name="mainAssemblyFileName"/> names the
    /// assembly containing the IStrategyPlugin (for example, <c>MyStrategy.dll</c>).</summary>
    public static void Write(string pluginDirectory, string mainAssemblyFileName, string outputPath)
    {
        var pluginDirectoryFull = Path.GetFullPath(pluginDirectory);
        var mainAssemblyFull = Path.GetFullPath(Path.Combine(pluginDirectoryFull, mainAssemblyFileName));
        if (!File.Exists(mainAssemblyFull))
            throw new FileNotFoundException($"Main assembly '{mainAssemblyFileName}' not found in {pluginDirectory}.");

        var outputFull = Path.GetFullPath(outputPath);
        var sourceFiles = Directory.EnumerateFiles(pluginDirectoryFull, "*", SearchOption.AllDirectories)
            .Select(file => (
                FullPath: Path.GetFullPath(file),
                RelativePath: Path.GetRelativePath(pluginDirectoryFull, file).Replace('\\', '/')))
            .ToArray();

        // Preflight before deleting or replacing output. An output path that resolves to an input
        // would otherwise destroy that input (most critically, the main assembly) before packing.
        if (PathsAlias(outputFull, mainAssemblyFull))
            throw new InvalidOperationException(
                $"Package output '{outputPath}' aliases the main assembly '{mainAssemblyFileName}'.");
        foreach (var sourceFile in sourceFiles)
        {
            if (PathsAlias(outputFull, sourceFile.FullPath))
                throw new InvalidOperationException(
                    $"Package output '{outputPath}' aliases source file '{sourceFile.RelativePath}'.");
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        File.Delete(outputFull);
        using var zip = ZipFile.Open(outputFull, ZipArchiveMode.Create);
        foreach (var sourceFile in sourceFiles)
        {
            if (string.Equals(sourceFile.RelativePath, IndexEntryName, StringComparison.OrdinalIgnoreCase)) continue;

            using (var stream = File.OpenRead(sourceFile.FullPath))
                files[sourceFile.RelativePath] = Convert.ToHexString(SHA256.HashData(stream));
            zip.CreateEntryFromFile(sourceFile.FullPath, sourceFile.RelativePath);
        }

        var index = new IndexDto(CurrentFormatVersion, mainAssemblyFileName, files);
        using var indexStream = zip.CreateEntry(IndexEntryName).Open();
        JsonSerializer.Serialize(indexStream, index, JsonOptions);
    }

    /// <summary>Extracts <paramref name="packagePath"/> into a fresh temp folder after validating
    /// the v1 index, archive bounds, path safety, exact file set, and every sha256. Throws
    /// <see cref="InvalidDataException"/> for an invalid package. The caller owns deleting the
    /// returned folder.</summary>
    public static (string ExtractedDir, string MainAssemblyName) ExtractAndVerify(string packagePath) =>
        ExtractAndVerifyWithLimits(packagePath, DefaultExtractionLimits);

    internal static (string ExtractedDir, string MainAssemblyName) ExtractAndVerifyWithLimits(
        string packagePath,
        ExtractionLimits limits,
        string? tempDirectoryRoot = null)
    {
        ValidateLimits(limits);

        using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);

        if (packageStream.Length > limits.MaxPackageBytes)
            throw new InvalidDataException(
                $"Package exceeds the {limits.MaxPackageBytes} byte compressed-size limit.");

        using var zip = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        var layout = ValidateArchiveLayout(zip, limits);
        var index = ReadAndValidateIndex(layout.IndexEntry, limits);
        ValidateExactFileSet(layout, index);

        var tempRoot = Path.GetFullPath(tempDirectoryRoot ?? Path.GetTempPath());
        var tempDir = Path.Combine(tempRoot, "daxalgo-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var actualTotal = layout.IndexEntry.Length;
            foreach (var (entryName, entry) in layout.Files)
            {
                var expectedHash = index.Files[entryName];
                var destination = GetExtractionPath(tempDir, entryName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var copied = CopyAndHashEntry(
                    entry,
                    destination,
                    expectedHash,
                    actualTotal,
                    limits);
                actualTotal = checked(actualTotal + copied);
            }

            return (tempDir, Path.GetFileNameWithoutExtension(index.MainAssembly));
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            throw;
        }
    }

    private static ArchiveLayout ValidateArchiveLayout(ZipArchive zip, ExtractionLimits limits)
    {
        if (zip.Entries.Count > limits.MaxEntryCount)
            throw new InvalidDataException(
                $"Package contains {zip.Entries.Count} entries; the limit is {limits.MaxEntryCount}.");

        var pathRegistry = new PathRegistry();
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var canonicalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? indexEntry = null;
        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in zip.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var path = ValidatePackagePath(entry.FullName, isDirectory, limits);
            pathRegistry.Add(path);
            RejectLinkEntry(entry);

            var uncompressedLength = entry.Length;
            var compressedLength = entry.CompressedLength;
            if (uncompressedLength < 0 || compressedLength < 0)
                throw new InvalidDataException($"Package entry has an invalid size: '{entry.FullName}'.");
            if (compressedLength > limits.MaxPackageBytes)
                throw new InvalidDataException(
                    $"Package entry '{entry.FullName}' declares more compressed data than the package-size limit.");
            if (isDirectory && uncompressedLength != 0)
                throw new InvalidDataException($"Directory entry contains data: '{entry.FullName}'.");
            if (uncompressedLength > limits.MaxEntryUncompressedBytes)
                throw new InvalidDataException(
                    $"Package entry '{entry.FullName}' exceeds the {limits.MaxEntryUncompressedBytes} byte expanded-size limit.");

            CheckCompressionRatio(entry.FullName, uncompressedLength, compressedLength, limits);
            totalUncompressed = checked(totalUncompressed + uncompressedLength);
            totalCompressed = checked(totalCompressed + compressedLength);
            if (totalUncompressed > limits.MaxTotalUncompressedBytes)
                throw new InvalidDataException(
                    $"Package exceeds the {limits.MaxTotalUncompressedBytes} byte total expanded-size limit.");

            if (string.Equals(path.Canonical, IndexEntryName, StringComparison.OrdinalIgnoreCase))
            {
                if (isDirectory || !string.Equals(entry.FullName, IndexEntryName, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"The integrity index must be the single root entry named exactly '{IndexEntryName}', not '{entry.FullName}'.");
                if (indexEntry is not null)
                    throw new InvalidDataException($"Package contains duplicate '{IndexEntryName}' entries.");
                indexEntry = entry;
                continue;
            }

            if (isDirectory) continue;
            files.Add(entry.FullName, entry);
            canonicalFiles.Add(path.Canonical, entry.FullName);
        }

        CheckCompressionRatio("the package as a whole", totalUncompressed, totalCompressed, limits);

        if (indexEntry is null)
            throw new InvalidDataException(
                $"Not a {Extension} package - the {IndexEntryName} integrity index is missing.");
        if (indexEntry.Length > limits.MaxIndexBytes)
            throw new InvalidDataException(
                $"The {IndexEntryName} integrity index exceeds the {limits.MaxIndexBytes} byte limit.");

        return new ArchiveLayout(indexEntry, files, canonicalFiles);
    }

    private static ParsedIndex ReadAndValidateIndex(ZipArchiveEntry indexEntry, ExtractionLimits limits)
    {
        var bytes = ReadEntryBytes(indexEntry, limits.MaxIndexBytes);
        var jsonBytes = bytes.AsMemory();
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            jsonBytes = jsonBytes[Encoding.UTF8.Preamble.Length..];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid {IndexEntryName} integrity index: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Invalid {IndexEntryName}: the root must be a JSON object.");

            int? formatVersion = null;
            string? mainAssembly = null;
            JsonElement? filesElement = null;
            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var canonicalPropertyName = NormalizeForComparison(property.Name, "index property");
                if (!propertyNames.Add(canonicalPropertyName))
                    throw new InvalidDataException(
                        $"Invalid {IndexEntryName}: duplicate property alias '{property.Name}'.");

                if (string.Equals(canonicalPropertyName, "formatVersion", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsed))
                        throw new InvalidDataException($"Invalid {IndexEntryName}: formatVersion must be an integer.");
                    formatVersion = parsed;
                }
                else if (string.Equals(canonicalPropertyName, "mainAssembly", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException($"Invalid {IndexEntryName}: mainAssembly must be a string.");
                    mainAssembly = property.Value.GetString();
                }
                else if (string.Equals(canonicalPropertyName, "files", StringComparison.OrdinalIgnoreCase))
                {
                    filesElement = property.Value;
                }
            }

            if (formatVersion is null)
                throw new InvalidDataException($"Invalid {IndexEntryName}: formatVersion is missing.");
            if (formatVersion != CurrentFormatVersion)
                throw new InvalidDataException(
                    $"Unsupported {IndexEntryName} formatVersion {formatVersion}; only version {CurrentFormatVersion} is supported.");
            if (string.IsNullOrWhiteSpace(mainAssembly))
                throw new InvalidDataException($"Invalid {IndexEntryName}: mainAssembly is missing.");
            if (filesElement is null || filesElement.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Invalid {IndexEntryName}: files must be an object.");

            var mainPath = ValidatePackagePath(mainAssembly, isDirectory: false, limits);
            if (mainAssembly.Contains('/', StringComparison.Ordinal)
                || !mainAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid mainAssembly in the package index: '{mainAssembly}'.");

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var canonicalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pathRegistry = new PathRegistry();
            foreach (var property in filesElement.Value.EnumerateObject())
            {
                if (files.Count >= limits.MaxEntryCount - 1)
                    throw new InvalidDataException(
                        $"The {IndexEntryName} files list exceeds the package entry-count limit.");
                var path = ValidatePackagePath(property.Name, isDirectory: false, limits);
                if (string.Equals(path.Canonical, IndexEntryName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Invalid {IndexEntryName}: the files list must not contain the index itself or an alias of it.");
                pathRegistry.Add(path);

                if (property.Value.ValueKind != JsonValueKind.String
                    || !TryParseSha256(property.Value.GetString(), out var hash))
                    throw new InvalidDataException(
                        $"Invalid sha256 for '{property.Name}' in {IndexEntryName}; expected exactly 64 hexadecimal characters.");

                files.Add(property.Name, hash);
                canonicalFiles.Add(path.Canonical, property.Name);
            }

            if (!files.ContainsKey(mainAssembly))
            {
                if (canonicalFiles.TryGetValue(mainPath.Canonical, out var alias))
                    throw new InvalidDataException(
                        $"mainAssembly '{mainAssembly}' aliases indexed path '{alias}'; names must match exactly.");
                throw new InvalidDataException(
                    $"Package index does not list its declared main assembly '{mainAssembly}'.");
            }

            return new ParsedIndex(mainAssembly, files, canonicalFiles);
        }
    }

    private static void ValidateExactFileSet(ArchiveLayout layout, ParsedIndex index)
    {
        foreach (var entryName in layout.Files.Keys)
        {
            if (index.Files.ContainsKey(entryName)) continue;

            var canonical = NormalizePathForComparison(entryName);
            if (index.CanonicalFiles.TryGetValue(canonical, out var alias))
                throw new InvalidDataException(
                    $"Package index path '{alias}' aliases archive entry '{entryName}'; names must match exactly.");
            throw new InvalidDataException(
                $"Package contains a file not covered by its integrity index: '{entryName}'.");
        }

        foreach (var indexedName in index.Files.Keys)
        {
            if (layout.Files.ContainsKey(indexedName)) continue;

            var canonical = NormalizePathForComparison(indexedName);
            if (layout.CanonicalFiles.TryGetValue(canonical, out var alias))
                throw new InvalidDataException(
                    $"Package index path '{indexedName}' aliases archive entry '{alias}'; names must match exactly.");
            throw new InvalidDataException(
                $"Package is missing a file its integrity index lists: '{indexedName}'.");
        }

        if (!layout.Files.ContainsKey(index.MainAssembly))
            throw new InvalidDataException(
                $"Package does not contain its declared main assembly '{index.MainAssembly}'.");
    }

    private static long CopyAndHashEntry(
        ZipArchiveEntry entry,
        string destination,
        byte[] expectedHash,
        long alreadyExtracted,
        ExtractionLimits limits)
    {
        using var source = entry.Open();
        using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;

            copied = checked(copied + read);
            if (copied > limits.MaxEntryUncompressedBytes)
                throw new InvalidDataException(
                    $"Package entry '{entry.FullName}' exceeds the {limits.MaxEntryUncompressedBytes} byte expanded-size limit while extracting.");
            if (checked(alreadyExtracted + copied) > limits.MaxTotalUncompressedBytes)
                throw new InvalidDataException(
                    $"Package exceeds the {limits.MaxTotalUncompressedBytes} byte total expanded-size limit while extracting.");

            target.Write(buffer, 0, read);
            hasher.AppendData(buffer, 0, read);
        }

        if (copied != entry.Length)
            throw new InvalidDataException(
                $"Package entry length changed while extracting '{entry.FullName}'.");

        var actualHash = hasher.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException(
                $"Package integrity check failed (tampered content): '{entry.FullName}'.");

        return copied;
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, long byteLimit)
    {
        if (entry.Length > byteLimit)
            throw new InvalidDataException(
                $"The {entry.FullName} entry exceeds the {byteLimit} byte limit.");

        using var source = entry.Open();
        using var destination = new MemoryStream(entry.Length > int.MaxValue ? 0 : (int)entry.Length);
        var buffer = new byte[16 * 1024];
        long copied = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            copied = checked(copied + read);
            if (copied > byteLimit)
                throw new InvalidDataException(
                    $"The {entry.FullName} entry exceeds the {byteLimit} byte limit while reading.");
            destination.Write(buffer, 0, read);
        }

        if (copied != entry.Length)
            throw new InvalidDataException($"Package entry length changed while reading '{entry.FullName}'.");
        return destination.ToArray();
    }

    private static ValidatedPath ValidatePackagePath(
        string rawPath,
        bool isDirectory,
        ExtractionLimits limits)
    {
        if (string.IsNullOrEmpty(rawPath))
            throw new InvalidDataException("Package contains an empty path.");
        if (rawPath.Length > limits.MaxPathLength)
            throw new InvalidDataException(
                $"Package path exceeds the {limits.MaxPathLength} character limit: '{rawPath}'.");
        if (rawPath.Contains('\\'))
            throw new InvalidDataException($"Package path uses a backslash: '{rawPath}'.");
        if (rawPath.StartsWith('/'))
            throw new InvalidDataException($"Package path is absolute or UNC-rooted: '{rawPath}'.");

        var path = isDirectory && rawPath.EndsWith('/')
            ? rawPath[..^1]
            : rawPath;
        if (string.IsNullOrEmpty(path))
            throw new InvalidDataException($"Package contains an empty path: '{rawPath}'.");
        if (!isDirectory && rawPath.EndsWith('/'))
            throw new InvalidDataException($"File path has a directory suffix: '{rawPath}'.");

        var segments = path.Split('/');
        if (segments.Length > limits.MaxPathDepth)
            throw new InvalidDataException(
                $"Package path exceeds the {limits.MaxPathDepth} segment depth limit: '{rawPath}'.");

        var normalizedSegments = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
                throw new InvalidDataException($"Package path contains an empty segment: '{rawPath}'.");
            if (segment is "." or "..")
                throw new InvalidDataException(
                    $"Package entry escapes or aliases the extraction path through traversal: '{rawPath}'.");
            if (i == 0 && segment.Length >= 2 && IsAsciiLetter(segment[0]) && segment[1] == ':')
                throw new InvalidDataException($"Package path uses a drive-qualified name: '{rawPath}'.");
            if (segment.IndexOfAny(WindowsInvalidFileNameChars) >= 0 || segment.Any(char.IsControl))
                throw new InvalidDataException($"Package path contains a Windows-invalid or ADS character: '{rawPath}'.");
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException($"Package path has a trailing dot or space alias: '{rawPath}'.");
            if (IsReservedWindowsDeviceName(segment))
                throw new InvalidDataException($"Package path uses a reserved Windows device name: '{rawPath}'.");

            normalizedSegments[i] = NormalizeForComparison(segment, "package path");
        }

        var canonical = string.Join('/', normalizedSegments);
        if (canonical.Length > limits.MaxPathLength)
            throw new InvalidDataException(
                $"Normalized package path exceeds the {limits.MaxPathLength} character limit: '{rawPath}'.");
        return new ValidatedPath(path, canonical, isDirectory);
    }

    private static string NormalizePathForComparison(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = NormalizeForComparison(segments[i], "package path");
        return string.Join('/', segments);
    }

    private static string NormalizeForComparison(string value, string kind)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Invalid Unicode in {kind}: '{value}'.", ex);
        }
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stem.Length != 4) return false;
        var prefix = stem[..3];
        if (!prefix.Equals("COM", StringComparison.OrdinalIgnoreCase)
            && !prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase))
            return false;
        return stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static bool TryParseSha256(string? value, out byte[] hash)
    {
        hash = [];
        if (value is null || value.Length != 64 || value.Any(c => !IsAsciiHex(c))) return false;
        hash = Convert.FromHexString(value);
        return true;
    }

    private static bool IsAsciiHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool PathsAlias(string firstPath, string secondPath)
    {
        var first = NormalizeForComparison(Path.GetFullPath(firstPath), "file path");
        var second = NormalizeForComparison(Path.GetFullPath(secondPath), "file path");
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;

        // Resolve a final-component symbolic link when one exists. Lexical normalization above
        // handles relative/case/Unicode aliases; this additionally catches output links to inputs.
        var firstTarget = File.Exists(firstPath)
            ? File.ResolveLinkTarget(firstPath, returnFinalTarget: true)
            : null;
        var secondTarget = File.Exists(secondPath)
            ? File.ResolveLinkTarget(secondPath, returnFinalTarget: true)
            : null;
        if (firstTarget is null && secondTarget is null) return false;
        first = NormalizeForComparison(Path.GetFullPath(firstTarget?.FullName ?? firstPath), "file path");
        second = NormalizeForComparison(Path.GetFullPath(secondTarget?.FullName ?? secondPath), "file path");
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckCompressionRatio(
        string name,
        long uncompressedBytes,
        long compressedBytes,
        ExtractionLimits limits)
    {
        if (uncompressedBytes == 0) return;
        if (compressedBytes == 0)
            throw new InvalidDataException(
                $"Package entry '{name}' has non-empty content but no compressed data.");
        var bytesSubjectToRatio = Math.Max(0, uncompressedBytes - limits.CompressionRatioSlackBytes);
        var minimumCompressedBytes = bytesSubjectToRatio == 0
            ? 0
            : 1 + ((bytesSubjectToRatio - 1) / limits.MaxCompressionRatio);
        if (compressedBytes < minimumCompressedBytes)
            throw new InvalidDataException(
                $"Package entry '{name}' exceeds the {limits.MaxCompressionRatio}:1 compression-ratio limit.");
    }

    private static void RejectLinkEntry(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixMode == unixSymbolicLink || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Package entry may not be a symbolic link or reparse point: '{entry.FullName}'.");
    }

    private static string GetExtractionPath(string tempDir, string entryName)
    {
        var destination = Path.GetFullPath(
            Path.Combine(tempDir, entryName.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = tempDir.EndsWith(Path.DirectorySeparatorChar)
            ? tempDir
            : tempDir + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Package entry escapes the extraction folder: '{entryName}'.");
        return destination;
    }

    private static void ValidateLimits(ExtractionLimits limits)
    {
        if (limits.MaxPackageBytes <= 0
            || limits.MaxEntryCount <= 0
            || limits.MaxIndexBytes <= 0
            || limits.MaxEntryUncompressedBytes <= 0
            || limits.MaxTotalUncompressedBytes <= 0
            || limits.MaxCompressionRatio <= 0
            || limits.CompressionRatioSlackBytes < 0
            || limits.MaxPathLength <= 0
            || limits.MaxPathDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "All extraction limits must be positive.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best effort; the original validation error is more useful */ }
    }

    private sealed class PathRegistry
    {
        private readonly Dictionary<string, (string Original, bool IsDirectory)> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

        public void Add(ValidatedPath path)
        {
            if (_entries.TryGetValue(path.Canonical, out var existing))
            {
                if (existing.IsDirectory != path.IsDirectory)
                    throw new InvalidDataException(
                        $"Package contains a directory/file conflict between '{existing.Original}' and '{path.Original}'.");
                throw new InvalidDataException(
                    $"Package contains duplicate exact, case-insensitive, or Unicode-normalized path aliases: " +
                    $"'{existing.Original}' and '{path.Original}'.");
            }

            if (path.IsDirectory)
            {
                if (_files.Contains(path.Canonical))
                    throw new InvalidDataException($"Package path is both a file and directory: '{path.Original}'.");
            }
            else if (_directories.Contains(path.Canonical))
            {
                throw new InvalidDataException(
                    $"Package file conflicts with a directory or parent of another file: '{path.Original}'.");
            }

            var slash = path.Canonical.IndexOf('/');
            while (slash >= 0)
            {
                var ancestor = path.Canonical[..slash];
                if (_files.Contains(ancestor))
                    throw new InvalidDataException(
                        $"Package file is used as a directory parent: '{path.Original}'.");
                _directories.Add(ancestor);
                slash = path.Canonical.IndexOf('/', slash + 1);
            }

            if (path.IsDirectory)
                _directories.Add(path.Canonical);
            else
                _files.Add(path.Canonical);
            _entries.Add(path.Canonical, (path.Original, path.IsDirectory));
        }
    }
}
