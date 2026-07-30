using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Minimal public-side DAXQ detector. It deliberately knows none of the protected VM contract: a
/// candidate must have the <c>.daxq</c> extension and a bounded, cleartext root
/// <c>manifest.json</c> whose <c>kind</c> is <c>daxq</c>. Full format, signature, integrity, and
/// decryption validation belongs to <see cref="IProtectedStrategyEngine"/>.
/// </summary>
internal static class DaxqPackageDetector
{
    private const string PackageExtension = ".daxq";
    private const string ManifestEntryName = "manifest.json";
    private const string PackageKind = "daxq";
    private const int MaxPackageBytes = 10 * 1024 * 1024;
    private const int MaxArchiveEntries = 128;
    private const int MaxManifestBytes = 64 * 1024;

    public static bool HasPackageExtension(string path) =>
        string.Equals(Path.GetExtension(path), PackageExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns public discovery metadata for a DAXQ package, or <c>null</c> when a file with
    /// the extension is not a DAXQ package. A present but malformed root manifest is reported as an
    /// invalid manifest rather than being allowed to consume unbounded input.</summary>
    public static DaxqPackageMetadata? TryRead(string path)
    {
        if (!HasPackageExtension(path)) return null;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaxPackageBytes)
                throw new ProtectedStrategyManifestException(
                    path, $"package size must be between 1 and {MaxPackageBytes} bytes");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxArchiveEntries)
                throw new ProtectedStrategyManifestException(
                    path, $"archive contains more than {MaxArchiveEntries} entries");
            var manifests = archive.Entries
                .Where(e => string.Equals(e.FullName, ManifestEntryName, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (manifests.Length == 0) return null;
            if (manifests.Length != 1)
                throw new ProtectedStrategyManifestException(path, "contains more than one root manifest.json entry");

            var bytes = ReadBounded(manifests[0], path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProtectedStrategyManifestException(path, "root manifest.json must be a JSON object");

            if (!root.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), PackageKind, StringComparison.Ordinal))
                return null;

            string? strategyId = null;
            if (root.TryGetProperty("strategyId", out var id) && id.ValueKind == JsonValueKind.String)
                strategyId = id.GetString();

            return new DaxqPackageMetadata(strategyId);
        }
        catch (ProtectedStrategyManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException
                                   or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ProtectedStrategyManifestException(path, ex.Message, ex);
        }
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, string path)
    {
        if (entry.Length > MaxManifestBytes)
            throw new ProtectedStrategyManifestException(path,
                $"root manifest.json exceeds the {MaxManifestBytes}-byte discovery limit");

        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, MaxManifestBytes));
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var remaining = MaxManifestBytes + 1 - total;
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            output.Write(buffer, 0, read);
            total += read;
            if (total > MaxManifestBytes)
                throw new ProtectedStrategyManifestException(path,
                    $"root manifest.json exceeds the {MaxManifestBytes}-byte discovery limit");
        }
        return output.ToArray();
    }
}

internal sealed record DaxqPackageMetadata(string? StrategyId);

internal sealed class ProtectedStrategyManifestException : Exception
{
    public ProtectedStrategyManifestException(string packagePath, string reason, Exception? inner = null)
        : base($"Invalid protected-strategy manifest in '{packagePath}': {reason}", inner)
    {
    }
}
